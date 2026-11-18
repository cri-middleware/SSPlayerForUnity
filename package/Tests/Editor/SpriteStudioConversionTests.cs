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

            // Cleanup. The output is under temporaryCachePath, outside the project, so nothing imports it and
            // it can go at once; clearing the attributes keeps a read-only file from blocking the delete.
            foreach (var file in Directory.GetFiles(outDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(outDir, true);
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
