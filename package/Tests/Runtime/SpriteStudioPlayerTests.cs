using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.Tests
{
    public class SpriteStudioPlayerTests
    {
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
