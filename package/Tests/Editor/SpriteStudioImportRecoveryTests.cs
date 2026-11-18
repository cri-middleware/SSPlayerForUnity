using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Editor.Tests
{
    /// <summary>
    /// What the import does when things are not as it left them: a kept .mat whose texture reference broke, and
    /// a folder scan the user cancels.
    /// </summary>
    public class SpriteStudioImportRecoveryTests
    {
        [Test]
        public void AKeptMaterial_WithABrokenTexture_GetsItBack_AndOtherwiseStaysTheUsers()
        {
            var mat = new Material(Shader.Find("SpriteStudio/Default"));
            var texture = new Texture2D(1, 1);
            var theirs = new Texture2D(1, 1);
            try
            {
                Assert.IsTrue(SpriteStudioAssetPostprocessor.RelinkBrokenTexture(mat, texture));
                Assert.AreSame(texture, mat.mainTexture);

                mat.mainTexture = theirs;
                Assert.IsFalse(SpriteStudioAssetPostprocessor.RelinkBrokenTexture(mat, texture));
                Assert.AreSame(theirs, mat.mainTexture, "a texture the user chose is left alone");
            }
            finally
            {
                Object.DestroyImmediate(mat);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(theirs);
            }
        }

        [UnityTest]
        public IEnumerator AFinishedScan_HandsOverWhatItFound()
        {
            bool completed = false;
            var scan = SpriteStudioDirectoryScan.Start(new[] { MakeEmptyDirectory() }, false, _ => completed = true);

            yield return WaitForTheEnd(scan);

            Assert.IsTrue(completed);
        }

        [UnityTest]
        public IEnumerator ACanceledScan_ConvertsNothing()
        {
            bool completed = false;
            var scan = SpriteStudioDirectoryScan.Start(new[] { MakeEmptyDirectory() }, false, _ => completed = true);

            scan.Cancel();
            yield return WaitForTheEnd(scan);

            Assert.IsFalse(completed);
        }

        private static string MakeEmptyDirectory()
        {
            string dir = Path.Combine(Application.temporaryCachePath, "ss_scan_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static IEnumerator WaitForTheEnd(SpriteStudioDirectoryScan scan)
        {
            double deadline = EditorApplication.timeSinceStartup + 30.0;
            while (!scan.IsFinished)
            {
                Assert.Less(EditorApplication.timeSinceStartup, deadline, "The scan did not end.");
                yield return null;
            }
        }
    }
}
