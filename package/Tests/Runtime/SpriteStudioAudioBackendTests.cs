using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// 内蔵オーディオ再生のルーティング (SpriteStudioAudioController + SpriteStudioAudioBackend) を検証する。
    /// 実音 (PlayOneShot) は batchmode/-nographics で鳴らせないため、ここで担保するのは差し替え口の
    /// 解決順 (個体バックエンド > グローバルバックエンド > 内蔵) まで。
    /// </summary>
    public class SpriteStudioAudioBackendTests
    {
        /// <summary>呼び出しを記録するだけのバックエンド (再生はしない)。</summary>
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

                Assert.AreEqual(1, perObject.Calls, "個体バックエンドへ届くはず");
                Assert.AreEqual(0, global.Calls, "個体バックエンドがグローバルより優先されるはず");
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

                Assert.AreEqual(1, global.Calls, "個体指定が無ければグローバルへフォールバックするはず");
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

                // バックエンド無し + asset null → 内蔵経路は解決できず安全に no-op (AudioSource も付けない)。
                Assert.DoesNotThrow(() => ctrl.Play(in ev, null, null, 1f));
                Assert.IsNull(go.GetComponent<AudioSource>(), "解決できない時は AudioSource を付けないはず");
            }
            finally
            {
                SpriteStudioAudioBackend.Global = prevGlobal;
                Object.DestroyImmediate(go);
            }
        }
    }
}
