using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Playback logic shared by SpriteStudioPlayer (MeshRenderer) and SpriteStudioGraphic (uGUI).
    /// The two have different base classes (MonoBehaviour vs Graphic) so they cannot share via inheritance;
    /// instead, each component holds this core by composition and delegates to it.
    /// Serialized settings (animation name, loop, etc.) stay on each component for Unity serialization compatibility.
    /// </summary>
    internal sealed class SpriteStudioPlaybackCore
    {
        /// <summary>Internal player (FFI context). Null when not created / after disposal.</summary>
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
        /// Sets up the data for the specified animation (posed at the first frame). Does not start playback.
        /// Call <see cref="Play()"/> separately to start.
        /// This is the bare setup: the Rust setup_animation resets the loop count and the playback section
        /// (both are per-transition — the transition is what states them) and keeps speed, frame skip, the
        /// frame-rate override and the direction / style. Nothing is re-applied here; the owning component
        /// pushes its own configuration back in its SetAnimation, which is where the loop count is restored.
        /// logContext is the tag for warning messages. Returns true if setup succeeded.
        /// </summary>
        /// <summary>
        /// Whether <paramref name="animationName"/> is the animation the native runtime already has set
        /// up. The owning component checks this before doing anything, so that re-asserting the current
        /// animation costs nothing — see its SetAnimation for why that matters.
        /// </summary>
        public bool IsAnimationSetUp(string animationName)
            => Internal != null && !string.IsNullOrEmpty(animationName) && Internal.CurrentAnimationName == animationName;

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

        /// <summary>Starts playing the currently set-up animation from the beginning.</summary>
        public void Play()
        {
            if (Internal == null) return;
            Internal.Play();
            OnAnimationStarted?.Invoke();
        }

        /// <summary>Starts playing the currently set-up animation from the specified frame.</summary>
        public void Play(float startFrame)
        {
            if (Internal == null) return;
            Internal.Play(startFrame);
            OnAnimationStarted?.Invoke();
        }

        /// <summary>Advances the animation and schedules the mesh-build job (does not wait for the Job to complete).</summary>
        public void TickAndSchedule(float deltaTime, bool subFrameEnabled)
        {
            if (Internal == null) return;
            Internal.Tick(deltaTime, subFrameEnabled);
            Internal.ScheduleMeshUpdate(deltaTime);
        }
    }
}
