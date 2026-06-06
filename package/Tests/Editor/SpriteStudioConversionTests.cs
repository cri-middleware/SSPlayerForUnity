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

            // コンバート完了を待機
            float timeout = 10f;
            double startTime = UnityEditor.EditorApplication.timeSinceStartup;
            while (!File.Exists(expectedSsabPath) && (UnityEditor.EditorApplication.timeSinceStartup - startTime) < timeout)
            {
                yield return null;
            }

            Assert.IsTrue(File.Exists(expectedSsabPath), "Conversion did not produce the expected .ssab file within timeout.");

            // Cleanup
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, true);
            }
        }
    }
}
