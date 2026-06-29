using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Player / Graphic に内蔵される音声再生ヘルパ (MonoBehaviour ではない素の C# クラス)。
    /// オーディオイベント 1 件を、差し替え口 (SpriteStudioAudioBackend) があればそちらへ、
    /// 無ければ内蔵の AudioClip ワンショット再生へルーティングする。
    ///
    /// 解決順: 個体バックエンド > グローバルバックエンド > 内蔵 (ResolveAudioClip + PlayOneShot)。
    /// AudioSource は内蔵経路で初めて必要になった時に owner へ自動付与する (音を使わない個体には付けない)。
    /// </summary>
    public sealed class SpriteStudioAudioController
    {
        private readonly GameObject _owner;
        private AudioSource _audioSource;

        public SpriteStudioAudioController(GameObject owner)
        {
            _owner = owner;
        }

        /// <summary>オーディオイベント 1 件を再生する。MVP はワンショット (loop_num は無視)。</summary>
        public void Play(in SpriteStudioAudio ev, SpriteStudioAnimeAsset asset,
                         SpriteStudioAudioBackend backend, float volume)
        {
            // 差し替え口: 個体 > グローバル。どちらかあれば内蔵再生はしない。
            var be = backend != null ? backend : SpriteStudioAudioBackend.Global;
            if (be != null)
            {
                be.PlayAudio(in ev, asset, _owner);
                return;
            }

            // 内蔵: インポート済み AudioClip を解決してワンショット。非対応/未解決は安全に無音。
            var clip = asset != null ? asset.ResolveAudioClip(ev.SoundListNameHash, ev.SoundNameHash) : null;
            if (clip == null) return;

            EnsureAudioSource();
            _audioSource.PlayOneShot(clip, volume);
        }

        private void EnsureAudioSource()
        {
            if (_audioSource == null)
            {
                _audioSource = _owner.GetComponent<AudioSource>();
                if (_audioSource == null)
                {
                    _audioSource = _owner.AddComponent<AudioSource>();
                    _audioSource.playOnAwake = false;
                }
            }
        }
    }
}
