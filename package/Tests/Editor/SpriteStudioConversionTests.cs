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
            string sspjPath = Path.GetFullPath("../../SpriteStudio-SDK/tests/Ringo/Ringo.sspj");
            string outDir = Path.Combine(Application.temporaryCachePath, "TestConvertOut");
            string expectedSsabPath = outDir + "/Ringo.ssab";

            // Ensure clean state
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, true);
            }
            Directory.CreateDirectory(outDir);

            // Call SpriteStudioConversionRunner directly
            SpriteStudioConversionRunner.Enqueue(sspjPath, outDir);

            // Wait for conversion to finish (wait until the Runner is idle to ensure the native side releases its file handle)
            float timeout = 10f;
            double startTime = UnityEditor.EditorApplication.timeSinceStartup;
            while (SpriteStudioConversionRunner.IsProcessing && (UnityEditor.EditorApplication.timeSinceStartup - startTime) < timeout)
            {
                yield return null;
            }

            Assert.IsTrue(File.Exists(expectedSsabPath), "Conversion did not produce the expected .ssab file within timeout.");

            // Cleanup: even after conversion completes, Unity's AssetDatabase import (.meta generation) may still
            // be running, so Directory.Delete can transiently fail with "Directory not empty". Retry while waiting a few frames.
            for (int attempt = 0; attempt < 60 && Directory.Exists(outDir); attempt++)
            {
                // Clear attributes and retry to prevent lock-induced exceptions
                foreach (var file in Directory.GetFiles(outDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(outDir, true);
            }
        }

        [UnityTest]
        public IEnumerator ConversionRunner_WithInvalidSspj_DoesNotCrash()
        {
            string outDir = Path.Combine(Application.temporaryCachePath, "TestConvertOut_Invalid");
            string dummySspj = outDir + "/Invalid.sspj";

            if (!Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }
            File.WriteAllText(dummySspj, "INVALID XML FILE");

            // Enqueue should fail gracefully (or log error) but not crash Unity
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(".*Converter Error.*"));
            Assert.DoesNotThrow(() => SpriteStudioConversionRunner.Enqueue(dummySspj, outDir));

            float timeout = 5f;
            double startTime = UnityEditor.EditorApplication.timeSinceStartup;
            while (SpriteStudioConversionRunner.IsProcessing && (UnityEditor.EditorApplication.timeSinceStartup - startTime) < timeout)
            {
                yield return null;
            }

            if (Directory.Exists(outDir))
            {
                foreach (var file in Directory.GetFiles(outDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(outDir, true);
            }
        }
    }
}
