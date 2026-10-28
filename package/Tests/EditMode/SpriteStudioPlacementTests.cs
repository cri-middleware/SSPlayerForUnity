using NUnit.Framework;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// Fit and align, as <see cref="SpriteStudioGraphic"/> puts an animation's canvas into its rect. Y is up,
    /// as in a RectTransform, so Top is the rect's high end.
    /// </summary>
    public class SpriteStudioPlacementTests
    {
        // A 100x50 box around the origin, in a 400x100 rect whose pivot sits a quarter of the way in.
        private static readonly Rect Box = new Rect(-50f, -25f, 100f, 50f);
        private static readonly Rect View = new Rect(-100f, -50f, 400f, 100f);

        private static (Vector2 origin, float scale) Resolve(SpriteStudioFit fit, SpriteStudioAlign align, Rect? box = null, Rect? view = null)
        {
            SpriteStudioPlacement.Resolve(box ?? Box, view ?? View, fit, align, out var origin, out float scale);
            return (origin, scale);
        }

        // Where a box corner lands: origin + scale * p.
        private static Vector2 Place((Vector2 origin, float scale) placement, Vector2 p) => placement.origin + placement.scale * p;

        [Test]
        public void Contain_TakesTheSmallerScale_AndCentres()
        {
            var placed = Resolve(SpriteStudioFit.Contain, SpriteStudioAlign.Center);

            Assert.AreEqual(2f, placed.scale);
            Assert.AreEqual(View.center, Place(placed, Box.center));
        }

        [Test]
        public void Cover_TakesTheLargerScale()
        {
            Assert.AreEqual(4f, Resolve(SpriteStudioFit.Cover, SpriteStudioAlign.Center).scale);
        }

        [Test]
        public void Align_PutsTheBoxEdgeOnTheRectEdge()
        {
            var left = Resolve(SpriteStudioFit.Contain, SpriteStudioAlign.Left);
            Assert.AreEqual(View.xMin, Place(left, Box.min).x, 1e-4f);
            Assert.AreEqual(View.center.y, Place(left, Box.center).y, 1e-4f);

            var topRight = Resolve(SpriteStudioFit.Contain, SpriteStudioAlign.TopRight);
            Assert.AreEqual(View.xMax, Place(topRight, Box.max).x, 1e-4f);
            Assert.AreEqual(View.yMax, Place(topRight, Box.max).y, 1e-4f, "Top is the high end of a Y-up rect");

            var bottom = Resolve(SpriteStudioFit.Cover, SpriteStudioAlign.Bottom);
            Assert.AreEqual(View.yMin, Place(bottom, Box.min).y, 1e-4f);
        }

        [Test]
        public void None_IsScaleOne_WithTheOriginAtTheRectsCentre()
        {
            var placed = Resolve(SpriteStudioFit.None, SpriteStudioAlign.TopLeft);

            Assert.AreEqual(1f, placed.scale);
            Assert.AreEqual(View.center, placed.origin);
        }

        [Test]
        public void AnEmptyBoxOrRect_IsNotFitted()
        {
            var noBox = Resolve(SpriteStudioFit.Contain, SpriteStudioAlign.Left, box: new Rect(0f, 0f, 0f, 50f));
            Assert.AreEqual(1f, noBox.scale);
            Assert.AreEqual(View.center, noBox.origin);

            var noView = Resolve(SpriteStudioFit.Cover, SpriteStudioAlign.Center, view: new Rect(10f, 20f, 0f, 0f));
            Assert.AreEqual(1f, noView.scale);
            Assert.AreEqual(new Vector2(10f, 20f), noView.origin);

            SpriteStudioPlacement.Resolve(null, View, SpriteStudioFit.Contain, SpriteStudioAlign.Center, out var origin, out float scale);
            Assert.AreEqual(1f, scale);
            Assert.AreEqual(View.center, origin);
        }

        [Test]
        public void CanvasRect_PlacesTheCanvasAroundTheOriginByItsPivot()
        {
            Assert.AreEqual(new Rect(-50f, -25f, 100f, 50f), SpriteStudioPlacement.CanvasRect(100f, 50f, 0f, 0f));
            // Pivot (0, -0.5): the origin on the canvas's bottom edge, the box wholly above it.
            Assert.AreEqual(new Rect(-50f, 0f, 100f, 50f), SpriteStudioPlacement.CanvasRect(100f, 50f, 0f, -0.5f));
            Assert.AreEqual(new Rect(-100f, -25f, 100f, 50f), SpriteStudioPlacement.CanvasRect(100f, 50f, 0.5f, 0f));
        }
    }
}
