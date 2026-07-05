using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// An asynchronous session that recursively scans a dropped/specified directory with libssconverter's
    /// (FFI) scan API and collects the .sspj files beneath it.
    ///
    /// Directory traversal itself is delegated to the native side (`ss_converter_scan_dir` ...); C# just
    /// polls in EditorApplication.update and observes progress non-blockingly.
    /// The scan runs on a separate thread so the editor does not freeze. A budget (depth/count/time)
    /// prevents runaway scans.
    /// </summary>
    internal sealed class SpriteStudioDirectoryScan
    {
        // Default budget (same values as the Godot version)
        private const uint DefaultMaxDepth = 16;
        private const ulong DefaultMaxMillis = 5000;
        private static readonly nuint DefaultMaxEntries = 50000;

        // For "unbounded rescan" (depth is large enough / count is the max / time is unlimited)
        // nuint.MaxValue does not exist in .NET Standard 2.1, so obtain the max by casting from ulong.MaxValue.
        private const uint UnboundedMaxDepth = 64;
        private const ulong UnboundedMaxMillis = 0;
        private static readonly nuint UnboundedMaxEntries = unchecked((nuint)ulong.MaxValue);

        private readonly Queue<string> _dirs;
        private readonly List<string> _results = new List<string>();
        private readonly bool _interactive;
        private readonly Action<List<string>> _onComplete;

        private IntPtr _ctx;          // The context currently scanning (Zero if none)
        private string _currentDir;
        private int _progressId = -1;
        private bool _canceled;

        private SpriteStudioDirectoryScan(IList<string> dirs, bool interactive, Action<List<string>> onComplete)
        {
            _dirs = new Queue<string>(dirs);
            _interactive = interactive;
            _onComplete = onComplete;
        }

        /// <summary>Scans the directories asynchronously and, on completion, calls onComplete with the collected .sspj list.</summary>
        public static void Start(IList<string> dirs, bool interactive, Action<List<string>> onComplete)
        {
            var session = new SpriteStudioDirectoryScan(dirs, interactive, onComplete);
            session._progressId = Progress.Start("SpriteStudio Scan", "Scanning for .sspj ...");
            Progress.RegisterCancelCallback(session._progressId, session.OnCancel);
            EditorApplication.update += session.Poll;
            session.PumpNextDir();
        }

        private bool OnCancel()
        {
            _canceled = true;
            AbortCurrent();
            return true;
        }

        private void AbortCurrent()
        {
            if (_ctx == IntPtr.Zero) return;
            unsafe
            {
                var c = (SpriteStudio.SsConverter.Context*)_ctx.ToPointer();
                SpriteStudio.SsConverter.NativeMethods.ss_converter_abort(c);
            }
        }

        private void PumpNextDir()
        {
            if (_canceled || _ctx != IntPtr.Zero || _dirs.Count == 0) return;
            _currentDir = _dirs.Dequeue();
            StartScan(_currentDir, DefaultMaxDepth, DefaultMaxEntries, DefaultMaxMillis);
        }

        private void StartScan(string dir, uint depth, nuint entries, ulong millis)
        {
            unsafe
            {
                var ctx = SpriteStudio.SsConverter.NativeMethods.ss_converter_create();
                var dirBytes = Encoding.UTF8.GetBytes(dir + "\0");
                fixed (byte* dirPtr = dirBytes)
                {
                    SpriteStudio.SsConverter.NativeMethods.ss_converter_scan_dir(ctx, dirPtr, depth, entries, millis);
                }
                _ctx = (IntPtr)ctx;
            }
        }

        private void Poll()
        {
            // Called from EditorApplication.update. If an exception leaks, Unity unregisters the update
            // delegate and the session becomes stuck (progress bar frozen, onComplete never called).
            // On exception, terminate safely and self-recover.
            try
            {
                PollInner();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SSPlayer] Directory scan poll error: {e.Message}");
                // Do not destroy a context whose scan thread is still running (this SDK's destroy does not
                // join, it only frees the Box, so destroying while running could cause a UAF). Issue only an
                // abort request and abandon it (leaking one context on this rare error path is accepted as a
                // trade-off for avoiding a UAF).
                if (_ctx != IntPtr.Zero)
                {
                    AbortCurrent();
                    _ctx = IntPtr.Zero;
                }
                _canceled = true;
                Finish();
            }
        }

        private void PollInner()
        {
            if (_ctx != IntPtr.Zero)
            {
                unsafe
                {
                    var ctx = (SpriteStudio.SsConverter.Context*)_ctx.ToPointer();

                    if (_progressId != -1 && !_canceled)
                    {
                        nuint found = SpriteStudio.SsConverter.NativeMethods.ss_converter_scan_found(ctx);
                        nuint visited = SpriteStudio.SsConverter.NativeMethods.ss_converter_scan_visited(ctx);
                        Progress.Report(_progressId, 0f,
                            $"Scanning '{Path.GetFileName(_currentDir)}' ... found {found} (visited {visited})");
                    }

                    if (!SpriteStudio.SsConverter.NativeMethods.ss_converter_is_finished(ctx))
                    {
                        return; // still scanning
                    }

                    // Finished (is_finished=true, the scan thread has ended) -> collect the results.
                    // Even if collection throws, always destroy the context and release _ctx. Since it is
                    // finished, destroy is safe, and this prevents a leak and a stuck _ctx (= no further
                    // directories progress).
                    bool budgetExceeded = false;
                    var dirResults = new List<string>();
                    try
                    {
                        budgetExceeded = SpriteStudio.SsConverter.NativeMethods.ss_converter_scan_budget_exceeded(ctx);
                        nuint count = SpriteStudio.SsConverter.NativeMethods.ss_converter_scan_result_count(ctx);
                        for (nuint i = 0; i < count; i++)
                        {
                            byte* p = SpriteStudio.SsConverter.NativeMethods.ss_converter_scan_result_at(ctx, i);
                            string s = PtrToStringUtf8(p);
                            if (!string.IsNullOrEmpty(s)) dirResults.Add(s);
                        }
                    }
                    finally
                    {
                        SpriteStudio.SsConverter.NativeMethods.ss_converter_destroy(ctx);
                        _ctx = IntPtr.Zero;
                    }

                    if (!_canceled)
                    {
                        HandleDirCompleted(dirResults, budgetExceeded);
                    }
                }

                // If a rescan has not started, move on to the next directory
                if (_ctx == IntPtr.Zero) PumpNextDir();
            }

            // No context is scanning and the queue is drained or canceled -> end the session
            if (_ctx == IntPtr.Zero && (_canceled || _dirs.Count == 0))
            {
                Finish();
            }
        }

        private void HandleDirCompleted(List<string> dirResults, bool budgetExceeded)
        {
            if (!budgetExceeded)
            {
                _results.AddRange(dirResults);
                return;
            }

            if (!_interactive)
            {
                Debug.LogWarning($"[SSPlayer] Scan budget exceeded for '{_currentDir}'. Using {dirResults.Count} found so far.");
                _results.AddRange(dirResults);
                return;
            }

            int choice = EditorUtility.DisplayDialogComplex(
                "SpriteStudio: Scan limit reached",
                $"'{_currentDir}'\nFound {dirResults.Count} .sspj file(s) (scan limit reached).\nWhat would you like to do?",
                "Use what was found", "Cancel", "Rescan without limit");

            switch (choice)
            {
                case 0: // Use what was found
                    _results.AddRange(dirResults);
                    break;
                case 1: // Cancel (stop the whole session)
                    _canceled = true;
                    break;
                default: // 2: Rescan without limit (redo the same directory, discard the partial results)
                    StartScan(_currentDir, UnboundedMaxDepth, UnboundedMaxEntries, UnboundedMaxMillis);
                    break;
            }
        }

        private void Finish()
        {
            EditorApplication.update -= Poll;
            if (_progressId != -1)
            {
                Progress.Remove(_progressId);
                _progressId = -1;
            }
            _onComplete?.Invoke(_results);
        }

        private static unsafe string PtrToStringUtf8(byte* p)
        {
            if (p == null) return null;
            int len = 0;
            while (p[len] != 0) len++;
            return len == 0 ? string.Empty : Encoding.UTF8.GetString(p, len);
        }
    }
}
