using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Dedicated Timeline track that drives a SpriteStudioPlayer.
    /// SpriteStudio animations can be laid out clip by clip, and seeking is performed by the Rust core's
    /// timestamp-based, deterministic evaluation, so scrubbing the seek bar back and forth never corrupts state.
    /// (The bind target is SpriteStudioPlayer. The Canvas-side SpriteStudioGraphic is not supported.)
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
