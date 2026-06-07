using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Editor.Tests
{
    public class SpriteStudioConversionTests
    {
        [UnityTest]
        public IEnumerator ConversionRunner_CanConvertSspjToSsab()
        {
            string sspjPath = Path.GetFullPath("../../SpriteStudio7-SDK/tests/Ringo/Ringo.sspj");
            string outDir = Path.GetFullPath("../../package/Tests/Runtime/Resources/TestConvertOut");
            string expectedSsabPath = outDir + "/Ringo.ssab";

            // Ensure clean state
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, true);
            }
            Directory.CreateDirectory(outDir);

            // SpriteStudioConversionRunner を直接呼び出す
            SpriteStudioConversionRunner.Enqueue(sspjPath, outDir);

            // コンバート完了を待機。.ssab ファイルの存在だけで進むと、ランナーの後処理 (ProcessFinished) や
            // 出力ファイル書き込みがまだ続いている最中に Cleanup が走り、Directory.Delete が
            // "Directory not empty" で競合する。IsIdle (キューと実行中が空) になるまで待ってから後始末する。
            float timeout = 15f;
            double startTime = UnityEditor.EditorApplication.timeSinceStartup;
            while (!SpriteStudioConversionRunner.IsIdle && (UnityEditor.EditorApplication.timeSinceStartup - startTime) < timeout)
            {
                yield return null;
            }

            Assert.IsTrue(File.Exists(expectedSsabPath), "Conversion did not produce the expected .ssab file within timeout.");

            // Cleanup: 変換完了後も Unity の AssetDatabase インポート (.meta 生成) がまだ走っていることがあり、
            // Directory.Delete が一時的に "Directory not empty" で失敗しうる。数フレーム待ちつつリトライする。
            for (int attempt = 0; attempt < 60 && Directory.Exists(outDir); attempt++)
            {
                bool retry = false;
                try { Directory.Delete(outDir, true); }
                catch (IOException) { retry = true; }
                catch (UnauthorizedAccessException) { retry = true; }
                if (!retry) break;
                yield return null;   // yield は catch の外で行う (C# は catch 内 yield 不可)
            }
        }
    }
}
