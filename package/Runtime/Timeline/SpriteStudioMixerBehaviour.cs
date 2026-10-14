using UnityEngine.Playables;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Mixer for SpriteStudioTrack. On the bound SpriteStudioPlayer, it sets the animation of the
    /// highest-weight clip and deterministically seeks to the clip-local time.
    /// SpriteStudio cannot blend multiple animations, so it adopts the single most dominant clip.
    /// </summary>
    internal class SpriteStudioMixerBehaviour : PlayableBehaviour
    {
        private SpriteStudioPlayer _boundPlayer;

        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var player = playerData as SpriteStudioPlayer;
            _boundPlayer = player;
            if (player == null) return;

            // Pick the single highest-weight clip (blending is impossible, so adopt the dominant clip).
            int inputCount = playable.GetInputCount();
            int best = -1;
            float bestWeight = 0f;
            for (int i = 0; i < inputCount; i++)
            {
                float w = playable.GetInputWeight(i);
                if (w > bestWeight)
                {
                    bestWeight = w;
                    best = i;
                }
            }

            // If every clip is out of range (a gap), release control.
            if (best < 0 || bestWeight <= 0f)
            {
                player.ReleaseTimelineControl();
                return;
            }

            var input = (ScriptPlayable<SpriteStudioPlayableBehaviour>)playable.GetInput(best);
            // input.GetTime() is the clip-local time (seconds). The Rust side evaluates it deterministically against that timestamp.
            player.EvaluateTimelineClip(input.GetBehaviour().AnimationName, input.GetTime());
        }

        public override void OnPlayableDestroy(Playable playable)
        {
            if (_boundPlayer != null)
            {
                _boundPlayer.ReleaseTimelineControl();
                _boundPlayer = null;
            }
        }
    }
}
