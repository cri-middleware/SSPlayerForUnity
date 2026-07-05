using System;
using UnityEngine.Playables;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// PlayableBehaviour corresponding to a single clip on a SpriteStudioTrack.
    /// Holds the name of the SpriteStudio animation to play back (the actual driving is done by SpriteStudioMixerBehaviour).
    /// </summary>
    [Serializable]
    public class SpriteStudioPlayableBehaviour : PlayableBehaviour
    {
        public string AnimationName;
    }
}
