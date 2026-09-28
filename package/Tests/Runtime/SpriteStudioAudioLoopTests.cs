using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Verifies the structural behavior of built-in audio (SpriteStudioAudioController).
    /// Since real sound cannot play under batchmode/-nographics, this only guarantees AudioSource allocation /
    /// stop / reuse, and that a sound already playing is never cut (playback timing/count depend on dspTime and
    /// are out of scope). loop_num is the play count (1 = once / N >= 2 = N times); &lt;= 0 cannot occur per
    /// spec and does not play.
    /// </summary>
    public class SpriteStudioAudioLoopTests
    {
        private static SpriteStudioAnimeAsset MakeAssetWithSound(uint listHash, uint nameHash, out AudioClip clip)
        {
            clip = AudioClip.Create("ss-loop-test", 4410, 1, 44100, false);   // 0.1s dummy
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.Sounds = new List<SpriteStudioAnimeAsset.SoundClipEntry>
            {
                new SpriteStudioAnimeAsset.SoundClipEntry
                {
                    SoundListNameHash = listHash,
                    SoundNameHash = nameHash,
                    Clip = clip,
                },
            };
            return asset;
        }

        private static SpriteStudioAudio Ev(int part, uint listHash, uint nameHash, int loopNum)
            => new SpriteStudioAudio
            {
                PartIndex = part,
                SoundListNameHash = listHash,
                SoundNameHash = nameHash,
                LoopNum = loopNum,
            };

        [Test]
        public void LoopNumZeroOrNegative_DoesNotPlay()
        {
            // loop_num <= 0 cannot occur per spec (SS cannot specify an infinite loop). Built-in does not play it.
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(10u, 20u, out var clip);
            try
            {
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 10u, 20u, 0), asset, 1f);
                ctrl.Play(Ev(1, 10u, 20u, -1), asset, 1f);

                Assert.IsNull(go.GetComponent<AudioSource>(), "loop_num <= 0 does not play (does not create an AudioSource)");
            }
            finally { Cleanup(asset, clip, go); }
        }

        [Test]
        public void OnePlay_DoesNotLoop_AndSeveralPlaysDo()
        {
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(15u, 25u, out var clip);
            try
            {
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 15u, 25u, 1), asset, 1f);
                ctrl.Play(Ev(1, 15u, 25u, 2), asset, 1f);

                var sources = go.GetComponents<AudioSource>();
                Assert.AreEqual(2, sources.Length);
                Assert.IsFalse(sources[0].loop, "One play needs no loop");
                Assert.IsTrue(sources[1].loop, "loop_num 2 loops the clip and ends it after two lengths");
            }
            finally { Cleanup(asset, clip, go); }
        }

        [Test]
        public void RefireWhilePlaying_StartsAnotherVoice_WithoutCuttingTheFirst()
        {
            // A key crossed again while its sound still plays (an animation loop re-passing the frame) overlaps
            // rather than cutting the sound that has started.
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(11u, 21u, out var clip);
            try
            {
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 11u, 21u, 2), asset, 1f);
                var first = go.GetComponent<AudioSource>();
                ctrl.Play(Ev(0, 11u, 21u, 2), asset, 1f);

                var sources = go.GetComponents<AudioSource>();
                Assert.AreEqual(2, sources.Length, "The refire should take a voice of its own");
                Assert.AreSame(clip, first.clip, "The first voice keeps its sound");
            }
            finally { Cleanup(asset, clip, go); }
        }

        [Test]
        public void StopAll_StopsAndFreesSources()
        {
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(13u, 23u, out var clip);
            try
            {
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 13u, 23u, 2), asset, 1f);
                ctrl.StopAll();

                var sources = go.GetComponents<AudioSource>();
                Assert.IsFalse(sources[0].isPlaying, "StopAll should stop the sound");
                Assert.IsNull(sources[0].clip, "A stopped sound should be returned to the pool with clip cleared");
            }
            finally { Cleanup(asset, clip, go); }
        }

        [Test]
        public void StopAll_StopsEverySound()
        {
            // Teardown and the host's StopAllSounds take everything with them -- single plays and repeats alike.
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(18u, 28u, out var clip);
            try
            {
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 18u, 28u, 1), asset, 1f);
                ctrl.Play(Ev(1, 18u, 28u, 2), asset, 1f);
                ctrl.StopAll();

                int withClip = go.GetComponents<AudioSource>().Count(s => s.clip != null);
                Assert.AreEqual(0, withClip, "StopAll should clear every voice");
            }
            finally { Cleanup(asset, clip, go); }
        }

        [Test]
        public void StoppedVoice_IsReused_NotDuplicated()
        {
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(16u, 26u, out var clip);
            try
            {
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 16u, 26u, 2), asset, 1f);
                ctrl.StopAll();
                ctrl.Play(Ev(0, 16u, 26u, 2), asset, 1f);   // should be reused from the pool

                Assert.AreEqual(1, go.GetComponents<AudioSource>().Length,
                    "Playback after a stop should reuse the pooled AudioSource, not add a new one");
            }
            finally { Cleanup(asset, clip, go); }
        }

        private static void Cleanup(SpriteStudioAnimeAsset asset, AudioClip clip, GameObject go)
        {
            Object.DestroyImmediate(asset);
            Object.DestroyImmediate(clip);
            Object.DestroyImmediate(go);
        }
    }
}
