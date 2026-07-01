using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// 内蔵音声 (SpriteStudioAudioController) の構造的挙動を検証する (SDK doc §4 準拠)。
    /// 実音は batchmode/-nographics で鳴らせないため、担保するのは AudioSource の確保/停止/一時停止と
    /// 同一性キー (パーツ単位) による多重再生防止まで (再生タイミング/回数は dspTime 依存のため対象外)。
    /// loop_num は再生回数 (1=単発 / N>=2=N回)。&lt;=0 は仕様上来ない → 防御的に鳴らさない。
    /// </summary>
    public class SpriteStudioAudioLoopTests
    {
        private static SpriteStudioAnimeAsset MakeAssetWithSound(uint listHash, uint nameHash, out AudioClip clip)
        {
            clip = AudioClip.Create("ss-loop-test", 4410, 1, 44100, false);   // 0.1s ダミー
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
            // loop_num <= 0 は仕様上来ない (SS で無限ループ指定不可)。内蔵は防御的に鳴らさない。
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(10u, 20u, out var clip);
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 10u, 20u, 0), asset, null, 1f);
                ctrl.Play(Ev(1, 10u, 20u, -1), asset, null, 1f);

                Assert.IsNull(go.GetComponent<AudioSource>(), "loop_num <= 0 は鳴らさない (AudioSource を作らない)");
            }
            finally { Cleanup(prevGlobal, asset, clip, go); }
        }

        [Test]
        public void SinglePlay_RefireSameKey_DoesNotDuplicate()
        {
            // SDK doc §4: 同一パーツで同じ音声を再トリガーしたら多重再生を防ぐ (単発も dedup)。
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(15u, 25u, out var clip);
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 15u, 25u, 1), asset, null, 1f);    // 単発
                ctrl.Play(Ev(0, 15u, 25u, 1), asset, null, 1f);    // 同一キー再発火 → 重ねず 1 本で鳴らし直し

                var sources = go.GetComponents<AudioSource>();
                Assert.AreEqual(1, sources.Length, "同一キーの単発は多重再生せず 1 本のはず");
                Assert.IsFalse(sources[0].loop, "単発は loop=true にしないはず");
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

                ctrl.Play(Ev(0, 11u, 21u, 2), asset, null, 1f);    // 有限 (loopNum=2 → 2 回)
                ctrl.Play(Ev(0, 11u, 21u, 2), asset, null, 1f);    // 再発火 → 同じ 1 本で鳴らし直し

                var sources = go.GetComponents<AudioSource>();
                Assert.AreEqual(1, sources.Length, "同一キーのループ再発火は同じ AudioSource を使い回すはず");
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
                ctrl.Play(Ev(1, 12u, 22u, 2), asset, null, 1f);   // 別パーツ → 別ボイス

                Assert.AreEqual(2, go.GetComponents<AudioSource>().Length, "パーツが違えば別ボイスになるはず");
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
                Assert.IsFalse(sources[0].isPlaying, "StopAll で音声は止まるはず");
                Assert.IsNull(sources[0].clip, "停止した音声はプールへ返され clip がクリアされるはず");
            }
            finally { Cleanup(prevGlobal, asset, clip, go); }
        }

        [Test]
        public void StopLoops_LeavesSinglePlay()
        {
            // アニメ切替 / 終了 (StopLoops) はループを止めるが、単発は自己終了に任せて残す (ユーザー決定「あ」)。
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(18u, 28u, out var clip);
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);

                ctrl.Play(Ev(0, 18u, 28u, 1), asset, null, 1f);    // part0: 単発
                ctrl.Play(Ev(1, 18u, 28u, 2), asset, null, 1f);    // part1: ループ (2 回)
                ctrl.StopLoops();

                int withClip = go.GetComponents<AudioSource>().Count(s => s.clip != null);
                Assert.AreEqual(1, withClip, "StopLoops はループを止める (clip クリア) が単発は残すはず");
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
                ctrl.Play(Ev(0, 16u, 26u, 2), asset, null, 1f);   // プールから再利用されるはず

                Assert.AreEqual(1, go.GetComponents<AudioSource>().Length,
                    "停止後の再生はプールの AudioSource を再利用し、新規追加しないはず");
            }
            finally { Cleanup(prevGlobal, asset, clip, go); }
        }

        [Test]
        public void PauseAll_ResumeAll_DoNotThrow()
        {
            var go = new GameObject("ss-audio");
            var asset = MakeAssetWithSound(19u, 29u, out var clip);
            var prevGlobal = SpriteStudioAudioBackend.Global;
            try
            {
                SpriteStudioAudioBackend.Global = null;
                var ctrl = new SpriteStudioAudioController(go);
                ctrl.Play(Ev(0, 19u, 29u, 2), asset, null, 1f);

                Assert.DoesNotThrow(() => { ctrl.PauseAll(); ctrl.ResumeAll(); });
                Assert.AreEqual(1, go.GetComponents<AudioSource>().Length, "Pause/Resume はボイスを増減させないはず");
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

                Assert.AreEqual(1, backend.Calls, "イベントは backend へ届くはず");
                Assert.IsNull(go.GetComponent<AudioSource>(), "backend 経路では内蔵 AudioSource を作らないはず");
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

        /// <summary>呼び出し回数だけ数えるバックエンド (再生はしない)。</summary>
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
