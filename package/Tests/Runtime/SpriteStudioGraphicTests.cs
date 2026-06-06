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
            Assert.AreEqual(0, graphic.LoopCount);
            Assert.IsTrue(graphic.AutoPlay);
        }
    }
}
