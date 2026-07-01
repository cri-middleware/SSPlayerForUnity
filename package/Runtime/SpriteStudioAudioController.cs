using System.Collections.Generic;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Player / Graphic に内蔵される音声再生ヘルパ (MonoBehaviour ではない素の C# クラス)。
    /// オーディオイベント 1 件を、差し替え口 (SpriteStudioAudioBackend) があればそちらへ、
    /// 無ければ内蔵の AudioClip 再生へルーティングする (SDK doc 11_optional_features.ja.md §4 準拠)。
    ///
    /// 解決順: 個体バックエンド > グローバルバックエンド > 内蔵 (ResolveAudioClip)。
    /// 内蔵再生は**パーツ単位でアクティブな音声を管理**する。同一性キー = (PartIndex,
    /// SoundListNameHash+SoundNameHash) で 1 キーにつき最大 1 ボイス。同じキーが再発火したら
    /// 直前を停止して鳴らし直す (多重再生を防ぐ)。loop_num で回数を決める:
    ///   * &lt;= 0 (0・負数) : 無限ループ。
    ///   * 1             : 単発 (1 回再生)。
    ///   * &gt;= 2       : 有限 N 回再生。
    /// AudioSource は必要時に owner へ自動付与し、終了後はプールで再利用する。
    /// 逆再生では鳴らさない (順再生フィルタは呼び出し側 Player/Graphic が担当)。
    /// </summary>
    public sealed class SpriteStudioAudioController
    {
        private readonly GameObject _owner;

        // パーツ単位のアクティブ音声: 同一性キー -> ボイス。1 キーにつき最大 1 ボイス。
        private readonly Dictionary<VoiceKey, Voice> _voices = new Dictionary<VoiceKey, Voice>();
        private readonly Stack<AudioSource> _pool = new Stack<AudioSource>();   // 返却済み AudioSource の再利用
        private List<VoiceKey> _scratch;                                        // 列挙中削除を避ける一時バッファ

        public SpriteStudioAudioController(GameObject owner)
        {
            _owner = owner;
        }

        /// <summary>オーディオイベント 1 件を再生する (内蔵経路はパーツ単位で dedup)。</summary>
        public void Play(in SpriteStudioAudio ev, SpriteStudioAnimeAsset asset,
                         SpriteStudioAudioBackend backend, float volume)
        {
            // 差し替え口: 個体 > グローバル。どちらかあれば内蔵再生はしない (回数管理も backend の責務)。
            var be = backend != null ? backend : SpriteStudioAudioBackend.Global;
            if (be != null)
            {
                be.PlayAudio(in ev, asset, _owner);
                return;
            }

            // 内蔵: インポート済み AudioClip を解決。非対応/未解決は安全に無音。
            var clip = asset != null ? asset.ResolveAudioClip(ev.SoundListNameHash, ev.SoundNameHash) : null;
            if (clip == null) return;

            ReclaimFinished();   // 終了済みボイスを遅延回収 (毎フレーム tick 不要)

            var key = new VoiceKey(ev.PartIndex, ev.SoundListNameHash, ev.SoundNameHash);
            if (_voices.TryGetValue(key, out var voice))
            {
                // 再発火 (同一キー): 直前を停止して鳴らし直す (SDK doc §4・多重再生防止)。
                StartVoice(voice, clip, volume, ev.LoopNum);
                return;
            }

            var v = new Voice { Source = Rent() };
            StartVoice(v, clip, volume, ev.LoopNum);
            _voices[key] = v;
        }

        /// <summary>ループ音を停止する (アニメ切替で呼ぶ)。単発は自己終了に任せて止めない。</summary>
        public void StopLoops() => Sweep(loopsOnly: true);

        /// <summary>全音声を停止する (無効化 / 破棄で呼ぶ)。</summary>
        public void StopAll() => Sweep(loopsOnly: false);

        /// <summary>再生中の全音声を一時停止する (アニメ pause と同期・SDK doc §4)。</summary>
        public void PauseAll()
        {
            foreach (var kv in _voices) kv.Value.Source.Pause();
        }

        /// <summary>一時停止した音声を再開する (アニメ resume / play と同期・SDK doc §4)。</summary>
        public void ResumeAll()
        {
            foreach (var kv in _voices) kv.Value.Source.UnPause();
        }

        // --- 内部 -------------------------------------------------------------

        // ボイスを loop_num に応じて (再) 開始する。既存 Source を stop してから鳴らし直す。
        private static void StartVoice(Voice v, AudioClip clip, float volume, int loopNum)
        {
            var src = v.Source;
            src.Stop();
            src.clip = clip;
            src.volume = volume;
            double dsp = AudioSettings.dspTime;

            if (loopNum <= 0)
            {
                // 無限ループ。
                src.loop = true;
                src.PlayScheduled(dsp);
                v.Infinite = true;
                v.PlaysOnce = false;
                v.EndDsp = double.PositiveInfinity;
            }
            else if (loopNum == 1)
            {
                // 単発 (1 回再生)。自己終了。
                src.loop = false;
                src.PlayScheduled(dsp);
                v.Infinite = false;
                v.PlaysOnce = true;
                v.EndDsp = dsp + (double)clip.length;
            }
            else
            {
                // 有限 N 回再生 (N >= 2)。N 長で終了予約。
                src.loop = true;
                src.PlayScheduled(dsp);
                double end = dsp + (double)clip.length * loopNum;
                src.SetScheduledEndTime(end);
                v.Infinite = false;
                v.PlaysOnce = false;
                v.EndDsp = end;
            }
        }

        // 予約終了を過ぎたボイス (単発・有限) を回収してプールへ返す。要求時にだけ掃くので idle コストは無い。
        private void ReclaimFinished()
        {
            if (_voices.Count == 0) return;
            EnsureScratch();

            double now = AudioSettings.dspTime;
            foreach (var kv in _voices)
            {
                if (!kv.Value.Infinite && now >= kv.Value.EndDsp)   // 無限は自然終了しない
                    _scratch.Add(kv.Key);
            }
            FreeScratch();
        }

        // loopsOnly=true ならループ (単発以外) だけを、false なら全ボイスを停止してプールへ返す。
        private void Sweep(bool loopsOnly)
        {
            if (_voices.Count == 0) return;
            EnsureScratch();

            foreach (var kv in _voices)
            {
                if (loopsOnly && kv.Value.PlaysOnce) continue;   // 単発は残す (自己終了)
                _scratch.Add(kv.Key);
            }
            FreeScratch();
        }

        private void EnsureScratch()
        {
            if (_scratch == null) _scratch = new List<VoiceKey>();
            _scratch.Clear();
        }

        private void FreeScratch()
        {
            for (int i = 0; i < _scratch.Count; i++)
            {
                var k = _scratch[i];
                Free(_voices[k].Source);
                _voices.Remove(k);
            }
        }

        private AudioSource Rent()
        {
            AudioSource src = _pool.Count > 0 ? _pool.Pop() : null;
            if (src == null)
            {
                src = _owner.AddComponent<AudioSource>();
                src.playOnAwake = false;
            }
            return src;
        }

        private void Free(AudioSource src)
        {
            if (src == null) return;
            src.Stop();
            src.clip = null;
            _pool.Push(src);
        }

        // (PartIndex, (SoundListNameHash << 32) | SoundNameHash) を同一性キーにする。
        // IEquatable 実装で Dictionary キー使用時のボクシングを避ける (ゼロ GC)。
        private readonly struct VoiceKey : System.IEquatable<VoiceKey>
        {
            private readonly int _part;
            private readonly ulong _sound;

            public VoiceKey(int part, uint listHash, uint nameHash)
            {
                _part = part;
                _sound = ((ulong)listHash << 32) | nameHash;
            }

            public bool Equals(VoiceKey o) => _part == o._part && _sound == o._sound;
            public override bool Equals(object o) => o is VoiceKey k && Equals(k);
            public override int GetHashCode() => (_part * 397) ^ _sound.GetHashCode();
        }

        private sealed class Voice
        {
            public AudioSource Source;
            public bool Infinite;    // loop_num <= 0
            public bool PlaysOnce;   // loop_num == 1 (単発。StopLoops では止めない)
            public double EndDsp;    // 単発/有限の予約終了 dspTime。無限は +∞。
        }
    }
}
