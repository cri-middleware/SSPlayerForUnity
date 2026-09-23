using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Verifies the structural behavior of built-in audio (SpriteStudioAudioController).
    /// Since real sound cannot play under batchmode/-nographics, this only guarantees AudioSource allocation/stop/pause
    /// and duplicate-playback prevention via the identity key (per part) (playback timing/count depend on dspTime and are out of scope).
    /// loop_num is the play count (1=one-shot / N>=2=N times). &lt;=0 cannot occur per spec -> defensively does not play.
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
            // loop_num <= 0 cannot occur per spec (SS cannot specify an infinite loop). Built-in defensively does not play.
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(10u, 20u, out var clip);
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 10u, 20u, 0), asset, null, 1f);
                ctrl.Play(Ev(1, 10u, 20u, -1), asset, null, 1f);

                Assert.IsNull(go.GetComponent<AudioSource>(), "loop_num <= 0 does not play (does not create an AudioSource)");
            }
            finally { Cleanup(prevGlobal, asset, clip, go); }
        }

        [Test]
        public void SinglePlay_RefireSameKey_DoesNotDuplicate()
        {
            // Retriggering the same sound on the same part prevents duplicate playback (one-shots are deduped too).
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(15u, 25u, out var clip);
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 15u, 25u, 1), asset, null, 1f);    // one-shot
                ctrl.Play(Ev(0, 15u, 25u, 1), asset, null, 1f);    // same key refire -> not stacked, replayed on the single voice

                var sources = go.GetComponents<AudioSource>();
                Assert.AreEqual(1, sources.Length, "A one-shot on the same key should not duplicate; it should stay a single voice");
                Assert.IsFalse(sources[0].loop, "A one-shot should not set loop=true");
            }
            finally { Cleanup(prevGlobal, asset, clip, go); }
        }

        [Test]
        public void FiniteLoop_RefireSameKey_ReusesSingleSource()
        {
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(11u, 21u, out var clip);
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 11u, 21u, 2), asset, null, 1f);    // finite (loopNum=2 -> 2 times)
                ctrl.Play(Ev(0, 11u, 21u, 2), asset, null, 1f);    // refire -> replayed on the same single voice

                var sources = go.GetComponents<AudioSource>();
                Assert.AreEqual(1, sources.Length, "A loop refire on the same key should reuse the same AudioSource");
                Assert.IsTrue(sources[0].loop);
            }
            finally { Cleanup(prevGlobal, asset, clip, go); }
        }

        [Test]
        public void DifferentParts_SameSound_AreIndependent()
        {
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(12u, 22u, out var clip);
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 12u, 22u, 2), asset, null, 1f);
                ctrl.Play(Ev(1, 12u, 22u, 2), asset, null, 1f);   // different part -> different voice

                Assert.AreEqual(2, go.GetComponents<AudioSource>().Length, "Different parts should produce different voices");
            }
            finally { Cleanup(prevGlobal, asset, clip, go); }
        }

        [Test]
        public void StopAll_StopsAndFreesSources()
        {
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(13u, 23u, out var clip);
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 13u, 23u, 2), asset, null, 1f);
                ctrl.StopAll();

                var sources = go.GetComponents<AudioSource>();
                Assert.IsFalse(sources[0].isPlaying, "StopAll should stop the sound");
                Assert.IsNull(sources[0].clip, "A stopped sound should be returned to the pool with clip cleared");
            }
            finally { Cleanup(prevGlobal, asset, clip, go); }
        }

        [Test]
        public void StopAll_StopsEverySound()
        {
            // Teardown takes everything with it -- one-shots and finite loops alike.
            // There is no longer a partial sweep: nothing short of disable / destroy
            // stops a sound that has started.
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(18u, 28u, out var clip);
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 18u, 28u, 1), asset, null, 1f);    // part0: one-shot
                ctrl.Play(Ev(1, 18u, 28u, 2), asset, null, 1f);    // part1: loop (2 times)
                ctrl.StopAll();

                int withClip = go.GetComponents<AudioSource>().Count(s => s.clip != null);
                Assert.AreEqual(0, withClip, "StopAll should clear every voice");
            }
            finally { Cleanup(prevGlobal, asset, clip, go); }
        }

        [Test]
        public void StoppedVoice_IsReused_NotDuplicated()
        {
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(16u, 26u, out var clip);
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 16u, 26u, 2), asset, null, 1f);
                ctrl.StopAll();
                ctrl.Play(Ev(0, 16u, 26u, 2), asset, null, 1f);   // should be reused from the pool

                Assert.AreEqual(1, go.GetComponents<AudioSource>().Length,
                    "Playback after a stop should reuse the pooled AudioSource, not add a new one");
            }
            finally { Cleanup(prevGlobal, asset, clip, go); }
        }

        [Test]
        public void Backend_HandlesEvent_NoBuiltInAudioSource()
        {
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(14u, 24u, out var clip);
            var backend = ScriptableObject.CreateInstance<CountingBackend>();
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 14u, 24u, 1), asset, backend, 1f);

                Assert.AreEqual(1, backend.Calls, "The event should reach the backend");
                Assert.IsNull(go.GetComponent<AudioSource>(), "The backend path should not create a built-in AudioSource");
            }
            finally
            {
                Object.DestroyImmediate(backend);
                Cleanup(prevGlobal, asset, clip, go);
            }
        }

        private static void Cleanup(SpriteStudioAudioBackend prevGlobal, SpriteStudioAnimeAsset asset, AudioClip clip, GameObject go)
        {
            SpriteStudioAudioBackend.Global = prevGlobal;
            Object.DestroyImmediate(asset);
            Object.DestroyImmediate(clip);
            Object.DestroyImmediate(go);
        }

        /// <summary>A backend that only counts the number of calls (does not play).</summary>
        private class CountingBackend : SpriteStudioAudioBackend
        {
            public int Calls;
            public override void PlayAudio(in SpriteStudioAudio ev, SpriteStudioAnimeAsset asset, GameObject source)
            {
                Calls++;
            }
        }
    }
}
