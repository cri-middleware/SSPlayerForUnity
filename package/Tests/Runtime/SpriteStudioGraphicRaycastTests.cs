using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// SpriteStudioGraphic draws its animation in the RectTransform's local space, but uGUI gates hit-testing
    /// on the RectTransform rect (adjusted by raycastPadding) BEFORE Graphic.Raycast runs. Without help the
    /// clickable area is the (unrelated) default rect, not the art -- the gap a QA report hit. The graphic
    /// drives raycastPadding so the effective raycast rectangle equals the drawn mesh's AABB, covering the whole
    /// art without resizing the RectTransform. These tests pin that.
    /// </summary>
    public class SpriteStudioGraphicRaycastTests
    {
        private const string RingoAssetPath = "Ringo/Ringo"; // Resources.Load
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";

        private byte[] _ringo;
        private readonly List<Object> _trash = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0)
            {
                _ringo = asset.SsabData;
            }
            else
            {
                string p = Path.GetFullPath(RingoSsabPath);
                _ringo = File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _trash) if (o != null) Object.DestroyImmediate(o);
            _trash.Clear();
        }

        // A graphic that has actually rendered one frame of Ringo's walk, so its mesh (and bounds) are built
        // and UpdateRaycastArea has run via ApplyAndRender.
        private SpriteStudioGraphic MakeRenderedGraphic(string anim = "walk")
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            var go = new GameObject("RaycastGraphic", typeof(RectTransform));
            _trash.Add(go);
            go.AddComponent<Canvas>();
            go.AddComponent<CanvasRenderer>();
            var graphic = go.AddComponent<SpriteStudioGraphic>();

            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = _ringo;
            _trash.Add(asset);
            graphic.AnimeAsset = asset;

            Assert.IsTrue(graphic.SetAnimation(anim), $"{anim} should be found");
            graphic.Play();

            var ip = graphic.InternalPlayer;
            ip.SetSkipFrames(false);
            ip.Tick(1f / 60f, false);
            ip.ScheduleMeshUpdate(1f / 60f);
            // Build the mesh + bounds and drive raycastPadding via the same path ApplyAndRender uses, minus the
            // canvas material step (which needs an imported asset's atlas textures, absent for this raw-bytes asset).
            graphic.RebuildForTest();
            return graphic;
        }

        [Test]
        public void RaycastArea_MatchesTheDrawnMeshBounds()
        {
            var graphic = MakeRenderedGraphic();
            Bounds b = graphic.RenderedLocalBounds;
            Assume.That(b.size.x, Is.GreaterThan(1f), "the walk frame should have drawn a non-trivial mesh");
            Assume.That(b.size.y, Is.GreaterThan(1f));

            Rect r = graphic.rectTransform.rect;
            Vector4 p = graphic.raycastPadding;

            // Effective raycast rectangle = rect adjusted by padding (left, bottom, right, top; positive insets).
            const float tol = 1e-3f;
            Assert.AreEqual(b.min.x, r.xMin + p.x, tol, "left edge should reach the mesh's left");
            Assert.AreEqual(b.min.y, r.yMin + p.y, tol, "bottom edge should reach the mesh's bottom");
            Assert.AreEqual(b.max.x, r.xMax - p.z, tol, "right edge should reach the mesh's right");
            Assert.AreEqual(b.max.y, r.yMax - p.w, tol, "top edge should reach the mesh's top");
        }

        [Test]
        public void RaycastArea_MakesAnOffRectArtPointClickable()
        {
            var graphic = MakeRenderedGraphic();
            Bounds b = graphic.RenderedLocalBounds;
            Rect r = graphic.rectTransform.rect;
            Vector4 p = graphic.raycastPadding;

            // A point just inside the art's right edge, at its vertical centre.
            Vector2 pt = new Vector2(b.max.x - 0.01f, b.center.y);

            // The whole point of the fix: this lands outside the bare RectTransform rect...
            Assume.That(r.Contains(pt), Is.False, "the sampled art point should sit outside the plain rect");

            // ...but inside the effective (padded) raycast rectangle, so uGUI's rect gate now lets it through.
            bool insideEffective =
                pt.x >= r.xMin + p.x && pt.x <= r.xMax - p.z &&
                pt.y >= r.yMin + p.y && pt.y <= r.yMax - p.w;
            Assert.IsTrue(insideEffective, "an art point beyond the bare rect must fall inside the padded raycast area");
        }
    }
}
