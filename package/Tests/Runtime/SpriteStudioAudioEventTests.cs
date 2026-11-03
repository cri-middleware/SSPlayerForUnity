using System.Collections;
using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Observe that OnAudio fires while an animation with audio (ssaudio) events plays. Audio events
    /// flow through the same passed-event path as UserData / Signal, so this mainly confirms the C#
    /// dispatch side -- OnAudio is the only event kind whose firing had not yet been verified.
    ///
    /// The "Sound" fixture is a pack inside the SDK's overall.sspj (tests/overall/Sound.ssae). Since .ssab is not
    /// committed (it is a build artifact), [UnitySetUp] converts it on demand the same way as RingoPlaybackTests:
    ///   1) If already built into Resources (Player build / cached Editor), use that.
    ///   2) If a generated file exists under Resources, read it directly.
    ///   3) Editor only: convert overall.sspj into a temp directory outside Assets and import "only" the generated
    ///      Sound.ssab into package/Tests/Runtime/Resources/Sound/ (so it gets bundled into the Player build).
    /// This test uses only the SsabData bytes (audio events do not need textures), so even if texture-resolution
    /// warnings appear during import, SsabData is still set and playback/event verification is unaffected.
    /// </summary>
    public class SpriteStudioAudioEventTests
    {
        private const string SoundResourcePath = "Sound/Sound"; // For Resources.Load
        private const string SoundResSsabPath = "../../package/Tests/Runtime/Resources/Sound/Sound.ssab";
        private const string OverallSspjPath = "../../SpriteStudio-SDK/tests/overall/overall.sspj";

        private byte[] _sound;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            // 1) If already built into Resources, use that.
            var asset = Resources.Load<SpriteStudioAnimeAsset>(SoundResourcePath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0)
            {
                _sound = asset.SsabData;
                yield break;
            }

            // 2) If a generated .ssab exists under Resources, read it directly.
            string resSsab = Path.GetFullPath(SoundResSsabPath);
            if (File.Exists(resSsab))
            {
                _sound = File.ReadAllBytes(resSsab);
                yield break;
            }

#if UNITY_EDITOR
            // 3) Editor: convert overall.sspj on demand and import only the generated Sound.ssab into Resources.
            //    The conversion target is a temp directory outside Assets (the other 21 packs are not imported into Unity). Poll
            //    is driven via reflection without waiting for EditorApplication.update (same way as RingoPlaybackTests).
            string sspj = Path.GetFullPath(OverallSspjPath);
            Assert.IsTrue(File.Exists(sspj), $"overall.sspj not found: {sspj}");

            string tmpDir = Path.Combine(Application.temporaryCachePath, "ss_sound_fixture");
            Directory.CreateDirectory(tmpDir);
            string tmpSound = Path.Combine(tmpDir, "Sound.ssab");

            var runnerType = System.Type.GetType("SpriteStudio.Editor.SpriteStudioConversionRunner, SpriteStudio.Editor");
            var enqueue = runnerType?.GetMethod("Enqueue", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            enqueue?.Invoke(null, new object[] { sspj, tmpDir });

            var poll = runnerType?.GetMethod("Poll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            float timeout = 60f; // overall.sspj has many packs, so conversion takes time
            while ((!File.Exists(tmpSound) || !ConversionRunnerProbe.IsIdle(runnerType)) && timeout > 0f)
            {
                poll?.Invoke(null, null);
                yield return null;
                timeout -= Time.deltaTime;
            }

            if (File.Exists(tmpSound))
            {
                // Import only Sound.ssab into Resources for the Player build.
                Directory.CreateDirectory(Path.GetDirectoryName(resSsab));
                File.Copy(tmpSound, resSsab, overwrite: true);

                var assetDatabaseType = System.Type.GetType("UnityEditor.AssetDatabase, UnityEditor");
                assetDatabaseType?.GetMethod("Refresh", new System.Type[0])?.Invoke(null, null);

                // For this session, proceed reliably by reading the bytes directly (do not wait for import to finish).
                _sound = File.ReadAllBytes(resSsab);
            }
#endif
            yield return null;
        }

        [Test]
        public void OnAudio_Fires_WhilePlayingAudioAnimation()
        {
            Assert.IsNotNull(_sound, $"Sound.ssab is required (generated from overall.sspj): {Path.GetFullPath(SoundResSsabPath)}");
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_sound);

                int audio = 0;
                player.OnAudio += a =>
                {
                    audio++;
                    Debug.Log($"[AUDIO] sound={a.SoundName} part={a.PartName} loop={a.LoopNum} frame={player.GetFrame():0.0}");
                };

                // Use the first animation (do not hard-code the name).
                string anim = player.GetAnimationName(0);
                Assert.IsTrue(player.SetupAnimation(anim), $"The first animation '{anim}' should be found");
                player.SetLoop(1); // Play only once to pass through all frames
                player.Play();

                int guard = 0;
                while (player.IsPlaying() && guard++ < 10000) player.Tick(1f / 60f, false);

                Debug.Log($"[AUDIO] summary: audio={audio} animation={anim} guard={guard}");
                Assert.Greater(audio, 0, "OnAudio should fire during a single playthrough of an animation with audio events");
            }
        }

        // The first animation, and the frame of its first audio key, found by playing it through.
        private static void FindFirstAudioKey(byte[] ssab, out string animation, out int keyFrame)
        {
            int found = int.MinValue;
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(ssab);
                player.OnAudio += a => { if (found == int.MinValue) found = a.FrameNo; };
                animation = player.GetAnimationName(0);
                Assert.IsTrue(player.SetupAnimation(animation));
                player.SetLoop(1);
                player.Play();
                int guard = 0;
                while (player.IsPlaying() && found == int.MinValue && guard++ < 10000) player.Tick(1f / 60f, false);
            }
            Assert.AreNotEqual(int.MinValue, found, "The animation should carry an audio key");
            keyFrame = found;
        }

        // The Sound pack as the overall fixture imports it: with the clips beside it resolved, which the copy
        // under Resources/Sound, a lone .ssab, has none of.
        private const string SoundWithClipsResourcePath = "overall/Sound";

        // A player on the Sound pack with its clips, playing its first animation, and the frame of that
        // animation's first audio key.
        private static SpriteStudioPlayer MakePlayingSoundPlayer(out int keyFrame)
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(SoundWithClipsResourcePath);
            Assert.IsTrue(asset != null && asset.Sounds.Count > 0, "The overall fixture's Sound pack, with its clips, is required");
            FindFirstAudioKey(asset.SsabData, out string animation, out keyFrame);
            var player = new GameObject("SoundPlayer").AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;
            Assert.IsTrue(player.SetAnimation(animation));
            player.Play();
            return player;
        }

        [Test]
        public void SeekWhilePlaying_NotifiesTheLandingFramesKey_ButDoesNotPlayIt()
        {
            if (!Application.isPlaying) Assert.Ignore("The built-in playback runs in Play mode only.");
            var player = MakePlayingSoundPlayer(out int keyFrame);
            try
            {
                int notified = 0;
                player.OnAudio += a => { if (a.FrameNo == keyFrame) notified++; };

                player.FrameNo = keyFrame;
                player.PrepareFrame(1f / 60f, out _, out _);   // the next update reports the landing frame's keys

                Assert.Greater(notified, 0, "The landing frame's key is still notified");
                Assert.IsNull(player.GetComponent<AudioSource>(), "but the playhead crossed nothing, so it does not sound");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [Test]
        public void SeekWhileStopped_NotifiesTheLandingFramesKey_ButDoesNotPlayIt()
        {
            if (!Application.isPlaying) Assert.Ignore("The built-in playback runs in Play mode only.");
            var player = MakePlayingSoundPlayer(out int keyFrame);
            try
            {
                int notified = 0;
                player.OnAudio += a => { if (a.FrameNo == keyFrame) notified++; };

                player.Stop();
                player.FrameNo = keyFrame;
                player.PrepareFrame(1f / 60f, out _, out _);   // a stopped player reports the landing frame too

                Assert.Greater(notified, 0, "The landing frame's key is still notified");
                Assert.IsNull(player.GetComponent<AudioSource>(), "but a scrub of a stopped player does not sound");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [Test]
        public void SeekWhilePaused_NotifiesTheLandingFramesKey_ButDoesNotPlayIt()
        {
            if (!Application.isPlaying) Assert.Ignore("The built-in playback runs in Play mode only.");
            var player = MakePlayingSoundPlayer(out int keyFrame);
            try
            {
                int notified = 0;
                player.OnAudio += a => { if (a.FrameNo == keyFrame) notified++; };

                player.Pause();
                player.FrameNo = keyFrame;
                player.PrepareFrame(1f / 60f, out _, out _);

                Assert.Greater(notified, 0, "The landing frame's key is still notified");
                Assert.IsNull(player.GetComponent<AudioSource>(), "but a scrub of a paused player does not sound");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [Test]
        public void PlayAfterASeek_SoundsTheStartFramesKey()
        {
            if (!Application.isPlaying) Assert.Ignore("The built-in playback runs in Play mode only.");
            var player = MakePlayingSoundPlayer(out int keyFrame);
            try
            {
                player.Stop();
                player.FrameNo = keyFrame;
                player.Play();   // starts over from its own frame, the first key's
                player.PrepareFrame(1f / 60f, out _, out _);

                Assert.IsNotNull(player.GetComponent<AudioSource>(), "Play() drops the seek before it, so its start frame sounds");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [Test]
        public void PlayingOntoAKey_SoundsIt_UntilStopAllSounds()
        {
            // The control for the test above: the same key, reached by playing rather than by a seek. The
            // animation's first key sits on its start frame, which the first update after Play reports and plays.
            if (!Application.isPlaying) Assert.Ignore("The built-in playback runs in Play mode only.");
            var player = MakePlayingSoundPlayer(out _);
            try
            {
                player.PrepareFrame(1f / 60f, out _, out _);

                var source = player.GetComponent<AudioSource>();
                Assert.IsNotNull(source, "A key the playhead reaches by playing sounds");
                Assert.IsNotNull(source.clip);

                player.StopAllSounds();
                Assert.IsNull(source.clip, "StopAllSounds silences what the player started");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }
    }
}
