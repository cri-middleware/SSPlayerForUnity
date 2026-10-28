using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using SpriteStudio.Runtime;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// <see cref="SpriteStudioGraphic"/> fits the animation's canvas into its RectTransform, and asks a layout
    /// for the canvas's size.
    /// </summary>
    public class SpriteStudioGraphicFitTests
    {
        private GameObject _canvas;

        [SetUp]
        public void SetUp()
        {
            _canvas = new GameObject("FitCanvas", typeof(Canvas));
        }

        [TearDown]
        public void TearDown()
        {
            if (_canvas != null) Object.DestroyImmediate(_canvas);
        }

        private SpriteStudioGraphic MakeGraphic(string animation = "walk")
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>("Ringo/Ringo");
            Assert.IsNotNull(asset, "Ringo.ssab is required.");
            var go = new GameObject("FitGraphic", typeof(RectTransform));
            go.transform.SetParent(_canvas.transform, false);
            var graphic = go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = asset;
            Assert.IsTrue(graphic.SetAnimation(animation));
            return graphic;
        }

        private static Rect CanvasOf(SpriteStudioGraphic graphic)
        {
            Assert.IsTrue(graphic.InternalPlayer.TryGetCanvasRect(null, out Rect box));
            return box;
        }

        // The drawn vertices' bounds in the rect's local space.
        private static Rect DrawnBounds(SpriteStudioGraphic graphic)
        {
            graphic.ApplyFit();
            graphic.InternalPlayer.ScheduleMeshUpdate(0f);
            graphic.ApplyAndRender();
            var b = graphic.CanvasMesh.bounds;
            return new Rect(b.min.x, b.min.y, b.size.x, b.size.y);
        }

        [Test]
        public void Contain_ScalesTheCanvasIntoTheRect_AroundItsCentre()
        {
            var graphic = MakeGraphic();
            Rect box = CanvasOf(graphic);
            graphic.rectTransform.sizeDelta = new Vector2(box.width / 2f, box.height);

            graphic.ApplyFit();

            Assert.AreEqual(0.5f, graphic.InternalPlayer.VertexScale, 1e-5f, "Width is the tighter axis");
            Vector2 centre = graphic.InternalPlayer.VertexOffset + 0.5f * box.center;
            Assert.AreEqual(graphic.rectTransform.rect.center.x, centre.x, 1e-3f);
            Assert.AreEqual(graphic.rectTransform.rect.center.y, centre.y, 1e-3f);
        }

        [Test]
        public void ResizingTheRect_ResizesWhatIsDrawn()
        {
            var graphic = MakeGraphic();
            Rect box = CanvasOf(graphic);
            graphic.rectTransform.sizeDelta = box.size;
            Rect atNative = DrawnBounds(graphic);

            graphic.rectTransform.sizeDelta = box.size * 2f;
            Rect atDouble = DrawnBounds(graphic);

            Assert.AreEqual(atNative.width * 2f, atDouble.width, 1e-2f);
            Assert.AreEqual(atNative.height * 2f, atDouble.height, 1e-2f);
        }

        [Test]
        public void None_DrawsAtTheAuthoredSize_WithTheOriginAtTheRectsCentre()
        {
            var graphic = MakeGraphic();
            graphic.rectTransform.pivot = new Vector2(0f, 0f);
            graphic.rectTransform.sizeDelta = new Vector2(10f, 20f);
            graphic.Fit = SpriteStudioFit.None;

            graphic.ApplyFit();

            Assert.AreEqual(1f, graphic.InternalPlayer.VertexScale);
            Assert.AreEqual(new Vector2(5f, 10f), graphic.InternalPlayer.VertexOffset);
        }

        [Test]
        public void LayoutAsksForTheCanvasSize_AndSetNativeSizeGivesIt()
        {
            var graphic = MakeGraphic();
            Rect box = CanvasOf(graphic);
            ILayoutElement layout = graphic;

            Assert.AreEqual(box.width, layout.preferredWidth);
            Assert.AreEqual(box.height, layout.preferredHeight);

            graphic.SetNativeSize();
            Assert.AreEqual(box.size, graphic.rectTransform.rect.size);
        }

        [Test]
        public void ContentSizeFitter_SizesTheRectToTheCanvas()
        {
            var graphic = MakeGraphic();
            Rect box = CanvasOf(graphic);
            var fitter = graphic.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            LayoutRebuilder.ForceRebuildLayoutImmediate(graphic.rectTransform);

            Assert.AreEqual(box.size, graphic.rectTransform.rect.size);
        }

        [Test]
        public void BeforeAnAnimationIsSetUp_TheLayoutReadsTheOneNamed()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>("Ringo/Ringo");
            Assert.IsNotNull(asset, "Ringo.ssab is required.");
            var go = new GameObject("FitGraphicUnset", typeof(RectTransform));
            go.transform.SetParent(_canvas.transform, false);
            var graphic = go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = asset;
            graphic.CurrentAnimation = "walk";
            Assert.IsFalse(graphic.InternalPlayer.TryGetCanvasRect(null, out _), "Nothing is set up yet");
            Assert.IsTrue(graphic.InternalPlayer.TryGetCanvasRect("walk", out Rect walk));

            Assert.AreEqual(walk.width, ((ILayoutElement)graphic).preferredWidth);
        }
    }
}
