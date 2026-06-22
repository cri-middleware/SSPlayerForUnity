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

        public event System.Action OnAnimationStarted;
        public event System.Action OnAnimationFinished;
        public event System.Action OnAnimationLooped;
        public event System.Action<SpriteStudioUserData> OnUserData;
        public event System.Action<SpriteStudioSignal> OnSignal;
        public event System.Action<SpriteStudioAudio> OnAudio;

        // The asset whose ssab is currently loaded into Internal. Used to detect an inspector asset swap so
        // EnsureCreated reloads (the inspector writes the serialized field directly, bypassing the setter).
        private SpriteStudioAnimeAsset _loadedAsset;

        public void EnsureCreated(SpriteStudioAnimeAsset asset, bool subFrameEnabled)
        {
            if (Internal == null)
            {
                Internal = new SpriteStudioInternalPlayer { SubFrameEnabled = subFrameEnabled };
                HookEvents();
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

        // Relay the Internal player's events to this core's events. Named methods (not lambdas) so they can
        // be unsubscribed in Dispose before the Internal is torn down — keeping subscriptions paired even if
        // the Internal is later recreated.
        private void HookEvents()
        {
            if (Internal == null) return;
            Internal.OnAnimationFinished += RelayAnimationFinished;
            Internal.OnAnimationLooped += RelayAnimationLooped;
            Internal.OnUserData += RelayUserData;
            Internal.OnSignal += RelaySignal;
            Internal.OnAudio += RelayAudio;
        }

        private void UnhookEvents()
        {
            if (Internal == null) return;
            Internal.OnAnimationFinished -= RelayAnimationFinished;
            Internal.OnAnimationLooped -= RelayAnimationLooped;
            Internal.OnUserData -= RelayUserData;
            Internal.OnSignal -= RelaySignal;
            Internal.OnAudio -= RelayAudio;
        }

        private void RelayAnimationFinished() => OnAnimationFinished?.Invoke();
        private void RelayAnimationLooped() => OnAnimationLooped?.Invoke();
        private void RelayUserData(SpriteStudioUserData user) => OnUserData?.Invoke(user);
        private void RelaySignal(SpriteStudioSignal sig) => OnSignal?.Invoke(sig);
        private void RelayAudio(SpriteStudioAudio audio) => OnAudio?.Invoke(audio);

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
                UnhookEvents();
                Internal.Dispose();
                Internal = null;
            }
            _loadedAsset = null;
        }

        /// <summary>
        /// 指定アニメーションのデータをセットアップする (先頭フレームで posed)。再生は開始しない。
        /// 開始するには別途 <see cref="Play()"/> を呼ぶ。ループ/速度/スキップ/方向などの再生設定はここでは
        /// 適用しない: Rust の setup_animation がこれらを既定値 (Forward/Normal 等) へ戻すため、必要なら
        /// 呼び出し側が setup 後に別途適用する (Godot プレイヤーと同じ流れ)。
        /// logContext は警告メッセージのタグ。setup に成功すれば true。
        /// </summary>
        public bool SetAnimation(string animationName, string logContext)
        {
            if (Internal == null || string.IsNullOrEmpty(animationName)) return false;

            if (!Internal.SetupAnimation(animationName))
            {
                Debug.LogWarning($"[{logContext}] Failed to setup animation: {animationName}");
                return false;
            }
            return true;
        }

        /// <summary>現在セットアップ済みのアニメーションを先頭から再生開始する。</summary>
        public void Play()
        {
            if (Internal == null) return;
            Internal.Play();
            OnAnimationStarted?.Invoke();
        }

        /// <summary>現在セットアップ済みのアニメーションを指定フレームから再生開始する。</summary>
        public void Play(float startFrame)
        {
            if (Internal == null) return;
            Internal.Play(startFrame);
            OnAnimationStarted?.Invoke();
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
