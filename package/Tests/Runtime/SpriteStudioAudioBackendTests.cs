using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Verifies the routing of built-in audio playback (SpriteStudioAudioController + SpriteStudioAudioBackend).
    /// Since real sound (PlayOneShot) cannot play under batchmode/-nographics, this only guarantees the override hook's
    /// resolution order (per-object backend > global backend > built-in).
    /// </summary>
    public class SpriteStudioAudioBackendTests
    {
        /// <summary>A backend that only records calls (does not play).</summary>
        private class RecordingBackend : SpriteStudioAudioBackend
        {
            public int Calls;
            public SpriteStudioAudio Last;
            public override void PlayAudio(in SpriteStudioAudio ev, SpriteStudioAnimeAsset asset, GameObject source)
            {
                Calls++;
                Last = ev;
            }
        }

        [Test]
        public void Controller_RoutesToPerObjectBackend_OverGlobal()
        {
            var go = new GameObject("ss-audio-ctrl");
            var perObject = ScriptableObject.CreateInstance<RecordingBackend>();
            var global = ScriptableObject.CreateInstance<RecordingBackend>();
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = global;
                var ctrl = new SpriteStudioAudioController(go);
                var ev = new SpriteStudioAudio { SoundName = "a", SoundNameHash = 2u };

                ctrl.Play(in ev, null, perObject, 1f);

                Assert.AreEqual(1, perObject.Calls, "Should reach the per-object backend");
                Assert.AreEqual(0, global.Calls, "The per-object backend should take priority over the global one");
                Assert.AreEqual(2u, perObject.Last.SoundNameHash);
            }
            finally
            {
                SpriteStudioAudioBackend.Global = prevGlobal;
                Object.DestroyImmediate(perObject);
                Object.DestroyImmediate(global);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Controller_FallsBackToGlobalBackend_WhenNoPerObject()
        {
            var go = new GameObject("ss-audio-ctrl");
            var global = ScriptableObject.CreateInstance<RecordingBackend>();
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = global;
                var ctrl = new SpriteStudioAudioController(go);
                var ev = new SpriteStudioAudio { SoundName = "b", SoundNameHash = 7u };

                ctrl.Play(in ev, null, null, 1f);

                Assert.AreEqual(1, global.Calls, "Without a per-object assignment, it should fall back to the global backend");
                Assert.AreEqual(7u, global.Last.SoundNameHash);
            }
            finally
            {
                SpriteStudioAudioBackend.Global = prevGlobal;
                Object.DestroyImmediate(global);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Controller_BuiltIn_NoBackend_DoesNotThrow_AndAddsNoAudioSourceForNullAsset()
        {
            var go = new GameObject("ss-audio-ctrl");
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);
                var ev = new SpriteStudioAudio { SoundName = "c", SoundNameHash = 1u };

                // No backend + null asset -> the built-in path cannot resolve and is a safe no-op (does not add an AudioSource).
                Assert.DoesNotThrow(() => ctrl.Play(in ev, null, null, 1f));
                Assert.IsNull(go.GetComponent<AudioSource>(), "When it cannot resolve, it should not add an AudioSource");
            }
            finally
            {
                SpriteStudioAudioBackend.Global = prevGlobal;
                Object.DestroyImmediate(go);
            }
        }
    }
}
