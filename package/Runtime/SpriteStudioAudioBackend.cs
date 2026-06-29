using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// 音声再生の差し替え口 (ScriptableObject)。既定では Player/Graphic が内蔵の AudioClip
    /// ワンショット再生を行うが、CRI ADX / Wwise / FMOD 等の自前バックエンドへ流したい場合は
    /// このクラスを継承して PlayAudio を実装し、以下のいずれかで差し替える:
    ///   - プロジェクト全体: 起動時に <see cref="Global"/> へ代入 (全 Player/Graphic に適用)。
    ///   - 個体ごと:        Player/Graphic の "Audio Backend" フィールドに割り当て。
    ///
    /// デザイナは何も設定しなくてよい (null のまま内蔵再生)。上級者は 1 か所差し替えれば済む。
    /// file_path / alias 等が必要なら asset.TryGetSoundInfo() で引ける。
    /// </summary>
    public abstract class SpriteStudioAudioBackend : ScriptableObject
    {
        /// <summary>
        /// プロジェクト全体の既定バックエンド。個体側に明示割り当てが無い場合に使われる。
        /// 起動時に一度設定する想定 (Editor のドメインリロードでリセットされる)。
        /// </summary>
        public static SpriteStudioAudioBackend Global { get; set; }

        /// <summary>
        /// 1 件のオーディオイベントを再生する。
        /// </summary>
        /// <param name="ev">発火したオーディオイベント (alias / hash / loop_num)。</param>
        /// <param name="asset">発火元の AnimeAsset。ResolveAudioClip / TryGetSoundInfo に使える。</param>
        /// <param name="source">発火元の GameObject (3D 定位やプール管理の起点に使える)。</param>
        public abstract void PlayAudio(in SpriteStudioAudio ev, SpriteStudioAnimeAsset asset, GameObject source);
    }
}
