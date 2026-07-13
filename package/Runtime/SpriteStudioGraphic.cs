using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Layer 3: Player component for Canvas (uGUI).
    /// Feeds meshes directly to the CanvasRenderer.
    /// Bypasses Unity UI's standard rebuild flow (Rebuild) to achieve highly efficient rendering.
    /// Playback logic is delegated to the shared core (SpriteStudioPlaybackCore).
    /// In Play mode the SpriteStudioUpdateManager drives it in bulk; in edit mode (ExecuteAlways) it
    /// updates the preview in its own LateUpdate.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    [RequireComponent(typeof(CanvasRenderer))]
    [ExecuteAlways]
    public class SpriteStudioGraphic : MaskableGraphic, ISpriteStudioRenderable
    {
        [Header("SpriteStudio Asset")]
        [Tooltip("The converted ssab asset to render")]
        [SerializeField] private SpriteStudioAnimeAsset _animeAsset;

        public SpriteStudioAnimeAsset AnimeAsset
        {
            get => _animeAsset;
            set
            {
                _animeAsset = value;
                _core.ReloadAsset(_animeAsset);
            }
        }

        [Header("Audio")]
        [Tooltip("Play sounds from audio events (ADIO). When ON, plays them built-in with no extra component required.")]
        [SerializeField] private bool _playAudio = true;
        [Tooltip("Volume of built-in playback (AudioClip one-shot). Ignored when an Audio Backend is used.")]
        [Range(0f, 1f)]
        [SerializeField] private float _audioVolume = 1f;
        [Tooltip("Playback override hook (optional). Assign this to route to a custom audio backend such as audio middleware. " +
                 "When empty, resolution falls back to the global default, then built-in AudioClip playback.")]
        [SerializeField] private SpriteStudioAudioBackend _audioBackend;

        private SpriteStudioAudioController _audioController;

        [Header("Playback Settings")]
        [Tooltip("Animation name to play (e.g. pack name / animation name)")]
        [SerializeField] private string _animationName = "";
        public string AnimationName
        {
            get => _animationName;
            set => _animationName = value;
        }

        [Tooltip("Whether to start playback automatically on startup")]
        [SerializeField] private bool _autoPlay = true;
        public bool AutoPlay
        {
            get => _autoPlay;
            set => _autoPlay = value;
        }

        [Tooltip("Loop count. -1 = infinite loop / 1 = play once / n = play n times")]
        [SerializeField] private int _loopCount = -1;
        public int LoopCount
        {
            get => _loopCount;
            set
            {
                _loopCount = value;
                _core.Internal?.SetLoop(value);
            }
        }

        [Tooltip("Playback speed scale of the animation")]
        [SerializeField] private float _speedScale = 1.0f;
        public float SpeedScale
        {
            get => _speedScale;
            set
            {
                _speedScale = value;
                _core.Internal?.SetSpeed(_speedScale);
            }
        }

        [Tooltip("Whether to enable sub-frame rendering (interpolation between frames)")]
        [SerializeField] private bool _subFrameEnabled = false;
        public bool SubFrameEnabled
        {
            get => _core.Internal != null ? _core.Internal.SubFrameEnabled : _subFrameEnabled;
            set
            {
                _subFrameEnabled = value;
                if (_core.Internal != null) _core.Internal.SubFrameEnabled = value;
            }
        }

        [Tooltip("Real-time tracking (timed). ON: advance by elapsed time even when frames drop under load. OFF (fixed): always advance exactly one frame per update (frame-by-frame).")]
        [SerializeField] private bool _skipFrames = true;
        public bool SkipFrames
        {
            get => _core.Internal != null ? _core.Internal.IsSkipFrames() : _skipFrames;
            set
            {
                _skipFrames = value;
                _core.Internal?.SetSkipFrames(value);
            }
        }

        /// <summary>
        /// Selects the animation to play and sets its data up (posed at the first frame).
        /// Does not start playback; call <see cref="Play()"/> to start. Playback settings such as
        /// loop/speed/skip are not applied here (setup resets them to defaults). To apply settings after
        /// switching at runtime, set the <see cref="LoopCount"/> / <see cref="SpeedScale"/> /
        /// <see cref="SkipFrames"/> properties after setup. Returns true if
        /// setup succeeded.
        /// </summary>
        public bool SetAnimation(string animName)
        {
            _animationName = animName;
            _audioController?.StopLoops();   // On animation switch, stop the previous animation's loop sounds (let one-shots end on their own)
            return _core.SetAnimation(_animationName, "SpriteStudioGraphic");
        }

        // Push the serialized playback parameters (loop / speed / skip) to the runtime. SetAnimation only
        // sets the animation up; the Rust setup resets these to defaults, so they are applied separately
        // afterwards — loop/speed/skip are configured after the animation
        // is set. Direction/style (Reverse/PingPong) have no serialized default and stay at the setup
        // default (Forward/Normal) until set via their properties.
        private void ApplyPlaybackSettings()
        {
            var p = _core.Internal;
            if (p == null) return;
            p.SetLoop(_loopCount);
            p.SetSpeed(_speedScale);
            p.SetSkipFrames(_skipFrames);
        }

        /// <summary>Starts playing the currently set-up animation from the beginning.</summary>
        public void Play() { _core.Play(); _audioController?.ResumeAll(); }   // Explicit play lifts the suspend (SDK doc §4)

        /// <summary>Starts playing the currently set-up animation from the specified frame.</summary>
        public void Play(float startFrame) { _core.Play(startFrame); _audioController?.ResumeAll(); }

        #region Playback Control
        // Thinly expose, as the component's public API, the playback controls the internal player (Rust
        // runtime) already provides. State queries, seek, pause, reverse playback, etc. are all implemented
        // natively; this is delegation only (no new logic).

        /// <summary>Whether a playback session is active. True even while paused (false when stopped / not playing).</summary>
        public bool IsPlaying => _core.Internal?.IsPlaying() ?? false;

        /// <summary>Whether currently paused (true only while a playback session is active and paused).</summary>
        public bool IsPaused
        {
            get
            {
                // The native is_pausing defaults to true (set even when not playing), so also check that
                // playback is actually running.
                var p = _core.Internal;
                return p != null && p.IsPlaying() && p.IsPausing();
            }
        }

        /// <summary>
        /// Toggles playback pause / resume (keeps the current frame). Flips the native is_pausing
        /// (play() rewinds to the start, so it cannot be used to resume).
        /// </summary>
        public void Pause()
        {
            _core.Internal?.Pause();
            // Sync audio with the animation pause/resume (SDK doc §4). Pause is a toggle, so branch on the resulting state.
            if (_audioController != null && _core.Internal != null)
            {
                if (_core.Internal.IsPausing()) _audioController.PauseAll();
                else _audioController.ResumeAll();
            }
        }

        /// <summary>Stops playback.</summary>
        public void Stop() => _core.Internal?.Stop();

        /// <summary>Current playback frame (fractional allowed). Set to seek to an arbitrary frame.</summary>
        public float CurrentFrame
        {
            get => _core.Internal?.GetFrame() ?? 0f;
            set => _core.Internal?.SetFrame(value);
        }

        /// <summary>Total frame count of the current animation (end frame + 1). 0 when nothing is loaded.</summary>
        public int TotalFrames => _core.Internal?.GetTotalFrames() ?? 0;

        /// <summary>Playback frame rate (fps). Set to change it at runtime. Get returns 0 when nothing is loaded.</summary>
        public int FrameRate
        {
            get => _core.Internal?.GetFrameRate() ?? 0;
            set => _core.Internal?.SetFrameRate(value);
        }

        /// <summary>
        /// Whether reverse (Backward) playback is on. <see cref="SetAnimation(string)"/> resets this to
        /// Forward every time, so to start in reverse set it to true after SetAnimation (either before or
        /// after Play).
        /// </summary>
        public bool Reverse
        {
            get => (_core.Internal?.GetPlaybackDirection() ?? 0) != 0;
            set
            {
                var p = _core.Internal;
                if (p != null) p.SetPlaybackStyle(value ? 1 : 0, p.GetPlaybackStyle());
            }
        }

        /// <summary>
        /// Whether ping-pong (round-trip) playback is on. <see cref="SetAnimation(string)"/> resets this to
        /// Normal every time, so to start in ping-pong set it to true after SetAnimation (either before or
        /// after Play).
        /// </summary>
        public bool PingPong
        {
            get => (_core.Internal?.GetPlaybackStyle() ?? 0) != 0;
            set
            {
                var p = _core.Internal;
                if (p != null) p.SetPlaybackStyle(p.GetPlaybackDirection(), value ? 1 : 0);
            }
        }

        /// <summary>
        /// Limits the playback range by frame (frame-based version of SS6's playback-range spec; label
        /// specification is not supported).
        /// </summary>
        public void SetAnimationSection(int startFrame, int endFrame) => _core.Internal?.SetAnimationSection(startFrame, endFrame);

        #endregion

        #region Part Overrides
        // Mirror of SpriteStudioPlayer's per-part override API (colour / cell / visibility) for the
        // Canvas path. See SpriteStudioPlayer for the semantics; the runtime owns the override table
        // and its lifecycle, so nothing is cached or re-applied here.

        /// <summary>Resolve a part name to its index, or -1 if the part is not in the loaded asset.</summary>
        public int FindPartIndex(string partName) => _core.Internal?.ResolvePartIndex(partName) ?? -1;

        /// <summary>Force-hide a part (or revert it to the animation), optionally cascading to its descendants.</summary>
        public bool SetPartVisibilityOverride(string partName, bool hide, bool cascade = false)
            => _core.Internal?.SetPartVisibilityOverride(partName, hide, cascade) ?? false;

        /// <inheritdoc cref="SetPartVisibilityOverride(string, bool, bool)"/>
        public bool SetPartVisibilityOverride(int partIndex, bool hide, bool cascade = false)
            => _core.Internal?.SetPartVisibilityOverride(partIndex, hide, cascade) ?? false;

        /// <summary>Clear a part's visibility override (revert to the animation).</summary>
        public bool ClearPartVisibilityOverride(string partName) => _core.Internal?.ClearPartVisibilityOverride(partName) ?? false;

        /// <inheritdoc cref="ClearPartVisibilityOverride(string)"/>
        public bool ClearPartVisibilityOverride(int partIndex) => _core.Internal?.ClearPartVisibilityOverride(partIndex) ?? false;

        /// <summary>Override a Normal part's colour with a single value (all four corners).</summary>
        public bool SetPartColorOverride(string partName, SpriteStudioBlendOp blendOp, Color color,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => _core.Internal?.SetPartColorOverride(partName, blendOp, color, priority) ?? false;

        /// <inheritdoc cref="SetPartColorOverride(string, SpriteStudioBlendOp, Color, SpriteStudioOverridePriority)"/>
        public bool SetPartColorOverride(int partIndex, SpriteStudioBlendOp blendOp, Color color,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => _core.Internal?.SetPartColorOverride(partIndex, blendOp, color, priority) ?? false;

        /// <summary>Override a Normal part's colour with four distinct corners (left-top, right-top, left-bottom, right-bottom).</summary>
        public bool SetPartColorOverride(string partName, SpriteStudioBlendOp blendOp,
            Color leftTop, Color rightTop, Color leftBottom, Color rightBottom,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => _core.Internal?.SetPartColorOverride(partName, blendOp, leftTop, rightTop, leftBottom, rightBottom, priority) ?? false;

        /// <inheritdoc cref="SetPartColorOverride(string, SpriteStudioBlendOp, Color, Color, Color, Color, SpriteStudioOverridePriority)"/>
        public bool SetPartColorOverride(int partIndex, SpriteStudioBlendOp blendOp,
            Color leftTop, Color rightTop, Color leftBottom, Color rightBottom,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => _core.Internal?.SetPartColorOverride(partIndex, blendOp, leftTop, rightTop, leftBottom, rightBottom, priority) ?? false;

        /// <summary>Clear a part's colour override (revert to the animation).</summary>
        public bool ClearPartColorOverride(string partName) => _core.Internal?.ClearPartColorOverride(partName) ?? false;

        /// <inheritdoc cref="ClearPartColorOverride(string)"/>
        public bool ClearPartColorOverride(int partIndex) => _core.Internal?.ClearPartColorOverride(partIndex) ?? false;

        /// <summary>Make a Normal / mask part draw a different cell, named by its cellmap (without .ssce) and cell name.</summary>
        public bool SetPartCellOverride(string partName, string cellmapName, string cellName,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => _core.Internal?.SetPartCellOverride(partName, cellmapName, cellName, priority) ?? false;

        /// <inheritdoc cref="SetPartCellOverride(string, string, string, SpriteStudioOverridePriority)"/>
        public bool SetPartCellOverride(int partIndex, string cellmapName, string cellName,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => _core.Internal?.SetPartCellOverride(partIndex, cellmapName, cellName, priority) ?? false;

        /// <summary>Clear a part's cell override (revert to the animation).</summary>
        public bool ClearPartCellOverride(string partName) => _core.Internal?.ClearPartCellOverride(partName) ?? false;

        /// <inheritdoc cref="ClearPartCellOverride(string)"/>
        public bool ClearPartCellOverride(int partIndex) => _core.Internal?.ClearPartCellOverride(partIndex) ?? false;

        /// <summary>Clear every per-part override on this player.</summary>
        public bool ClearAllPartOverrides() => _core.Internal?.ClearAllPartOverrides() ?? false;

        /// <summary>Names of the cellmaps in the loaded asset (for discovering cell-override targets).</summary>
        public string[] GetCellMapNames() => _core.Internal?.GetCellMapNames() ?? System.Array.Empty<string>();

        /// <summary>Names of the cells in the named cellmap (for discovering cell-override targets).</summary>
        public string[] GetCellNames(string cellmapName) => _core.Internal?.GetCellNames(cellmapName) ?? System.Array.Empty<string>();

        #endregion

        protected override void Start()
        {
            base.Start();
            if (string.IsNullOrEmpty(_animationName)) return;
            // Always call SetAnimation so the first frame is posed even when AutoPlay is off; actually start
            // playback only when AutoPlay is on. SetAnimation does not apply playback settings, so apply them
            // explicitly after setup (property-application order: animation -> loop/speed/skip -> play).
            if (SetAnimation(_animationName))
            {
                ApplyPlaybackSettings();
                if (_autoPlay) Play();
            }
        }

        private readonly SpriteStudioPlaybackCore _core = new SpriteStudioPlaybackCore();

        public event System.Action OnAnimationStarted
        {
            add => _core.OnAnimationStarted += value;
            remove => _core.OnAnimationStarted -= value;
        }
        public event System.Action OnAnimationFinished
        {
            add => _core.OnAnimationFinished += value;
            remove => _core.OnAnimationFinished -= value;
        }
        public event System.Action OnAnimationLooped
        {
            add => _core.OnAnimationLooped += value;
            remove => _core.OnAnimationLooped -= value;
        }
        public event System.Action<SpriteStudioUserData> OnUserData
        {
            add => _core.OnUserData += value;
            remove => _core.OnUserData -= value;
        }
        public event System.Action<SpriteStudioSignal> OnSignal
        {
            add => _core.OnSignal += value;
            remove => _core.OnSignal -= value;
        }
        public event System.Action<SpriteStudioAudio> OnAudio
        {
            add => _core.OnAudio += value;
            remove => _core.OnAudio -= value;
        }

        [Header("Events")]
        public UnityEvent OnAnimationStartedEvent = new UnityEvent();
        public UnityEvent OnAnimationFinishedEvent = new UnityEvent();
        public UnityEvent OnAnimationLoopedEvent = new UnityEvent();
        public UserDataUnityEvent OnUserDataEvent = new UserDataUnityEvent();
        public SignalUnityEvent OnSignalEvent = new SignalUnityEvent();
        public AudioUnityEvent OnAudioEvent = new AudioUnityEvent();

        private Mesh _sharedMesh;

        protected override void OnEnable()
        {
            // A Graphic drives a CanvasRenderer. RequireComponent adds one for newly created objects;
            // also self-heal a GameObject that was created before this requirement existed so that
            // canvasRenderer.materialCount does not throw a MissingComponentException.
            if (GetComponent<CanvasRenderer>() == null) gameObject.AddComponent<CanvasRenderer>();

            base.OnEnable();

            if (_sharedMesh == null)
            {
                _sharedMesh = new Mesh();
                _sharedMesh.name = "SpriteStudio_CanvasMesh";
                _sharedMesh.MarkDynamic();
            }

            _core.EnsureCreated(_animeAsset, _subFrameEnabled);
            // Canvas path: drop SpriteStudio internal masking; a parent uGUI Mask/RectMask2D clips this
            // graphic read-only via the per-submesh stencil wrap below.
            if (_core.Internal != null) _core.Internal.SuppressInternalMask = true;
            _canvasMaterialDirty = true;
            SpriteStudioUpdateManager.Register(this);

            _core.OnAnimationStarted += InvokeAnimationStarted;
            _core.OnAnimationFinished += InvokeAnimationFinished;
            _core.OnAnimationLooped += InvokeAnimationLooped;
            _core.OnUserData += InvokeUserData;
            _core.OnSignal += InvokeSignal;
            _core.OnAudio += InvokeAudio;

            // In edit mode, build the start-frame static preview right after placement (setup consumed in LateUpdate).
            _editPreviewNeedsSetup = true;
        }

        private void InvokeAnimationStarted() => OnAnimationStartedEvent?.Invoke();
        private void InvokeAnimationFinished()
        {
            _audioController?.StopLoops();   // On animation end, stop loop sounds (let one-shots end on their own)
            OnAnimationFinishedEvent?.Invoke();
        }
        private void InvokeAnimationLooped() => OnAnimationLoopedEvent?.Invoke();
        private void InvokeUserData(SpriteStudioUserData data) => OnUserDataEvent?.Invoke(data);
        private void InvokeSignal(SpriteStudioSignal data) => OnSignalEvent?.Invoke(data);
        private void InvokeAudio(SpriteStudioAudio data)
        {
            OnAudioEvent?.Invoke(data);   // Notification (observation channel) always fires regardless of playback direction.
            // Built-in/Backend playback (zero-config). Only while running and playing forward (SDK doc §4: no sound on reverse playback).
            if (_playAudio && Application.isPlaying && (_core.Internal?.IsPlayingForward() ?? false))
            {
                if (_audioController == null) _audioController = new SpriteStudioAudioController(gameObject);
                _audioController.Play(in data, _animeAsset, _audioBackend, _audioVolume);
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();

            SpriteStudioUpdateManager.Unregister(this);

            _core.OnAnimationStarted -= InvokeAnimationStarted;
            _core.OnAnimationFinished -= InvokeAnimationFinished;
            _core.OnAnimationLooped -= InvokeAnimationLooped;
            _core.OnUserData -= InvokeUserData;
            _core.OnSignal -= InvokeSignal;
            _core.OnAudio -= InvokeAudio;
            _audioController?.StopAll();   // On disable, stop all built-in sounds

            _core.Dispose();

            // Release the uGUI stencil wrapper (refcounted by StencilMaterial) and invalidate the
            // material signature so OnEnable re-resolves from scratch.
            ReleaseCanvasStencil();
            _lastMaterialSignature = 0;
            _lastStencilDepth = -1;
            _canvasMaterialDirty = true;

            if (_sharedMesh != null)
            {
                if (Application.isPlaying)
                    Destroy(_sharedMesh);
                else
                    DestroyImmediate(_sharedMesh);
                _sharedMesh = null;
            }
        }

        // Flag for whether the edit-mode preview should SetupAnimation once (set in OnEnable / OnValidate).
        private bool _editPreviewNeedsSetup;

        private void LateUpdate()
        {
            // Play mode is centrally managed by SpriteStudioUpdateManager.
            // Only the edit-mode (ExecuteAlways) static preview is updated here. Start() does not run in edit
            // mode and SetupAnimation is never called, so right after placement an empty frame is built and it
            // "flashes and disappears". Explicitly set up -> pin the start frame, and rebuild/reassert every
            // frame without advancing (ApplyAndRender every frame rather than dirty-once, so the mesh keeps
            // showing even after uGUI clears the CanvasRenderer's mesh).
            if (Application.isPlaying) return;
            // Self-heal if the .ssab was still importing when OnEnable ran (Editor restart race): pick the
            // ssab up once it is available (EnsureCreated is a no-op once loaded). Without this an empty
            // Internal created at OnEnable would never get its ssab, so SetupAnimation fails forever.
            _core.EnsureCreated(_animeAsset, _subFrameEnabled);
            if (_core.Internal == null || !_core.Internal.HasSsab) return;
            if (_editPreviewNeedsSetup)
            {
                if (string.IsNullOrEmpty(_animationName) || !_core.Internal.SetupAnimation(_animationName)) return;
                _core.Internal.SetFrame(_core.Internal.GetAnimationSectionStart());
                _editPreviewNeedsSetup = false;
            }
            _core.Internal.ScheduleMeshUpdate(0f); // Static: build the start frame without advancing
            ApplyAndRender();
        }

#if UNITY_EDITOR
        // The inspector writes the serialized fields directly and does not call the property setters.
        // To reflect in-play inspector edits into the runtime (Rust), sync them here. Graphic.OnValidate is
        // defined only within UNITY_EDITOR, so the override is wrapped in #if UNITY_EDITOR too. (Animation-name
        // switching is out of scope.)
        protected override void OnValidate()
        {
            base.OnValidate();
            // An inspector change (animation name, etc.) rebuilds the edit preview from setup.
            _editPreviewNeedsSetup = true;
            if (_core.Internal == null) return;
            _core.Internal.SetSpeed(_speedScale);
            _core.Internal.SetLoop(_loopCount);
            _core.Internal.SetSkipFrames(_skipFrames);
            _core.Internal.SubFrameEnabled = _subFrameEnabled;
        }
#endif

        // --- ISpriteStudioRenderable (driven by SpriteStudioUpdateManager) ---

        public void PrepareFrame(float deltaTime, out System.IntPtr handle, out float drawFrame)
        {
            if (_core.Internal == null) { handle = System.IntPtr.Zero; drawFrame = 0f; return; }
            _core.Internal.PrepareFrameData(deltaTime, true, out handle, out drawFrame);
        }

        public void ConsumeFrame(System.IntPtr frameDataPtr, int len, float deltaTime)
        {
            if (_core.Internal == null) return;
            _core.Internal.ConsumeFrameData(frameDataPtr, len, deltaTime);
        }

        public void ApplyAndRender()
        {
            if (_core.Internal == null || _sharedMesh == null) return;

            // Single-material Canvas: build the whole graphic as ONE submesh (perBatchSubmesh: false) and
            // drive it with one material; MIX/ADD are split per-vertex in-shader. CanvasRenderer only
            // accepts 16-bit index meshes, so upload 16-bit.
            _core.Internal.ApplyToMesh(_sharedMesh, false, use16BitIndices: true);
            EnsureCanvasShaderChannels();
            UpdateCanvasMaterials();
            // CanvasRenderer multiplies the mesh vertex colours by its own colour. The normal uGUI
            // rebuild (which we suppress) is what would push the Graphic colour into it; without that
            // the renderer colour stays at its uninitialised default (black) and zeroes the sprite RGB.
            // Push the Graphic colour every frame (default white = identity, also honours a user tint).
            canvasRenderer.SetColor(color);
            canvasRenderer.SetMesh(_sharedMesh);
        }

        // --- Single-material Canvas resolution (one atlas + one fixed PMA blend) ---
        // The whole graphic renders as ONE submesh with ONE material on the SpriteStudio/Canvas shader.
        // MIX vs ADD is handled per-vertex in-shader (a=0 trick); other blends collapse to MIX. SpriteStudio
        // internal masking is suppressed; the only stencil is the READ-ONLY test injected when this graphic
        // is nested under a uGUI Mask, via StencilMaterial.Add (we compute the mask depth ourselves and wrap
        // the single material, since MaskableGraphic.GetModifiedMaterial tracks only one material anyway).
        // RectMask2D needs nothing here — MaskableGraphic clips it through CanvasRenderer.EnableRectClipping.
        private Material _canvasStencilWrapped; // StencilMaterial.Add result to release (null if not wrapped)
        private uint _lastMaterialSignature;    // resolved atlas texture hash + 1 (0 = nothing resolved)
        private int _lastStencilDepth = -1;
        private bool _canvasMaterialDirty;
        private bool _warnedMultiTexture;

        private void UpdateCanvasMaterials()
        {
            if (_animeAsset == null || _core.Internal == null) return;

            int subCount = _core.Internal.SubmeshCount;
            if (subCount <= 0)
            {
                if (_lastMaterialSignature != 0)
                {
                    ReleaseCanvasStencil();
                    canvasRenderer.materialCount = 0;
                    _lastMaterialSignature = 0;
                }
                return;
            }

            var submeshes = _core.Internal.Submeshes;

            // Multi-atlas is not supported on the Canvas path: one CanvasRenderer = one mesh + one
            // material. Interim guard: draw with the first atlas only and warn once (other atlases
            // sample the wrong texture but never crash).
            if (!_warnedMultiTexture && _animeAsset.Textures != null && _animeAsset.Textures.Count > 1)
            {
                Debug.LogWarning($"[SpriteStudioGraphic] '{_animeAsset.name}' uses {_animeAsset.Textures.Count} atlases. " +
                                 "The Canvas (uGUI) path is single-atlas only: only the first atlas is drawn, so parts on other " +
                                 "atlases will sample the wrong texture. Merge to one atlas, or use the world (MeshRenderer) player.", this);
                _warnedMultiTexture = true;
            }

            uint texHash = submeshes[0].TextureHash;

            // Current uGUI mask depth (0 when not nested under a Mask). Checked every frame so a reparent
            // under/out of a Mask re-wraps the material even if the SetMaterialDirty signal is missed.
            int stencilDepth = maskable
                ? UnityEngine.UI.MaskUtilities.GetStencilDepth(transform, UnityEngine.UI.MaskUtilities.FindRootSortOverrideCanvas(transform))
                : 0;

            uint signature = texHash + 1u; // +1 so a valid hash never collides with the empty sentinel 0
            if (signature != _lastMaterialSignature || stencilDepth != _lastStencilDepth || _canvasMaterialDirty)
            {
                RebuildCanvasMaterial(texHash, stencilDepth);
                _lastMaterialSignature = signature;
                _lastStencilDepth = stencilDepth;
                _canvasMaterialDirty = false;
            }
        }

        private void RebuildCanvasMaterial(uint texHash, int stencilDepth)
        {
            ReleaseCanvasStencil();

            Material baseMat = _animeAsset.ResolveCanvasMaterial(texHash, 0);
            if (baseMat == null)
            {
                Debug.LogError($"[SpriteStudioGraphic] Canvas material for texture {texHash} could not be resolved.", this);
                canvasRenderer.materialCount = 0;
                return;
            }

            Material finalMat = baseMat;
            if (stencilDepth > 0)
            {
                // Read-only stencil test for a parent uGUI Mask (Comp Equal, Op Keep, no write).
                int stencilId = (1 << stencilDepth) - 1;
                _canvasStencilWrapped = UnityEngine.UI.StencilMaterial.Add(
                    baseMat, stencilId,
                    UnityEngine.Rendering.StencilOp.Keep,
                    UnityEngine.Rendering.CompareFunction.Equal,
                    UnityEngine.Rendering.ColorWriteMask.All,
                    stencilId, 0);
                finalMat = _canvasStencilWrapped;
            }

            canvasRenderer.materialCount = 1;
            canvasRenderer.SetMaterial(finalMat, 0);
        }

        // The mesh packs per-vertex [rate, blend_idx, isNormalized, reserved] into TEXCOORD1. A Canvas
        // only emits the shader channels listed in additionalShaderChannels; TexCoord1 is NOT included
        // by default, so without this the uv1 data is dropped/garbage and the part-color blend breaks
        // (renders black). Enable it on the canvas we render to.
        private void EnsureCanvasShaderChannels()
        {
            var c = canvas;
            if (c == null) return;
            const AdditionalCanvasShaderChannels needed = AdditionalCanvasShaderChannels.TexCoord1;
            if ((c.additionalShaderChannels & needed) != needed)
            {
                c.additionalShaderChannels |= needed;
            }
        }

        private void ReleaseCanvasStencil()
        {
            if (_canvasStencilWrapped != null)
            {
                UnityEngine.UI.StencilMaterial.Remove(_canvasStencilWrapped);
                _canvasStencilWrapped = null;
            }
        }

        // --- Avoid conflicts with the Canvas Rebuild flow ---
        // Prevent uGUI's automatic Layout/Vertex rebuild from firing and
        // destroying/reallocating our mesh on its own

        public override void SetVerticesDirty()
        {
            // Disabled: do not trigger the standard vertex rebuild
        }

        public override void SetMaterialDirty()
        {
            // Do NOT call base: the standard uGUI material rebuild (CanvasUpdateRegistry) would try to
            // drive our mesh/material through the normal Graphic path and fight our direct SetMesh/
            // SetMaterial. Instead just flag a re-resolve so a mask-hierarchy change (MaskableGraphic
            // raises SetMaterialDirty on stencil-state changes) re-wraps the per-submesh materials.
            _canvasMaterialDirty = true;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            // Disabled: do not let it convert to UI Vertex on its own
            vh.Clear();
        }
    }
}
