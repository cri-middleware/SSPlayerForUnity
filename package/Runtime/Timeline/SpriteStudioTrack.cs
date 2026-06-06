using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// SpriteStudioPlayer を駆動する専用 Timeline トラック。
    /// クリップごとに SpriteStudio アニメーションを並べて配置でき、シークは Rust コアの
    /// 時刻基準・決定論評価で行われるため、シークバーを前後に動かしても状態が破綻しない。
    /// (バインド対象は SpriteStudioPlayer。Canvas 用 SpriteStudioGraphic は対象外。)
    /// </summary>
    [TrackColor(0.2f, 0.6f, 0.9f)]
    [TrackClipType(typeof(SpriteStudioPlayableAsset))]
    [TrackBindingType(typeof(SpriteStudioPlayer))]
    public class SpriteStudioTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            return ScriptPlayable<SpriteStudioMixerBehaviour>.Create(graph, inputCount);
        }
    }
}
