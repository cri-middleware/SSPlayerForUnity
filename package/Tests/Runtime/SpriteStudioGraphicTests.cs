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
            Assert.DoesNotThrow(() => graphic.Play("dummy"));
        }

        [Test]
        public void InitialProperties_AreDefaulted()
        {
            var graphic = _go.AddComponent<SpriteStudioGraphic>();
            Assert.AreEqual(1f, graphic.SpeedScale);
            Assert.AreEqual(-1, graphic.LoopCount);
            Assert.IsTrue(graphic.AutoPlay);
            Assert.IsTrue(graphic.SkipFrames);
            Assert.IsFalse(graphic.SubFrameEnabled);
        }

        [Test]
        public void SettingProperties_ReflectsValues()
        {
            var graphic = _go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimationName = "TestAnimUI";
            graphic.AutoPlay = false;
            graphic.LoopCount = 5;
            graphic.SpeedScale = 2.0f;
            graphic.SubFrameEnabled = true;
            graphic.SkipFrames = false;

            Assert.AreEqual("TestAnimUI", graphic.AnimationName);
            Assert.AreEqual(false, graphic.AutoPlay);
            Assert.AreEqual(5, graphic.LoopCount);
            Assert.AreEqual(2.0f, graphic.SpeedScale);
            Assert.AreEqual(true, graphic.SubFrameEnabled);
            Assert.AreEqual(false, graphic.SkipFrames);
        }
    }
}
