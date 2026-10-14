using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// The track stack (motion blending) as the components expose it. The runtime owns the mixing and
    /// tests it itself; what is checked here is the player's half: the current animation follows a
    /// switch of track 0 at the call, the loop count the component holds is put back on what track 0
    /// switches to, track 0's settings are the component's own, the reads of nothing, and that the
    /// stack moves under the player's update, that a frame the stack did not move is not rebuilt, and
    /// that each event comes from the source that fired it, read from that source's animation. Time is
    /// advanced with manual Ticks.
    /// Ringo: attack1 is 20 frames at 60 fps, walk and wait1 are 80.
    /// </summary>
    public class SpriteStudioTrackTests
    {
        private const string RingoAssetPath = "Ringo/Ringo"; // for Resources.Load
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";
        // UserData's anime_1: 11 frames at 30 fps, a user-data key on frame 0 and one on frame 10.
        private const string UserDataAssetPath = "overall/UserData";
        private const string UserDataSsabPath = "../../package/Tests/Runtime/Resources/overall/UserData.ssab";
        private const float Dt = 1f / 60f;

        private byte[] _ringo;
        private byte[] _userData;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Object> _assets = new List<Object>();

        [UnitySetUp]
        public IEnumerator Setup()
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

            var userData = Resources.Load<SpriteStudioAnimeAsset>(UserDataAssetPath);
            if (userData != null && userData.SsabData != null && userData.SsabData.Length > 0)
            {
                _userData = userData.SsabData;
            }
            else
            {
                string p = Path.GetFullPath(UserDataSsabPath);
                _userData = File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            foreach (var a in _assets) if (a != null) Object.DestroyImmediate(a);
            _assets.Clear();
        }

        private SpriteStudioPlayer MakePlayer(string anim = "walk")
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = _ringo;
            _assets.Add(asset);
            var go = new GameObject("TrackTestPlayer");
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;
            Assert.IsTrue(player.SetAnimation(anim), $"Ringo has {anim}");
            player.Play();
            return player;
        }

        private static void Advance(SpriteStudioPlayer player, int ticks)
        {
            for (int i = 0; i < ticks; i++) player.InternalPlayer.Tick(Dt, false);
        }

        // ---- track 0 switches the current animation at the call ----

        [Test]
        public void Crossfade_OnTrackZero_SwitchesTheCurrentAnimationAtTheCall()
        {
            var player = MakePlayer("walk");

            Assert.IsTrue(player.Crossfade(0, "wait1", 0.5f));

            Assert.AreEqual("wait1", player.CurrentAnimation, "the component names the incoming animation");
            Assert.AreEqual("wait1", player.InternalPlayer.CurrentAnimationName, "and so does what a setup is judged against");
            Assert.GreaterOrEqual(player.GetTrackFadeProgress(0), 0f, "the fade is under way");
        }

        [Test]
        public void SetAnimation_GoesBackToTheAnimationACrossfadeLeft()
        {
            var player = MakePlayer("walk");
            player.Crossfade(0, "wait1", 0.5f);
            Advance(player, 5);

            Assert.IsTrue(player.SetAnimation("walk"));

            Assert.AreEqual("walk", player.InternalPlayer.CurrentAnimationName,
                "walk was taken for the animation still set up, and the call did nothing");
            Assert.AreEqual(-1f, player.GetTrackFadeProgress(0), "the setup folds the fade away");
        }

        [Test]
        public void SetAnimation_OfTheIncomingAnimation_LeavesTheFadeRunning()
        {
            var player = MakePlayer("walk");
            player.Crossfade(0, "wait1", 0.5f);
            Advance(player, 5);
            float progress = player.GetTrackFadeProgress(0);
            Assume.That(progress, Is.GreaterThan(0f).And.LessThan(1f));

            Assert.IsTrue(player.SetAnimation("wait1"), "wait1 is already the one selected");

            Assert.AreEqual(progress, player.GetTrackFadeProgress(0), 0.0001f, "nothing was set up again");
        }

        [Test]
        public void SetTrackAnimation_OnTrackZero_SwitchesTheCurrentAnimation_AndAboveDoesNot()
        {
            var player = MakePlayer("walk");

            Assert.IsTrue(player.SetTrackAnimation(1, "attack1"));
            Assert.AreEqual("walk", player.CurrentAnimation, "an overlay is not the current animation");
            Assert.AreEqual(2, player.TrackCount);

            Assert.IsTrue(player.SetTrackAnimation(0, "wait1"));
            Assert.AreEqual("wait1", player.CurrentAnimation);
            Assert.AreEqual(2, player.TrackCount, "a track animation on track 0 keeps the tracks above");
        }

        [Test]
        public void UnknownAnimation_IsRefused_AndChangesNothing()
        {
            var player = MakePlayer("walk");

            Assert.IsFalse(player.Crossfade(0, "no_such_animation", 0.5f));
            Assert.IsFalse(player.SetTrackAnimation(1, "no_such_animation"));
            Assert.AreEqual(-1, player.AddTrackSource(1, "no_such_animation", 1f));
            Assert.AreEqual("walk", player.CurrentAnimation);
        }

        // ---- what track 0 carries over a switch ----

        [Test]
        public void LoopCountAndSpeed_SurviveACrossfadeOnTrackZero()
        {
            var player = MakePlayer("walk");
            player.LoopCount = 1;
            player.SpeedScale = 2f;

            player.Crossfade(0, "wait1", 0.5f);

            Assert.AreEqual(1, player.GetTrackLoopCount(0), "the component puts its loop count back");
            Assert.AreEqual(2f, player.GetTrackSpeed(0), 0.0001f, "the runtime keeps track 0's speed");
            Assert.AreEqual(2f, player.SpeedScale, 0.0001f);
        }

        [Test]
        public void LoopCount_SurvivesATrackAnimationOnTrackZero()
        {
            var player = MakePlayer("walk");
            player.LoopCount = 1;

            player.SetTrackAnimation(0, "wait1");

            Assert.AreEqual(1, player.GetTrackLoopCount(0));
        }

        [Test]
        public void TrackZeroSettings_AreTheComponentsOwn()
        {
            var player = MakePlayer("walk");

            Assert.IsTrue(player.SetTrackLoopCount(0, 1));
            Assert.IsTrue(player.SetTrackSpeed(0, 0.5f));
            Assert.IsTrue(player.SetTrackFrameSkipEnabled(0, false));
            Assert.AreEqual(1, player.LoopCount);
            Assert.AreEqual(0.5f, player.SpeedScale, 0.0001f);
            Assert.IsFalse(player.FrameSkipEnabled);

            // A setup pushes the component's values back; they are the ones just written.
            player.SetAnimation("wait1");
            Assert.AreEqual(1, player.GetTrackLoopCount(0));
            Assert.AreEqual(0.5f, player.GetTrackSpeed(0), 0.0001f);
            Assert.IsFalse(player.IsTrackFrameSkipEnabled(0));

            // 0 is a synonym for forever, and the component keeps what it was given.
            Assert.IsTrue(player.SetTrackLoopCount(0, 0));
            Assert.AreEqual(0, player.LoopCount);
            Assert.AreEqual(-1, player.GetTrackLoopCount(0));
        }

        [Test]
        public void AnOverlayTrack_KeepsItsSpeed_ButNotItsLoopCount_AcrossAnimations()
        {
            var player = MakePlayer("walk");
            player.SetTrackAnimation(1, "attack1");
            Assert.IsTrue(player.SetTrackSpeed(1, 2f));
            Assert.IsTrue(player.SetTrackLoopCount(1, 1));
            Assert.IsTrue(player.SetTrackPlaybackDirection(1, SpriteStudioPlaybackDirection.Backward, SpriteStudioPlaybackStyle.PingPong));

            player.SetTrackAnimation(1, "wait1");

            Assert.AreEqual(2f, player.GetTrackSpeed(1), 0.0001f, "the speed is the track's");
            Assert.AreEqual(SpriteStudioPlaybackDirection.Backward, player.GetTrackPlaybackDirection(1));
            Assert.AreEqual(SpriteStudioPlaybackStyle.PingPong, player.GetTrackPlaybackStyle(1));
            Assert.AreEqual(-1, player.GetTrackLoopCount(1), "the loop count is the animation's, laid looping forever");
        }

        [Test]
        public void TrackSettings_RoundTrip()
        {
            var player = MakePlayer("walk");
            player.SetTrackAnimation(1, "attack1");

            Assert.IsTrue(player.SetTrackAlpha(1, 0.25f));
            Assert.AreEqual(0.25f, player.GetTrackAlpha(1), 0.0001f);
            Assert.IsTrue(player.SetTrackBlend(1, SpriteStudioTrackBlend.Additive));
            Assert.AreEqual(SpriteStudioTrackBlend.Additive, player.GetTrackBlend(1));
            Assert.IsTrue(player.SetTrackSync(1, SpriteStudioTrackSync.Start));
            Assert.AreEqual(SpriteStudioTrackSync.Start, player.GetTrackSync(1));
            Assert.IsTrue(player.SetTrackFrameRate(1, 30));
            Assert.AreEqual(30, player.GetTrackFrameRate(1));
            Assert.IsTrue(player.SetTrackOutgoingEventThreshold(1, 0.5f));
            Assert.AreEqual(0.5f, player.GetTrackOutgoingEventThreshold(1), 0.0001f);

            int source = player.AddTrackSource(1, "wait1", 0.5f);
            Assert.GreaterOrEqual(source, 0);
            Assert.AreEqual(0.5f, player.GetSourceWeight(source), 0.0001f);
            Assert.IsTrue(player.SetSourceWeight(source, 0.75f));
            Assert.AreEqual(0.75f, player.GetSourceWeight(source), 0.0001f);
            Assert.IsTrue(player.SetSourceEventThreshold(source, 0.5f));
            Assert.AreEqual(0.5f, player.GetSourceEventThreshold(source), 0.0001f);
            Assert.IsTrue(player.SetSourceFrame(source, 3f));
            Assert.AreEqual(3f, player.GetSourceFrame(source), 0.0001f);
        }

        [Test]
        public void ReadsOfNothing_ReturnTheirNoneValues()
        {
            var go = new GameObject("TrackTestEmpty");
            _objects.Add(go);
            var empty = go.AddComponent<SpriteStudioPlayer>();
            Assert.AreEqual(0, empty.TrackCount);
            Assert.AreEqual(-1f, empty.GetTrackFadeProgress(0));
            Assert.AreEqual(-1, empty.GetTrackMainSource(0));
            Assert.IsFalse(empty.Crossfade(0, "walk", 0.5f));

            var player = MakePlayer("walk");
            Assert.AreEqual(1, player.TrackCount);
            Assert.AreEqual(-1f, player.GetTrackFadeProgress(0), "track 0 is not fading");
            Assert.GreaterOrEqual(player.GetTrackMainSource(0), 0, "0 is a real handle");
            Assert.AreEqual(-1, player.GetTrackMainSource(5), "a track that does not exist");
            Assert.AreEqual(0, player.GetTrackLoopCount(5));
            Assert.AreEqual(0f, player.GetTrackAlpha(5));
            Assert.IsFalse(player.SetTrackAlpha(-1, 1f), "a negative track names none");
            Assert.AreEqual(0f, player.GetSourceWeight(-1), "a negative source names none");
        }

        // ---- the stack under the player's update ----

        [Test]
        public void ACrossfade_RunsToCompletion_AndHandsTheTrackOver()
        {
            var player = MakePlayer("walk");
            player.Crossfade(0, "wait1", 0.25f);

            Advance(player, 30);

            Assert.AreEqual(-1f, player.GetTrackFadeProgress(0), "a quarter-second fade is over after half a second");
            Assert.IsTrue(player.IsPlaying, "the incoming animation plays on");
            Assert.AreEqual("wait1", player.CurrentAnimation);
        }

        [Test]
        public void AnOverlay_MovesUnderTheUpdate_AndHoldsUnderPause()
        {
            var player = MakePlayer("walk");
            player.SetTrackAnimation(1, "wait1");
            float start = player.GetTrackFrame(1);

            Advance(player, 10);
            float moved = player.GetTrackFrame(1);
            Assert.Greater(moved, start, "the overlay is played, not posed");

            player.Pause();
            Advance(player, 10);
            Assert.AreEqual(moved, player.GetTrackFrame(1), 0.0001f, "a pause holds every track");
        }

        [Test]
        public void AnOutgoingAnimationRunningOut_DoesNotFinishThePlayer()
        {
            var player = MakePlayer("attack1");
            player.LoopCount = 1;
            player.Play();
            int finished = 0;
            player.OnAnimationFinished += () => finished++;

            // attack1 runs out a third of a second in; the fade and walk last longer.
            player.Crossfade(0, "walk", 1f);
            Advance(player, 30);

            Assert.IsFalse(player.IsFinished, "walk, the current animation, is still playing");
            Assert.AreEqual(0, finished, "the outgoing side's end is not reported");
        }

        [Test]
        public void AFiniteLoopCount_FinishesTheIncomingAnimationOnce()
        {
            var player = MakePlayer("walk");
            player.LoopCount = 1;
            player.Play();
            int finished = 0;
            player.OnAnimationFinished += () => finished++;

            player.Crossfade(0, "attack1", 0.1f);
            Advance(player, 60);

            Assert.IsTrue(player.IsFinished, "attack1 played its one loop");
            Assert.AreEqual(1, finished);
        }

        [Test]
        public void AnOverlay_FinishesOnItsOwnLoopCount_AndHoldsItsLastFrame()
        {
            var player = MakePlayer("walk");
            player.SetTrackAnimation(1, "attack1");
            player.SetTrackLoopCount(1, 1);

            Advance(player, 40);

            Assert.IsTrue(player.IsTrackFinished(1));
            Assert.IsFalse(player.IsFinished, "only track 0 finishes the player");
            Assert.AreEqual(2, player.TrackCount, "a finished overlay stays until the host clears it");
        }

        [Test]
        public void AnOverlayLoop_IsNotReportedOnAFrameThatDidNotAdvance()
        {
            var player = MakePlayer("walk");
            player.SetTrackAnimation(1, "attack1");

            bool looped = false;
            for (int i = 0; i < 60 && !looped; i++)
            {
                player.InternalPlayer.Tick(Dt, false);
                looped = player.TrackJustLooped(1);
            }
            Assert.IsTrue(looped, "attack1 loops within a second");

            // A Timeline-driven frame is prepared without an update.
            player.InternalPlayer.PrepareFrameData(Dt, false, out _, out _);
            Assert.IsFalse(player.TrackJustLooped(1), "the last update's loop is not this frame's");
        }

        [Test]
        public void TrackZerosLoop_IsNotReportedOnAFrameThatDidNotAdvance_UnderEitherName()
        {
            var player = MakePlayer("attack1");

            bool looped = false;
            for (int i = 0; i < 60 && !looped; i++)
            {
                player.InternalPlayer.Tick(Dt, false);
                looped = player.JustLooped;
            }
            Assert.IsTrue(looped, "attack1 loops within a second");
            Assert.IsTrue(player.TrackJustLooped(0), "track 0's loop under its track name too");

            player.InternalPlayer.PrepareFrameData(Dt, false, out _, out _);
            Assert.IsFalse(player.JustLooped, "the last update's loop is not this frame's");
            Assert.IsFalse(player.TrackJustLooped(0), "and both names agree");
        }

        [Test]
        public void ARestartedCrossfade_KeepsTheTrackMoving()
        {
            var player = MakePlayer("walk");
            player.Crossfade(0, "wait1", 0.5f);
            Advance(player, 5);

            Assert.IsTrue(player.Crossfade(0, "attack1", 0.5f), "a crossfade during another takes it over");
            Assert.AreEqual("attack1", player.CurrentAnimation);
            Assert.GreaterOrEqual(player.GetTrackFadeProgress(0), 0f);

            Assert.IsTrue(player.SetAnimation("walk"), "and the first animation is still one to go back to");
            Assert.AreEqual("walk", player.InternalPlayer.CurrentAnimationName);
        }

        // ---- a frame whose pose did not move is not rebuilt ----

        // Builds the current frame, then reports whether the next advancing frame skipped its rebuild.
        private static bool NextFrameSkipped(SpriteStudioPlayer player)
        {
            player.InternalPlayer.ScheduleMeshUpdate(0f);
            player.InternalPlayer.PrepareFrameData(Dt, true, out _, out _);
            return player.InternalPlayer._geometryBuilder._frameSkipped;
        }

        [Test]
        public void ATrackCall_RebuildsAPausedPlayerOnce_ThenItsFramesAreSkippedAgain()
        {
            var player = MakePlayer("walk");
            Assume.That(player.InternalPlayer._hasEffectParts || player.InternalPlayer._hasInstanceParts, Is.False,
                "Ringo's walk is rebuilt only when it moves");
            player.Pause();
            Assert.IsTrue(NextFrameSkipped(player), "a paused frame is the one already built");

            Assert.IsTrue(player.SetTrackAlpha(0, 0.5f));
            player.InternalPlayer.PrepareFrameData(Dt, true, out _, out _);
            Assert.IsFalse(player.InternalPlayer._geometryBuilder._frameSkipped, "the call is shown while paused");

            Assert.IsTrue(NextFrameSkipped(player), "and only once: the stack is not moving");
        }

        [Test]
        public void ATrackCall_RebuildsAStoppedPlayerOnce_ThenItsFramesAreSkippedAgain()
        {
            var player = MakePlayer("walk");
            player.SetTrackAnimation(1, "wait1");
            player.Stop();
            Assert.IsTrue(NextFrameSkipped(player), "a stopped frame is the one already built");

            Assert.IsTrue(player.SetTrackAlpha(1, 0.5f));
            player.InternalPlayer.PrepareFrameData(Dt, true, out _, out _);
            Assert.IsFalse(player.InternalPlayer._geometryBuilder._frameSkipped, "the call is shown while stopped");

            Assert.IsTrue(NextFrameSkipped(player), "and only once: a stopped clock moves no overlay");
        }

        [Test]
        public void ASourceAddedToTrackZero_KeepsTheRebuildsGoing_AfterTrackZeroFinishes()
        {
            var player = MakePlayer("attack1");
            player.LoopCount = 1;
            player.Play();
            Assert.GreaterOrEqual(player.AddTrackSource(0, "walk", 0.5f), 0);

            for (int i = 0; i < 40; i++) player.InternalPlayer.PrepareFrameData(Dt, true, out _, out _);
            Assert.IsTrue(player.IsFinished, "attack1 played its one loop");

            Assert.IsFalse(NextFrameSkipped(player), "walk, the added source, still moves the pose");
        }

        [Test]
        public void ACancelledCrossfade_KeepsTheRebuildsGoing_WhileItsOutgoingSidePlays()
        {
            var player = MakePlayer("walk");
            player.LoopCount = 1;
            player.Play();
            player.Crossfade(0, "attack1", 1f);
            for (int i = 0; i < 5; i++) player.InternalPlayer.PrepareFrameData(Dt, true, out _, out _);
            Assert.IsTrue(player.CancelTrackFade(0));

            for (int i = 0; i < 30; i++) player.InternalPlayer.PrepareFrameData(Dt, true, out _, out _);
            Assert.IsTrue(player.IsFinished, "attack1 played its one loop");

            Assert.IsFalse(NextFrameSkipped(player), "walk, the side the cancelled fade kept, still moves the pose");
        }

        [Test]
        public void AMovingOverlay_IsRebuilt_WhileTrackZeroHolds_AndNotUnderPause()
        {
            var player = MakePlayer("walk");
            Assert.IsTrue(player.SetTrackSpeed(0, 0f), "track 0's frame stands still");
            player.SetTrackAnimation(1, "wait1");
            player.InternalPlayer.PrepareFrameData(Dt, true, out _, out _);

            Assert.IsFalse(NextFrameSkipped(player), "the overlay moves the pose");

            player.Pause();
            player.InternalPlayer.PrepareFrameData(Dt, true, out _, out _);
            Assert.IsTrue(NextFrameSkipped(player), "a pause holds the overlay");
        }

        [Test]
        public void AFinishedCrossfade_StopsTheRebuilds_OnceTrackZeroHolds()
        {
            var player = MakePlayer("walk");
            player.Crossfade(0, "wait1", 0.1f);
            for (int i = 0; i < 30; i++) player.InternalPlayer.PrepareFrameData(Dt, true, out _, out _);
            Assert.AreEqual(-1f, player.GetTrackFadeProgress(0), "a tenth-of-a-second fade is over after half a second");

            Assert.IsTrue(player.SetTrackSpeed(0, 0f), "the incoming side's frame stands still");
            player.InternalPlayer.PrepareFrameData(Dt, true, out _, out _);
            Assert.IsTrue(NextFrameSkipped(player), "nothing moves after the fade");
        }

        [Test]
        public void Graphic_CrossfadeOnTrackZero_SwitchesItsCurrentAnimation()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = _ringo;
            _assets.Add(asset);
            var go = new GameObject("TrackTestGraphic");
            _objects.Add(go);
            go.AddComponent<Canvas>();
            go.AddComponent<CanvasRenderer>();
            var graphic = go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = asset;
            graphic.SetAnimation("walk");
            graphic.LoopCount = 1;
            graphic.Play();

            Assert.IsTrue(graphic.Crossfade(0, "wait1", 0.5f));

            Assert.AreEqual("wait1", graphic.CurrentAnimation);
            Assert.AreEqual(1, graphic.GetTrackLoopCount(0), "the graphic puts its loop count back too");
            Assert.IsTrue(graphic.SetAnimation("walk"));
            Assert.AreEqual("walk", graphic.InternalPlayer.CurrentAnimationName);
        }

        // ---- events: who fires, and from whose table ----

        // UserData's anime_1 set up to play once, not yet playing.
        private SpriteStudioPlayer MakeEventsPlayer(List<SpriteStudioUserData> received)
        {
            Assert.IsNotNull(_userData, "UserData.ssab is required.");
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = _userData;
            _assets.Add(asset);
            var go = new GameObject("TrackTestEvents");
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;
            Assert.IsTrue(player.SetAnimation("anime_1"), "UserData has anime_1");
            player.LoopCount = 1;
            player.OnUserData += received.Add;
            return player;
        }

        private static float Step(SpriteStudioPlayer player) => 1f / Mathf.Max(1, player.InternalPlayer.GetFrameRate());

        private static void RunToTheEnd(SpriteStudioPlayer player)
        {
            float step = Step(player);
            for (int i = 0; i < 600 && !player.IsFinished; i++) player.InternalPlayer.Tick(step, false);
        }

        private static List<SpriteStudioUserData> Frame10(List<SpriteStudioUserData> received) =>
            received.FindAll(d => d.FrameNo == 10);

        [Test]
        public void AnOverlay_FiresEvents_OnlyOnceOptedIn()
        {
            var received = new List<SpriteStudioUserData>();
            var player = MakeEventsPlayer(received);
            player.SetTrackAnimation(1, "anime_1");
            player.Play();
            RunToTheEnd(player);
            var once = Frame10(received);
            Assert.AreEqual(1, once.Count, "track 0's key alone by default");
            Assert.AreEqual(player.GetTrackMainSource(0), once[0].Source, "fired by track 0's main source");

            received.Clear();
            var optedIn = MakeEventsPlayer(received);
            optedIn.SetTrackAnimation(1, "anime_1");
            int overlay = optedIn.GetTrackMainSource(1);
            Assert.IsTrue(optedIn.SetSourceEventThreshold(overlay, 1f));
            optedIn.Play();
            RunToTheEnd(optedIn);
            var twice = Frame10(received);
            Assert.AreEqual(2, twice.Count, "the overlay's key too, once opted in");
            var sources = twice.ConvertAll(d => d.Source);
            CollectionAssert.Contains(sources, optedIn.GetTrackMainSource(0), "one from track 0");
            CollectionAssert.Contains(sources, overlay, "one from the overlay");
        }

        [Test]
        public void TheOutgoingSide_FiresByItsTracksThreshold()
        {
            var counts = new List<int>();
            foreach (float threshold in new[] { float.PositiveInfinity, 0.5f })
            {
                var received = new List<SpriteStudioUserData>();
                var player = MakeEventsPlayer(received);
                Assert.IsTrue(player.SetTrackOutgoingEventThreshold(0, threshold));
                float step = Step(player);
                player.Play();
                for (int i = 0; i < 8; i++) player.InternalPlayer.Tick(step, false);
                int outgoing = player.GetTrackMainSource(0);
                Assert.IsTrue(player.Crossfade(0, "anime_1", 10f * step));
                received.Clear();
                for (int i = 0; i < 4; i++) player.InternalPlayer.Tick(step, false);
                counts.Add(Frame10(received).FindAll(d => d.Source == outgoing).Count);
            }
            CollectionAssert.AreEqual(new[] { 0, 1 }, counts,
                "the outgoing side's frame-10 key: silent at +Infinity, once at 0.5");
        }

        [Test]
        public void Events_AfterACrossfadeOnTrackZero_ComeFromTheIncomingSource_AndItsTable()
        {
            var received = new List<SpriteStudioUserData>();
            var player = MakeEventsPlayer(received);
            float step = Step(player);
            player.Play();
            player.InternalPlayer.Tick(step, false);
            Assert.IsTrue(player.Crossfade(0, "anime_1", step));
            for (int i = 0; i < 3; i++) player.InternalPlayer.Tick(step, false);
            Assert.AreEqual(-1f, player.GetTrackFadeProgress(0), "the crossfade has completed");
            int incoming = player.GetTrackMainSource(0);

            received.Clear();
            for (int i = 0; i < 600 && Frame10(received).Count == 0; i++) player.InternalPlayer.Tick(step, false);

            var at10 = Frame10(received);
            Assert.AreEqual(1, at10.Count, "the frame-10 key fired");
            Assert.AreEqual(incoming, at10[0].Source, "from the incoming source");
            Assert.IsTrue(at10[0].HasInt);
            Assert.AreEqual(987654321, at10[0].IntValue, "read from its animation's table");
        }
    }
}
