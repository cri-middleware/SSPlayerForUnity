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

        [Tooltip("Runtime texture replacements, keyed by cellmap / external-texture name. Applied on load and " +
                 "kept when the asset is swapped. Set programmatically with SetCellmapTexture or edit here; " +
                 "use GetReplaceableTextureNames() to discover the replaceable names.")]
        [SerializeField] private System.Collections.Generic.List<SpriteStudioCellmapTextureOverride> _cellmapTextureOverrides = new System.Collections.Generic.List<SpriteStudioCellmapTextureOverride>();

        // Per-player, draw-time replacement map derived from _cellmapTextureOverrides (never serialized).
        private readonly SpriteStudioTextureOverride _textureOverride = new SpriteStudioTextureOverride();

        public SpriteStudioAnimeAsset AnimeAsset
        {
            get => _animeAsset;
            set
            {
                _animeAsset = value;
                _core.ReloadAsset(_animeAsset);
                // The cellmap name->hash table belongs to the new asset: re-resolve name-keyed replacements.
                _textureOverride.Rebuild(_animeAsset, _cellmapTextureOverrides);
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
        /// <summary>
        /// The configured animation name (the serialized field the inspector labels "Animation Name").
        /// Read-only to callers: assigning it would only rewrite the field without touching the runtime, so
        /// to switch animation at runtime call <see cref="SetAnimation"/> (the setter is internal, used by the
        /// editor/importer to seed the field).
        /// </summary>
        public string CurrentAnimation
        {
            get => _animationName;
            internal set => _animationName = value;
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
        /// <summary>
        /// Playback speed, as a multiple of the frame rate. Read back from the runtime, which clamps a zero
        /// or negative rate to a <b>stop</b> — reverse playback is <see cref="PlaybackDirection"/>'s business, not the
        /// speed's — so setting -1 and reading it back gives 0, the stop it actually got. Falls back to the
        /// serialized value before the native player exists.
        /// </summary>
        public float SpeedScale
        {
            get => _core.Internal != null ? _core.Internal.GetSpeed() : _speedScale;
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
        public bool FrameSkipEnabled
        {
            get => _core.Internal != null ? _core.Internal.IsFrameSkipEnabled() : _skipFrames;
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
        /// Does not start playback; call <see cref="Play()"/> to start.
        /// <para>
        /// <b>Selecting the animation already set up does nothing at all.</b> It is a switch, so with
        /// nothing to switch to it costs nothing and disturbs nothing: the playhead stays, playback is not
        /// returned to Stopped, and the loop budget is not re-armed — so a state machine may call
        /// <c>SetAnimation(state.clip)</c> every frame. To restart, call <see cref="Play()"/>, which heads
        /// out; to drop the overrides, <see cref="ClearAllPartOverrides"/>.
        /// </para>
        /// <see cref="LoopCount"/>, <see cref="SpeedScale"/> and <see cref="FrameSkipEnabled"/> all survive the
        /// switch: the runtime keeps speed and frame skip itself, and the loop count — which it does reset
        /// on every transition, since the transition is what states it — is re-applied here from this
        /// component's own value. <see cref="PlaybackDirection"/> / <see cref="PlaybackStyle"/> survive it too (the
        /// runtime keeps the direction and style; only the heading within a ping-pong round trip is
        /// re-derived, so a switch never begins mid-bounce).
        /// Returns true if setup succeeded.
        /// </summary>
        public bool SetAnimation(string animName)
        {
            _animationName = animName;
#if UNITY_EDITOR
            // Keep the OnValidate baseline in sync so a later unrelated inspector edit does not restart.
            // Before the early return below, not after: the name reached the component either way.
            _appliedAnimation = _animationName;
#endif
            // Already set up: a switch with nothing to switch to. Returning here is not just an
            // optimisation -- ApplyPlaybackSettings below calls SetLoop, and the runtime RE-ARMS the
            // remaining-loop counter on every write. A state machine that re-asserts its current
            // animation each frame would otherwise reset the loop budget each frame, and a finite
            // LoopCount would never run out. Restarting is Play()'s job; it heads out.
            if (_core.IsAnimationSetUp(animName)) return true;
            bool ok = _core.SetAnimation(_animationName, this);
            // The runtime resets the loop count on every setup, so a switch would otherwise turn a
            // configured `LoopCount = 1` back into looping forever -- silently, and only when the
            // animation was changed at runtime rather than from the inspector. Every sibling Player
            // re-applies it inside its own setAnimation for exactly this reason.
            if (ok) ApplyPlaybackSettings();
            return ok;
        }

        // Push the serialized playback parameters to the runtime. Loop is the one that has to be here:
        // the Rust setup resets it on every transition, so the component's own configuration would be
        // lost on each SetAnimation without this. Speed and frame skip survive setup on their own --
        // they are the host's settings, not the clip's -- and are pushed anyway, because this is also
        // the path a serialized value takes to reach a freshly created player (Start / OnEnable), where
        // nothing else has written it yet.
        // Direction/style have no serialized default, so they start at
        // Forward/Normal and stay wherever their properties last put them -- the Rust setup
        // keeps them across a transition, like the speed and the frame skip.
        private void ApplyPlaybackSettings()
        {
            var p = _core.Internal;
            if (p == null) return;
            p.SetLoop(_loopCount);
            p.SetSpeed(_speedScale);
            p.SetSkipFrames(_skipFrames);
        }

        /// <summary>Starts playing the currently set-up animation from the beginning.</summary>
        public void Play() { _core.Play(); }

        /// <summary>Starts playing the currently set-up animation from the specified frame.</summary>
        public void Play(float startFrame) { _core.Play(startFrame); }

        #region Playback Control
        // Thinly expose, as the component's public API, the playback controls the internal player (Rust
        // runtime) already provides. State queries, seek, pause, reverse playback, etc. are all implemented
        // natively; this is delegation only (no new logic).

        /// <summary>Whether a playback session is active. True even while paused (false when stopped / not playing).</summary>
        public bool IsPlaying => _core.Internal?.IsPlaying() ?? false;

        /// <summary>Whether currently paused (true only while a playback session is active and paused).</summary>
        /// <summary>
        /// Whether the last update crossed a loop boundary. A <b>pulse</b>, true only inside the tick
        /// that crossed — take <c>OnAnimationLooped</c> instead if you want it delivered rather than
        /// polled.
        /// </summary>
        public bool JustLooped => _core.Internal?.JustLooped() ?? false;

        /// <summary>Names of the animations in the loaded asset, empty before one is assigned.</summary>
        public string[] AnimationNames => _animeAsset != null && _animeAsset.AnimationNames != null
            ? _animeAsset.AnimationNames
            : System.Array.Empty<string>();

        public bool IsPausing
        {
            get
            {
                // The runtime keeps "paused" implying "playing": is_pausing is false before the first
                // play and after stop, and pausing a stopped animation is ignored. So this needs no
                // IsPlaying() guard of its own.
                var p = _core.Internal;
                return p != null && p.IsPausing();
            }
        }

        /// <summary>
        /// Whether every configured loop has played.
        /// <para>
        /// A <b>state</b>, not a pulse: it latches on completion and holds until the next
        /// <see cref="Play()"/> or <see cref="SetAnimation"/>, so a caller that was not subscribed
        /// when <see cref="OnAnimationFinished"/> fired can still ask whether the run is over. That
        /// also makes it what a coroutine waits on:
        /// <c>Play(); yield return new WaitUntil(() =&gt; player.IsFinished);</c> — <c>Play</c>
        /// re-arms it, so the wait does not fall through on a previous run's completion.
        /// </para>
        /// <para>
        /// Never true under an infinite <see cref="LoopCount"/>, and <b>not raised by
        /// <see cref="Stop"/></b> — a commanded stop is not a completion. That is what separates it
        /// from <c>!IsPlaying</c>, which cannot tell the two apart. Take
        /// <see cref="OnAnimationFinished"/> for the one-shot edge.
        /// </para>
        /// </summary>
        public bool IsFinished => _core.Internal?.IsFinished() ?? false;

        /// <summary>
        /// Which way the playhead is actually travelling this frame. This is <b>not</b>
        /// <see cref="PlaybackDirection"/>, which is only the heading that was configured: under
        /// <see cref="SpriteStudioPlaybackStyle.PingPong"/> the return leg runs backward while it still
        /// reads false. A speed of zero or below is a stop, not a reversal, and does not flip it.
        /// <para>
        /// This is the gate to put in front of audio. SpriteStudio has no reverse audio, so a sound
        /// key crossed on a backward leg is not meant to sound — the player applies exactly this
        /// test itself before handing one to the audio backend, and a project driving its own audio
        /// from <c>OnAudio</c> wants the same one. True before anything has played: forward is the
        /// resting state.
        /// </para>
        /// </summary>
        public bool IsPlayingForward => _core.Internal?.IsPlayingForward() ?? true;

        /// <summary>
        /// Holds playback on the current frame. Idempotent, and ignored while nothing is playing.
        /// Lift it with <see cref="Resume"/> — Play() rewinds to the start, so it cannot resume.
        /// </summary>
        public void Pause()
        {
            _core.Internal?.Pause();
        }

        /// <summary>
        /// Lifts a <see cref="Pause"/>, carrying on from the same frame. Idempotent, and a no-op on a
        /// stopped animation — starting one is Play()'s job, and it rewinds.
        /// </summary>
        public void Resume()
        {
            _core.Internal?.Resume();
        }

        /// <summary>
        /// Stops playback. The playhead <b>stays where it stands</b> — nothing rewinds, so the
        /// component keeps drawing the frame it stopped on. <see cref="Play()"/> is what returns to
        /// the start, and <see cref="FrameNo"/> seeks anywhere else.
        /// </summary>
        public void Stop() => _core.Internal?.Stop();

        /// <summary>Current playback frame (fractional allowed). Set to seek to an arbitrary frame.</summary>
        public float FrameNo
        {
            get => _core.Internal?.GetFrame() ?? 0f;
            set => _core.Internal?.SetFrame(value);
        }

        /// <summary>
        /// Frames in the range being played, both ends inclusive. This follows
        /// <see cref="SetAnimationSection"/>: without one it is the whole animation, with one it is the
        /// section. 0 when nothing is loaded.
        /// </summary>
        public int TotalFrames => _core.Internal?.GetTotalFrames() ?? 0;

        /// <summary>Playback frame rate (fps). Set to change it at runtime. Get returns 0 when nothing is loaded.</summary>
        public int FrameRate
        {
            get => _core.Internal?.GetFrameRate() ?? 0;
            set => _core.Internal?.SetFrameRate(value);
        }

        /// <summary>
        /// Which way the playhead is configured to walk. It <b>survives</b> <see cref="SetAnimation(string)"/>:
        /// "play this backwards" describes the playback, not the clip that happens to be loaded, so the
        /// runtime keeps it across a transition (as it keeps <see cref="SpeedScale"/> and
        /// <see cref="FrameSkipEnabled"/>). Set it once, whenever you like. There is no serialized default, so it
        /// starts Forward on a freshly created component.
        /// </summary>
        public SpriteStudioPlaybackDirection PlaybackDirection
        {
            get => (SpriteStudioPlaybackDirection)(_core.Internal?.GetPlaybackDirection() ?? 0);
            set
            {
                var p = _core.Internal;
                if (p != null) p.SetPlaybackStyle((int)value, p.GetPlaybackStyle());
            }
        }

        /// <summary>
        /// What the playhead does at the far end of the section. Like
        /// <see cref="PlaybackDirection"/> it <b>survives</b> <see cref="SetAnimation(string)"/>; only the
        /// heading within a round trip is re-derived, so a switch made on the return leg starts the new
        /// animation walking outward rather than mid-bounce. There is no serialized default, so it starts
        /// Normal on a freshly created component.
        /// </summary>
        public SpriteStudioPlaybackStyle PlaybackStyle
        {
            get => (SpriteStudioPlaybackStyle)(_core.Internal?.GetPlaybackStyle() ?? 0);
            set
            {
                var p = _core.Internal;
                if (p != null) p.SetPlaybackStyle(p.GetPlaybackDirection(), (int)value);
            }
        }

        /// <summary>
        /// Set the heading and the style together, which is the only way to change both without the
        /// intermediate state the two properties above would pass through.
        /// </summary>
        public void SetPlaybackDirection(SpriteStudioPlaybackDirection direction, SpriteStudioPlaybackStyle style)
            => _core.Internal?.SetPlaybackStyle((int)direction, (int)style);

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
        public int FindPartIndex(string partName) => _core.Internal?.FindPartIndex(partName) ?? -1;

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

        /// <summary>
        /// Override a Normal part's colour with a single value (all four corners).
        /// </summary>
        /// <remarks>
        /// The colour comes first and <paramref name="blendOp"/> trails with a default, which is the
        /// family's order (SDK: <c>20_design/40_api_conventions</c>) and lets the common case read
        /// <c>SetPartColorOverride("hat", Color.red)</c>. The C ABI puts the blend first; this face
        /// deliberately does not.
        /// </remarks>
        public bool SetPartColorOverride(string partName, Color color,
            SpriteStudioBlendOp blendOp = SpriteStudioBlendOp.Mix,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => _core.Internal?.SetPartColorOverride(partName, color, blendOp, priority) ?? false;

        /// <inheritdoc cref="SetPartColorOverride(string, Color, SpriteStudioBlendOp, SpriteStudioOverridePriority)"/>
        public bool SetPartColorOverride(int partIndex, Color color,
            SpriteStudioBlendOp blendOp = SpriteStudioBlendOp.Mix,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => _core.Internal?.SetPartColorOverride(partIndex, color, blendOp, priority) ?? false;

        /// <summary>
        /// Override a Normal part's colour with four distinct corners, for a gradient.
        /// </summary>
        /// <remarks>
        /// <paramref name="corners"/> is one argument rather than four, so this reads the same shape as
        /// its single-colour sibling, and the Brain's left-top / right-top / left-bottom / right-bottom
        /// order is carried by field names rather than by position — where swapping two of them silently
        /// mirrored the gradient.
        /// </remarks>
        public bool SetPartColorOverrideCorners(string partName, SpriteStudioCornerColors corners,
            SpriteStudioBlendOp blendOp = SpriteStudioBlendOp.Mix,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => _core.Internal?.SetPartColorOverrideCorners(partName, corners, blendOp, priority) ?? false;

        /// <inheritdoc cref="SetPartColorOverrideCorners(string, SpriteStudioCornerColors, SpriteStudioBlendOp, SpriteStudioOverridePriority)"/>
        public bool SetPartColorOverrideCorners(int partIndex, SpriteStudioCornerColors corners,
            SpriteStudioBlendOp blendOp = SpriteStudioBlendOp.Mix,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => _core.Internal?.SetPartColorOverrideCorners(partIndex, corners, blendOp, priority) ?? false;

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

        /// <summary>
        /// Part names in part-index order, empty before an <see cref="AnimeAsset"/> is set up.
        /// <para>
        /// A part authored without a name reads as the empty string rather than being skipped, so the
        /// array stays aligned with the <c>PartIndex</c> every event carries — which is what turns one
        /// back into something a human reads. <c>Length</c> is the part count.
        /// </para>
        /// </summary>
        public string[] GetPartNames() => _core.Internal?.GetPartNames() ?? System.Array.Empty<string>();

        /// <summary>Names of the cellmaps in the loaded asset (for discovering cell-override targets).</summary>
        public string[] GetCellmapNames() => _core.Internal?.GetCellmapNames() ?? System.Array.Empty<string>();

        /// <summary>Names of the cells in the named cellmap (for discovering cell-override targets).</summary>
        public string[] GetCellNames(string cellmapName) => _core.Internal?.GetCellNames(cellmapName) ?? System.Array.Empty<string>();

        #endregion

        private void Start()
        {
            if (string.IsNullOrEmpty(_animationName)) return;
            // Always call SetAnimation so the first frame is posed even when AutoPlay is off; actually start
            // playback only when AutoPlay is on. SetAnimation pushes the serialized loop/speed/skip itself,
            // so the order is simply animation -> play.
            if (SetAnimation(_animationName) && _autoPlay) Play();
        }

        private readonly SpriteStudioPlaybackCore _core = new SpriteStudioPlaybackCore();

        // The native player, exposed for tests that need to read what actually reached the runtime
        // rather than what this component was told. Internal, not public: a raw native handle is not
        // part of the package's API (ISpriteStudioRenderable.GetLiveHandle is explicit for the same
        // reason).
        internal SpriteStudioInternalPlayer InternalPlayer => _core.Internal;

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
                // Runtime-only, like every material this package builds: the owner destroys it in
                // OnDisable. Without the flag this mesh is serialized into the scene, because
                // [ExecuteAlways] builds it in edit mode and MeshFilter.sharedMesh is a serialized
                // field - so saving a scene that holds a player writes the whole mesh into it, and
                // rewrites it on every save. It is rebuilt here on load either way.
                _sharedMesh.hideFlags = HideFlags.HideAndDontSave;
                _sharedMesh.MarkDynamic(); // Optimize for per-frame updates
            }
            _meshFilter.sharedMesh = _sharedMesh;

            _core.EnsureCreated(_animeAsset, _subFrameEnabled);
            ApplyPixelsPerUnit();
            // Resolve the serialized cellmap texture replacements against the asset (applied on the next build).
            _textureOverride.Rebuild(_animeAsset, _cellmapTextureOverrides);
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
            DestroyTextureOverrideInstances();
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

            // Reflect inspector edits to the replacement list (or an asset swap) into the draw-time map.
            // Rebuild bumps the override version, so UpdateSubmeshMaterials rebuilds the material list.
            _textureOverride.Rebuild(_animeAsset, _cellmapTextureOverrides);

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
                        // When the animation/asset is changed in the inspector, set up again (which
                        // re-applies the playback settings) and restart playback from the head.
                        if (SetAnimation(_animationName)) Play();
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
                    tracker.ResolvedIndex = internalPlayer.FindPartIndex(tracker.partName);
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

                if (!internalPlayer.TryGetPartTransform(partIndex, out Matrix4x4 partLocal)) continue;

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

                // The three axes are independent, and each disabled one leaves the target's own value
                // alone rather than writing an identity — a constraint that drives less, not a transform
                // that replaces more.
                if (tracker.drivePosition) tracker.target.localPosition = local.GetColumn(3);
                if (tracker.driveRotation) tracker.target.localRotation = local.rotation;
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
            bool driveScale = false, SpriteStudioPartTrackerHide onHide = SpriteStudioPartTrackerHide.FollowAlways,
            bool drivePosition = true, bool driveRotation = true)
        {
            if (_partTrackers == null) _partTrackers = new List<SpriteStudioPartTracker>();
            var tracker = new SpriteStudioPartTracker
            {
                partName = partName,
                target = target,
                drivePosition = drivePosition,
                driveRotation = driveRotation,
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
            int idx = _core.Internal.FindPartIndex(partName);
            return idx >= 0 && _core.Internal.TryGetPartState(idx, out hidden, out _, out _);
        }

        /// <summary>
        /// Is the named part a bone-skinned mesh? Such a part cannot be followed by <see cref="RegisterPartTracker"/>
        /// as you would expect: its vertices are drawn by bound bones, but a tracker reads the part's own
        /// transform, which stays at its setup node (near the character root) — so the target lands off the art.
        /// To follow a skinned mesh's position, attach a null / bone-point (bone or joint) part at that spot in
        /// SpriteStudio and track that instead. Returns false when the part is unknown / not loaded; a rigid
        /// (deform-only) mesh reports false because it tracks correctly.
        /// </summary>
        public bool TryGetPartSkinnedMesh(string partName, out bool isSkinnedMesh)
        {
            isSkinnedMesh = false;
            if (_core.Internal == null) return false;
            int idx = _core.Internal.FindPartIndex(partName);
            return idx >= 0 && _core.Internal.TryGetPartSkinnedMesh(idx, out isSkinnedMesh);
        }

        /// <summary>Player-local matrix (1/PPU folded) of the named part for the current frame. False if unknown.</summary>
        /// <remarks>
        /// This is a general affine basis and may be a reflection, i.e. have a negative determinant.
        /// <see cref="FlipX"/> / <see cref="FlipY"/> mirror it, and so does a negative scale authored on the
        /// part or inherited from its parents (the draw matrix folds both in). They compose, so two
        /// mirrorings cancel: an already-mirrored part comes back with a positive determinant once the
        /// player is flipped. Never assume the sign from the flip settings alone.
        /// <para>
        /// A reflection is not a rotation, and a quaternion cannot represent one, so <c>matrix.rotation</c>
        /// and <c>matrix.lossyScale</c> are undefined whenever <c>matrix.determinant</c> is negative —
        /// reading them there yields garbage rather than a wrong-but-meaningful value. Always check the
        /// determinant first. The translation (column 3) is exact either way.
        /// </para>
        /// <para>
        /// To recover an orientation, make the basis proper first — negate one column — and carry the
        /// reflection separately, as a negative scale for instance. That is what part tracking does. Since
        /// the sign goes on the Y axis, a target driven with <c>driveScale</c> off is never mirrored itself
        /// and, under <see cref="FlipX"/> only, its orientation also lands 180 degrees away: expressing a
        /// horizontal mirror as a negative Y scale costs an extra half turn, and that is what remains in
        /// the rotation once the scale is dropped.
        /// </para>
        /// </remarks>
        public bool TryGetPartTransform(string partName, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            if (_core.Internal == null) return false;
            int idx = _core.Internal.FindPartIndex(partName);
            return idx >= 0 && _core.Internal.TryGetPartTransform(idx, out matrix);
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
            // Fold in the texture-replacement version so changing a replacement rebuilds the material list
            // even when the draw layout (count/hashes/blends/roles) is otherwise identical.
            signature = signature * 31u + (uint)_textureOverride.Version;
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
                // Texture-replacement clones are owned per-player too; rebuilt below alongside the list.
                DestroyTextureOverrideInstances();
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
                    // Whether mat is already a fresh instance this player owns (an AddOn per-part clone). Such
                    // a material can have its texture swapped in place; a shared/cached one must be cloned.
                    bool matIsOwnedInstance = false;
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
                                if (mat != null) { _addOnInstances.Add(mat); matIsOwnedInstance = true; }
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
                    // Runtime cellmap texture replacement (Player-side override): swap the bound texture for
                    // this batch's hash. Owned AddOn instances are edited in place; every other (shared)
                    // material is cloned so the asset's material stays untouched and per-player.
                    mat = ApplyTextureOverride(mat, info.TextureHash, matIsOwnedInstance);
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

        // Per-player material clones created for cellmap texture replacement (a clone of the asset-shared
        // material with its main texture swapped). Owned by this player and rebuilt with the material list.
        private readonly System.Collections.Generic.List<Material> _textureOverrideInstances = new System.Collections.Generic.List<Material>();

        // Apply a cellmap texture replacement to a resolved material. Returns the material unchanged when no
        // replacement targets this hash. An owned AddOn instance is swapped in place; a shared/cached
        // material is cloned (never mutated) so the replacement stays per-player and SRP-Batcher friendly.
        private Material ApplyTextureOverride(Material mat, uint textureHash, bool matIsOwnedInstance)
        {
            if (mat == null || !_textureOverride.HasAny || !_textureOverride.TryGet(textureHash, out var tex))
            {
                return mat;
            }
            if (matIsOwnedInstance)
            {
                mat.mainTexture = tex;
                return mat;
            }
            var clone = new Material(mat) { hideFlags = HideFlags.HideAndDontSave, name = mat.name + "_TexOverride" };
            clone.mainTexture = tex;
            _textureOverrideInstances.Add(clone);
            return clone;
        }

        // Destroy the texture-replacement material clones this player owns (HideAndDontSave; nothing else
        // releases them). Called on a material-list rebuild and on disable, like the AddOn instances.
        private void DestroyTextureOverrideInstances()
        {
            for (int i = 0; i < _textureOverrideInstances.Count; i++)
            {
                var m = _textureOverrideInstances[i];
                if (m == null) continue;
                if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
            }
            _textureOverrideInstances.Clear();
        }

        #region Runtime texture replacement (Player-side override)

        /// <summary>
        /// Replace, at runtime, the texture bound for a cellmap (or external-texture) name for THIS player
        /// only — the asset and other players are untouched. Pass <paramref name="texture"/> = null to
        /// restore the original. This is a Player-side override and is deliberately separate from the
        /// Override Layer API (per-part Color/Cell/Visibility); do not mix the two.
        ///
        /// The replacement is keyed by NAME and persists across an asset swap (it re-resolves to the new
        /// resource's hashes). It does not propagate into Instance children, which are independent players
        /// with their own textures — set it on the child too if needed.
        ///
        /// Note: a TextureChange (TCHG) frame binds the external-texture hash, so replacing a cellmap name
        /// also covers that cellmap's image-path hash; replace the external-texture name (see
        /// <see cref="GetReplaceableTextureNames"/>) to target a TCHG destination that resolves to a different texture.
        /// The replacement texture should share the original's size / atlas layout.
        ///
        /// The swap applies to every draw that binds the texture, mask writers included (a draw_as_mask part
        /// sampling this cellmap cuts against the replacement's alpha) — the same whole-map behaviour as
        /// ForGodot. Replace with a texture whose alpha silhouette matches if the cellmap is used as a mask.
        /// </summary>
        /// <param name="cellmapName">Cellmap name (e.g. "common") or external-texture name (e.g. "common.png").</param>
        /// <param name="texture">Replacement texture, or null to restore the original.</param>
        public void SetCellmapTexture(string cellmapName, Texture2D texture)
        {
            if (string.IsNullOrEmpty(cellmapName)) return;

            // Update the name-keyed serialized source of truth (add / replace / remove), then re-resolve.
            SpriteStudioTextureOverride.SetEntry(_cellmapTextureOverrides, cellmapName, texture);
            _textureOverride.Rebuild(_animeAsset, _cellmapTextureOverrides);
            // Do NOT touch the material list here: UpdateSubmeshMaterials reads job-managed submesh buffers,
            // so calling it off-cycle (e.g. from the caller's Update) could race an in-flight mesh job. The
            // version fold makes the next ApplyAndRender rebuild the materials; that call site completes the
            // job first. In edit mode nothing drives that, so poke the static preview.
#if UNITY_EDITOR
            if (!Application.isPlaying) _editPreviewDirty = true;
#endif
        }

        /// <summary>
        /// The current replacement texture for a cellmap name, or null when none is set (like ForGodot's
        /// get_cellmap_texture, this returns only the override value, never the original texture).
        /// </summary>
        public Texture2D GetCellmapTexture(string cellmapName)
        {
            return SpriteStudioTextureOverride.GetEntry(_cellmapTextureOverrides, cellmapName);
        }

        /// <summary>
        /// The names accepted by <see cref="SetCellmapTexture"/>: every cellmap name plus every
        /// external-texture (TextureChange target) name in the current asset. Empty when no asset is set.
        /// (Distinct from <see cref="GetCellmapNames"/>, which lists cellmaps only for the cell Override
        /// Layer; texture replacement can also target external-texture names.)
        /// </summary>
        public string[] GetReplaceableTextureNames()
        {
            return _animeAsset != null ? _animeAsset.GetReplaceableTextureNames() : System.Array.Empty<string>();
        }

        #endregion

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
