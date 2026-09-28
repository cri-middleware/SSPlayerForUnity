using System.Collections.Generic;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace SpriteStudio.Runtime.Tests
{
    public class SpriteStudioGraphicTests
    {
        private GameObject _go;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestGraphic");
            // Canvas needs to exist for Graphic to properly function without warnings sometimes
            var canvas = _go.AddComponent<Canvas>();
            _go.AddComponent<CanvasRenderer>();
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
            var graphic = _go.AddComponent<SpriteStudioGraphic>();
            Assert.IsNotNull(graphic);
        }

        [Test]
        public void Play_WithoutAnimeAsset_LogsWarningOrDoesNothing()
        {
            var graphic = _go.AddComponent<SpriteStudioGraphic>();
            Assert.DoesNotThrow(() => { graphic.SetAnimation("dummy"); graphic.Play(); });
        }

        [Test]
        public void InitialProperties_AreDefaulted()
        {
            var graphic = _go.AddComponent<SpriteStudioGraphic>();
            Assert.AreEqual(1f, graphic.SpeedScale);
            Assert.AreEqual(-1, graphic.LoopCount);
            Assert.IsTrue(graphic.AutoPlay);
            Assert.IsTrue(graphic.FrameSkipEnabled);
            Assert.IsFalse(graphic.SubFrameEnabled);
        }

        [Test]
        public void SettingProperties_ReflectsValues()
        {
            var graphic = _go.AddComponent<SpriteStudioGraphic>();
            graphic.CurrentAnimation = "TestAnimUI";
            graphic.AutoPlay = false;
            graphic.LoopCount = 5;
            graphic.SpeedScale = 2.0f;
            graphic.SubFrameEnabled = true;
            graphic.FrameSkipEnabled = false;

            Assert.AreEqual("TestAnimUI", graphic.CurrentAnimation);
            Assert.AreEqual(false, graphic.AutoPlay);
            Assert.AreEqual(5, graphic.LoopCount);
            Assert.AreEqual(2.0f, graphic.SpeedScale);
            Assert.AreEqual(true, graphic.SubFrameEnabled);
            Assert.AreEqual(false, graphic.FrameSkipEnabled);
        }

        // A graphic on Ringo's walk with its first frame built and handed to the CanvasRenderer.
        private SpriteStudioGraphic MakeBuiltGraphic()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>("Ringo/Ringo");
            Assert.IsNotNull(asset, "Ringo.ssab is required.");
            var graphic = _go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = asset;
            Assert.IsTrue(graphic.SetAnimation("walk"));
            graphic.InternalPlayer.ScheduleMeshUpdate(0f);
            graphic.ApplyAndRender();
            Assert.Greater(graphic.CanvasMesh.vertexCount, 0);
            return graphic;
        }

        [Test]
        public void Color_IsTheVertexColour_AndThePartColourRidesInTexCoord2()
        {
            var graphic = MakeBuiltGraphic();
            var mesh = graphic.CanvasMesh;
            foreach (var c in mesh.colors32)
                Assert.AreEqual(new Color32(255, 255, 255, c.a), c, "The default Color is white");
            var partColours = new List<Vector3>();
            mesh.GetUVs(2, partColours);
            Assert.AreEqual(mesh.vertexCount, partColours.Count, "Every vertex carries its part colour in TEXCOORD2");

            // Nothing is rebuilt (the frame is unchanged), yet the new colour has to reach the vertices.
            graphic.color = new Color(1f, 0f, 0f, 1f);
            graphic.InternalPlayer.ScheduleMeshUpdate(0f);
            graphic.ApplyAndRender();

            foreach (var c in graphic.CanvasMesh.colors32)
                Assert.AreEqual(new Color32(255, 0, 0, c.a), c, "Color should tint every vertex");
        }

        [Test]
        public void ApplyAndRender_LeavesTheCanvasRendererColourToUGUI()
        {
            var graphic = MakeBuiltGraphic();
            Assert.AreEqual(Color.white, graphic.canvasRenderer.GetColor(), "A CanvasRenderer starts white, as an Image's does");

            // What a Button's Color Tint transition or CrossFadeColor writes each step.
            var pressed = new Color(0.5f, 0.5f, 0.5f, 1f);
            graphic.canvasRenderer.SetColor(pressed);
            graphic.ApplyAndRender();

            Assert.AreEqual(pressed, graphic.canvasRenderer.GetColor());
        }
    }
}
