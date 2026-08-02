using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Timeline;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Layer 3: Player component for world coordinates (for 3D space / 2D scenes).
    /// Renders using Unity's Transform and MeshRenderer.
    /// Playback logic is delegated to the shared core (SpriteStudioPlaybackCore); the per-frame drive is
    /// batched by SpriteStudioUpdateManager as "all Schedule -> all Apply" (Job parallelization).
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    // [ExecuteAlways]: run OnEnable/LateUpdate in edit mode too, building the "start-frame static preview"
    // exactly once when placed in the scene (does not play). The per-frame drive in Play mode is still
    // centrally managed by SpriteStudioUpdateManager as before (the manager does not run in edit mode).
    [ExecuteAlways]
    public class SpriteStudioPlayer : MonoBehaviour, ITimeControl, ISpriteStudioRenderable
    {
        [Header("SpriteStudio Asset")]
        [Tooltip("The converted ssab asset to render")]
        [SerializeField] private SpriteStudioAnimeAsset _animeAsset;

        [Tooltip("Per-part AddOn / custom shader catalog. Optional: leave empty to disable per-part " +
                 "shader dispatch. The built-in AddOns ship pre-registered in the package catalog.")]
        [SerializeField] private SpriteStudioShaderCatalog _shaderCatalog;

        [Tooltip("Registration list of user-owned Transforms that follow specified parts. The player does not create GameObjects; " +
                 "it only drives the registered Transforms every frame (constraint style).")]
        [SerializeField] private List<SpriteStudioPartTracker> _partTrackers = new List<SpriteStudioPartTracker>();

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

        public SpriteStudioAnimeAsset AnimeAsset
        {
            get => _animeAsset;
            set
            {
                _animeAsset = value;
                _core.ReloadAsset(_animeAsset);
                // The part set (and thus name->index mapping) may have changed: re-resolve trackers.
                MarkPartTrackersDirty();
#if UNITY_EDITOR
                _appliedAsset = _animeAsset;
#endif
            }
        }

        [Header("Sorting")]
        [SerializeField] private string _sortingLayerName = "Default";
        [SerializeField] private int _sortingOrder = 0;

        public string SortingLayerName
        {
            get => _meshRenderer != null ? _meshRenderer.sortingLayerName : _sortingLayerName;
            set
            {
                _sortingLayerName = value;
                if (_meshRenderer != null) _meshRenderer.sortingLayerName = value;
            }
        }

        public int SortingOrder
        {
            get => _meshRenderer != null ? _meshRenderer.sortingOrder : _sortingOrder;
            set
            {
                _sortingOrder = value;
                if (_meshRenderer != null) _meshRenderer.sortingOrder = value;
            }
        }

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

        [Tooltip("Loop count. -1 = infinite loop (0 is a synonym) / 1 = play once / n = play n times")]
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

        [Tooltip("Pixels per one Unity world unit. Converts SS pixel coordinates into world units. Default 1 = 1:1 pixel scale (as before). Set to e.g. 100 to match Unity's sprite convention.")]
        [SerializeField] private float _pixelsPerUnit = 1f;
        public float PixelsPerUnit
        {
            get => _pixelsPerUnit;
            set
            {
                _pixelsPerUnit = value;
                ApplyPixelsPerUnit();
            }
        }

        [Tooltip("Mirror the animation horizontally. Affects only what this player draws — children and colliders stay put (negating the Transform's scale would drag them along).")]
        [SerializeField] private bool _flipX = false;
        /// <summary>
        /// Mirror the drawn animation horizontally, in the manner of <c>SpriteRenderer.flipX</c>: only this
        /// player's geometry is mirrored, so the GameObject's children and colliders are left alone. Part
        /// trackers follow the mirrored pose.
        /// </summary>
        public bool FlipX
        {
            get => _flipX;
            set { _flipX = value; ApplyRootTransform(); }
        }

        [Tooltip("Mirror the animation vertically. Affects only what this player draws — children and colliders stay put.")]
        [SerializeField] private bool _flipY = false;
        /// <inheritdoc cref="FlipX"/>
        public bool FlipY
        {
            get => _flipY;
            set { _flipY = value; ApplyRootTransform(); }
        }

        [Tooltip("Shifts the drawn animation in world units without moving the Transform, so children and colliders stay put.")]
        [SerializeField] private Vector2 _offset = Vector2.zero;
        /// <summary>
        /// Shifts the drawn animation by this many world units without moving the Transform — useful for
        /// nudging the artwork off its authored origin while the GameObject keeps marking the logical
        /// position that gameplay and physics use. Applied after <see cref="PixelsPerUnit"/>, so it is in
        /// Unity units rather than SpriteStudio pixels.
        /// </summary>
        public Vector2 Offset
        {
            get => _offset;
            set { _offset = value; ApplyRootTransform(); }
        }

        // Reflect PPU into the internal player's vertex scale (= 1/PPU). Set on the top level only; leave
        // Instance children at VertexScale=1 (the parent scales them as a whole, so avoid double scaling).
        private void ApplyPixelsPerUnit()
        {
            if (_core.Internal != null)
                _core.Internal.VertexScale = 1f / Mathf.Max(_pixelsPerUnit, 0.0001f);
            ApplyRootTransform();
        }

        // Flip / offset live next to the vertex scale on the internal player, which folds all three into the
        // one root transform the mesh job and the part trackers share.
        private void ApplyRootTransform()
        {
            if (_core.Internal == null) return;
            _core.Internal.FlipX = _flipX;
            _core.Internal.FlipY = _flipY;
            _core.Internal.VertexOffset = _offset;
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
            bool ok = _core.SetAnimation(_animationName, "SpriteStudioPlayer");
#if UNITY_EDITOR
            // Keep the OnValidate baseline in sync so a later unrelated inspector edit does not restart.
            _appliedAnimation = _animationName;
#endif
            return ok;
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
        public void Play() { _core.Play(); _audioController?.ResumeAll(); }   // Explicit play lifts the suspend

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
            // Sync audio with the animation pause/resume. Pause is a toggle, so branch on the resulting state.
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
        // Per-part runtime overrides (colour / cell / visibility), the SS6 PartsColorChange /
        // CellChangeParts / HideSet equivalents. They win over both the keyframe and the blend. The
        // runtime owns the override table and its lifecycle (see SpriteStudioOverridePriority), so
        // nothing is cached or re-applied here. Every method returns false when the part is unknown
        // or the runtime rejects the call (bad blend op / priority, unresolvable cell).
        //
        // Part indices are stable within one asset: resolve a name once with FindPartIndex and pass
        // the index if you set overrides often.

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

        private void Start()
        {
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

        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Mesh _sharedMesh;

        private void OnEnable()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();

            // Initial apply of the draw order
            _meshRenderer.sortingLayerName = _sortingLayerName;
            _meshRenderer.sortingOrder = _sortingOrder;

            // Build the single mesh for zero-allocation rendering (do not recreate it)
            if (_sharedMesh == null)
            {
                _sharedMesh = new Mesh();
                _sharedMesh.name = "SpriteStudio_DynamicMesh";
                _sharedMesh.MarkDynamic(); // Optimize for per-frame updates
            }
            _meshFilter.sharedMesh = _sharedMesh;

            _core.EnsureCreated(_animeAsset, _subFrameEnabled);
            ApplyPixelsPerUnit();
            SpriteStudioUpdateManager.Register(this);

            _core.OnAnimationStarted += InvokeAnimationStarted;
            _core.OnAnimationFinished += InvokeAnimationFinished;
            _core.OnAnimationLooped += InvokeAnimationLooped;
            _core.OnUserData += InvokeUserData;
            _core.OnSignal += InvokeSignal;
            _core.OnAudio += InvokeAudio;
#if UNITY_EDITOR
            // Baseline so the first in-play OnValidate (e.g. tweaking speed) is not mistaken for an
            // asset/animation change and does not restart playback.
            _appliedAsset = _animeAsset;
            _appliedAnimation = _animationName;
            // In edit mode, build the start-frame static preview once right after placement (consumed in LateUpdate).
            if (!Application.isPlaying) _editPreviewDirty = true;
#endif
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
            // Built-in/Backend playback (zero-config). Only while running and playing forward (no sound on reverse playback).
            if (_playAudio && Application.isPlaying && (_core.Internal?.IsPlayingForward() ?? false))
            {
                if (_audioController == null) _audioController = new SpriteStudioAudioController(gameObject);
                _audioController.Play(in data, _animeAsset, _audioBackend, _audioVolume);
            }
        }

        private void OnDisable()
        {
            SpriteStudioUpdateManager.Unregister(this);

            _core.OnAnimationStarted -= InvokeAnimationStarted;
            _core.OnAnimationFinished -= InvokeAnimationFinished;
            _core.OnAnimationLooped -= InvokeAnimationLooped;
            _core.OnUserData -= InvokeUserData;
            _core.OnSignal -= InvokeSignal;
            _core.OnAudio -= InvokeAudio;
            _audioController?.StopAll();   // On disable, stop all built-in sounds

            // Release the per-part AddOn material instances and force a material-list rebuild on
            // re-enable (the cleared list count no longer matches the submesh count).
            DestroyAddOnInstances();
            _submeshMaterials.Clear();
            _core.Dispose();
            // Internal was disposed, so reset the Timeline control state too. Without this, on re-enable the
            // new (animation-not-set-up) native handle would be wrongly judged "same name, no setup needed"
            // and rendering would break.
            ReleaseTimelineControl();
        }

        private void OnDestroy()
        {
            _audioController?.StopAll();   // Cleanup on destroy (safeguard)
            if (_sharedMesh != null)
            {
                if (Application.isPlaying)
                    Destroy(_sharedMesh);
                else
                    DestroyImmediate(_sharedMesh);
                _sharedMesh = null;
            }
        }

#if UNITY_EDITOR
        // Baseline of the core settings (asset / animation) currently applied to the runtime, so OnValidate
        // can tell an actual asset/animation change apart from tweaking speed etc. Editor-only; rebuilt on
        // play entry by OnEnable. NonSerialized so it never persists.
        [System.NonSerialized] private SpriteStudioAnimeAsset _appliedAsset;
        [System.NonSerialized] private string _appliedAnimation;

        // The inspector writes the serialized fields directly and does not call the property setters.
        // So, to reflect in-play inspector edits into the runtime (Rust), sync them here.
        // OnValidate is an Editor-only callback (not called in builds), so it is wrapped in #if UNITY_EDITOR.
        private void OnValidate()
        {
            // Live-tunable params (do not restart playback).
            if (_core.Internal != null)
            {
                _core.Internal.SetSpeed(_speedScale);
                _core.Internal.SetLoop(_loopCount);
                _core.Internal.SetSkipFrames(_skipFrames);
                _core.Internal.SubFrameEnabled = _subFrameEnabled;
                // The inspector writes the serialized fields directly, bypassing the property setters, so
                // the root transform has to be pushed from here or a flip / offset / PPU edit would appear
                // to do nothing until something else happened to reapply it.
                ApplyPixelsPerUnit();
            }
            if (_meshRenderer != null)
            {
                _meshRenderer.sortingLayerName = _sortingLayerName;
                _meshRenderer.sortingOrder = _sortingOrder;
            }

            // Fundamental settings: changing the asset or animation in the inspector must refresh the
            // runtime (the serialized fields are written directly, bypassing the property setters).
            bool assetChanged = !ReferenceEquals(_appliedAsset, _animeAsset);
            bool animChanged = _appliedAnimation != _animationName;
            if (assetChanged)
            {
                // New part set: trackers must re-resolve their names.
                MarkPartTrackersDirty();
            }

            if (Application.isPlaying)
            {
                // Reflect changes live during play. Reload the ssab on an asset swap, then (re)start the
                // animation from the head when the asset or animation changed.
                if (_core.Internal != null)
                {
                    if (assetChanged)
                    {
                        _core.EnsureCreated(_animeAsset, _subFrameEnabled);
                        ApplyPixelsPerUnit();
                    }
                    if ((assetChanged || animChanged) && !string.IsNullOrEmpty(_animationName))
                    {
                        // When the animation/asset is changed in the inspector, set up again, re-apply the
                        // playback settings, and restart playback from the head.
                        if (SetAnimation(_animationName)) { ApplyPlaybackSettings(); Play(); }
                    }
                }
            }
            else
            {
                // Edit mode rebuilds the preview (EnsureCreated reloads the ssab on an asset change).
                _editPreviewDirty = true;
            }

            _appliedAsset = _animeAsset;
            _appliedAnimation = _animationName;
        }

        // Edit-mode static preview. With [ExecuteAlways], LateUpdate is called every editor frame, but only
        // when _editPreviewDirty is set does it "build the start frame once" and hold it static (does not play).
        // In Play mode the SpriteStudioUpdateManager drives it, so this does nothing.
        private bool _editPreviewDirty;

        private void LateUpdate()
        {
            if (Application.isPlaying) return;
            if (!_editPreviewDirty) return;
            // Self-heal if the .ssab was still importing when OnEnable ran (Editor restart race): keep
            // retrying while the runtime has no ssab yet, and only clear the dirty flag (build once) once
            // it is ready. A setup failure after that is a config error, not a race, so we do not loop.
            _core.EnsureCreated(_animeAsset, _subFrameEnabled);
            if (_core.Internal == null || !_core.Internal.HasSsab) return;
            _editPreviewDirty = false;
            BuildEditPreview();
        }

        private void BuildEditPreview()
        {
            if (_core.Internal == null || _animeAsset == null) return;
            // With no animation chosen yet, preview the asset's first one so a player that was placed
            // without a name is visible instead of silently drawing nothing. Deliberately not written back
            // to _animationName: this is a preview convenience, not a scene edit, so nothing is serialized
            // and Play mode keeps honouring the empty name.
            string previewAnimation = string.IsNullOrEmpty(_animationName)
                ? _core.Internal.GetAnimationName(0)
                : _animationName;
            if (string.IsNullOrEmpty(previewAnimation)) return;
            if (!_core.Internal.SetupAnimation(previewAnimation)) return;
            // Pin to the start frame and synchronously build just one frame without advancing (ScheduleMeshUpdate is advance=false).
            _core.Internal.SetFrame(_core.Internal.GetAnimationSectionStart());
            _core.Internal.ScheduleMeshUpdate(0f);
            ApplyAndRender();
        }
#endif

        // --- ISpriteStudioRenderable (driven by SpriteStudioUpdateManager) ---

        public void PrepareFrame(float deltaTime, out System.IntPtr handle, out float drawFrame)
        {
            if (_core.Internal == null) { handle = System.IntPtr.Zero; drawFrame = 0f; return; }
            // Under Timeline control, render the frame set by SetTime, so do not auto-advance (advance=false)
            _core.Internal.PrepareFrameData(deltaTime, !_timelineControlled, out handle, out drawFrame);
        }

        /// <inheritdoc/>
        // Explicit implementation: only the update manager needs this, and a raw native pointer must not
        // become part of the package's public API.
        System.IntPtr ISpriteStudioRenderable.GetLiveHandle() => _core.Internal != null ? _core.Internal.NativeHandle : System.IntPtr.Zero;

        public void ConsumeFrame(System.IntPtr frameDataPtr, int len, float deltaTime)
        {
            if (_core.Internal == null) return;
            // Under Timeline control, do not advance effects/instances by wall-clock dt (dt=0 to keep deterministic seeking)
            _core.Internal.ConsumeFrameData(frameDataPtr, len, _timelineControlled ? 0f : deltaTime);
        }

        public void ApplyAndRender()
        {
            if (_core.Internal == null) return;

            // Bind the per-part shader catalog before the build so the geometry builder can carve out
            // is-per-part AddOn parts (cheap reference assignment; null = no per-part dispatch).
            _core.Internal.ShaderCatalog = _shaderCatalog;

            // Split SubMeshes per batch and apply a separate material per texture
            _core.Internal.ApplyToMesh(_sharedMesh, true);
            UpdateSubmeshMaterials();

            // After the frame's world matrices are final, drive any registered part trackers. Done here
            // (LateUpdate, before the render phase) so attached renderers update in the same frame.
            MirrorTrackers();
        }

        /// <summary>
        /// Drive each registered tracker's target Transform from its part's frame for this frame.
        /// Writes local TRS in the target's parent space, so a target parented under the player matches the
        /// part exactly (scale included) while a target placed elsewhere gets the exact world pose
        /// (scale approximate only when its parent is rotated + non-uniformly scaled). Position/rotation
        /// are always exact. Scale is written only when driveScale is on (localScale divided out).
        /// </summary>
        private void MirrorTrackers()
        {
            if (_partTrackers == null || _partTrackers.Count == 0) return;
            var internalPlayer = _core.Internal;
            if (internalPlayer == null) return;

            Matrix4x4 rootToWorld = transform.localToWorldMatrix;

            for (int i = 0; i < _partTrackers.Count; i++)
            {
                var tracker = _partTrackers[i];
                if (tracker == null || tracker.target == null) continue;

                // Resolve part index (cached; rebuilt on asset reload via MarkPartTrackersDirty).
                if (tracker.ResolvedIndex == SpriteStudioPartTracker.ResolveDirty)
                    tracker.ResolvedIndex = internalPlayer.ResolvePartIndex(tracker.partName);
                int partIndex = tracker.ResolvedIndex;

                if (partIndex < 0)
                {
                    // Part absent from this animation: nothing to follow, so deactivate the target.
                    if (tracker.target.gameObject.activeSelf) tracker.target.gameObject.SetActive(false);
                    continue;
                }

                // A hidden part still has a valid matrix, so FollowAlways keeps tracking it; Deactivate hides.
                bool hasState = internalPlayer.TryGetPartState(partIndex, out bool hidden, out float lsx, out float lsy);
                if (hidden && tracker.onHide == SpriteStudioPartTrackerHide.Deactivate)
                {
                    if (tracker.target.gameObject.activeSelf) tracker.target.gameObject.SetActive(false);
                    continue;
                }

                if (!internalPlayer.TryGetPartLocalMatrix(partIndex, out Matrix4x4 partLocal)) continue;

                // Re-activate if it was auto-deactivated (absent / hidden) on a previous frame.
                if (!tracker.target.gameObject.activeSelf) tracker.target.gameObject.SetActive(true);

                // Remove the part's own localScale (it scales the basis columns) so only the inherited
                // scale drives the target. Exact; a degenerate (~0) localScale axis is left untouched.
                if (tracker.driveScale && hasState)
                {
                    if (Mathf.Abs(lsx) > 1e-6f) partLocal.SetColumn(0, partLocal.GetColumn(0) / lsx);
                    if (Mathf.Abs(lsy) > 1e-6f) partLocal.SetColumn(1, partLocal.GetColumn(1) / lsy);
                }

                // Express the desired world pose in the target's parent-local space, then write local TRS.
                Matrix4x4 desiredWorld = rootToWorld * partLocal;
                Transform parent = tracker.target.parent;
                Matrix4x4 local = parent != null ? parent.worldToLocalMatrix * desiredWorld : desiredWorld;

                // FlipX/FlipY mirror the basis, and a mirror is not a rotation — Matrix4x4.rotation is
                // undefined on a negative-determinant matrix and would hand back a garbage quaternion. Make
                // the basis proper by negating one column, and put the mirror back as a negative scale, which
                // is how a 2D child renders one anyway. Position (column 3) is exact either way, so a tracker
                // follows its part correctly even with driveScale off; only the mirroring of the target's own
                // artwork needs driveScale.
                bool mirrored = local.determinant < 0f;
                if (mirrored) local.SetColumn(1, -local.GetColumn(1));

                tracker.target.localPosition = local.GetColumn(3);
                tracker.target.localRotation = local.rotation;
                if (tracker.driveScale)
                {
                    // 2D player: drive X/Y, preserve the target's own Z scale.
                    Vector3 s = local.lossyScale;
                    if (mirrored) s.y = -s.y;
                    Vector3 cur = tracker.target.localScale;
                    tracker.target.localScale = new Vector3(s.x, s.y, cur.z);
                }
            }
        }

        // --- Part tracking public API ---

        /// <summary>Register a part tracker at runtime. Returns the created entry.</summary>
        public SpriteStudioPartTracker RegisterPartTracker(string partName, Transform target,
            bool driveScale = false, SpriteStudioPartTrackerHide onHide = SpriteStudioPartTrackerHide.FollowAlways)
        {
            if (_partTrackers == null) _partTrackers = new List<SpriteStudioPartTracker>();
            var tracker = new SpriteStudioPartTracker
            {
                partName = partName,
                target = target,
                driveScale = driveScale,
                onHide = onHide,
            };
            _partTrackers.Add(tracker);
            return tracker;
        }

        /// <summary>Remove all trackers whose target is the given Transform. Returns the number removed.</summary>
        public int UnregisterPartTracker(Transform target)
        {
            if (_partTrackers == null) return 0;
            return _partTrackers.RemoveAll(t => t != null && t.target == target);
        }

        /// <summary>Is the named part hidden on the current frame? False if the part is unknown / absent.</summary>
        public bool TryGetPartHidden(string partName, out bool hidden)
        {
            hidden = false;
            if (_core.Internal == null) return false;
            int idx = _core.Internal.ResolvePartIndex(partName);
            return idx >= 0 && _core.Internal.TryGetPartState(idx, out hidden, out _, out _);
        }

        /// <summary>Player-local matrix (1/PPU folded) of the named part for the current frame. False if unknown.</summary>
        public bool TryGetPartLocalMatrix(string partName, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            if (_core.Internal == null) return false;
            int idx = _core.Internal.ResolvePartIndex(partName);
            return idx >= 0 && _core.Internal.TryGetPartLocalMatrix(idx, out matrix);
        }

        /// <summary>Force part trackers to re-resolve their part index (call after the asset/part set changes).</summary>
        private void MarkPartTrackersDirty()
        {
            if (_partTrackers == null) return;
            for (int i = 0; i < _partTrackers.Count; i++)
                if (_partTrackers[i] != null) _partTrackers[i].ResolvedIndex = SpriteStudioPartTracker.ResolveDirty;
        }

        private readonly System.Collections.Generic.List<Material> _submeshMaterials = new System.Collections.Generic.List<Material>();
        private uint _lastMaterialSignature;
        private MaterialPropertyBlock _maskMpb;

        /// <summary>
        /// Assigns, per SubMesh (= draw batch), the material matching its texture hash + stencil role.
        /// Skips reassignment if the layout (SubMesh count / texture / blend / role / op / comp) is identical
        /// to the previous frame. The mask-write SubMesh threshold (_MaskThreshold) can change every frame,
        /// so it is set separately via a MaterialPropertyBlock.
        /// </summary>
        private void UpdateSubmeshMaterials()
        {
            if (_animeAsset == null || _meshRenderer == null || _core.Internal == null) return;

            int subCount = _core.Internal.SubmeshCount;
            if (subCount <= 0) return;

            var submeshes = _core.Internal.Submeshes;

            // Layout signature. threshold is per-frame variable, so it is not included here and is passed via the MPB.
            uint signature = (uint)subCount;
            bool hasWrite = false;
            bool hasAddOn = false;
            for (int s = 0; s < subCount; s++)
            {
                var info = submeshes[s];
                signature = signature * 31u + info.TextureHash;
                signature = signature * 31u + info.ShaderHash;
                signature = signature * 31u + info.BlendType;
                signature = signature * 31u + (uint)info.Role;
                signature = signature * 31u + info.StencilOp;
                signature = signature * 31u + info.StencilComp;
                if (info.Role == StencilRole.Write) hasWrite = true;
                if (info.ShaderHash != 0) hasAddOn = true;
            }

            if (signature != _lastMaterialSignature || _submeshMaterials.Count != subCount)
            {
                _lastMaterialSignature = signature;
                _submeshMaterials.Clear();
                // Per-part AddOn material instances are owned by this player; recreate them when the
                // submesh layout changes (rare). Destroy the previous set first so they do not leak.
                DestroyAddOnInstances();
                if (_submeshMaterials.Capacity < subCount)
                {
                    _submeshMaterials.Capacity = subCount;
                }

                // Fallback for the unresolved case. Even if our own Materials is empty, an external reference
                // may still resolve, so do not early-return here; resolve per submesh instead.
                var mats = _animeAsset.Materials;
                Material fallback = (mats != null && mats.Count > 0) ? mats[0] : null;

                for (int s = 0; s < subCount; s++)
                {
                    var info = submeshes[s];
                    Material mat;
                    switch (info.Role)
                    {
                        case StencilRole.Write:
                            mat = _animeAsset.ResolveMaskWriteMaterial(info.TextureHash, info.StencilOp);
                            break;
                        case StencilRole.Test:
                            mat = _animeAsset.ResolveTestMaterial(info.TextureHash, info.BlendType, info.StencilComp);
                            break;
                        default:
                            if (info.ShaderHash != 0
                                && SpriteStudioAddOnRegistry.TryResolveShader(info.ShaderHash, _shaderCatalog, out var addonShader, out _))
                            {
                                // Per-part AddOn: its own material instance (so its per-frame params do
                                // not stomp other parts'), tracked for destruction. Params are pushed in
                                // the per-frame pass below.
                                mat = _animeAsset.CreatePerPartMaterialInstance(addonShader, info.TextureHash, info.BlendType);
                                if (mat != null) _addOnInstances.Add(mat);
                                else mat = _animeAsset.ResolveMaterial(info.TextureHash, info.BlendType);
                            }
                            else
                            {
                                // Resolve self -> Instance external reference, then apply the per-blend material variant.
                                mat = _animeAsset.ResolveMaterial(info.TextureHash, info.BlendType);
                            }
                            break;
                    }
                    if (mat == null)
                    {
                        // Recoverable fallback (the asset has no material/texture assigned, or it did not resolve).
                        // This path is only taken when the signature changes, so it does not spam. Rendering is
                        // visually broken but execution can continue, so Warning (a config-issue notice) is the
                        // appropriate severity.
                        Debug.LogWarning($"[SpriteStudioPlayer] TextureHash {info.TextureHash} not resolved (Submesh {s}, role {info.Role}). Falling back to material 0.");
                        mat = fallback;
                    }
                    _submeshMaterials.Add(mat);
                }
                _meshRenderer.SetSharedMaterials(_submeshMaterials);
            }

            // Set the mask-write SubMesh cutout threshold every frame via a per-submesh MaterialPropertyBlock
            // (the threshold can animate, so update it independently of the material-list cache).
            if (hasWrite)
            {
                for (int s = 0; s < subCount; s++)
                {
                    var info = submeshes[s];
                    if (info.Role != StencilRole.Write) continue;
                    if (_maskMpb == null) _maskMpb = new MaterialPropertyBlock();
                    _meshRenderer.GetPropertyBlock(_maskMpb, s);
                    _maskMpb.SetFloat(SpriteStudioMaterial.IdMaskThreshold, info.MaskThreshold);
                    _meshRenderer.SetPropertyBlock(_maskMpb, s);
                }
            }

            // Per-part AddOn params: pushed onto each part's own material instance every frame (the
            // params animate), independent of the signature-cached material list. Set on the Material
            // (not a MaterialPropertyBlock) so the per-part draws stay SRP Batcher compatible.
            if (hasAddOn)
            {
                for (int s = 0; s < subCount; s++)
                {
                    var info = submeshes[s];
                    if (info.ShaderHash == 0) continue;
                    if (s < _submeshMaterials.Count)
                    {
                        var m = _submeshMaterials[s];
                        if (m != null) SpriteStudioMaterial.ApplyAddOnParams(m, info);
                    }
                }
            }
        }

        private readonly System.Collections.Generic.List<Material> _addOnInstances = new System.Collections.Generic.List<Material>();

        // Destroy the per-part AddOn material instances this player owns (created per submesh-layout
        // change). The clones are HideAndDontSave, so nothing else releases them.
        private void DestroyAddOnInstances()
        {
            for (int i = 0; i < _addOnInstances.Count; i++)
            {
                var m = _addOnInstances[i];
                if (m == null) continue;
                if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
            }
            _addOnInstances.Clear();
        }

        #region ITimeControl (Timeline integration)

        private bool _timelineControlled;

        public void OnControlTimeStart()
        {
            _timelineControlled = true;
            // Set the animation up when clip control begins
            if (_core.Internal != null && !string.IsNullOrEmpty(_animationName))
            {
                _core.Internal.SetupAnimation(_animationName);
            }
        }

        public void SetTime(double time)
        {
            // Seek via Timeline: convert clip-local time (seconds) to a frame number and set it
            if (_core.Internal == null) return;
            int fps = _core.Internal.GetFrameRate();
            if (fps <= 0) fps = 60;
            _core.Internal.SetFrame((float)(time * fps));
        }

        public void OnControlTimeStop()
        {
            _timelineControlled = false;
        }

        #endregion

        #region SpriteStudioTrack (dedicated Timeline track) integration

        // The animation name the dedicated track currently has set up. Re-run SetupAnimation only when it changes.
        private string _timelineCurrentAnim;

        /// <summary>
        /// Called by the SpriteStudioTrack mixer when evaluating a clip. Switches to the specified animation
        /// (only when it changes) and seeks to the frame for the clip-local time (seconds). State is computed
        /// in bulk by Rust based on the time, so no matter how far the seek bar is scrubbed back and forth it
        /// stays deterministic and does not break.
        /// NOTE: Play-mode only (SpriteStudioPlayer is not ExecuteAlways, so in edit mode the Internal/Mesh
        /// are not created and nothing renders). Edit-mode seek preview is future work.
        /// </summary>
        public void EvaluateTimelineClip(string animationName, double clipLocalTime)
        {
            if (_core.Internal == null) return;
            _timelineControlled = true;

            if (!string.IsNullOrEmpty(animationName) && animationName != _timelineCurrentAnim)
            {
                if (_core.Internal.SetupAnimation(animationName))
                    _timelineCurrentAnim = animationName;
            }

            int fps = _core.Internal.GetFrameRate();
            if (fps <= 0) fps = 60;
            _core.Internal.SetFrame((float)(clipLocalTime * fps));
            // Rendering is done by the UpdateManager via PrepareFrame(advance=false) -> ApplyAndRender (same path as SetTime).
        }

        /// <summary>Releases Timeline control (on mixer graph teardown / track deactivation).</summary>
        public void ReleaseTimelineControl()
        {
            _timelineControlled = false;
            _timelineCurrentAnim = null;
        }

        #endregion
    }
}
