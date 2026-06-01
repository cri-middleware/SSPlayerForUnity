using UnityEngine;
using UnityEditor;
using System.IO;

namespace SpriteStudio.Editor
{
    public class SpriteStudioAssetPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            bool hasConversion = false;

            foreach (string str in importedAssets)
            {
                if (str.EndsWith(".sspj"))
                {
                    hasConversion |= ConvertSspj(str);
                }
            }

            if (hasConversion)
            {
                // 新しく書き出された .ssab やテクスチャを Unity に認識させる
                AssetDatabase.Refresh();
            }
        }

        private static bool ConvertSspj(string sspjPath)
        {
            string sspjAbsolutePath = Path.GetFullPath(sspjPath);
            string sspjName = Path.GetFileNameWithoutExtension(sspjPath);
            // .sspj と同じ階層を出力先ベースとする
            string baseOutputDir = Path.GetDirectoryName(sspjAbsolutePath);
            string finalOutputDir = Path.Combine(baseOutputDir, sspjName);

            if (!Directory.Exists(finalOutputDir))
            {
                Directory.CreateDirectory(finalOutputDir);
            }

            Debug.Log($"[SSPlayer] Converting: {sspjPath} to {finalOutputDir}");

            unsafe
            {
                var context = SpriteStudio.SsConverter.NativeMethods.ss_converter_create();
                
                var sspjBytes = System.Text.Encoding.UTF8.GetBytes(sspjAbsolutePath + "\0");
                var outDirBytes = System.Text.Encoding.UTF8.GetBytes(finalOutputDir + "\0");

                fixed (byte* sspjPtr = sspjBytes)
                fixed (byte* outDirPtr = outDirBytes)
                {
                    SpriteStudio.SsConverter.NativeMethods.ss_converter_convert(context, sspjPtr, outDirPtr, null);
                }

                while (!SpriteStudio.SsConverter.NativeMethods.ss_converter_is_finished(context))
                {
                    System.Threading.Thread.Sleep(10);
                }

                var result = SpriteStudio.SsConverter.NativeMethods.ss_converter_get_result(context);
                if (result != SpriteStudio.SsConverter.CConverterError.Success)
                {
                    byte* errData;
                    nuint errLen;
                    SpriteStudio.SsConverter.NativeMethods.ss_converter_get_error(context, &errData, &errLen);
                    if (errData != null && errLen > 0)
                    {
                        var errStr = System.Text.Encoding.UTF8.GetString(errData, (int)errLen);
                        Debug.LogError($"[SSPlayer] Converter Error: {errStr}");
                    }
                    else
                    {
                        Debug.LogError($"[SSPlayer] Converter Error: {result}");
                    }
                }
                else
                {
                    Debug.Log($"[SSPlayer] Convert Success: {sspjAbsolutePath}");
                }

                SpriteStudio.SsConverter.NativeMethods.ss_converter_destroy(context);
            }

            return true;
        }
    }
}
