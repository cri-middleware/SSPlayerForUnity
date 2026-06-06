using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    public class SpriteStudioConversionTests
    {
        [UnityTest]
        public IEnumerator ConversionRunner_CanConvertSspjToSsab()
        {
#if UNITY_EDITOR
            string sspjPath = Path.GetFullPath("SpriteStudio7-SDK/tests/Ringo/Ringo.sspj");
            string outDir = Path.GetFullPath("package/Tests/Runtime/Resources/TestConvertOut");
            string expectedSsabPath = outDir + "/Ringo.ssab";

            // Ensure clean state
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, true);
            }
            Directory.CreateDirectory(outDir);

            // リフレクションで SpriteStudioConversionRunner を呼び出す
            var runnerType = System.Type.GetType("SpriteStudio.Editor.SpriteStudioConversionRunner, SpriteStudio.Editor");
            Assert.IsNotNull(runnerType, "SpriteStudioConversionRunner type could not be found.");

            var enqueueMethod = runnerType.GetMethod("Enqueue", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(enqueueMethod, "Enqueue method could not be found.");

            enqueueMethod.Invoke(null, new object[] { sspjPath, outDir });

            // コンバート完了を待機
            float timeout = 10f;
            while (!File.Exists(expectedSsabPath) && timeout > 0)
            {
                yield return null;
                timeout -= Time.deltaTime;
            }

            Assert.IsTrue(File.Exists(expectedSsabPath), "Conversion did not produce the expected .ssab file within timeout.");

            // Cleanup
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, true);
            }
#else
            Assert.Ignore("Conversion test only runs in Unity Editor.");
            yield return null;
#endif
        }
    }
}
