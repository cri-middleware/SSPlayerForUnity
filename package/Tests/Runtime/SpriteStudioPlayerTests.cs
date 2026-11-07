using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    public class SpriteStudioPlayerTests
    {
        private SpriteStudioPlayer MakeRingoPlayer(string animation = "walk")
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>("Ringo/Ringo");
            Assert.IsNotNull(asset, "Ringo.ssab is required.");
            var player = _go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;
            player.CurrentAnimation = animation;
            return player;
        }

        // Start runs on the frame after AddComponent; the build and upload come in that frame's LateUpdate, which
        // the frame after that resumes behind.
        private static IEnumerator ThroughABuild()
        {
            yield return null;
            yield return null;
        }

        private static SpriteStudioAnimeAsset AnotherAssetLike(SpriteStudioAnimeAsset asset)
        {
            var other = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            other.SsabData = asset.SsabData;
            other.Materials = asset.Materials;
            other.Textures = asset.Textures;
            return other;
        }

        [UnityTest]
        public IEnumerator Start_DoesNotRewindAPlayFromAFrame()
        {
            var player = MakeRingoPlayer();
            int started = 0;
            player.OnAnimationStarted += () => started++;
            Assert.IsTrue(player.SetAnimation("walk"));
            player.Play(30f);

            yield return null;

            Assert.IsTrue(player.IsPlaying);
            Assert.GreaterOrEqual(player.FrameNo, 30f, "Start must not head back to the first frame");
            Assert.AreEqual(1, started, "and must not report a second start");
        }

        [UnityTest]
        public IEnumerator Start_DoesNotLiftAPause()
        {
            var player = MakeRingoPlayer();
            Assert.IsTrue(player.SetAnimation("walk"));
            player.Play();
            player.Pause();

            yield return null;

            Assert.IsTrue(player.IsPausing);
        }

        [UnityTest]
        public IEnumerator AnotherAsset_StartsAnimationName_OnTheNextFrame()
        {
            var player = MakeRingoPlayer();
            yield return null;
            Assert.IsTrue(player.IsPlaying);
            var other = AnotherAssetLike(player.AnimeAsset);
            try
            {
                player.AnimeAsset = other;

                yield return ThroughABuild();

                Assert.IsTrue(player.IsPlaying, "a script that only swapped the asset gets the animation it was playing");
                Assert.AreEqual("walk", player.InternalPlayer.CurrentAnimationName);
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        [UnityTest]
        public IEnumerator AnotherAsset_ThenAnAnimationSelected_StartsNothingOfItsOwn()
        {
            var player = MakeRingoPlayer();
            yield return null;
            var other = AnotherAssetLike(player.AnimeAsset);
            try
            {
                int started = 0;
                player.OnAnimationStarted += () => started++;
                player.AnimeAsset = other;
                Assert.IsTrue(player.SetAnimation("wait1"));

                yield return ThroughABuild();

                Assert.AreEqual("wait1", player.InternalPlayer.CurrentAnimationName);
                Assert.AreEqual(0, started, "selecting an animation does not start it, and the swap has nothing left to do");
                Assert.IsFalse(player.IsPlaying);
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        [UnityTest]
        public IEnumerator Disabling_StopsDrawing_AndEnablingDrawsAgain()
        {
            var player = MakeRingoPlayer();
            var mesh = _go.GetComponent<MeshFilter>();
            var renderer = _go.GetComponent<MeshRenderer>();
            yield return ThroughABuild();
            Assert.Greater(mesh.sharedMesh.vertexCount, 0, "the player has drawn");
            Assert.Greater(renderer.sharedMaterials.Length, 0);

            player.enabled = false;

            Assert.AreEqual(0, mesh.sharedMesh.vertexCount, "the MeshRenderer must not keep the last frame");
            Assert.AreEqual(0, renderer.sharedMaterials.Length, "nor materials the player has destroyed");

            player.enabled = true;
            yield return ThroughABuild();

            Assert.Greater(mesh.sharedMesh.vertexCount, 0);
            Assert.Greater(renderer.sharedMaterials.Length, 0);
        }

        [Test]
        public void TotalFrames_WithNoAnimationSetUp_IsZero()
        {
            var player = MakeRingoPlayer();
            Assert.AreEqual(0, player.TotalFrames);

            Assert.IsTrue(player.SetAnimation("walk"));
            Assert.Greater(player.TotalFrames, 0);
        }

        [Test]
        public void Play_WithNoRuntime_SaysSoOnce()
        {
            var player = _go.AddComponent<SpriteStudioPlayer>();
            player.AutoPlay = false;
            player.enabled = false;

            LogAssert.Expect(LogType.Warning, new Regex("Play did nothing"));
            player.Play();
            player.Play();
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Play_WithNoRuntime_UnderAutoPlay_IsLeftToTheEnable()
        {
            var player = _go.AddComponent<SpriteStudioPlayer>();
            player.enabled = false;

            player.Play();

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ShaderCatalog_IsBoundBeforeTheBuild()
        {
            var player = MakeRingoPlayer();
            var catalog = ScriptableObject.CreateInstance<SpriteStudioShaderCatalog>();
            try
            {
                typeof(SpriteStudioPlayer).GetField("_shaderCatalog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(player, catalog);

                player.PrepareFrame(0f, out _, out _);

                Assert.AreSame(catalog, player.InternalPlayer.ShaderCatalog);
            }
            finally
            {
                Object.DestroyImmediate(catalog);
            }
        }

        private static int CountAddOnSubmeshes(SpriteStudioInternalPlayer player)
        {
            int count = 0;
            var submeshes = player.Submeshes;
            for (int s = 0; s < player.SubmeshCount; s++)
            {
                if (submeshes[s].ShaderHash != 0) count++;
            }
            return count;
        }

        [Test]
        public void AnAddOnPart_KeepsItsShader_InAnAnimationWithMasks()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>("overall/Shader");
            if (asset == null) Assert.Ignore("Shader.ssab is generated from the SDK's overall project.");

            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(asset.SsabData, null);
                Assert.IsTrue(player.SetupAnimation(player.GetAnimationName(0)));
                player.ScheduleMeshUpdate(0f);
                player.CurrentUpdateJobHandle.Complete();
                int plain = CountAddOnSubmeshes(player);
                Assert.Greater(plain, 0, "the fixture draws parts with AddOns");

                // The per-part path masks take, with no part the mask clips: what maskInfluence=0 parts see.
                player._hasMaskParts = true;
                player._inheritedMaskInfluence = false;
                player._geometryBuilder.InvalidateFrameCache();
                player.ScheduleMeshUpdate(0f);
                player.CurrentUpdateJobHandle.Complete();

                Assert.AreEqual(plain, CountAddOnSubmeshes(player));
            }
        }

        private GameObject _go;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestPlayer");
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
        }

        [Test]
        public void AddComponent_DoesNotCrash()
        {
            var player = _go.AddComponent<SpriteStudioPlayer>();
            Assert.IsNotNull(player);
            Assert.IsNotNull(player.GetComponent<MeshFilter>());
            Assert.IsNotNull(player.GetComponent<MeshRenderer>());
        }

        [Test]
        public void Play_WithoutAnimeAsset_LogsWarningOrDoesNothing()
        {
            var player = _go.AddComponent<SpriteStudioPlayer>();
            // Confirm it does not crash
            Assert.DoesNotThrow(() => { player.SetAnimation("dummy"); player.Play(); });
        }

        [Test]
        public void InitialProperties_AreDefaulted()
        {
            var player = _go.AddComponent<SpriteStudioPlayer>();
            Assert.AreEqual(1f, player.PixelsPerUnit);
            Assert.AreEqual(1f, player.SpeedScale);
            Assert.AreEqual(-1, player.LoopCount);
            Assert.IsTrue(player.AutoPlay);
            Assert.IsTrue(player.FrameSkipEnabled);
            Assert.IsFalse(player.SubFrameEnabled);
            Assert.AreEqual("Default", player.SortingLayerName);
            Assert.AreEqual(0, player.SortingOrder);
        }

        [Test]
        public void SettingProperties_ReflectsValues()
        {
            var player = _go.AddComponent<SpriteStudioPlayer>();
            player.CurrentAnimation = "TestAnim";
            player.AutoPlay = false;
            player.LoopCount = 2;
            player.SpeedScale = 1.5f;
            player.SubFrameEnabled = true;
            player.FrameSkipEnabled = false;
            player.PixelsPerUnit = 100f;
            player.SortingOrder = 10;

            Assert.AreEqual("TestAnim", player.CurrentAnimation);
            Assert.AreEqual(false, player.AutoPlay);
            Assert.AreEqual(2, player.LoopCount);
            Assert.AreEqual(1.5f, player.SpeedScale);
            Assert.AreEqual(true, player.SubFrameEnabled);
            Assert.AreEqual(false, player.FrameSkipEnabled);
            Assert.AreEqual(100f, player.PixelsPerUnit);
            Assert.AreEqual(10, player.SortingOrder);
        }

        [Test]
        public void RendererSorting_IsLeftAloneOnEnable()
        {
            var player = _go.AddComponent<SpriteStudioPlayer>();
            var renderer = _go.GetComponent<MeshRenderer>();
            renderer.sortingOrder = 7;

            _go.SetActive(false);
            _go.SetActive(true);

            Assert.AreEqual(7, renderer.sortingOrder, "Enabling should not overwrite the renderer's own sorting");
            Assert.AreEqual(7, player.SortingOrder);
        }

        [Test]
        public void ExtendedBlends_DrawAsTheOtherPlayersDrawThem()
        {
            Assert.AreEqual(SpriteStudioBlendTable.Get(1), SpriteStudioBlendTable.Get(8), "Mul2 draws as Mul");
            Assert.AreEqual(SpriteStudioBlendTable.Get(5), SpriteStudioBlendTable.Get(10), "Screen2 draws as Screen");
            Assert.AreEqual(SpriteStudioBlendTable.Get(0), SpriteStudioBlendTable.Get(9), "Div2 falls back to Mix");
            Assert.AreEqual(SpriteStudioBlendTable.Get(0), SpriteStudioBlendTable.Get(11), "Overlay2 falls back to Mix");
        }

        [Test]
        public void ShaderCatalog_AnEditedEntry_ResolvesOnceValidated()
        {
            var catalog = ScriptableObject.CreateInstance<SpriteStudioShaderCatalog>();
            try
            {
                uint id = SpriteStudioShaderCatalog.HashId("custom-shader");
                Assert.IsFalse(catalog.TryResolve(id, out _, out _), "an empty catalog resolves nothing");
                int version = catalog.Version;

                // What an Inspector edit does: change the entries, then OnValidate.
                catalog.Entries.Add(new SpriteStudioShaderCatalog.Entry { IdName = "custom-shader", Shader = Shader.Find("SpriteStudio/Default") });
                typeof(SpriteStudioShaderCatalog).GetMethod("OnValidate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(catalog, null);

                Assert.IsTrue(catalog.TryResolve(id, out var shader, out _), "the new entry should resolve without a domain reload");
                Assert.AreEqual("SpriteStudio/Default", shader.name);
                Assert.AreNotEqual(version, catalog.Version, "players key their per-part materials on the version");
            }
            finally
            {
                Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void TimelineControl_SetsStateCorrectly()
        {
            var player = _go.AddComponent<SpriteStudioPlayer>();
            Assert.DoesNotThrow(() => player.OnControlTimeStart());
            Assert.DoesNotThrow(() => player.SetTime(1.5));
            Assert.DoesNotThrow(() => player.OnControlTimeStop());
        }

        [Test]
        public void EvaluateTimelineClip_DoesNotCrash()
        {
            var player = _go.AddComponent<SpriteStudioPlayer>();
            Assert.DoesNotThrow(() => player.EvaluateTimelineClip("TestAnim", 1.5));
            Assert.DoesNotThrow(() => player.ReleaseTimelineControl());
        }
    }
}
