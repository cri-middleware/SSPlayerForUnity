using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Runs .sspj -> .ssab conversion via libssconverter with a "queue + concurrency limit + progress display".
    ///
    /// - Non-blocking because it polls in EditorApplication.update (the editor does not freeze during conversion).
    /// - Even when a large number is enqueued (e.g. dragging a folder), native contexts are created only when a
    ///   slot is free, and concurrency is capped by the CPU count (prevents too many threads).
    /// - Shows progress in the status bar via UnityEditor.Progress and supports cancellation.
    /// </summary>
    public static class SpriteStudioConversionRunner
    {
        private struct Job
        {
            public string SspjPath;
            public string OutputDir;
        }

        private sealed class Active
        {
            public IntPtr Context;
            public string SspjPath;
            public string OutputDir;
            /// <summary>The .ssab already in the output folder when this job started, with their write times.</summary>
            public Dictionary<string, DateTime> SsabBefore;
        }

        private static readonly List<Job> _pending = new List<Job>();
        private static readonly List<Active> _active = new List<Active>();
        private static bool _polling;
        // A domain reload while jobs run would drop this class's state -- the queue, and the native
        // contexts still converting -- so the batch never finishes: its .ssab are not registered in the
        // source map and never refreshed. Script reloads wait until the batch ends, as they do for an import.
        private static bool _reloadLocked;

        // Progress (a single Progress for the whole running batch; lives until the queue is empty)
        private static int _progressId = -1;
        private static int _total;
        private static int _completed;
        private static int _succeeded;
        private static int _failed;
        private static bool _canceled;

        /// <summary>
        /// Raised on the main thread once per job that has finished, with the .sspj and whether it converted.
        /// A converter error and an aborted job both report false.
        /// </summary>
        public static event Action<string, bool> JobFinished;

        /// <summary>True if a conversion is currently running or pending.</summary>
        public static bool IsProcessing => _polling || _pending.Count > 0 || _active.Count > 0;

        private static int Concurrency => Mathf.Clamp(SystemInfo.processorCount - 1, 1, 8);

        /// <summary>True when there are no pending or running jobs and all post-processing (ProcessFinished) is done.
        /// Used as a wait condition, e.g. in tests, to "delete the output directory only after conversion is fully finished".</summary>
        public static bool IsIdle => _pending.Count == 0 && _active.Count == 0;

        /// <summary>Enqueues one conversion job. Starts it immediately if a slot is free.</summary>
        public static void Enqueue(string sspjPath, string outputDir)
        {
            if (string.IsNullOrEmpty(sspjPath) || !File.Exists(sspjPath))
            {
                Debug.LogError($"[SSPlayer] SSPJ file not found: {sspjPath}");
                return;
            }

            try
            {
                if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);
                // The one place the output path is normalized, so that "two jobs, one folder" below is a
                // string comparison the callers cannot defeat with a different spelling of the same path.
                outputDir = Path.GetFullPath(outputDir);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SSPlayer] Failed to create output dir '{outputDir}': {e.Message}");
                return;
            }

            _canceled = false;
            _pending.Add(new Job { SspjPath = sspjPath, OutputDir = outputDir });
            _total++;

            EnsureProgress();

            if (!_polling)
            {
                EditorApplication.update += Poll;
                _polling = true;
                if (!_reloadLocked)
                {
                    EditorApplication.LockReloadAssemblies();
                    _reloadLocked = true;
                }
            }

            Pump();
        }

        private static void EnsureProgress()
        {
            if (_progressId == -1)
            {
                _progressId = Progress.Start("SpriteStudio Import", "Converting .sspj ...");
                Progress.RegisterCancelCallback(_progressId, OnCancel);
            }
            ReportProgress();
        }

        private static bool OnCancel()
        {
            _canceled = true;

            // A job dropped before it ever ran converted nothing, and says so, so that a subscriber does not
            // go on believing it was done.
            var dropped = new List<Job>(_pending);
            _pending.Clear();
            foreach (var job in dropped)
            {
                RaiseJobFinished(job.SspjPath, false);
            }

            unsafe
            {
                foreach (var a in _active)
                {
                    var ctx = (SpriteStudio.SsConverter.Context*)a.Context.ToPointer();
                    SpriteStudio.SsConverter.NativeMethods.ss_converter_abort(ctx);
                }
            }
            return true; // In-flight jobs have been asked to abort; they will be collected in Poll.
        }

        private static void Pump()
        {
            int cap = Concurrency;
            while (!_canceled && _active.Count < cap)
            {
                int index = NextStartableIndex();
                if (index < 0) break; // everything left is waiting for a folder a running job holds

                var job = _pending[index];
                _pending.RemoveAt(index);
                StartJob(job);
            }
        }

        /// <summary>
        /// The first pending job whose output folder no running job holds, or -1 when every pending job has
        /// to wait. Two conversions writing one folder would have their converter threads racing over the
        /// same .ssab and the same copied textures, so they take turns instead. Reached whenever two .sspj
        /// share a file name -- the importer's plan catches that within one batch, but each Reimport of a
        /// .sspj inside the project enqueues on its own, and those the plan never sees together.
        /// </summary>
        private static int NextStartableIndex()
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (!IsOutputDirActive(_pending[i].OutputDir)) return i;
            }
            return -1;
        }

        private static bool IsOutputDirActive(string outputDir)
        {
            foreach (var active in _active)
            {
                if (string.Equals(active.OutputDir, outputDir, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void StartJob(Job job)
        {
            unsafe
            {
                var context = SpriteStudio.SsConverter.NativeMethods.ss_converter_create();
                if (context == null)
                {
                    // If the context can't be created it isn't added to _active and progress won't advance,
                    // so Progress never reaches 100%. Treat it as completed to avoid stalling.
                    Debug.LogError($"[SSPlayer] ss_converter_create() failed for '{Path.GetFileName(job.SspjPath)}'. Skipping.");
                    _completed++;
                    _failed++;
                    RaiseJobFinished(job.SspjPath, false);
                    return;
                }

                try
                {
                    // Taken before the conversion starts, so that ProcessFinished can tell what this job
                    // wrote from what was already lying in the folder.
                    var ssabBefore = SnapshotSsabs(job.OutputDir);

                    var sspjBytes = Encoding.UTF8.GetBytes(job.SspjPath + "\0");
                    var outDirBytes = Encoding.UTF8.GetBytes(job.OutputDir + "\0");

                    // The Rust side (ss_converter_convert) copies the CStr via into_owned before spawning,
                    // so it is safe even if the buffer is freed after leaving the fixed block (no use-after-free).
                    fixed (byte* sspjPtr = sspjBytes)
                    fixed (byte* outDirPtr = outDirBytes)
                    {
                        SpriteStudio.SsConverter.NativeMethods.ss_converter_convert(context, sspjPtr, outDirPtr, false, null);
                    }

                    _active.Add(new Active
                    {
                        Context = (IntPtr)context,
                        SspjPath = job.SspjPath,
                        OutputDir = job.OutputDir,
                        SsabBefore = ssabBefore,
                    });
                }
                catch (Exception e)
                {
                    // Failed to start the conversion (marshalling, etc.). If the context isn't destroyed here it
                    // leaks forever, and since it isn't added to _active, progress also stalls. Treat it as
                    // completed and reclaim it.
                    Debug.LogError($"[SSPlayer] Failed to start conversion for '{Path.GetFileName(job.SspjPath)}': {e.Message}");
                    SpriteStudio.SsConverter.NativeMethods.ss_converter_destroy(context);
                    _completed++;
                    _failed++;
                    RaiseJobFinished(job.SspjPath, false);
                }
            }

            ReportProgress(Path.GetFileName(job.SspjPath));
        }

        private static void Poll()
        {
            bool anyCompleted = false;
            // Raised after the loop rather than inside it: a handler is free to enqueue more work, and the
            // loop is walking _active. Built only when something finished -- Poll runs every editor frame.
            List<(string SspjPath, bool Succeeded)> outcomes = null;

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var conversion = _active[i];

                bool finished;
                unsafe
                {
                    var ctx = (SpriteStudio.SsConverter.Context*)conversion.Context.ToPointer();
                    finished = SpriteStudio.SsConverter.NativeMethods.ss_converter_is_finished(ctx);
                }
                if (!finished) continue; // still converting

                // Result processing (file enumeration / source map registration) can throw. If the exception
                // propagates to EditorApplication.update, Poll is unregistered, the whole pipeline stops, and
                // the context leaks. Always do destroy / reclaim / progress increment in finally to self-recover.
                var outcome = JobOutcome.Failed;
                try
                {
                    outcome = ProcessFinished(conversion);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[SSPlayer] Error finalizing conversion of '{Path.GetFileName(conversion.SspjPath)}': {e.Message}");
                }
                finally
                {
                    unsafe
                    {
                        var ctx = (SpriteStudio.SsConverter.Context*)conversion.Context.ToPointer();
                        SpriteStudio.SsConverter.NativeMethods.ss_converter_destroy(ctx);
                    }
                    _active.RemoveAt(i);
                    _completed++;
                    // An aborted job is neither: the user stopped it, and Finish() reports the cancel itself.
                    if (outcome == JobOutcome.Succeeded) _succeeded++;
                    else if (outcome == JobOutcome.Failed) _failed++;
                    if (outcomes == null) outcomes = new List<(string SspjPath, bool Succeeded)>();
                    outcomes.Add((conversion.SspjPath, outcome == JobOutcome.Succeeded));
                    anyCompleted = true;
                }
            }

            if (outcomes != null)
            {
                foreach (var outcome in outcomes)
                {
                    RaiseJobFinished(outcome.SspjPath, outcome.Succeeded);
                }
            }

            if (anyCompleted)
            {
                Pump(); // fill the freed slots with the next jobs
                ReportProgress();
            }

            // All done (or, on cancel, pending is empty + active has been reclaimed) -> clean up
            if (_active.Count == 0 && _pending.Count == 0)
            {
                Finish();
            }
        }

        private enum JobOutcome
        {
            Succeeded,
            Failed,
            Aborted,
        }

        /// <summary>Processes the result of a finished conversion (success/failure log + source map registration of the generated .ssab).
        /// The caller (Poll) always destroys the context in its finally block.</summary>
        private static JobOutcome ProcessFinished(Active conversion)
        {
            unsafe
            {
                var ctx = (SpriteStudio.SsConverter.Context*)conversion.Context.ToPointer();
                var result = SpriteStudio.SsConverter.NativeMethods.ss_converter_get_result(ctx);
                if (result == SpriteStudio.SsConverter.CConverterError.ErrorAborted)
                {
                    // The user asked for this one to stop, so it is not an error to report.
                    return JobOutcome.Aborted;
                }
                if (result != SpriteStudio.SsConverter.CConverterError.Success)
                {
                    byte* errData;
                    nuint errLen;
                    SpriteStudio.SsConverter.NativeMethods.ss_converter_get_error(ctx, &errData, &errLen);
                    string name = Path.GetFileName(conversion.SspjPath);
                    if (errData != null && errLen > 0)
                    {
                        var errStr = Encoding.UTF8.GetString(errData, (int)errLen);
                        Debug.LogError($"[SSPlayer] Converter Error ({name}): {errStr}");
                    }
                    else
                    {
                        Debug.LogError($"[SSPlayer] Converter Error ({name}): {result}");
                    }
                    return JobOutcome.Failed;
                }
            }

            // Success: register the .ssab this conversion wrote, together, and save just once at the end
            // (avoid rewriting the entire map JSON for every single file).
            var writtenSsabs = CollectWrittenSsabs(conversion);
            if (writtenSsabs.Count > 0)
            {
                foreach (var ssab in writtenSsabs)
                {
                    SpriteStudioSourceMap.Register(ssab, conversion.SspjPath, save: false);
                }
                SpriteStudioSourceMap.SaveMap();
            }

            return JobOutcome.Succeeded;
        }

        /// <summary>
        /// The .ssab in the output folder that this conversion created or rewrote.
        /// The folder is an ordinary project folder that may also hold output from an earlier, different
        /// .sspj -- overwriting one project's folder with another's is a confirmed choice, and only the
        /// files actually written change hands. Registering the whole folder instead would point a stale
        /// file's Reconvert at a project that never produced it.
        /// </summary>
        private static List<string> CollectWrittenSsabs(Active conversion)
        {
            var written = new List<string>();
            foreach (var ssab in Directory.GetFiles(conversion.OutputDir, "*.ssab", SearchOption.AllDirectories))
            {
                if (conversion.SsabBefore.TryGetValue(ssab, out DateTime before) &&
                    before == File.GetLastWriteTimeUtc(ssab))
                {
                    continue; // untouched by this conversion
                }
                written.Add(ssab);
            }
            return written;
        }

        private static Dictionary<string, DateTime> SnapshotSsabs(string outputDir)
        {
            var snapshot = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(outputDir)) return snapshot;

            foreach (var ssab in Directory.GetFiles(outputDir, "*.ssab", SearchOption.AllDirectories))
            {
                snapshot[ssab] = File.GetLastWriteTimeUtc(ssab);
            }
            return snapshot;
        }

        private static void RaiseJobFinished(string sspjPath, bool succeeded)
        {
            try
            {
                JobFinished?.Invoke(sspjPath, succeeded);
            }
            catch (Exception e)
            {
                // A subscriber must not be able to stop the pipeline: Poll runs on EditorApplication.update.
                Debug.LogError($"[SSPlayer] A JobFinished handler threw: {e.Message}");
            }
        }

        private static void Finish()
        {
            EditorApplication.update -= Poll;
            _polling = false;
            if (_reloadLocked)
            {
                _reloadLocked = false;
                EditorApplication.UnlockReloadAssemblies();
            }

            if (_progressId != -1)
            {
                Progress.Remove(_progressId);
                _progressId = -1;
            }

            bool wasCanceled = _canceled;
            int converted = _succeeded;
            int failed = _failed;
            _total = 0;
            _completed = 0;
            _succeeded = 0;
            _failed = 0;
            _canceled = false;

            // Let Unity import the generated .ssab / textures (once, at the end of the batch)
            AssetDatabase.Refresh();

            // Count what was converted, not what was attempted: the tally is the only account of a batch
            // once its individual errors have scrolled out of the console.
            if (wasCanceled)
            {
                Debug.Log($"[SSPlayer] Import canceled. ({converted} converted)");
            }
            else if (failed > 0)
            {
                Debug.LogWarning($"[SSPlayer] Convert finished: {converted} file(s), {failed} failed (see the errors above).");
            }
            else if (converted > 0)
            {
                Debug.Log($"[SSPlayer] Convert finished: {converted} file(s).");
            }
        }

        private static void ReportProgress(string currentName = null)
        {
            if (_progressId == -1) return;
            float ratio = _total > 0 ? (float)_completed / _total : 0f;
            string desc = currentName != null
                ? $"Converting {_completed}/{_total}: {currentName}"
                : $"Converting {_completed}/{_total}";
            Progress.Report(_progressId, ratio, desc);
        }
    }
}
