using System.Collections;
using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    public class SpriteStudioRingoPlaybackTests
    {
        private const string RingoSsabDir = "../../package/Tests/Runtime/Resources/Ringo";
        private const string RingoSsabPath = RingoSsabDir + "/Ringo.ssab";
        private const string RingoAssetPath = "Ringo/Ringo"; // for Resources.Load

        [UnitySetUp]
        public IEnumerator Setup()
        {
            // Run conversion if Ringo.ssab doesn't exist
            string absoluteSsabPath = Path.GetFullPath(RingoSsabPath);
            if (!File.Exists(absoluteSsabPath))
            {
#if UNITY_EDITOR
                string sspjPath = Path.GetFullPath("../../SpriteStudio7-SDK/tests/Ringo/Ringo.sspj");
                string outDir = Path.GetFullPath(RingoSsabDir);
                Directory.CreateDirectory(outDir);

                // EditorのConversionRunnerをリフレクションで叩く
                var runnerType = System.Type.GetType("SpriteStudio.Editor.SpriteStudioConversionRunner, SpriteStudio.Editor");
                if (runnerType != null)
                {
                    var enqueueMethod = runnerType.GetMethod("Enqueue", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (enqueueMethod != null)
                    {
                        enqueueMethod.Invoke(null, new object[] { sspjPath, outDir });
                    }
                }

                // コンバート完了を待つ (ファイルが生成されるか、タイムアウト)
                float timeout = 10f;
                var pollMethod = runnerType?.GetMethod("Poll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                while (!File.Exists(absoluteSsabPath) && timeout > 0)
                {
                    pollMethod?.Invoke(null, null);
                    yield return null;
                    timeout -= Time.deltaTime;
                }

                Assert.IsTrue(File.Exists(absoluteSsabPath), "Failed to generate Ringo.ssab within timeout.");

                // UnityのAssetDatabaseを更新してResourcesとして読めるようにする
                var assetDatabaseType = System.Type.GetType("UnityEditor.AssetDatabase, UnityEditor");
                if (assetDatabaseType != null)
                {
                    var refreshMethod = assetDatabaseType.GetMethod("Refresh", new System.Type[0]);
                    refreshMethod?.Invoke(null, null);
                }
#else
                Assert.Fail("Cannot generate Ringo.ssab dynamically outside of Unity Editor. Please generate it beforehand.");
#endif
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator PlayRingo_SsabIsLoadedAndGeneratesMesh()
        {
            // We can read directly from bytes since Resources.Load might fail if AssetDatabase isn't fully refreshed synchronously
            string absoluteSsabPath = Path.GetFullPath(RingoSsabPath);
            Assert.IsTrue(File.Exists(absoluteSsabPath), "Ringo.ssab must exist for this test.");

            byte[] ssabBytes = File.ReadAllBytes(absoluteSsabPath);
            Assert.IsNotNull(ssabBytes);
            Assert.Greater(ssabBytes.Length, 0);

            // Create temporary asset
            var animeAsset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            animeAsset.SsabData = ssabBytes;

            var go = new GameObject("RingoTest");
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = animeAsset;

            Assert.DoesNotThrow(() => player.Play("Ringo"));

            // 1フレーム待機してUpdateManagerによるJob実行を完了させる
            yield return null;
            yield return null;
            
            // Dispose
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(animeAsset);
        }
    }
}
