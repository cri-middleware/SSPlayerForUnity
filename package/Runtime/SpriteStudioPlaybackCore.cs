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

        // The asset whose ssab is currently loaded into Internal. Used to detect an inspector asset swap so
        // EnsureCreated reloads (the inspector writes the serialized field directly, bypassing the setter).
        private SpriteStudioAnimeAsset _loadedAsset;

        public void EnsureCreated(SpriteStudioAnimeAsset asset, bool subFrameEnabled)
        {
            if (Internal == null)
            {
                Internal = new SpriteStudioInternalPlayer { SubFrameEnabled = subFrameEnabled };
            }

            // Load the ssab when (a) none is loaded yet, or (b) the referenced asset changed. (a) is the
            // self-heal case: on an Editor restart the referenced .ssab can still be re-importing when
            // OnEnable first runs (asset / SsabData momentarily null), so we retry until it is ready.
            // (b) handles an inspector asset swap, which bypasses the C# property setter. LoadSsab is safe
            // to call repeatedly.
            if (asset != null && asset.SsabData != null
                && (!Internal.HasSsab || !ReferenceEquals(asset, _loadedAsset)))
            {
                Internal.LoadSsab(asset.SsabData, asset.ResolveExternalSsab);
                _loadedAsset = asset;
            }
        }

        public void ReloadAsset(SpriteStudioAnimeAsset asset)
        {
            if (Internal != null && asset != null && asset.SsabData != null)
            {
                Internal.LoadSsab(asset.SsabData, asset.ResolveExternalSsab);
                _loadedAsset = asset;
            }
        }

        public void Dispose()
        {
            if (Internal != null)
            {
                Internal.Dispose();
                Internal = null;
            }
            _loadedAsset = null;
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
