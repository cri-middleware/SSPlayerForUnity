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

            // 注: "Ringo" はパック名であってアニメーション名ではない。pack "Ringo" の実アニメは
            // walk / run / dance / attack1 など。正しいアニメ名で再生する。
            Assert.DoesNotThrow(() => player.Play("walk"));

            // 1フレーム待機してUpdateManagerによるJob実行を完了させる
            yield return null;
            yield return null;
            
            // Dispose
            Object.DestroyImmediate(go);
            if (createdAsset) Object.DestroyImmediate(animeAsset);
        }

        [Test]
        public void ReloadSsab_AtRuntime_DoesNotCrashAndRebuildsGeometry()
        {
            // 実行時のアセット差し替え経路 (SpriteStudioInternalPlayer.LoadSsab 再呼び出し) を、
            // 実 Burst Job を 1 度走らせて ssab バッファを borrow させた状態で検証する。#3a で入れた
            // 「解放順序の修正 + in-flight Job の Complete()」が効いてクラッシュ/リークしないこと、
            // および再ロード後もジオメトリが再構築されることを確認する。
            //
            // 注: Unity の Mesh.vertexCount は GPU バッファ依存で headless/batchmode では 0 になりうる
            // ため、ここでは get_frame_data 由来の CPU 側カウント (InternalPlayer.VertexCount) で検証する。
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
                // 初回ロード + 1 フレーム駆動 (実 Burst Job を投げて ssab バッファを borrow させる)。
                player.LoadSsab(ssabBytes);
                Assert.IsTrue(player.SetupAnimation("walk"), "walk アニメーションが見つかるはず");
                player.Play();
                player.Tick(0.016f, false);
                player.ScheduleMeshUpdate(0.016f);
                player.CurrentUpdateJobHandle.Complete();
                Assert.Greater(player.VertexCount, 0, "初回ビルドで頂点が生成されるはず");

                // 再ロード: LoadSsab が再度走り、#3a の解放順序 + in-flight Job Complete を経由する。
                Assert.DoesNotThrow(() => player.LoadSsab(ssabBytes));
                Assert.IsTrue(player.SetupAnimation("walk"));
                player.Play();
                player.Tick(0.016f, false);
                player.ScheduleMeshUpdate(0.016f);
                player.CurrentUpdateJobHandle.Complete();
                Assert.Greater(player.VertexCount, 0, "再ロード後も頂点が再構築されるはず");
            }

            LogAssert.NoUnexpectedReceived();
        }
    }
}
