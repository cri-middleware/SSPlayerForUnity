using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Verifies the runtime cellmap texture-replacement feature (a Player-side override, separate from the
    /// Override Layer API).
    ///
    /// Three layers:
    ///   (A) Asset layer — name discovery and name→hash resolution parsed from the .ssab (cellmaps AND
    ///       external textures). Pure; needs only SsabData.
    ///   (B) Component layer — the name-keyed API round-trips (set / get / null-clears) on both the world
    ///       player and the uGUI graphic, and the replaceable-name list matches the asset.
    ///   (C) Draw layer — a replacement actually swaps the bound texture on a submesh material without
    ///       mutating the asset-shared material, and clearing restores it. Requires the imported Ringo
    ///       (with materials); skipped via Assume when the fixture has no resolved materials.
    ///
    /// The fixture reuses the existing Ringo.
    /// </summary>
    public class SpriteStudioTextureReplaceTests
    {
        private const string RingoAssetPath = "Ringo/Ringo"; // for Resources.Load
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";

        private byte[] _ringo;
        private SpriteStudioAnimeAsset _imported; // Resources-owned; NOT destroyed in teardown.
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Object> _assets = new List<Object>();

        [UnitySetUp]
        public IEnumerator Setup()
        {
            _imported = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            if (_imported != null && _imported.SsabData != null && _imported.SsabData.Length > 0)
            {
                _ringo = _imported.SsabData;
            }
            else
            {
                string p = Path.GetFullPath(RingoSsabPath);
                _ringo = File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            // Only assets this test created (never the Resources-owned imported asset).
            foreach (var a in _assets) if (a != null) Object.DestroyImmediate(a);
            _assets.Clear();
        }

        // --- helpers ---

        private SpriteStudioAnimeAsset MakeAsset()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = _ringo;
            _assets.Add(asset);
            return asset;
        }

        private SpriteStudioPlayer MakePlayer(SpriteStudioAnimeAsset asset, string anim = "walk")
        {
            var go = new GameObject("TextureReplacePlayer");
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;
            player.SetAnimation(anim);
            player.Play();
            return player;
        }

        private SpriteStudioGraphic MakeGraphic(SpriteStudioAnimeAsset asset, string anim = "walk")
        {
            var go = new GameObject("TextureReplaceGraphic");
            _objects.Add(go);
            go.AddComponent<Canvas>();
            go.AddComponent<CanvasRenderer>();
            var graphic = go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = asset;
            graphic.SetAnimation(anim);
            graphic.Play();
            return graphic;
        }

        private static SpriteStudioInternalPlayer InternalOf(object component)
        {
            var field = component.GetType().GetField("_core", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "the component should hold its playback core in a '_core' field");
            var core = field.GetValue(component) as SpriteStudioPlaybackCore;
            Assert.IsNotNull(core, "the '_core' field should hold a SpriteStudioPlaybackCore");
            Assert.IsNotNull(core.Internal, "the core should have created its internal player");
            return core.Internal;
        }

        private static void Build(SpriteStudioInternalPlayer player)
        {
            player.ScheduleMeshUpdate(1f / 60f);
            player.CurrentUpdateJobHandle.Complete();
        }

        private static bool AnyMaterialUsesTexture(Material[] mats, Texture tex)
        {
            if (mats == null) return false;
            foreach (var m in mats) if (m != null && m.mainTexture == tex) return true;
            return false;
        }

        private static bool AnyMaterialTextured(Material[] mats)
        {
            if (mats == null) return false;
            foreach (var m in mats) if (m != null && m.mainTexture != null) return true;
            return false;
        }

        // ============================================================
        // (A) Asset layer: discovery + name->hash resolution
        // ============================================================

        [Test]
        public void ReplaceableTextureNames_AreDiscoverable_AndResolveToHashes()
        {
            var asset = MakeAsset();
            var names = asset.GetReplaceableTextureNames();
            Assert.Greater(names.Length, 0, "Ringo should expose at least one replaceable texture name");

            foreach (var n in names)
            {
                Assert.IsTrue(asset.TryGetCellmapHashes(n, out var hashes), $"'{n}' should resolve to hash key(s)");
                Assert.IsNotNull(hashes);
                Assert.Greater(hashes.Length, 0, $"'{n}' should have at least one hash key");
            }
        }

        [Test]
        public void TryGetCellmapHashes_RejectsUnknownAndEmpty()
        {
            var asset = MakeAsset();
            Assert.IsFalse(asset.TryGetCellmapHashes("no_such_cellmap", out var h1));
            Assert.IsNull(h1);
            Assert.IsFalse(asset.TryGetCellmapHashes("", out _));
            Assert.IsFalse(asset.TryGetCellmapHashes(null, out _));
        }

        // ============================================================
        // (B) Component layer: name-keyed API round-trips
        // ============================================================

        [Test]
        public void Player_SetCellmapTexture_RoundTripsByName_AndNullClears()
        {
            var player = MakePlayer(MakeAsset());
            string name = player.GetReplaceableTextureNames()[0];

            var tex = new Texture2D(2, 2) { name = "ReplA" };
            _assets.Add(tex);

            Assert.IsNull(player.GetCellmapTexture(name), "no replacement set initially");
            player.SetCellmapTexture(name, tex);
            Assert.AreEqual(tex, player.GetCellmapTexture(name), "the replacement should round-trip by name");

            player.SetCellmapTexture(name, null);
            Assert.IsNull(player.GetCellmapTexture(name), "null should clear the replacement");
        }

        [Test]
        public void Graphic_SetCellmapTexture_RoundTripsByName_AndNullClears()
        {
            var graphic = MakeGraphic(MakeAsset());
            string name = graphic.GetReplaceableTextureNames()[0];

            var tex = new Texture2D(2, 2) { name = "ReplB" };
            _assets.Add(tex);

            Assert.IsNull(graphic.GetCellmapTexture(name));
            graphic.SetCellmapTexture(name, tex);
            Assert.AreEqual(tex, graphic.GetCellmapTexture(name));

            graphic.SetCellmapTexture(name, null);
            Assert.IsNull(graphic.GetCellmapTexture(name));
        }

        [Test]
        public void Component_ReplaceableTextureNames_MatchAsset()
        {
            var asset = MakeAsset();
            var player = MakePlayer(asset);
            Assert.AreEqual(asset.GetReplaceableTextureNames(), player.GetReplaceableTextureNames());
        }

        // ============================================================
        // (C) Draw layer: the replacement swaps the bound texture
        // ============================================================

        [Test]
        public void SetCellmapTexture_SwapsSubmeshTexture_WithoutMutatingSharedMaterial()
        {
            // Needs the imported asset (materials + textures bound at import); skip where unavailable.
            Assume.That(_imported != null && _imported.Materials != null && _imported.Materials.Count > 0,
                "imported Ringo with materials is required for the draw-layer test");

            var player = MakePlayer(_imported);
            var internalP = InternalOf(player);
            Build(internalP);
            player.ApplyAndRender();

            var mr = player.GetComponent<MeshRenderer>();
            Assert.IsNotNull(mr);
            Assume.That(AnyMaterialTextured(mr.sharedMaterials), Is.True,
                "the walk frame should have at least one textured submesh to observe the swap");

            var repl = new Texture2D(4, 4) { name = "ReplDraw" };
            _assets.Add(repl);

            // Replace every replaceable name so whichever cellmap is on screen this frame is covered.
            foreach (var n in player.GetReplaceableTextureNames()) player.SetCellmapTexture(n, repl);
            player.ApplyAndRender();

            Assert.IsTrue(AnyMaterialUsesTexture(mr.sharedMaterials, repl),
                "a submesh material should now bind the replacement texture");
            // The per-player replacement must never mutate the asset-shared materials.
            foreach (var m in _imported.Materials)
            {
                if (m != null) Assert.AreNotSame(repl, m.mainTexture, "the asset-shared material must not be mutated");
            }

            // Clearing restores: no submesh material should carry the replacement any more.
            foreach (var n in player.GetReplaceableTextureNames()) player.SetCellmapTexture(n, null);
            player.ApplyAndRender();
            Assert.IsFalse(AnyMaterialUsesTexture(mr.sharedMaterials, repl),
                "clearing the replacement should restore the original textures");
        }
    }
}
