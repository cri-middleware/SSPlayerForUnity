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
            if (Internal == null)
            {
                Internal = new SpriteStudioInternalPlayer { SubFrameEnabled = subFrameEnabled };
            }

            // Self-heal: load the ssab as soon as the asset is actually available. On an Editor restart
            // the referenced .ssab can still be re-importing when OnEnable first runs (asset / SsabData
            // momentarily null); the previous `if (Internal != null) return` guard then left the empty
            // Internal stuck forever. Calling EnsureCreated again (the edit-preview retry, or the next
            // reload) now picks the ssab up once it is ready. LoadSsab is safe to call repeatedly.
            if (!Internal.HasSsab && asset != null && asset.SsabData != null)
            {
                Internal.LoadSsab(asset.SsabData, asset.ResolveExternalSsab);
            }
        }

        public void ReloadAsset(SpriteStudioAnimeAsset asset)
        {
            if (Internal != null && asset != null && asset.SsabData != null)
            {
                Internal.LoadSsab(asset.SsabData, asset.ResolveExternalSsab);
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
        public void Play(string animationName, int loopCount, float speedScale, bool skipFrames, string logContext)
        {
            if (Internal == null || string.IsNullOrEmpty(animationName)) return;

            if (Internal.SetupAnimation(animationName))
            {
                // FFI 契約: direction 0=Forward / 非0=Backward, style 0=Normal / 非0=PingPong
                Internal.SetPlaybackStyle(0, 0); // Forward, Normal
                Internal.SetLoop(loopCount);  // -1 = 無限ループ / 1 = 1回 / n = n回
                Internal.SetSpeed(speedScale);
                // skip_frames も Rust の setup() が既定 (true) へ戻すため、loop/speed と同じくここで再適用する。
                Internal.SetSkipFrames(skipFrames);
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
