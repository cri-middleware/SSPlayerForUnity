using System.Collections;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Editor.Tests
{
    /// <summary>
    /// The inspector's Edit Mode preview, end to end: Play moves the player with the editor's clock, Pause holds
    /// the frame, Stop draws the start frame again.
    /// </summary>
    public class SpriteStudioEditModePreviewDriverTests
    {
        private GameObject _go;
        private SpriteStudioPlayer _player;

        [SetUp]
        public void SetUp()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>("Ringo/Ringo");
            Assert.IsNotNull(asset, "Ringo fixture must be importable (warm the Library and re-run if null).");
            _go = new GameObject("EditModePreviewDriverTest");
            _player = _go.AddComponent<SpriteStudioPlayer>();
            _player.AnimeAsset = asset;
        }

        [TearDown]
        public void TearDown()
        {
            SpriteStudioEditModePreview.StopAll();
            if (_go != null) Object.DestroyImmediate(_go);
            _go = null;
        }

        private Vector3[] Vertices() => _go.GetComponent<MeshFilter>().sharedMesh.vertices;

        private static IEnumerator Wait(double seconds)
        {
            double until = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < until) yield return null;
        }

        private Vector3[] StartPose()
        {
            Assert.IsTrue(SpriteStudioEditModePreview.TryGetSection(_player, out int start, out int end, out _));
            Assert.Greater(end, start + 10, "The fixture's first animation needs room to move.");
            SpriteStudioEditModePreview.Seek(_player, start);
            return Vertices();
        }

        [UnityTest]
        public IEnumerator Play_MovesThePlayerInEditMode()
        {
            var startPose = StartPose();

            SpriteStudioEditModePreview.Play(_player);
            Assert.IsTrue(SpriteStudioEditModePreview.IsPlaying(_player));
            yield return Wait(0.3);

            Assert.Greater(SpriteStudioEditModePreview.CurrentFrame(_player), 0f);
            CollectionAssert.AreNotEqual(startPose, Vertices(), "Playing in Edit Mode must draw later frames.");
            Assert.IsEmpty(_player.CurrentAnimation, "The preview is a view and must not write the scene.");
        }

        [UnityTest]
        public IEnumerator Pause_HoldsTheFrame()
        {
            StartPose();
            SpriteStudioEditModePreview.Play(_player);
            yield return Wait(0.2);

            SpriteStudioEditModePreview.Pause(_player);
            float held = SpriteStudioEditModePreview.CurrentFrame(_player);
            yield return null;
            var heldPose = Vertices();
            yield return Wait(0.2);

            Assert.IsFalse(SpriteStudioEditModePreview.IsPlaying(_player));
            Assert.AreEqual(held, SpriteStudioEditModePreview.CurrentFrame(_player));
            CollectionAssert.AreEqual(heldPose, Vertices());
        }

        [UnityTest]
        public IEnumerator Stop_DrawsTheStartFrameAgain()
        {
            var startPose = StartPose();
            SpriteStudioEditModePreview.Play(_player);
            yield return Wait(0.3);
            CollectionAssert.AreNotEqual(startPose, Vertices());

            SpriteStudioEditModePreview.Stop(_player);

            Assert.IsFalse(SpriteStudioEditModePreview.IsPlaying(_player));
            CollectionAssert.AreEqual(startPose, Vertices(), "Stop must leave the scene as the static preview draws it.");
        }
    }
}
