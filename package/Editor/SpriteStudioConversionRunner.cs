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
        }

        private static readonly Queue<Job> _pending = new Queue<Job>();
        private static readonly List<Active> _active = new List<Active>();
        private static bool _polling;

        // Progress (a single Progress for the whole running batch; lives until the queue is empty)
        private static int _progressId = -1;
        private static int _total;
        private static int _completed;
        private static bool _canceled;

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
            }
            catch (Exception e)
            {
                Debug.LogError($"[SSPlayer] Failed to create output dir '{outputDir}': {e.Message}");
                return;
            }

            _canceled = false;
            _pending.Enqueue(new Job { SspjPath = sspjPath, OutputDir = outputDir });
            _total++;

            EnsureProgress();

            if (!_polling)
            {
                EditorApplication.update += Poll;
                _polling = true;
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
            _pending.Clear();
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
            while (!_canceled && _active.Count < cap && _pending.Count > 0)
            {
                StartJob(_pending.Dequeue());
            }
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
                    return;
                }

                try
                {
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
                }
            }

            ReportProgress(Path.GetFileName(job.SspjPath));
        }

        private static void Poll()
        {
            bool anyCompleted = false;

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
                try
                {
                    ProcessFinished(conversion);
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
                    anyCompleted = true;
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

        /// <summary>Processes the result of a finished conversion (success/failure log + source map registration of the generated .ssab).
        /// The caller (Poll) always destroys the context in its finally block.</summary>
        private static void ProcessFinished(Active conversion)
        {
            unsafe
            {
                var ctx = (SpriteStudio.SsConverter.Context*)conversion.Context.ToPointer();
                var result = SpriteStudio.SsConverter.NativeMethods.ss_converter_get_result(ctx);
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
                    return;
                }
            }

            // Success: register the generated .ssab files together and save just once at the end
            // (avoid rewriting the entire map JSON for every single file).
            var generatedSsabs = Directory.GetFiles(conversion.OutputDir, "*.ssab", SearchOption.AllDirectories);
            if (generatedSsabs.Length > 0)
            {
                foreach (var ssab in generatedSsabs)
                {
                    SpriteStudioSourceMap.Register(ssab, conversion.SspjPath, save: false);
                }
                SpriteStudioSourceMap.SaveMap();
            }
        }

        private static void Finish()
        {
            EditorApplication.update -= Poll;
            _polling = false;

            if (_progressId != -1)
            {
                Progress.Remove(_progressId);
                _progressId = -1;
            }

            bool wasCanceled = _canceled;
            int converted = _completed;
            _total = 0;
            _completed = 0;
            _canceled = false;

            // Let Unity import the generated .ssab / textures (once, at the end of the batch)
            AssetDatabase.Refresh();

            if (wasCanceled)
            {
                Debug.Log($"[SSPlayer] Import canceled. ({converted} converted)");
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
