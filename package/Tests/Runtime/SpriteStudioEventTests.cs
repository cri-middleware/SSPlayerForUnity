using System.Collections;
using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Verifies the SSAB event-polling lifecycle (finished / looped). Uses the existing Ringo, and in particular
    /// confirms that OnAnimationFinished fires exactly once at the moment playback ends
    /// (regression test for edge detection against is_end_frame_reached getting stuck / double-firing on landing + end).
    /// </summary>
    public class SpriteStudioEventTests
    {
        private const string RingoAssetPath = "Ringo/Ringo"; // for Resources.Load
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";

        private byte[] _ringo;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            // The converted Ringo is in Resources. If not present, read directly from the file.
            var ringoAsset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            if (ringoAsset != null && ringoAsset.SsabData != null && ringoAsset.SsabData.Length > 0)
            {
                _ringo = ringoAsset.SsabData;
            }
            else
            {
                string p = Path.GetFullPath(RingoSsabPath);
                _ringo = File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
            yield return null;
        }

        /// <summary>Advance with Tick until playback stops (= is_playing becomes false).</summary>
        private static void DriveToEnd(SpriteStudioInternalPlayer player, int maxTicks = 5000)
        {
            int guard = 0;
            while (player.IsPlaying() && guard++ < maxTicks) player.Tick(1f / 60f, false);
        }

        [Test]
        public void OnAnimationFinished_FiresExactlyOnce_WhenPlayOnceCompletes()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                int finished = 0;
                player.OnAnimationFinished += () => finished++;

                Assert.IsTrue(player.SetupAnimation("walk"), "The walk animation should be found");
                player.SetLoop(1);   // play only once
                player.Play();

                DriveToEnd(player);
                Assert.IsFalse(player.IsPlaying(), "A single playback should complete and stop");

                // Should not re-fire when advanced further after stopping (edge-detection regression test. Without
                // detection, is_end_frame_reached gets stuck and fires every frame, and even a single playback
                // would fire twice on landing + end).
                for (int i = 0; i < 180; i++) player.Tick(1f / 60f, false);

                Assert.AreEqual(1, finished, "OnAnimationFinished should fire exactly once on completion");
            }
        }

        [Test]
        public void OnAnimationFinished_DoesNotFire_DuringInfiniteLoop()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                int finished = 0;
                player.OnAnimationFinished += () => finished++;

                Assert.IsTrue(player.SetupAnimation("walk"));
                player.SetLoop(-1);  // infinite loop
                player.Play();
                for (int i = 0; i < 600; i++) player.Tick(1f / 60f, false);

                Assert.AreEqual(0, finished, "OnAnimationFinished should not fire during an infinite loop");
                Assert.IsTrue(player.IsPlaying(), "An infinite loop should keep playing");
            }
        }

        [Test]
        public void OnAnimationLooped_Fires_DuringInfiniteLoop()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                int looped = 0;
                player.OnAnimationLooped += () => looped++;

                Assert.IsTrue(player.SetupAnimation("walk"));
                player.SetLoop(-1);
                player.Play();
                int guard = 0;
                while (looped < 1 && guard++ < 5000) player.Tick(1f / 60f, false);

                Assert.GreaterOrEqual(looped, 1, "OnAnimationLooped should fire when crossing a loop boundary");
            }
        }

        [Test]
        public void Events_DoNotFire_WhenNeverPlayed()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_ringo);
                int finished = 0, looped = 0;
                player.OnAnimationFinished += () => finished++;
                player.OnAnimationLooped += () => looped++;

                Assert.IsTrue(player.SetupAnimation("walk"));
                // Don't call Play() (is_playing stays false).
                for (int i = 0; i < 120; i++) player.Tick(1f / 60f, false);

                Assert.AreEqual(0, finished, "OnAnimationFinished should not fire when never played");
                Assert.AreEqual(0, looped, "OnAnimationLooped should not fire when never played");
            }
        }
    }
}
