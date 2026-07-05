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
        [Tooltip("Name of the SpriteStudio animation to play back over this clip range (pack name/animation name)")]
        public string AnimationName = "";

        // SpriteStudio cannot blend two skeletal animations, so clip blending/interpolation is not exposed
        // (the mixer deterministically adopts the single highest-weight clip).
        public ClipCaps clipCaps => ClipCaps.None;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<SpriteStudioPlayableBehaviour>.Create(graph);
            playable.GetBehaviour().AnimationName = AnimationName;
            return playable;
        }
    }
}
