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
            while (!File.Exists(tmpSound) && timeout > 0f)
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

        /// <summary>
        /// Confirms that AnimeAsset.TryGetSoundInfo() can look up ssab sound metadata (alias / file_path / duration)
        /// using the hash key of a sound that actually fired. A round-trip verification of the opt-in accessor that
        /// supplements file_path etc. not carried by the firing payload (addresses only). Unknown hashes return false.
        /// </summary>
        [Test]
        public void TryGetSoundInfo_ResolvesMetadata_ForFiredSound()
        {
            Assert.IsNotNull(_sound, $"Sound.ssab is required: {Path.GetFullPath(SoundResSsabPath)}");

            // Capture one hash of a sound that actually fired (a key guaranteed to exist in the ssab).
            SpriteStudioAudio captured = default;
            bool got = false;
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_sound);
                player.OnAudio += a => { if (!got) { captured = a; got = true; } };
                string anim = player.GetAnimationName(0);
                Assert.IsTrue(player.SetupAnimation(anim), $"The first animation '{anim}' should be found");
                player.SetLoop(1);
                player.Play();
                int guard = 0;
                while (player.IsPlaying() && guard++ < 10000) player.Tick(1f / 60f, false);
            }
            Assert.IsTrue(got, "At least one audio event should be captured");

            // From an AnimeAsset loaded with the same ssab, that sound's metadata should be resolvable.
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            try
            {
                asset.SsabData = _sound;

                bool ok = asset.TryGetSoundInfo(captured.SoundListNameHash, captured.SoundNameHash, out var info);
                Assert.IsTrue(ok, "The fired sound's hash should exist in the ssab's SoundList");
                Assert.IsFalse(string.IsNullOrEmpty(info.FilePath), "file_path should be retrievable");
                Assert.IsFalse(string.IsNullOrEmpty(info.Alias), "alias should be retrievable");
                // The firing payload's SoundName is diagnostic (optional), so verify the match only if present.
                if (!string.IsNullOrEmpty(captured.SoundName))
                    Assert.AreEqual(captured.SoundName, info.Alias, "alias should match the firing payload's SoundName");
                Debug.Log($"[AUDIO] TryGetSoundInfo: alias={info.Alias} file={info.FilePath} ms={info.TimeTotalMs}");

                // An unknown hash returns false, and out is default.
                Assert.IsFalse(asset.TryGetSoundInfo(0xDEADBEEFu, 0xCAFEBABEu, out var none),
                    "An unknown hash should return false");
                Assert.IsNull(none.FilePath);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
