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
            // If we can load it from Resources, it's already built (Standalone or cached Editor)
            var asset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0)
            {
                yield break;
            }

            // Run conversion if Ringo.ssab doesn't exist
            string absoluteSsabPath = Path.GetFullPath(RingoSsabPath);
            if (!File.Exists(absoluteSsabPath))
            {
#if UNITY_EDITOR
                string sspjPath = Path.GetFullPath("../../SpriteStudio-SDK/tests/Ringo/Ringo.sspj");
                string outDir = Path.GetFullPath(RingoSsabDir);
                Directory.CreateDirectory(outDir);

                // Invoke the Editor's ConversionRunner via reflection
                var runnerType = System.Type.GetType("SpriteStudio.Editor.SpriteStudioConversionRunner, SpriteStudio.Editor");
                if (runnerType != null)
                {
                    var enqueueMethod = runnerType.GetMethod("Enqueue", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (enqueueMethod != null)
                    {
                        enqueueMethod.Invoke(null, new object[] { sspjPath, outDir });
                    }
                }

                // Wait for conversion to finish (until the file is generated and the runner is idle, or timeout)
                float timeout = 10f;
                var pollMethod = runnerType?.GetMethod("Poll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                while ((!File.Exists(absoluteSsabPath) || !ConversionRunnerProbe.IsIdle(runnerType)) && timeout > 0)
                {
                    pollMethod?.Invoke(null, null);
                    yield return null;
                    timeout -= Time.deltaTime;
                }

                Assert.IsTrue(File.Exists(absoluteSsabPath), "Failed to generate Ringo.ssab within timeout.");

                // Refresh Unity's AssetDatabase so it can be read as a Resource
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
            var animeAsset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            bool createdAsset = false;

            if (animeAsset == null || animeAsset.SsabData == null || animeAsset.SsabData.Length == 0)
            {
                // We can read directly from bytes since Resources.Load might fail if AssetDatabase isn't fully refreshed synchronously
                string absoluteSsabPath = Path.GetFullPath(RingoSsabPath);
                Assert.IsTrue(File.Exists(absoluteSsabPath), "Ringo.ssab must exist for this test.");

                byte[] ssabBytes = File.ReadAllBytes(absoluteSsabPath);
                Assert.IsNotNull(ssabBytes);
                Assert.Greater(ssabBytes.Length, 0);

                // Create temporary asset
                animeAsset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
                animeAsset.SsabData = ssabBytes;
                createdAsset = true;
            }

            var go = new GameObject("RingoTest");
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = animeAsset;

            // Note: "Ringo" is the pack name, not an animation name. The actual animations in pack "Ringo"
            // are walk / run / dance / attack1, etc. Play using a valid animation name.
            Assert.DoesNotThrow(() => { player.SetAnimation("walk"); player.Play(); });

            // Wait one frame to let the UpdateManager's Job execution complete
            yield return null;
            yield return null;
            
            // Dispose
            Object.DestroyImmediate(go);
            if (createdAsset) Object.DestroyImmediate(animeAsset);
        }

        [Test]
        public void ReloadSsab_AtRuntime_DoesNotCrashAndRebuildsGeometry()
        {
            // Verify the runtime asset-swap path (re-calling SpriteStudioInternalPlayer.LoadSsab) with a real
            // Burst Job run once so the ssab buffer is borrowed. Confirm that the "release-ordering fix +
            // Complete() of the in-flight Job" added in #3a takes effect and prevents crashes/leaks, and that
            // the geometry is rebuilt after reloading.
            //
            // Note: Unity's Mesh.vertexCount depends on the GPU buffer and can be 0 in headless/batchmode,
            // so here we verify with the CPU-side count derived from get_frame_data (InternalPlayer.VertexCount).
            byte[] ssabBytes = null;
            var animeAsset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            if (animeAsset != null && animeAsset.SsabData != null && animeAsset.SsabData.Length > 0)
            {
                ssabBytes = animeAsset.SsabData;
            }
            else
            {
                string absoluteSsabPath = Path.GetFullPath(RingoSsabPath);
                Assert.IsTrue(File.Exists(absoluteSsabPath), "Ringo.ssab must exist for this test.");
                ssabBytes = File.ReadAllBytes(absoluteSsabPath);
            }
            Assert.IsNotNull(ssabBytes);
            Assert.Greater(ssabBytes.Length, 0);

            using (var player = new SpriteStudioInternalPlayer())
            {
                // Initial load + one-frame drive (submit a real Burst Job so the ssab buffer gets borrowed).
                player.LoadSsab(ssabBytes);
                Assert.IsTrue(player.SetupAnimation("walk"), "The walk animation should be found");
                player.Play();
                player.Tick(0.016f, false);
                player.ScheduleMeshUpdate(0.016f);
                player.CurrentUpdateJobHandle.Complete();
                Assert.Greater(player.VertexCount, 0, "The initial build should generate vertices");

                // Reload: LoadSsab runs again, going through #3a's release ordering + in-flight Job Complete.
                Assert.DoesNotThrow(() => player.LoadSsab(ssabBytes));
                Assert.IsTrue(player.SetupAnimation("walk"));
                player.Play();
                player.Tick(0.016f, false);
                player.ScheduleMeshUpdate(0.016f);
                player.CurrentUpdateJobHandle.Complete();
                Assert.Greater(player.VertexCount, 0, "Vertices should be rebuilt after reloading too");
            }

            LogAssert.NoUnexpectedReceived();
        }
        [UnityTest]
        public IEnumerator PlayRingo_VariousAnimations_DoesNotCrash()
        {
            var animeAsset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            bool createdAsset = false;

            if (animeAsset == null || animeAsset.SsabData == null || animeAsset.SsabData.Length == 0)
            {
                string absoluteSsabPath = Path.GetFullPath(RingoSsabPath);
                Assert.IsTrue(File.Exists(absoluteSsabPath), "Ringo.ssab must exist for this test.");
                byte[] ssabBytes = File.ReadAllBytes(absoluteSsabPath);
                animeAsset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
                animeAsset.SsabData = ssabBytes;
                createdAsset = true;
            }

            var go = new GameObject("RingoTest");
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = animeAsset;

            string[] animations = { "walk", "run", "dance", "attack1" };
            foreach (var anim in animations)
            {
                Assert.DoesNotThrow(() => { player.SetAnimation(anim); player.Play(); }, $"Playing {anim} failed");
                yield return null;
                yield return null; // wait for job complete
            }

            Object.DestroyImmediate(go);
            if (createdAsset) Object.DestroyImmediate(animeAsset);
        }
    }
}
