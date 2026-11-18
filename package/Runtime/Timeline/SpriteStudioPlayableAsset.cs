using System;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// A clip placed on a SpriteStudioTrack. Specifies the name of the SpriteStudio animation to play back over this range.
    /// </summary>
    [Serializable]
    public class SpriteStudioPlayableAsset : PlayableAsset, ITimelineClipAsset
    {
        [Tooltip("Name of the animation to play over this clip range, as listed in the asset.")]
        public string AnimationName = "";

        // Clips are not blended on the Timeline: the mixer plays the single highest-weight clip.
        public ClipCaps clipCaps => ClipCaps.None;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<SpriteStudioPlayableBehaviour>.Create(graph);
            playable.GetBehaviour().AnimationName = AnimationName;
            return playable;
        }
    }
}
