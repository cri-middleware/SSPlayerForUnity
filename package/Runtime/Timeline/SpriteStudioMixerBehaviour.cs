using UnityEngine.Playables;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// SpriteStudioTrack の mixer。バインドされた SpriteStudioPlayer に対し、最大ウェイトのクリップの
    /// アニメーションを設定し、クリップローカル時間へ決定論的にシークする。
    /// SpriteStudio は複数アニメをブレンドできないため、最も支配的なクリップ 1 つを採用する。
    /// </summary>
    public class SpriteStudioMixerBehaviour : PlayableBehaviour
    {
        private SpriteStudioPlayer _boundPlayer;

        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var player = playerData as SpriteStudioPlayer;
            _boundPlayer = player;
            if (player == null) return;

            // 最大ウェイトのクリップを 1 つ選ぶ (ブレンド不可のため支配的なクリップを採用)。
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

            // どのクリップも区間外 (ギャップ) なら制御を手放す。
            if (best < 0 || bestWeight <= 0f)
            {
                player.ReleaseTimelineControl();
                return;
            }

            var input = (ScriptPlayable<SpriteStudioPlayableBehaviour>)playable.GetInput(best);
            // input.GetTime() はクリップローカル時間 (秒)。Rust 側で時刻基準に決定論評価される。
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
