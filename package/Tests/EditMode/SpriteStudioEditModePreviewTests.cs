using System.Reflection;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// The inspector's Edit Mode preview and a Timeline scrub in Edit Mode draw other frames than the start one
    /// by seeking: the pose changes, no event fires, nothing is serialized, and letting go brings the start
    /// frame back.
    /// </summary>
    public class SpriteStudioEditModePreviewTests
    {
        private const string RingoAssetPath = "Ringo/Ringo";
        private const string UserDataAssetPath = "EventDispatch/UserData";

        private GameObject _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            _go = null;
        }

        // --- the clock ---

        [Test]
        public void FrameAt_AdvancesByElapsedTime()
        {
            Assert.AreEqual(18f, SpriteStudioEditPreviewClock.FrameAt(10, 19, 32, 1f, 10f, 0.25, false));
        }

        [Test]
        public void FrameAt_LoopsOverTheSection()
        {
            // 16 frames into a 10-frame section.
            Assert.AreEqual(16f, SpriteStudioEditPreviewClock.FrameAt(10, 19, 32, 1f, 10f, 0.5, false));
            // From frame 18, two frames on wraps to the start.
            Assert.AreEqual(10f, SpriteStudioEditPreviewClock.FrameAt(10, 19, 32, 1f, 18f, 0.0625, false));
        }

        [Test]
        public void FrameAt_FollowsTheSpeedScale()
        {
            Assert.AreEqual(18f, SpriteStudioEditPreviewClock.FrameAt(10, 19, 32, 2f, 10f, 0.125, false));
        }

        [Test]
        public void FrameAt_HoldsAtZeroSpeed()
        {
            Assert.AreEqual(14f, SpriteStudioEditPreviewClock.FrameAt(10, 19, 32, 0f, 14f, 3.0, false));
        }

        [Test]
        public void FrameAt_KeepsTheFractionOnlyWithSubFrames()
        {
            Assert.AreEqual(10.5f, SpriteStudioEditPreviewClock.FrameAt(10, 19, 32, 1f, 10f, 1.0 / 64.0, true));
            Assert.AreEqual(10f, SpriteStudioEditPreviewClock.FrameAt(10, 19, 32, 1f, 10f, 1.0 / 64.0, false));
        }

        // --- the world player ---

        private static SpriteStudioInternalPlayer GetInternal(Component component)
        {
            var coreField = component.GetType().GetField("_core", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(coreField, component.GetType().Name + "._core not found (was it renamed?).");
            var core = coreField.GetValue(component);
            var internalProperty = core.GetType().GetProperty("Internal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(internalProperty, "SpriteStudioPlaybackCore.Internal not found (was it renamed?).");
            return internalProperty.GetValue(core) as SpriteStudioInternalPlayer;
        }

        private static void Invoke(SpriteStudioPlayer player, string method, params object[] args)
        {
            var m = typeof(SpriteStudioPlayer).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "SpriteStudioPlayer." + method + " not found (was it renamed?).");
            m.Invoke(player, args);
        }

        private static bool EditPreviewDirty(SpriteStudioPlayer player)
        {
            var f = typeof(SpriteStudioPlayer).GetField("_editPreviewDirty", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "SpriteStudioPlayer._editPreviewDirty not found (was it renamed?).");
            return (bool)f.GetValue(player);
        }

        private static SpriteStudioAnimeAsset Load(string path)
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(path);
            Assert.IsNotNull(asset, path + " must be importable (warm the Library and re-run if null).");
            return asset;
        }

        private SpriteStudioPlayer PlacePlayer(string assetPath, string animation = null)
        {
            _go = new GameObject("EditModePreviewTest");
            var player = _go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = Load(assetPath);
            if (animation != null) player.CurrentAnimation = animation;
            return player;
        }

        private Vector3[] Vertices() => _go.GetComponent<MeshFilter>().sharedMesh.vertices;

        // The start frame, then a frame whose pose differs from it.
        private static void Section(ISpriteStudioEditPreview preview, out int start, out int mid, out int fps)
        {
            Assert.IsTrue(preview.TryGetEditPreviewSection(out start, out int end, out fps));
            Assert.Greater(end, start + 2, "The fixture's first animation needs more than a couple of frames.");
            mid = start + (end - start) / 2;
        }

        [Test]
        public void ShowEditPreviewFrame_DrawsThatFrame()
        {
            var player = PlacePlayer(RingoAssetPath);
            var preview = (ISpriteStudioEditPreview)player;
            Invoke(player, "BuildEditPreview");
            var startPose = Vertices();
            Assert.Greater(startPose.Length, 0);

            Section(preview, out _, out int mid, out _);
            preview.ShowEditPreviewFrame(mid);

            Assert.AreEqual((float)mid, GetInternal(player).GetFrame());
            CollectionAssert.AreNotEqual(startPose, Vertices(), "The preview must draw the frame it was given.");
        }

        [Test]
        public void ShowEditPreviewFrame_ReachesTheLastFrame()
        {
            var player = PlacePlayer(RingoAssetPath, "wait1");
            var preview = (ISpriteStudioEditPreview)player;
            Assert.IsTrue(preview.TryGetEditPreviewSection(out int start, out int end, out int fps));
            Assert.AreEqual(0, start);
            Assert.AreEqual(79, end, "wait1 is 80 frames long; the preview must cover all of it.");

            preview.ShowEditPreviewFrame(50);
            var at50 = Vertices();
            preview.ShowEditPreviewFrame(end);

            Assert.AreEqual((float)end, GetInternal(player).GetFrame());
            CollectionAssert.AreNotEqual(at50, Vertices(), "Frames past 50 must draw too.");
            Assert.AreEqual(79f, SpriteStudioEditPreviewClock.FrameAt(start, end, fps, 1f, start, 79.5 / fps, false));
        }

        [Test]
        public void EndEditPreview_GoesBackToTheStartFrame()
        {
            var player = PlacePlayer(RingoAssetPath);
            var preview = (ISpriteStudioEditPreview)player;
            Invoke(player, "BuildEditPreview");
            var startPose = Vertices();
            Section(preview, out _, out int mid, out _);
            preview.ShowEditPreviewFrame(mid);

            preview.EndEditPreview();

            Assert.IsTrue(EditPreviewDirty(player), "Ending the preview must ask LateUpdate for the start frame.");
            Invoke(player, "BuildEditPreview");
            CollectionAssert.AreEqual(startPose, Vertices());
        }

        [Test]
        public void ShowEditPreviewFrame_LeavesTheNameUnset()
        {
            var player = PlacePlayer(RingoAssetPath);
            var preview = (ISpriteStudioEditPreview)player;
            Section(preview, out _, out int mid, out _);

            preview.ShowEditPreviewFrame(mid);

            Assert.IsEmpty(player.CurrentAnimation, "The preview is a view and must not write the scene.");
        }

        [Test]
        public void ShowEditPreviewFrame_FiresNoEvents()
        {
            var player = PlacePlayer(UserDataAssetPath, "anime_1");
            var preview = (ISpriteStudioEditPreview)player;
            int fired = 0;
            player.OnUserDataEvent.AddListener(_ => fired++);
            player.OnSignalEvent.AddListener(_ => fired++);
            player.OnAnimationLoopedEvent.AddListener(() => fired++);
            Assert.IsTrue(preview.TryGetEditPreviewSection(out int start, out int end, out _));

            // Through the USER keys at frames 0 and 10, and around again.
            for (int i = 0; i < 2; i++)
                for (int frame = start; frame <= end; frame++)
                    preview.ShowEditPreviewFrame(frame);

            Assert.AreEqual(0, fired, "Drawing a frame in Edit Mode must not call into the scene's scripts.");
        }

        [Test]
        public void TimelineScrub_DrawsInEditModeAndLetsGo()
        {
            var player = PlacePlayer(RingoAssetPath);
            var preview = (ISpriteStudioEditPreview)player;
            Invoke(player, "BuildEditPreview");
            var startPose = Vertices();
            Section(preview, out int start, out int mid, out int fps);
            string animation = GetInternal(player).GetAnimationName(0);

            player.EvaluateTimelineClip(animation, (double)(mid - start) / fps);

            Assert.IsTrue(preview.EditPreviewBlocked, "The inspector's preview must leave a Timeline-driven player alone.");
            CollectionAssert.AreNotEqual(startPose, Vertices(), "Scrubbing the Timeline in Edit Mode must move the player.");

            player.ReleaseTimelineControl();

            Assert.IsFalse(preview.EditPreviewBlocked);
            Assert.IsTrue(EditPreviewDirty(player), "Letting go must ask LateUpdate for the start frame.");
            Invoke(player, "BuildEditPreview");
            CollectionAssert.AreEqual(startPose, Vertices());
        }

        [Test]
        public void TimelineScrub_AfterThePreviewSetsTheClipUpAgain()
        {
            var player = PlacePlayer(RingoAssetPath);
            var internalPlayer = GetInternal(player);
            string first = internalPlayer.GetAnimationName(0);
            string second = internalPlayer.GetAnimationName(1);
            Assert.AreNotEqual(first, second);
            player.CurrentAnimation = second;

            player.EvaluateTimelineClip(first, 0.0);
            // The static preview sets the inspector's animation up over the clip's...
            Invoke(player, "BuildEditPreview");
            Assert.AreEqual(second, internalPlayer.CurrentAnimationName);
            // ...so the next evaluation of the same clip must set it up again rather than seek in the wrong one.
            player.EvaluateTimelineClip(first, 0.0);
            Assert.AreEqual(first, internalPlayer.CurrentAnimationName);
        }

        // --- the canvas graphic ---

        [Test]
        public void Graphic_ShowEditPreviewFrame_SeeksTheRuntime()
        {
            _go = new GameObject("EditModePreviewGraphicTest", typeof(RectTransform));
            var graphic = _go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = Load(RingoAssetPath);
            var preview = (ISpriteStudioEditPreview)graphic;
            Section(preview, out _, out int mid, out _);

            preview.ShowEditPreviewFrame(mid);

            Assert.AreEqual((float)mid, GetInternal(graphic).GetFrame(),
                "LateUpdate rebuilds the frame the runtime is on, so the preview only has to seek.");
            Assert.IsEmpty(graphic.CurrentAnimation);
        }
    }
}
