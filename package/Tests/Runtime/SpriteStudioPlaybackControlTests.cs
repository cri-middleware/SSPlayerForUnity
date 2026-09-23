using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Verifies the playback-control API added to SpriteStudioPlayer / SpriteStudioGraphic in Tier 1
    /// (IsPlaying / IsPaused / Pause / Stop / FrameNo / TotalFrames /
    ///  FrameRate / Reverse / PingPong / SetAnimationSection).
    ///
    /// These are thin delegations to the internal player (Rust runtime), so we check them at two layers:
    ///   (A) Component layer — delegations that synchronously reflect native state, round-trips, and null safety.
    ///       Thanks to [ExecuteAlways], AddComponent runs OnEnable and executes AnimeAsset assignment through Play
    ///       synchronously, so we can verify deterministically without frame driving (UpdateManager / yield).
    ///   (B) Internal-player layer — advance behavior via manual Tick (Pause halts / Resume carries on at the
    ///       current frame / reverse playback decreases the frame / section settings are reflected). This
    ///       nails down the semantics that the component's Pause etc. delegate to.
    ///
    /// The fixture reuses the existing Ringo. Assumptions that depend on animation length are guarded with Assume
    /// (inconclusive for short animations).
    /// </summary>
    public class SpriteStudioPlaybackControlTests
    {
        private const string RingoAssetPath = "Ringo/Ringo"; // for Resources.Load
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";

        private byte[] _ringo;
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
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            // Destroying the GameObject → OnDisable → _core.Dispose() also releases native resources.
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            foreach (var a in _assets) if (a != null) Object.DestroyImmediate(a);
            _assets.Clear();
        }

        // --- helpers ---

        private SpriteStudioAnimeAsset MakeAsset()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = _ringo;
            _assets.Add(asset);
            return asset;
        }

        // Returns a Player already playing Ringo's "walk" (asset assignment through Play runs synchronously).
        private SpriteStudioPlayer MakePlayingPlayer(string anim = "walk")
        {
            var go = new GameObject("Tier1PlaybackPlayer");
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = MakeAsset();
            player.SetAnimation(anim);
            player.Play();
            return player;
        }

        // ============================================================
        // (A) Component layer: synchronous delegation / round-trips
        // ============================================================

        [Test]
        public void Play_MarksPlaying_NotPaused()
        {
            var player = MakePlayingPlayer();
            Assert.IsTrue(player.IsPlaying, "Should be playing after Play");
            Assert.IsFalse(player.IsPausing, "Should not be paused right after Play");
        }

        [Test]
        public void AfterPlay_TotalFrames_And_FrameRate_ArePositive()
        {
            var player = MakePlayingPlayer();
            Assert.Greater(player.TotalFrames, 0, "Total frame count should be positive");
            Assert.Greater(player.FrameRate, 0, "Frame rate should be positive");
        }

        [Test]
        public void TotalFrames_CountsTheSection_NotFromFrameZero()
        {
            var player = MakePlayingPlayer();
            Assume.That(player.TotalFrames, Is.GreaterThan(4), "walk is too short for a 1..3 section");

            player.SetAnimationSection(1, 3);
            // Both ends inclusive: 1, 2, 3. This used to report `end + 1` (= 4), counting from frame 0
            // whatever the section started at, which is the one shape no sibling Player uses.
            Assert.AreEqual(3, player.TotalFrames);
        }

        [Test]
        public void SpeedScale_ReadsBackTheStopThatANegativeRateBecame()
        {
            var player = MakePlayingPlayer();

            player.SpeedScale = 0.5f;
            Assert.AreEqual(0.5f, player.SpeedScale, 0.0001f, "A positive rate reads back as itself");

            // The runtime clamps a zero or negative rate to a stop — reverse playback is Reverse's
            // business, not the speed's — so the host must be able to see the stop it actually got.
            // Reading a cached -1 back would say the opposite of what playback is doing.
            player.SpeedScale = -1f;
            Assert.AreEqual(0f, player.SpeedScale, 0.0001f, "A negative rate reads back as the stop it became");
        }

        [Test]
        public void IsFinished_LatchesOnCompletion_AndIsNotRaisedByStop()
        {
            var player = MakePlayingPlayer();
            Assert.IsFalse(player.IsFinished, "A fresh play has not finished");

            // Re-arm with a finite count. The runtime resets the loop counter on setup, so the count
            // has to be in effect when Play heads out.
            player.LoopCount = 1;
            player.Play();
            Assert.IsFalse(player.IsFinished, "Play re-arms the latch");

            int guard = 0;
            while (player.IsPlaying && guard++ < 5000) player.InternalPlayer.Tick(1f / 60f, false);
            Assume.That(player.IsPlaying, Is.False, "one loop should complete within the tick budget");

            Assert.IsTrue(player.IsFinished, "Every configured loop has played");

            // Sticky, unlike JustLooped: this is the whole reason for publishing it next to the
            // OnAnimationFinished edge. A caller that was not subscribed can still ask afterwards.
            for (int i = 0; i < 120; i++) player.InternalPlayer.Tick(1f / 60f, false);
            Assert.IsTrue(player.IsFinished, "The state holds until the next play/setup");

            player.Play();
            Assert.IsFalse(player.IsFinished, "Play re-arms it again");
        }

        [Test]
        public void IsFinished_SeparatesACompletedRunFromAStoppedOne()
        {
            // The distinction !IsPlaying cannot make, and the reason substituting it for completion
            // is a defect.
            var player = MakePlayingPlayer();
            player.LoopCount = 1;
            player.Play();

            player.InternalPlayer.Tick(1f / 60f, false);
            player.Stop();

            Assert.IsFalse(player.IsPlaying, "Stop ends playback");
            Assert.IsFalse(player.IsFinished, "...but a commanded stop is not a completion");
        }

        [Test]
        public void GetPartNames_IsIndexAlignedWithFindPartIndex()
        {
            var player = MakePlayingPlayer();
            var names = player.GetPartNames();

            Assume.That(names.Length, Is.GreaterThan(0), "Ringo has parts");

            // Length is the part count -- there is no separate accessor, and this is what makes the
            // list a replacement for one rather than an addition next to it.
            for (int i = 0; i < names.Length; i++)
            {
                if (string.IsNullOrEmpty(names[i])) continue;   // unnamed parts hold their slot
                Assert.AreEqual(i, player.FindPartIndex(names[i]),
                    $"names[{i}] must resolve back to {i}");
            }

            // The array is a copy: handing out the live cache would let a caller corrupt the table the
            // event dispatch path reads.
            names[0] = "clobbered";
            Assert.AreNotEqual("clobbered", player.GetPartNames()[0]);
        }

        [Test]
        public void TryGetPartSkinnedMesh_FlagsSkinnedMeshesButNotBonesNullsOrUnknowns()
        {
            var player = MakePlayingPlayer();

            // Ringo's hand_r is a bone-skinned mesh: its vertices are driven by the arm bones while its own
            // node sits near the root, which is exactly why a tracker on it lands off the art. This both
            // exercises the flag and cross-checks that diagnosis.
            Assert.IsTrue(player.TryGetPartSkinnedMesh("hand_r", out bool handSkinned), "hand_r should resolve");
            Assert.IsTrue(handSkinned, "hand_r is a bone-skinned mesh");

            // A bone (armature) and the root (null) are proper track targets — not skinned meshes.
            Assert.IsTrue(player.TryGetPartSkinnedMesh("bone_14", out bool boneSkinned), "bone_14 should resolve");
            Assert.IsFalse(boneSkinned, "an armature bone is not a skinned mesh");
            Assert.IsTrue(player.TryGetPartSkinnedMesh("root", out bool rootSkinned), "root should resolve");
            Assert.IsFalse(rootSkinned, "a null part is not a skinned mesh");

            // Unknown part: returns false, out defaults to false.
            Assert.IsFalse(player.TryGetPartSkinnedMesh("does_not_exist", out bool unknownSkinned));
            Assert.IsFalse(unknownSkinned);
        }

        [Test]
        public void Pause_SetsPaused_ButStillPlaying()
        {
            var player = MakePlayingPlayer();
            player.Pause();
            Assert.IsTrue(player.IsPausing, "Should be paused after Pause");
            Assert.IsTrue(player.IsPlaying, "The playback session should stay active (IsPlaying=true) even while paused");
        }

        [Test]
        public void Pause_IsIdempotent_AndResumeLiftsIt()
        {
            // Pause is a hold, not a toggle: pausing twice leaves it held, and Resume is what lifts it
            // (Play would rewind). This test used to assert the toggle semantics an older API had.
            var player = MakePlayingPlayer();
            player.Pause();
            player.Pause();
            Assert.IsTrue(player.IsPausing, "Pause is idempotent — a second call must not lift the hold");
            Assert.IsTrue(player.IsPlaying);

            player.Resume();
            Assert.IsFalse(player.IsPausing, "Resume lifts the hold");
            Assert.IsTrue(player.IsPlaying);
        }

        [Test]
        public void SetAnimation_WithTheAnimationAlreadySetUp_ChangesNothing()
        {
            var player = MakePlayingPlayer();
            player.FrameSkipEnabled = false;   // one Tick = one frame
            for (int i = 0; i < 3; i++) player.InternalPlayer.Tick(1f / 60f, false);

            float frame = player.FrameNo;
            Assume.That(frame, Is.GreaterThan(0f), "playback should have advanced before we re-assert");

            Assert.IsTrue(player.SetAnimation("walk"), "re-asserting the current animation still succeeds");

            // A switch with nothing to switch to. Before this was a no-op it ran a full setup_animation,
            // which rewound the playhead and returned the controller to Stopped.
            Assert.AreEqual(frame, player.FrameNo, 0.001f, "the playhead must not rewind");
            Assert.IsTrue(player.IsPlaying, "playback must not be returned to Stopped");
        }

        [Test]
        public void SetAnimation_WithTheAnimationAlreadySetUp_DoesNotReArmTheLoopBudget()
        {
            var player = MakePlayingPlayer();
            player.LoopCount = 2;
            player.FrameSkipEnabled = false;
            player.Play();
            Assume.That(player.InternalPlayer.GetRemainLoops(), Is.EqualTo(2));

            // Burn one whole cycle, so the budget has visibly counted down.
            int total = player.TotalFrames;
            for (int i = 0; i < total + 2; i++) player.InternalPlayer.Tick(1f / 60f, false);
            int remaining = player.InternalPlayer.GetRemainLoops();
            Assume.That(remaining, Is.EqualTo(1), "one cycle should have completed");

            player.SetAnimation("walk");

            // Writing the loop count re-arms the budget, and the old code wrote it on every call. A state
            // machine re-asserting its clip each frame would reset the budget each frame, so a finite
            // LoopCount would never run out.
            Assert.AreEqual(remaining, player.InternalPlayer.GetRemainLoops(),
                "re-asserting the current animation must not restart the loop budget");
        }

        [Test]
        public void SetAnimation_SetsUpAgainAfterAnotherPackIsBound()
        {
            var player = MakePlayingPlayer();
            Assume.That(player.InternalPlayer.CurrentAnimationName, Is.EqualTo("walk"));

            // A different asset — which, being built from the same bytes here, names an animation "walk"
            // too. Nothing is set up on it, so the name matching the PREVIOUS pack's selection must not
            // be taken for "already set up", or the new pack is left bound and unplayable.
            player.AnimeAsset = MakeAsset();
            Assert.IsNull(player.InternalPlayer.CurrentAnimationName, "a load clears what was set up");

            Assert.IsTrue(player.SetAnimation("walk"));
            Assert.AreEqual("walk", player.InternalPlayer.CurrentAnimationName);
            Assert.Greater(player.TotalFrames, 0, "the new pack is playable");
        }

        [Test]
        public void SetAnimation_KeepsTheConfiguredLoopCount()
        {
            var go = new GameObject("LoopRetentionPlayer");
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = MakeAsset();
            player.LoopCount = 1;

            Assert.IsTrue(player.SetAnimation("walk"));
            // Read the runtime, not the component's field: the field is what we just set and would agree
            // with itself either way. setup_animation resets the runtime's own count to the infinite
            // sentinel, so the component has to push its configuration back — and did not, which turned
            // a "play once" into a forever loop on any runtime animation switch.
            Assert.AreEqual(1, player.InternalPlayer.GetLoop(),
                "A runtime SetAnimation must not revert the configured loop count to infinite");

            // Again across a second switch, which is the shape a state machine actually uses.
            Assert.IsTrue(player.SetAnimation("walk"));
            Assert.AreEqual(1, player.InternalPlayer.GetLoop());
        }

        // Assigning the animation-name property (the field the inspector labels "Animation Name") is a plain
        // field write with no side effect: it does NOT re-set-up the runtime, and Play() replays whatever is
        // already set up rather than adopting the field. Only SetAnimation() switches the runtime. This is the
        // asymmetry a QA report hit — the inspector switches on edit (via OnValidate) while the script property
        // does not — so it is pinned here as intended behaviour.
        [Test]
        public void AnimationNameProperty_DoesNotSwitchTheRuntime_OnlySetAnimationDoes()
        {
            var player = MakePlayingPlayer("walk");
            Assume.That(player.InternalPlayer.CurrentAnimationName, Is.EqualTo("walk"),
                "precondition: the player is set up on walk");

            // (1) Writing the property updates the serialized field but leaves the runtime on walk.
            player.CurrentAnimation = "attack1";
            Assert.AreEqual("attack1", player.CurrentAnimation, "the field reads back what was written");
            Assert.AreEqual("walk", player.InternalPlayer.CurrentAnimationName,
                "the property setter has no side effect — the runtime is still on the old animation");

            // (2) Play() replays the currently set-up animation; it does not read the field.
            player.Play();
            Assert.AreEqual("walk", player.InternalPlayer.CurrentAnimationName,
                "Play() restarts the set-up animation (walk); it does not adopt the property value");

            // (3) SetAnimation is the switch.
            Assert.IsTrue(player.SetAnimation("attack1"), "attack1 should be found in Ringo");
            Assert.AreEqual("attack1", player.InternalPlayer.CurrentAnimationName,
                "SetAnimation is what actually switches the runtime animation");
        }

        [Test]
        public void Graphic_SetAnimation_KeepsTheConfiguredLoopCount()
        {
            var go = new GameObject("LoopRetentionGraphic");
            _objects.Add(go);
            go.AddComponent<Canvas>();
            go.AddComponent<CanvasRenderer>();
            var graphic = go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = MakeAsset();
            graphic.LoopCount = 1;

            Assert.IsTrue(graphic.SetAnimation("walk"));
            Assert.AreEqual(1, graphic.InternalPlayer.GetLoop(),
                "The uGUI component owes the same guarantee as the world one");
        }

        [Test]
        public void Stop_ClearsPlaying()
        {
            var player = MakePlayingPlayer();
            player.Stop();
            Assert.IsFalse(player.IsPlaying, "Should not be playing after Stop");
        }

        [Test]
        public void CurrentFrame_Seek_RoundTrips()
        {
            var player = MakePlayingPlayer();
            player.Pause(); // pause so the value doesn't change from advancing
            float target = Mathf.Min(2f, Mathf.Max(0f, player.TotalFrames - 1));
            player.FrameNo = target;
            Assert.AreEqual(target, player.FrameNo, 0.001f, "The seeked value should be readable");
        }

        [Test]
        public void Reverse_Property_RoundTrips()
        {
            var player = MakePlayingPlayer();
            player.PlaybackDirection = SpriteStudioPlaybackDirection.Backward;
            Assert.AreEqual(SpriteStudioPlaybackDirection.Backward, player.PlaybackDirection, "Backward should be reflected");
            player.PlaybackDirection = SpriteStudioPlaybackDirection.Forward;
            Assert.AreEqual(SpriteStudioPlaybackDirection.Forward, player.PlaybackDirection, "Forward should be reflected");
        }

        [Test]
        public void PingPong_Property_RoundTrips()
        {
            var player = MakePlayingPlayer();
            player.PlaybackStyle = SpriteStudioPlaybackStyle.PingPong;
            Assert.AreEqual(SpriteStudioPlaybackStyle.PingPong, player.PlaybackStyle, "PingPong should be reflected");
            player.PlaybackStyle = SpriteStudioPlaybackStyle.Normal;
            Assert.AreEqual(SpriteStudioPlaybackStyle.Normal, player.PlaybackStyle, "Normal should be reflected");
        }

        [Test]
        public void ReverseAndPingPong_AreIndependent()
        {
            // The Reverse / PingPong setters read the orthogonal axis (direction / style) from native and preserve it.
            // Verify that setting one doesn't clobber the other (regression test for the custom logic added in Tier 1).
            var player = MakePlayingPlayer();
            player.PlaybackDirection = SpriteStudioPlaybackDirection.Backward;
            player.PlaybackStyle = SpriteStudioPlaybackStyle.PingPong;
            Assert.AreEqual(SpriteStudioPlaybackDirection.Backward, player.PlaybackDirection, "Setting the style should not clear the direction");
            Assert.AreEqual(SpriteStudioPlaybackStyle.PingPong, player.PlaybackStyle, "Setting the direction should not clear the style");

            player.PlaybackDirection = SpriteStudioPlaybackDirection.Forward;
            Assert.AreEqual(SpriteStudioPlaybackDirection.Forward, player.PlaybackDirection);
            Assert.AreEqual(SpriteStudioPlaybackStyle.PingPong, player.PlaybackStyle, "Clearing the direction should not clear the style");
        }

        [Test]
        public void SetAnimationSection_DoesNotThrow()
        {
            var player = MakePlayingPlayer();
            Assert.DoesNotThrow(() => player.SetAnimationSection(1, 3));
        }

        [Test]
        public void FrameRate_Set_DoesNotThrow()
        {
            var player = MakePlayingPlayer();
            Assert.DoesNotThrow(() => player.FrameRate = 30);
        }

        // ============================================================
        // (A') null safety: return defaults without throwing even when the internal player is uncreated / disposed
        // ============================================================

        [Test]
        public void Controls_OnAssetlessPlayer_ReturnDefaults_AndDoNotThrow()
        {
            var go = new GameObject("Tier1Assetless");
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>(); // AnimeAsset not set → animation not set up

            // Even when never played / never loaded, the state accessors return sane defaults without breaking.
            Assert.IsFalse(player.IsPlaying);
            Assert.IsFalse(player.IsPausing);
            Assert.AreEqual(0, player.FrameRate);
            Assert.AreEqual(0f, player.FrameNo, 0.001f);
            Assert.AreEqual(SpriteStudioPlaybackDirection.Forward, player.PlaybackDirection);
            Assert.AreEqual(SpriteStudioPlaybackStyle.Normal, player.PlaybackStyle);
            // TotalFrames can fall back to the native default (end_frame=0 → 1) when not loaded, so we don't
            // assert its value — only that the getters/setters don't throw.

            Assert.DoesNotThrow(() =>
            {
                _ = player.TotalFrames;
                player.Pause(); player.Pause(); player.Stop();
                player.FrameNo = 5f; player.FrameRate = 30;
                player.PlaybackDirection = SpriteStudioPlaybackDirection.Backward; player.PlaybackStyle = SpriteStudioPlaybackStyle.PingPong;
                player.SetAnimationSection(0, 1);
            });
        }

        [Test]
        public void Controls_AfterDisable_DoNotThrow_AndReturnDefaults()
        {
            var player = MakePlayingPlayer();
            player.gameObject.SetActive(false); // OnDisable → _core.Dispose() → Internal=null

            Assert.IsFalse(player.IsPlaying, "Should not be playing after disposal");
            Assert.AreEqual(0, player.TotalFrames);

            Assert.DoesNotThrow(() =>
            {
                player.Pause(); player.Pause(); player.Stop();
                _ = player.FrameNo; player.FrameNo = 1f;
                _ = player.PlaybackDirection; player.PlaybackDirection = SpriteStudioPlaybackDirection.Backward;
                player.SetAnimationSection(0, 1);
            });
        }

        // ============================================================
        // (A'') Smoke test that the Graphic (uGUI) version exposes the same API
        // ============================================================

        [Test]
        public void Graphic_PlayPauseStop_StateRoundTrips()
        {
            var go = new GameObject("Tier1PlaybackGraphic");
            _objects.Add(go);
            go.AddComponent<Canvas>();
            go.AddComponent<CanvasRenderer>();
            var graphic = go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = MakeAsset();
            graphic.SetAnimation("walk");
            graphic.Play();

            Assert.IsTrue(graphic.IsPlaying, "Should be playing after Play");
            Assert.Greater(graphic.TotalFrames, 0);

            graphic.Pause();
            Assert.IsTrue(graphic.IsPausing);

            graphic.Stop();
            Assert.IsFalse(graphic.IsPlaying);

            graphic.PlaybackDirection = SpriteStudioPlaybackDirection.Backward;
            Assert.AreEqual(SpriteStudioPlaybackDirection.Backward, graphic.PlaybackDirection);
        }

        // ============================================================
        // (B) Internal-player layer: advance behavior via manual Tick (deterministic)
        // ============================================================

        [Test]
        public void Internal_Pause_Halts_And_Resume_ContinuesFromCurrentFrame()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                Assert.IsTrue(player.SetupAnimation("walk"), "The walk animation should be found");
                player.SetLoop(-1);          // avoid stopping/rewinding at the range ends; verify with monotonic increase
                player.Play();
                player.SetSkipFrames(false); // 1 Tick = 1 frame (advance deterministically by frame count)

                int total = player.GetTotalFrames();
                int step = Mathf.Max(2, total / 5);
                Assume.That(total, Is.GreaterThan(step * 2), "walk is too short (this test needs a length that doesn't loop within step*2 frames)");

                for (int i = 0; i < step; i++) player.Tick(1f / 60f, false);
                float f0 = player.GetFrame();
                Assert.Greater(f0, 0f, "Playback should advance the frame");

                // No advance while paused (native pause() raises is_pausing and holds the timeline)
                player.Pause();
                for (int i = 0; i < step; i++) player.Tick(1f / 60f, false);
                Assert.AreEqual(f0, player.GetFrame(), 0.5f, "The frame should not advance while paused");

                // Resume: play() rewinds to the start so it can't be used. resume() lifts the hold while
                // keeping the current frame (the same path the component's Resume() delegates to).
                player.Resume();
                for (int i = 0; i < step; i++) player.Tick(1f / 60f, false);
                Assert.Greater(player.GetFrame(), f0, "Resume should continue from the current frame (not return to the start)");
            }
        }

        [Test]
        public void Internal_Reverse_DecreasesFrame()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                Assert.IsTrue(player.SetupAnimation("walk"));
                player.SetLoop(-1);
                player.Play();
                player.SetSkipFrames(false);

                int total = player.GetTotalFrames();
                int mid = Mathf.Max(3, total / 2);
                Assume.That(total, Is.GreaterThan(mid), "walk is too short (needs a length longer than mid)");
                player.SetFrame(mid);

                player.SetPlaybackStyle(1, 0); // 1=Backward, 0=Normal
                float before = player.GetFrame();
                for (int i = 0; i < 2; i++) player.Tick(1f / 60f, false);
                Assert.Less(player.GetFrame(), before, "The frame should decrease during reverse playback");
            }
        }

        [Test]
        public void Internal_SetAnimationSection_UpdatesBounds()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                Assert.IsTrue(player.SetupAnimation("walk"));

                int total = player.GetTotalFrames();
                int end = Mathf.Min(3, total - 1);
                Assume.That(end, Is.GreaterThan(1), "walk is too short (section verification needs at least 4 frames)");

                player.SetAnimationSection(1, end);
                Assert.AreEqual(1, player.GetAnimationSectionStart(), "The section start should be reflected");
                Assert.AreEqual(end, player.GetAnimationSectionEnd(), "The section end should be reflected");
            }
        }
    }
}
