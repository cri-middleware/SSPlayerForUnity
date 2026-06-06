using System;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// SpriteStudioTrack に配置するクリップ。この区間で再生する SpriteStudio アニメーション名を指定する。
    /// </summary>
    [Serializable]
    public class SpriteStudioPlayableAsset : PlayableAsset, ITimelineClipAsset
    {
        [Tooltip("このクリップ区間で再生する SpriteStudio アニメーション名 (パック名/アニメ名)")]
        public string AnimationName = "";

        // SpriteStudio は 2 つのスケルタルアニメをブレンドできないため、クリップのブレンド/補間は公開しない
        // (mixer は最大ウェイトのクリップ 1 つを決定論的に採用する)。
        public ClipCaps clipCaps => ClipCaps.None;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<SpriteStudioPlayableBehaviour>.Create(graph);
            playable.GetBehaviour().AnimationName = AnimationName;
            return playable;
        }
    }
}
