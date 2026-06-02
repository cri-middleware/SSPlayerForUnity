using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// libssconverter の非同期変換を EditorApplication.update でポーリングし、完了時に
    /// 結果処理 (エラーログ / SourceMap 登録) と AssetDatabase.Refresh を行う。
    /// 従来はメインスレッドで Thread.Sleep ポーリングしており変換中エディタが固まっていたが、
    /// これによりノンブロッキングになる。複数の変換を同時に追跡できる。
    /// </summary>
    internal static class SpriteStudioConversionRunner
    {
        private sealed class Conversion
        {
            public IntPtr Context;
            public string SspjPath;
            public string OutputDir;
        }

        private static readonly List<Conversion> _active = new List<Conversion>();
        private static bool _polling;

        /// <summary>開始済みの変換コンテキストを追跡対象に登録する。</summary>
        public static void Track(IntPtr context, string sspjPath, string outputDir)
        {
            _active.Add(new Conversion { Context = context, SspjPath = sspjPath, OutputDir = outputDir });
            if (!_polling)
            {
                EditorApplication.update += Poll;
                _polling = true;
            }
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
                        if (errData != null && errLen > 0)
                        {
                            var errStr = Encoding.UTF8.GetString(errData, (int)errLen);
                            Debug.LogError($"[SSPlayer] Converter Error: {errStr}");
                        }
                        else
                        {
                            Debug.LogError($"[SSPlayer] Converter Error: {result}");
                        }
                    }
                    else
                    {
                        Debug.Log("[SSPlayer] Convert Success!");

                        var generatedSsabs = Directory.GetFiles(conversion.OutputDir, "*.ssab", SearchOption.AllDirectories);
                        foreach (var ssab in generatedSsabs)
                        {
                            SpriteStudioSourceMap.Register(ssab, conversion.SspjPath);
                        }
                    }

                    SpriteStudio.SsConverter.NativeMethods.ss_converter_destroy(ctx);
                }

                _active.RemoveAt(i);
                anyCompleted = true;
            }

            if (anyCompleted)
            {
                // 生成された .ssab / テクスチャを Unity に取り込ませる
                AssetDatabase.Refresh();
            }

            if (_active.Count == 0)
            {
                EditorApplication.update -= Poll;
                _polling = false;
            }
        }
    }
}
