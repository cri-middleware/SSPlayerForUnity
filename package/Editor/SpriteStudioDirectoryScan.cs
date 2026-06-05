using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// ドロップ/指定されたディレクトリを libssconverter (FFI) の scan API で再帰走査し、
    /// 配下の .sspj を収集する非同期セッション。
    ///
    /// ディレクトリ走査そのものはネイティブ側 (`ss_converter_scan_dir` … ) に委譲し、
    /// C# は EditorApplication.update でポーリングしてノンブロッキングに進捗を見るだけにする。
    /// 走査は別スレッドで動くためエディタは固まらない。バジェット(深さ/件数/時間)で暴走を防ぐ。
    /// </summary>
    internal sealed class SpriteStudioDirectoryScan
    {
        // 既定バジェット (Godot 版と同値)
        private const uint DefaultMaxDepth = 16;
        private const ulong DefaultMaxMillis = 5000;
        private static readonly nuint DefaultMaxEntries = 50000;

        // 「上限なし再スキャン」用 (depth は十分大きく / 件数は最大 / 時間無制限)
        // nuint.MaxValue は .NET Standard 2.1 に無いため ulong.MaxValue からキャストして最大値を得る。
        private const uint UnboundedMaxDepth = 64;
        private const ulong UnboundedMaxMillis = 0;
        private static readonly nuint UnboundedMaxEntries = unchecked((nuint)ulong.MaxValue);

        private readonly Queue<string> _dirs;
        private readonly List<string> _results = new List<string>();
        private readonly bool _interactive;
        private readonly Action<List<string>> _onComplete;

        private IntPtr _ctx;          // 走査中の context (なければ Zero)
        private string _currentDir;
        private int _progressId = -1;
        private bool _canceled;

        private SpriteStudioDirectoryScan(IList<string> dirs, bool interactive, Action<List<string>> onComplete)
        {
            _dirs = new Queue<string>(dirs);
            _interactive = interactive;
            _onComplete = onComplete;
        }

        /// <summary>ディレクトリ群を非同期走査し、完了時に収集した .sspj 一覧で onComplete を呼ぶ。</summary>
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
                        return; // まだ走査中
                    }

                    // 完了 → このディレクトリの結果を一旦ローカルに回収
                    bool budgetExceeded = SpriteStudio.SsConverter.NativeMethods.ss_converter_scan_budget_exceeded(ctx);
                    var dirResults = new List<string>();
                    nuint count = SpriteStudio.SsConverter.NativeMethods.ss_converter_scan_result_count(ctx);
                    for (nuint i = 0; i < count; i++)
                    {
                        byte* p = SpriteStudio.SsConverter.NativeMethods.ss_converter_scan_result_at(ctx, i);
                        string s = PtrToStringUtf8(p);
                        if (!string.IsNullOrEmpty(s)) dirResults.Add(s);
                    }

                    SpriteStudio.SsConverter.NativeMethods.ss_converter_destroy(ctx);
                    _ctx = IntPtr.Zero;

                    if (!_canceled)
                    {
                        HandleDirCompleted(dirResults, budgetExceeded);
                    }
                }

                // 再スキャンが始まっていなければ次のディレクトリへ
                if (_ctx == IntPtr.Zero) PumpNextDir();
            }

            // 走査中の context が無く、キュー枯渇 or キャンセル → セッション終了
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
                "SpriteStudio: スキャン上限に到達",
                $"'{_currentDir}'\n{dirResults.Count} 件の .sspj が見つかりました (走査上限に到達)。\nどうしますか?",
                "見つかった分を使う", "キャンセル", "上限なしで再スキャン");

            switch (choice)
            {
                case 0: // 見つかった分を使う
                    _results.AddRange(dirResults);
                    break;
                case 1: // キャンセル (セッション全体を停止)
                    _canceled = true;
                    break;
                default: // 2: 上限なしで再スキャン (同ディレクトリをやり直し、partial は捨てる)
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
