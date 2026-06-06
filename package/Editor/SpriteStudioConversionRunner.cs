using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// libssconverter による .sspj → .ssab 変換を「キュー + 同時実行上限 + 進捗表示」で実行する。
    ///
    /// - EditorApplication.update でポーリングするためノンブロッキング (変換中もエディタは固まらない)。
    /// - フォルダ D&D 等で大量にエンキューされても、ネイティブ context はスロットが空いた時にのみ
    ///   生成し、同時実行数を CPU 数で上限化する (スレッド過多を防止)。
    /// - UnityEditor.Progress でステータスバーに進捗を出し、キャンセルにも対応する。
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

        // 進捗 (実行中バッチ全体で 1 つの Progress。キューが空になるまで生存)
        private static int _progressId = -1;
        private static int _total;
        private static int _completed;
        private static bool _canceled;

        private static int Concurrency => Mathf.Clamp(SystemInfo.processorCount - 1, 1, 8);

        /// <summary>変換ジョブを 1 件キューに積む。スロットが空いていれば即実行を開始する。</summary>
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
            return true; // 進行中はアボート要求済み。Poll で回収される。
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

                var sspjBytes = Encoding.UTF8.GetBytes(job.SspjPath + "\0");
                var outDirBytes = Encoding.UTF8.GetBytes(job.OutputDir + "\0");

                fixed (byte* sspjPtr = sspjBytes)
                fixed (byte* outDirPtr = outDirBytes)
                {
                    SpriteStudio.SsConverter.NativeMethods.ss_converter_convert(context, sspjPtr, outDirPtr, null);
                }

                _active.Add(new Active
                {
                    Context = (IntPtr)context,
                    SspjPath = job.SspjPath,
                    OutputDir = job.OutputDir,
                });
            }

            ReportProgress(Path.GetFileName(job.SspjPath));
        }

        private static void Poll()
        {
            bool anyCompleted = false;

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var conversion = _active[i];
                unsafe
                {
                    var ctx = (SpriteStudio.SsConverter.Context*)conversion.Context.ToPointer();
                    if (!SpriteStudio.SsConverter.NativeMethods.ss_converter_is_finished(ctx))
                    {
                        continue; // まだ変換中
                    }

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
                    }
                    else
                    {
                        var generatedSsabs = Directory.GetFiles(conversion.OutputDir, "*.ssab", SearchOption.AllDirectories);
                        foreach (var ssab in generatedSsabs)
                        {
                            SpriteStudioSourceMap.Register(ssab, conversion.SspjPath);
                        }
                    }

                    SpriteStudio.SsConverter.NativeMethods.ss_converter_destroy(ctx);
                }

                _active.RemoveAt(i);
                _completed++;
                anyCompleted = true;
            }

            if (anyCompleted)
            {
                Pump(); // 空いたスロットへ次のジョブを補充
                ReportProgress();
            }

            // 全完了 (またはキャンセルで pending 空 + active 回収済み) → 後始末
            if (_active.Count == 0 && _pending.Count == 0)
            {
                Finish();
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

            // 生成された .ssab / テクスチャを Unity に取り込ませる (バッチ末尾で 1 回)
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
