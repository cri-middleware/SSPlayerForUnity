using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// SpriteStudioPlayer (MeshRenderer) と SpriteStudioGraphic (uGUI) で共通する再生ロジック。
    /// 両者は基底クラスが異なる (MonoBehaviour vs Graphic) ため継承で共有できないので、
    /// コンポジションで各コンポーネントがこのコアを保持して委譲する。
    /// 直列化される設定 (アニメ名・ループ等) は Unity のシリアライズ互換のため各コンポーネント側に残す。
    /// </summary>
    internal sealed class SpriteStudioPlaybackCore
    {
        /// <summary>内部プレイヤー (FFI コンテキスト)。未生成/破棄後は null。</summary>
        public SpriteStudioInternalPlayer Internal { get; private set; }

        public void EnsureCreated(SpriteStudioAnimeAsset asset, bool subFrameEnabled)
        {
            if (Internal != null) return;

            Internal = new SpriteStudioInternalPlayer { SubFrameEnabled = subFrameEnabled };
            if (asset != null && asset.SsabData != null)
            {
                Internal.LoadSsab(asset.SsabData);
            }
        }

        public void ReloadAsset(SpriteStudioAnimeAsset asset)
        {
            if (Internal != null && asset != null && asset.SsabData != null)
            {
                Internal.LoadSsab(asset.SsabData);
            }
        }

        public void Dispose()
        {
            if (Internal != null)
            {
                Internal.Dispose();
                Internal = null;
            }
        }

        /// <summary>指定アニメーションの再生を開始する。logContext は警告メッセージのタグ。</summary>
        public void Play(string animationName, bool loop, float speedScale, string logContext)
        {
            if (Internal == null || string.IsNullOrEmpty(animationName)) return;

            if (Internal.SetupAnimation(animationName))
            {
                Internal.SetPlaybackStyle(1, 0); // Forward, Normal
                Internal.SetLoop(loop ? -1 : 1);  // -1 = 無限ループ, 1 = 1回
                Internal.SetSpeed(speedScale);
                Internal.Play();
            }
            else
            {
                Debug.LogWarning($"[{logContext}] Failed to setup animation: {animationName}");
            }
        }

        /// <summary>アニメーションを進め、メッシュ構築ジョブをスケジュールする (Job の完了待ちはしない)。</summary>
        public void TickAndSchedule(float deltaTime, bool subFrameEnabled)
        {
            if (Internal == null) return;
            Internal.Tick(deltaTime, subFrameEnabled);
            Internal.ScheduleMeshUpdate(deltaTime);
        }
    }
}
