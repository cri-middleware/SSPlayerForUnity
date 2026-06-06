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
            // 落ちないことを確認
            Assert.DoesNotThrow(() => player.Play("dummy"));
        }

        [Test]
        public void InitialProperties_AreDefaulted()
        {
            var player = _go.AddComponent<SpriteStudioPlayer>();
            Assert.AreEqual(100f, player.PixelsPerUnit);
            Assert.AreEqual(1f, player.SpeedScale);
            Assert.AreEqual(0, player.LoopCount);
            Assert.IsTrue(player.AutoPlay);
        }
    }
}
