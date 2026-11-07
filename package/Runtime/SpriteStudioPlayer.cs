using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Timeline;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Plays a SpriteStudio animation (.ssab) in world space, through this GameObject's MeshFilter and
    /// MeshRenderer. Assign <see cref="AnimeAsset"/> and an animation name; <see cref="AutoPlay"/> starts it.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    // One MeshFilter and one MeshRenderer serve one player: a second on the same GameObject would assign its
    // own mesh and material list over the first's.
    [DisallowMultipleComponent]
    // [ExecuteAlways]: in edit mode it builds the start-frame preview itself, because
    // SpriteStudioUpdateManager, which drives Play mode, does not run there.
    [ExecuteAlways]
    [AddComponentMenu("SpriteStudio/SpriteStudio Player")]
    public class SpriteStudioPlayer : MonoBehaviour, ITimeControl, ISpriteStudioRenderable
#if UNITY_EDITOR
        , ISpriteStudioEditPreview
#endif
    {
        [Header("SpriteStudio Asset")]
        [Tooltip("The converted ssab asset to render")]
        [SerializeField] private SpriteStudioAnimeAsset _animeAsset;

        [Tooltip("Extra per-part shaders, or overrides for the built-in AddOns. Optional: the built-in AddOns draw without it.")]
        [SerializeField] private SpriteStudioShaderCatalog _shaderCatalog;

        [Tooltip("Registration list of user-owned Transforms that follow specified parts. The player does not create GameObjects; " +
                 "it only drives the registered Transforms every frame (constraint style).")]
        [SerializeField] private List<SpriteStudioPartTracker> _partTrackers = new List<SpriteStudioPartTracker>();

        [Header("Audio")]
        [Tooltip("Play the sounds of audio keys (ADIO) with the built-in playback. OFF: the audio events still fire, for a host that plays them itself.")]
        [SerializeField] private bool _playAudio = true;
        [Tooltip("Volume of the built-in playback.")]
        [Range(0f, 1f)]
        [SerializeField] private float _audioVolume = 1f;

        private SpriteStudioAudioController _audioController;

        [Tooltip("Runtime texture replacements, keyed by cellmap / external-texture name. Applied on load and " +
                 "kept when the asset is swapped. Set programmatically with SetCellmapTexture or edit here; " +
                 "use GetReplaceableTextureNames() to discover the replaceable names.")]
        [SerializeField] private System.Collections.Generic.List<SpriteStudioCellmapTextureOverride> _cellmapTextureOverrides = new System.Collections.Generic.List<SpriteStudioCellmapTextureOverride>();

        // Per-player, draw-time replacement map derived from _cellmapTextureOverrides (never serialized).
        private readonly SpriteStudioTextureOverride _textureOverride = new SpriteStudioTextureOverride();

        /// <summary>
        /// The animation asset to play. Assigning null unloads it, and the player draws nothing. Another asset
        /// loses what the runtime held for the last one (overrides, tracks, section), and on the next frame
        /// starts Animation Name as an enable does, unless a script selects an animation first.
        /// </summary>
        public SpriteStudioAnimeAsset AnimeAsset
        {
            get => _animeAsset;
            set
            {
                _animeAsset = value;
                if (_animeAsset == null)
                {
                    UnloadAsset();
                }
                else if (_core.Internal != null)
                {
                    _core.ReloadAsset(_animeAsset);
                    _timelineCurrentAnim = null;
                    _startAfterSwap = _started;
                }
                else if (isActiveAndEnabled)
                {
                    // The runtime went with the pack a null asset let go of; this one brings it back.
                    _core.EnsureCreated(_animeAsset, _subFrameEnabled);
                    ApplyPixelsPerUnit();
                    _startAfterSwap = _started;
                }
                // The cellmap name->hash table belongs to the new asset: re-resolve name-keyed replacements.
                _textureOverride.Rebuild(_animeAsset, _cellmapTextureOverrides);
                // The part set (and thus name->index mapping) may have changed: re-resolve trackers.
                MarkPartTrackersDirty();
#if UNITY_EDITOR
                _appliedAsset = _animeAsset;
#endif
            }
        }

        /// <summary>
        /// The Mesh Renderer's sorting layer -- the draw order belongs to the renderer, as it does for any
        /// renderer, and is set in its Inspector (Additional Settings) or here.
        /// </summary>
        public string SortingLayerName
        {
            get => SortingRenderer.sortingLayerName;
            set => SortingRenderer.sortingLayerName = value;
        }

        /// <summary>The Mesh Renderer's order in layer. See <see cref="SortingLayerName"/>.</summary>
        public int SortingOrder
        {
            get => SortingRenderer.sortingOrder;
            set => SortingRenderer.sortingOrder = value;
        }

        private MeshRenderer SortingRenderer => _meshRenderer != null ? _meshRenderer : GetComponent<MeshRenderer>();

        [Header("Playback Settings")]
        [Tooltip("Name of the animation to play, as listed in the asset.")]
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
        /// <summary>Whether the animation starts by itself when the player starts, and each time it is enabled again.</summary>
        public bool AutoPlay
        {
            get => _autoPlay;
            set => _autoPlay = value;
        }

        [Tooltip("Loop count. -1 = infinite loop (0 is a synonym) / 1 = play once / n = play n times")]
        [SerializeField] private int _loopCount = -1;
        /// <summary>How many times the animation plays: 1 once, n that many times, -1 (or 0) forever.</summary>
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
        /// <summary>Whether to draw between frames rather than on whole frames, for smooth motion at a high refresh rate or in slow motion.</summary>
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
        /// <summary>On: playback follows elapsed time, skipping frames under load. Off: each update advances exactly one frame.</summary>
        public bool FrameSkipEnabled
        {
            get => _core.Internal != null ? _core.Internal.IsFrameSkipEnabled() : _skipFrames;
            set
            {
                _skipFrames = value;
                _core.Internal?.SetSkipFrames(value);
            }
        }

        [Tooltip("Pixels per one Unity world unit. Converts SS pixel coordinates into world units. Default 1 = 1:1 pixel scale. Set to e.g. 100 to match Unity's sprite convention.")]
        [SerializeField] private float _pixelsPerUnit = 1f;
        /// <summary>SpriteStudio pixels per Unity world unit. 1 draws a pixel as one unit; 100 matches Unity's sprite convention.</summary>
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
            // configured `LoopCount = 1` back into looping forever.
            if (ok)
            {
                ApplyPlaybackSettings();
                _startAfterSwap = false;
                // While a Timeline drives this player its clip decides track 0: the clip that set its animation
                // up sets it up again at the next evaluation.
                _timelineCurrentAnim = null;
            }
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
        public void Play()
        {
            // With Auto Play on, the enable that creates the runtime starts it all the same.
            if (!_autoPlay) _core.ReportNotEnabled(nameof(Play), this);
            _core.Play();
        }

        /// <summary>Starts playing the currently set-up animation from the specified frame.</summary>
        public void Play(float startFrame)
        {
            _core.ReportNotEnabled(nameof(Play), this);
            _core.Play(startFrame);
        }

        #region Playback Control
        // Thinly expose, as the component's public API, the playback controls the internal player (Rust
        // runtime) already provides. State queries, seek, pause, reverse playback, etc. are all implemented
        // natively; this is delegation only (no new logic).

        /// <summary>Whether a playback session is active. True even while paused (false when stopped / not playing).</summary>
        public bool IsPlaying => _core.Internal?.IsPlaying() ?? false;

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

        /// <summary>Whether currently paused (true only while a playback session is active and paused).</summary>
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
        /// <see cref="Play()"/> or a switch to another animation, so a caller that was not subscribed
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
        /// reads <see cref="SpriteStudioPlaybackDirection.Forward"/>. A speed of zero or below is a stop,
        /// not a reversal, and does not flip it.
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
            _core.ReportNotEnabled(nameof(Pause), this);
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
            set
            {
                _core.ReportNotEnabled(nameof(FrameNo), this);
                _core.Seek(value);
            }
        }

        /// <summary>
        /// Stops every sound this component's built-in playback has started. Nothing else stops one:
        /// <see cref="Pause"/>, <see cref="Stop"/> and switching the animation leave a sound that has started
        /// to play out, so call this when the host wants silence now. Disabling or destroying the component
        /// stops them too.
        /// </summary>
        public void StopAllSounds() => _audioController?.StopAll();

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
        /// <see cref="FrameSkipEnabled"/>). It lives in the runtime, not in a serialized field. Disabling the component,
        /// or setting <see cref="AnimeAsset"/> to null, disposes that runtime: a value set before another is
        /// built is dropped, and the new one starts Forward.
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
        /// animation walking outward rather than mid-bounce. It lives in the runtime, not in a serialized field. Disabling the
        /// component, or setting <see cref="AnimeAsset"/> to null, disposes that runtime: a value set before
        /// another is built is dropped, and the new one starts Normal.
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
        /// Limits the playback range by frame, both ends included. An end outside the animation
        /// (<c>-1</c>, say) is the animation's edge on that side.
        /// Returns false, changing neither the section nor the playhead, when both ends are inside the
        /// animation and the end is before the start; the first refusal of a pair logs a warning.
        /// </summary>
        public bool SetAnimationSection(int startFrame, int endFrame) => _core.SetAnimationSection(startFrame, endFrame, this);

        /// <summary>
        /// <see cref="SetAnimationSection"/> with the ends named by labels of the animation set up: from
        /// <paramref name="startLabel"/>'s frame plus <paramref name="startOffset"/> to
        /// <paramref name="endLabel"/>'s frame plus <paramref name="endOffset"/>, as an Instance part's start
        /// and end labels are. <c>_start</c> and <c>_end</c> name the animation's own first and last frame, as
        /// they do in SpriteStudio. It does not play, and like any section <see cref="SetAnimation"/> to
        /// another animation drops it.
        /// Returns false, changing neither the section nor the playhead, when the animation has no such label
        /// or the frames it lands on are a section <see cref="SetAnimationSection"/> refuses; the first failure
        /// logs a warning. Before an animation is set up it returns false and says nothing.
        /// </summary>
        public bool SetAnimationSectionByLabel(string startLabel, string endLabel, int startOffset = 0, int endOffset = 0)
            => _core.SetAnimationSectionByLabel(startLabel, endLabel, startOffset, endOffset, this);

        /// <summary>
        /// The labels of <paramref name="animationName"/> — the animation set up when omitted — in frame
        /// order, with the frame range it was authored with. Null when the loaded asset has no such animation,
        /// or, with the name omitted, before one is set up. Read out of the asset on every call, so hold the
        /// table rather than calling this every frame.
        /// </summary>
        public SpriteStudioLabelTable GetLabelTable(string animationName = null) => _core.Internal?.GetLabelTable(animationName);

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
        /// Override a Normal part's colour with a single value (all four corners). The colour comes first
        /// and <paramref name="blendOp"/> trails with a default, so the common case reads
        /// <c>SetPartColorOverride("hat", Color.red)</c>.
        /// </summary>
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
        /// <paramref name="corners"/> is one argument, so the order is carried by field names rather
        /// than by position.
        /// </summary>
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

        #region Animation Mixing
        // The runtime's track stack (motion blending). Track 0 is the animation SetAnimation selects; the
        // tracks above it lay other animations of the same asset over it. SetAnimation to another animation
        // folds the stack back to track 0 alone, and every track's settings but track 0's go with it.
        // Durations are in seconds. Mutators return false when nothing is loaded, the animation name is not
        // in the asset, or the runtime refuses the call. A read of a track or source that does not exist
        // returns 0 (check TrackCount first), except GetTrackFadeProgress (-1 when not fading) and the
        // source handles (-1 for none), where 0 is a real answer.

        /// <summary>
        /// Lays an animation on a track, replacing what the track held. On track 0 it switches the
        /// animation as <see cref="SetAnimation"/> does, keeping the tracks above, and
        /// <see cref="CurrentAnimation"/> names it from the call on.
        /// </summary>
        public bool SetTrackAnimation(int track, string animName)
        {
            var p = _core.Internal;
            if (p == null || !p.SetTrackAnimation(track, animName)) return false;
            if (track == 0) OnTrackZeroSwitched();
            return true;
        }

        /// <summary>
        /// Fades <paramref name="animName"/> in on a track over <paramref name="duration"/> seconds while
        /// everything the track carried fades out. On track 0 the incoming animation is the current one
        /// from the call on: <see cref="CurrentAnimation"/>, <see cref="IsPlaying"/> /
        /// <see cref="IsFinished"/>, the frame and the events are its own, so <see cref="SetAnimation"/>
        /// can go back to the animation it left. A crossfade started during another takes the one in
        /// flight over where it stands.
        /// </summary>
        public bool Crossfade(int track, string animName, float duration)
        {
            var p = _core.Internal;
            if (p == null || !p.Crossfade(track, animName, duration)) return false;
            if (track == 0) OnTrackZeroSwitched();
            return true;
        }

        /// <summary>Number of tracks on the stack, track 0 included. 0 when nothing is loaded.</summary>
        public int TrackCount => _core.Internal?.GetTrackCount() ?? 0;

        /// <summary>Empties a track; above track 0 it no longer contributes to the pose.</summary>
        public bool ClearTrack(int track) => _core.Internal?.ClearTrack(track) ?? false;

        /// <summary>How strongly a track is laid over the ones below it: 0 contributes nothing, 1 is the track in full, above 1 extrapolates.</summary>
        public bool SetTrackAlpha(int track, float alpha) => _core.Internal?.SetTrackAlpha(track, alpha) ?? false;

        /// <inheritdoc cref="SetTrackAlpha"/>
        public float GetTrackAlpha(int track) => _core.Internal?.GetTrackAlpha(track) ?? 0f;

        /// <summary>Whether a track mixes toward its pose or adds its offset from the setup pose.</summary>
        public bool SetTrackBlend(int track, SpriteStudioTrackBlend blend) => _core.Internal?.SetTrackBlend(track, blend) ?? false;

        /// <inheritdoc cref="SetTrackBlend"/>
        public SpriteStudioTrackBlend GetTrackBlend(int track) => _core.Internal?.GetTrackBlend(track) ?? SpriteStudioTrackBlend.Mix;

        /// <summary>
        /// Where the next animation laid on the track starts: from its head, or at track 0's phase. Set it
        /// before laying the animation.
        /// </summary>
        public bool SetTrackSync(int track, SpriteStudioTrackSync sync) => _core.Internal?.SetTrackSync(track, sync) ?? false;

        /// <inheritdoc cref="SetTrackSync"/>
        public SpriteStudioTrackSync GetTrackSync(int track) => _core.Internal?.GetTrackSync(track) ?? SpriteStudioTrackSync.Free;

        /// <summary>Moves a track's playhead.</summary>
        public bool SetTrackFrame(int track, float frameNo) => _core.SetTrackFrame(track, frameNo);

        /// <inheritdoc cref="SetTrackFrame"/>
        public float GetTrackFrame(int track) => _core.Internal?.GetTrackFrame(track) ?? 0f;

        // A track's playback settings apply to every animation on it, those a crossfade fades in included,
        // and outlast them. Track 0's are the SpeedScale / FrameRate / FrameSkipEnabled / PlaybackDirection
        // this component already has, and its loop count is LoopCount.

        /// <summary>A track's speed, as a multiple of the frame rate. Track 0's is <see cref="SpeedScale"/>.</summary>
        public bool SetTrackSpeed(int track, float speed)
        {
            if (!(_core.Internal?.SetTrackSpeed(track, speed) ?? false)) return false;
            // SetAnimation pushes the serialized value back, which would undo a speed written past it.
            if (track == 0) _speedScale = speed;
            return true;
        }

        /// <inheritdoc cref="SetTrackSpeed"/>
        public float GetTrackSpeed(int track) => _core.Internal?.GetTrackSpeed(track) ?? 0f;

        /// <summary>A track's frame-rate override (fps). Track 0's is <see cref="FrameRate"/>.</summary>
        public bool SetTrackFrameRate(int track, int fps) => _core.Internal?.SetTrackFrameRate(track, fps) ?? false;

        /// <inheritdoc cref="SetTrackFrameRate"/>
        public int GetTrackFrameRate(int track) => _core.Internal?.GetTrackFrameRate(track) ?? 0;

        /// <summary>Whether a track follows real time. Track 0's is <see cref="FrameSkipEnabled"/>.</summary>
        public bool SetTrackFrameSkipEnabled(int track, bool enabled)
        {
            if (!(_core.Internal?.SetTrackFrameSkipEnabled(track, enabled) ?? false)) return false;
            // SetAnimation pushes the serialized value back, which would undo a value written past it.
            if (track == 0) _skipFrames = enabled;
            return true;
        }

        /// <inheritdoc cref="SetTrackFrameSkipEnabled"/>
        public bool IsTrackFrameSkipEnabled(int track) => _core.Internal?.IsTrackFrameSkipEnabled(track) ?? false;

        /// <summary>A track's heading and style. Track 0's are <see cref="PlaybackDirection"/> / <see cref="PlaybackStyle"/>.</summary>
        public bool SetTrackPlaybackDirection(int track, SpriteStudioPlaybackDirection direction, SpriteStudioPlaybackStyle style)
            => _core.Internal?.SetTrackPlaybackDirection(track, direction, style) ?? false;

        /// <inheritdoc cref="SetTrackPlaybackDirection"/>
        public SpriteStudioPlaybackDirection GetTrackPlaybackDirection(int track)
            => _core.Internal?.GetTrackPlaybackDirection(track) ?? SpriteStudioPlaybackDirection.Forward;

        /// <inheritdoc cref="SetTrackPlaybackDirection"/>
        public SpriteStudioPlaybackStyle GetTrackPlaybackStyle(int track)
            => _core.Internal?.GetTrackPlaybackStyle(track) ?? SpriteStudioPlaybackStyle.Normal;

        /// <summary>
        /// How many times the animation on a track plays (-1 or 0 = forever). Unlike the settings above it
        /// belongs to the animation: one laid on a track loops forever until this is set again. Track 0's
        /// is <see cref="LoopCount"/>, which this component puts back on every animation track 0 switches
        /// to. The read is 0 for a track with no animation, and -1 for one looping forever.
        /// </summary>
        public bool SetTrackLoopCount(int track, int count)
        {
            if (!(_core.Internal?.SetTrackLoopCount(track, count) ?? false)) return false;
            // Every switch of track 0 puts LoopCount back; written past it, a count would be undone there.
            if (track == 0) _loopCount = count;
            return true;
        }

        /// <inheritdoc cref="SetTrackLoopCount"/>
        public int GetTrackLoopCount(int track) => _core.Internal?.GetTrackLoopCount(track) ?? 0;

        /// <summary>
        /// Whether the animation on a track has played every loop. A finished track holds its last frame,
        /// still mixed in, until the host fades or clears it; no event is sent for it. Track 0's is
        /// <see cref="IsFinished"/>.
        /// </summary>
        public bool IsTrackFinished(int track) => _core.Internal?.IsTrackFinished(track) ?? false;

        /// <summary>
        /// Whether the last update crossed a loop boundary on a track: a pulse, and false on a frame that
        /// did not advance the animation. Track 0's is <see cref="JustLooped"/>.
        /// </summary>
        public bool TrackJustLooped(int track) => _core.Internal?.TrackJustLooped(track) ?? false;

        /// <summary>
        /// The outgoing side of a crossfade on the track fires events while its share, 1 - progress, is at
        /// least this. The default, +Infinity, silences it from the moment the fade begins; 0.5 lets it
        /// fire for the first half.
        /// </summary>
        public bool SetTrackOutgoingEventThreshold(int track, float threshold)
            => _core.Internal?.SetTrackOutgoingEventThreshold(track, threshold) ?? false;

        /// <inheritdoc cref="SetTrackOutgoingEventThreshold"/>
        public float GetTrackOutgoingEventThreshold(int track) => _core.Internal?.GetTrackOutgoingEventThreshold(track) ?? 0f;

        /// <summary>Fades a track's alpha to <paramref name="targetAlpha"/> over <paramref name="duration"/> seconds.</summary>
        public bool FadeTrack(int track, float targetAlpha, float duration) => _core.Internal?.FadeTrack(track, targetAlpha, duration) ?? false;

        /// <summary>How far a track's fade has gone, 0 to 1, or -1 when it is not fading.</summary>
        public float GetTrackFadeProgress(int track) => _core.Internal?.GetTrackFadeProgress(track) ?? -1f;

        /// <summary>Stops a track's fade where it stands.</summary>
        public bool CancelTrackFade(int track) => _core.Internal?.CancelTrackFade(track) ?? false;

        /// <summary>The handle of the source a track reports, or -1 for none.</summary>
        public int GetTrackMainSource(int track) => _core.Internal?.GetTrackMainSource(track) ?? -1;

        /// <summary>
        /// Adds an animation to a track beside what it holds, at <paramref name="weight"/>. Returns its
        /// source handle, or -1. A handle is reused once its source is released (a crossfade releases what
        /// it faded out), so do not hold one past its source.
        /// </summary>
        public int AddTrackSource(int track, string animName, float weight) => _core.Internal?.AddTrackSource(track, animName, weight) ?? -1;

        /// <summary>A source's weight within its track.</summary>
        public bool SetSourceWeight(int source, float weight) => _core.Internal?.SetSourceWeight(source, weight) ?? false;

        /// <inheritdoc cref="SetSourceWeight"/>
        public float GetSourceWeight(int source) => _core.Internal?.GetSourceWeight(source) ?? 0f;

        /// <summary>Moves a source's playhead.</summary>
        public bool SetSourceFrame(int source, float frameNo) => _core.Internal?.SetSourceFrame(source, frameNo) ?? false;

        /// <inheritdoc cref="SetSourceFrame"/>
        public float GetSourceFrame(int source) => _core.Internal?.GetSourceFrame(source) ?? 0f;

        /// <summary>
        /// A source fires events while its weight is at least this. The default, +Infinity, leaves only
        /// track 0's main animation firing.
        /// </summary>
        public bool SetSourceEventThreshold(int source, float threshold) => _core.Internal?.SetSourceEventThreshold(source, threshold) ?? false;

        /// <inheritdoc cref="SetSourceEventThreshold"/>
        public float GetSourceEventThreshold(int source) => _core.Internal?.GetSourceEventThreshold(source) ?? 0f;

        // The component's half of a crossfade or a track animation on track 0, which switch the animation
        // without a setup. The serialized name follows, so the inspector and CurrentAnimation name the
        // incoming animation, and SetAnimation of the outgoing one is a switch again. The
        // runtime lays the incoming animation looping forever, as a setup does, so LoopCount is put back;
        // speed, frame skip, frame rate and direction are track 0's and carry over on their own.
        private void OnTrackZeroSwitched()
        {
            var p = _core.Internal;
            _animationName = p.CurrentAnimationName;
#if UNITY_EDITOR
            // An Animation Name edited back to the outgoing animation is then a change to OnValidate.
            _appliedAnimation = _animationName;
#endif
            // While a Timeline drives this player its clip decides track 0: the clip that set its animation
            // up sets it up again at the next evaluation.
            _timelineCurrentAnim = null;
            p.SetLoop(_loopCount);
        }

        #endregion

        private void Start()
        {
            _started = true;
            StartPlayback();
        }

        // Whether Start has run. OnDisable disposes the native player, so a player enabled again comes back
        // with nothing set up; from the second enable on, OnEnable repeats what Start did, as an Animator
        // returns to its default state and a Play On Awake particle system plays again.
        [System.NonSerialized] private bool _started;

        // An asset was assigned after Start. Waiting for the next frame lets a script that assigns it and then
        // selects an animation, as most do, do so with no warning about the old name and no extra started event.
        [System.NonSerialized] private bool _startAfterSwap;

        private void StartPlayback()
        {
            _startAfterSwap = false;
            if (string.IsNullOrEmpty(_animationName)) return;
            // Always call SetAnimation so the first frame is posed even when AutoPlay is off; actually start
            // playback only when AutoPlay is on. SetAnimation pushes the serialized loop/speed/skip itself,
            // so the order is simply animation -> play. A script may have played it already, between the enable
            // and this first Start: Play would rewind what it asked for.
            if (SetAnimation(_animationName) && _autoPlay && !IsPlaying) Play();
        }

        private readonly SpriteStudioPlaybackCore _core = new SpriteStudioPlaybackCore();

        // The native player, exposed for tests that need to read what actually reached the runtime
        // rather than what this component was told. Internal, not public: a raw native handle is not
        // part of the package's API (ISpriteStudioRenderable.GetLiveHandle is explicit for the same
        // reason).
        internal SpriteStudioInternalPlayer InternalPlayer => _core.Internal;

        /// <summary>Raised by <see cref="Play()"/>, as playback starts.</summary>
        public event System.Action OnAnimationStarted
        {
            add => _core.OnAnimationStarted += value;
            remove => _core.OnAnimationStarted -= value;
        }
        /// <summary>Raised once when every configured loop has played. Never under an infinite <see cref="LoopCount"/>, and not by <see cref="Stop"/>.</summary>
        public event System.Action OnAnimationFinished
        {
            add => _core.OnAnimationFinished += value;
            remove => _core.OnAnimationFinished -= value;
        }
        /// <summary>Raised each time a loop returns to the beginning. The final loop finishes instead.</summary>
        public event System.Action OnAnimationLooped
        {
            add => _core.OnAnimationLooped += value;
            remove => _core.OnAnimationLooped -= value;
        }
        /// <summary>Raised for each user-data key the playhead reaches.</summary>
        public event System.Action<SpriteStudioUserData> OnUserData
        {
            add => _core.OnUserData += value;
            remove => _core.OnUserData -= value;
        }
        /// <summary>Raised for each signal key the playhead reaches.</summary>
        public event System.Action<SpriteStudioSignal> OnSignal
        {
            add => _core.OnSignal += value;
            remove => _core.OnSignal -= value;
        }
        /// <summary>Raised for each audio key the playhead reaches, whether or not the built-in playback sounds it.</summary>
        public event System.Action<SpriteStudioAudio> OnAudio
        {
            add => _core.OnAudio += value;
            remove => _core.OnAudio -= value;
        }

        /// <summary>Inspector counterpart of <see cref="OnAnimationStarted"/>.</summary>
        [Header("Events")]
        public UnityEvent OnAnimationStartedEvent = new UnityEvent();
        /// <summary>Inspector counterpart of <see cref="OnAnimationFinished"/>.</summary>
        public UnityEvent OnAnimationFinishedEvent = new UnityEvent();
        /// <summary>Inspector counterpart of <see cref="OnAnimationLooped"/>.</summary>
        public UnityEvent OnAnimationLoopedEvent = new UnityEvent();
        /// <summary>Inspector counterpart of <see cref="OnUserData"/>.</summary>
        public UserDataUnityEvent OnUserDataEvent = new UserDataUnityEvent();
        /// <summary>Inspector counterpart of <see cref="OnSignal"/>.</summary>
        public SignalUnityEvent OnSignalEvent = new SignalUnityEvent();
        /// <summary>Inspector counterpart of <see cref="OnAudio"/>.</summary>
        public AudioUnityEvent OnAudioEvent = new AudioUnityEvent();

        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Mesh _sharedMesh;

        private void OnEnable()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();

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
            if (_started && Application.isPlaying) StartPlayback();
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
            // Built-in playback, only while running and playing forward (SpriteStudio has no reverse audio), and
            // not for the keys a seek made while playing lands on: the playhead crossed none of them.
            if (_playAudio && Application.isPlaying && (_core.Internal?.IsPlayingForward() ?? false) && !_core.IsSeekLanding(data.FrameNo))
            {
                if (_audioController == null) _audioController = new SpriteStudioAudioController(gameObject);
                _audioController.Play(in data, _animeAsset, _audioVolume);
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
            UnloadAsset();
        }

        // Let go of everything that came from the loaded pack: its sounds, the material instances made for
        // it, and the native player that holds it. A player enabled again, or given another asset, builds
        // them again.
        private void ReleasePack()
        {
            _audioController?.StopAll();   // stop all built-in sounds

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

        // A null asset, or a player not enabled, draws nothing. The MeshRenderer, a component of its own, would
        // otherwise keep drawing the last mesh with materials that are gone: the ones ReleasePack destroys, and
        // the ones an Addressables release destroys with the asset (the parts then draw magenta). And with a
        // null asset the runtime would keep building the old pack every frame.
        private void UnloadAsset()
        {
            ReleasePack();
            if (_sharedMesh != null) _sharedMesh.Clear();
            if (_meshRenderer != null) _meshRenderer.SetSharedMaterials(_submeshMaterials);
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

        // An Animator, or a Timeline Animation Track, writes keyed fields straight into the component -- as the
        // Inspector does, bypassing the property setters -- and calls this after each evaluation, the hook
        // uGUI's Graphic uses for the same. Push the live-tunable ones, so a keyed Flip X, Offset or Speed
        // Scale reaches the drawing as a keyed SpriteRenderer.flipX does.
        private void OnDidApplyAnimationProperties()
        {
            var p = _core.Internal;
            if (p != null)
            {
                p.SetSpeed(_speedScale);
                p.SetSkipFrames(_skipFrames);
                p.SubFrameEnabled = _subFrameEnabled;
                // SetLoop re-arms the loops left, and this runs on every evaluation: write it only when the
                // keyed count differs from the runtime's, which keeps 0 and below as -1.
                if (p.GetLoop() != (_loopCount > 0 ? _loopCount : -1)) p.SetLoop(_loopCount);
            }
            ApplyPixelsPerUnit();
#if UNITY_EDITOR
            // The Animation window previews in edit mode, where nothing else rebuilds the static preview.
            if (!Application.isPlaying) _editPreviewDirty = true;
#endif
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
                // A pack assigned after None: the runtime went with the previous one.
                if (assetChanged && _animeAsset != null && _core.Internal == null && isActiveAndEnabled)
                    _core.EnsureCreated(_animeAsset, _subFrameEnabled);
                // Reflect changes live during play. Reload the ssab on an asset swap, then (re)start the
                // animation from the head when the asset or animation changed.
                if (_core.Internal != null && _animeAsset != null)
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
            // None in the Inspector lets go of the previous pack as the setter does, from LateUpdate:
            // releasing it destroys materials, which OnValidate must not do.
            if (assetChanged && _animeAsset == null) _unloadPending = true;

            _appliedAsset = _animeAsset;
            _appliedAnimation = _animationName;
        }

        [System.NonSerialized] private bool _unloadPending;

        // Edit-mode static preview. With [ExecuteAlways], LateUpdate is called every editor frame, but only
        // when _editPreviewDirty is set does it "build the start frame once" and hold it. Other frames are drawn
        // on demand, by the inspector's preview and a Timeline scrub, which set it again when they let go.
        // In Play mode the SpriteStudioUpdateManager drives it, so this does nothing.
        private bool _editPreviewDirty;

        private void LateUpdate()
        {
            if (_unloadPending)
            {
                _unloadPending = false;
                if (_animeAsset == null) UnloadAsset();
            }
            if (Application.isPlaying) return;
            // A reimported .ssab keeps this reference and brings new data, which EnsureCreated loads; so does
            // the self-heal if the .ssab was still importing when OnEnable ran (Editor restart race). Either
            // way what came from the previous pack is rebuilt, the preview included.
            if (_core.EnsureCreated(_animeAsset, _subFrameEnabled))
            {
                _textureOverride.Rebuild(_animeAsset, _cellmapTextureOverrides);
                MarkPartTrackersDirty();
                _editPreviewDirty = true;
            }
            // An edited AddOn catalog changes the per-part materials the preview drew with.
            if (_shaderCatalog != null && _shaderCatalog.Version != _previewCatalogVersion) _editPreviewDirty = true;
            if (!_editPreviewDirty) return;
            // Keep retrying while the runtime has no ssab yet, and only clear the dirty flag (build once) once
            // it is ready. A setup failure after that is a config error, not a race, so we do not loop.
            if (_core.Internal == null || !_core.Internal.HasSsab) return;
            _editPreviewDirty = false;
            _previewCatalogVersion = _shaderCatalog != null ? _shaderCatalog.Version : 0;
            BuildEditPreview();
        }

        // The catalog version the edit preview was last built against.
        private int _previewCatalogVersion;

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
            _editPreviewAnimation = null;
            // The setup replaces whatever animation a Timeline clip set up.
            _timelineCurrentAnim = null;
            if (!_core.Internal.SetupAnimation(previewAnimation)) return;
            _editPreviewAnimation = previewAnimation;
            // Pin to the start frame and synchronously build just one frame without advancing (ScheduleMeshUpdate is advance=false).
            _core.Internal.SetFrame(_core.Internal.GetAnimationSectionStart());
            DrawEditFrame();
        }

        // Builds the frame the runtime is on, without advancing: no event fires and no sound plays.
        private void DrawEditFrame()
        {
            if (!_core.Internal.HasSsab) return;
            _core.Internal.BindShaderCatalog(_shaderCatalog);
            _core.Internal.ScheduleMeshUpdate(0f);
            ApplyAndRender();
        }

        // The animation the edit preview has set up, so drawing another frame does not set it up again. Cleared
        // by anything else that sets an animation up.
        private string _editPreviewAnimation;

        private bool SetUpEditPreview()
        {
            if (_core.Internal == null || _animeAsset == null || !_core.Internal.HasSsab) return false;
            string previewAnimation = string.IsNullOrEmpty(_animationName)
                ? _core.Internal.GetAnimationName(0)
                : _animationName;
            if (string.IsNullOrEmpty(previewAnimation)) return false;
            if (previewAnimation == _editPreviewAnimation) return true;
            _timelineCurrentAnim = null;
            if (!_core.Internal.SetupAnimation(previewAnimation)) return false;
            _editPreviewAnimation = previewAnimation;
            return true;
        }

        bool ISpriteStudioEditPreview.TryGetEditPreviewSection(out int start, out int end, out int fps)
        {
            start = end = 0;
            fps = 60;
            if (!SetUpEditPreview()) return false;
            start = _core.Internal.GetAnimationSectionStart();
            end = _core.Internal.GetAnimationSectionEnd();
            int rate = _core.Internal.GetFrameRate();
            if (rate > 0) fps = rate;
            return true;
        }

        void ISpriteStudioEditPreview.ShowEditPreviewFrame(float frame)
        {
            if (Application.isPlaying || _timelineControlled || !SetUpEditPreview()) return;
            _core.Internal.SetFrame(frame);
            DrawEditFrame();
        }

        void ISpriteStudioEditPreview.EndEditPreview()
        {
            if (!Application.isPlaying) _editPreviewDirty = true;
        }

        float ISpriteStudioEditPreview.EditPreviewSpeedScale => _speedScale;
        bool ISpriteStudioEditPreview.EditPreviewSubFrame => _subFrameEnabled;
        bool ISpriteStudioEditPreview.EditPreviewBlocked => _timelineControlled;
#endif

        // --- ISpriteStudioRenderable (driven by SpriteStudioUpdateManager) ---
        // Explicit implementations, so none of it joins the package's public API: a raw native pointer, and
        // calls that are only right in the order the update manager makes them.

        void ISpriteStudioRenderable.PrepareFrame(float deltaTime, out System.IntPtr handle, out float drawFrame)
            => PrepareFrame(deltaTime, out handle, out drawFrame);

        internal void PrepareFrame(float deltaTime, out System.IntPtr handle, out float drawFrame)
        {
            if (_core.Internal == null) { handle = System.IntPtr.Zero; drawFrame = 0f; return; }
            if (_startAfterSwap && !_timelineControlled) StartPlayback();
            // Bind the per-part shader catalog before the build so the geometry builder can carve out
            // is-per-part AddOn parts.
            _core.Internal.BindShaderCatalog(_shaderCatalog);
            // Under Timeline control, render the frame set by SetTime, so do not auto-advance (advance=false)
            // In a finally: a handler that throws out of the update has still had its landing frame reported.
            try { _core.Internal.PrepareFrameData(deltaTime, !_timelineControlled, out handle, out drawFrame); }
            finally { _core.EndUpdate(); }
        }

        /// <inheritdoc/>
        System.IntPtr ISpriteStudioRenderable.GetLiveHandle() => _core.Internal != null ? _core.Internal.NativeHandle : System.IntPtr.Zero;

        void ISpriteStudioRenderable.ConsumeFrame(System.IntPtr frameDataPtr, int len, float deltaTime)
        {
            if (_core.Internal == null) return;
            _core.Internal.ConsumeFrameData(frameDataPtr, len, ConsumeDeltaTime(deltaTime));
        }

        SpriteStudioInternalPlayer ISpriteStudioRenderable.InternalPlayer => _core.Internal;

        float ISpriteStudioRenderable.ConsumeDeltaTime(float deltaTime) => ConsumeDeltaTime(deltaTime);

        // Under Timeline control, do not advance effects/instances by wall-clock dt (dt=0 to keep deterministic seeking)
        private float ConsumeDeltaTime(float deltaTime) => _timelineControlled ? 0f : deltaTime;

        void ISpriteStudioRenderable.ApplyAndRender() => ApplyAndRender();

        internal void ApplyAndRender()
        {
            if (_core.Internal == null) return;

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
                if (tracker == null) continue;
                // A target swapped out while this tracker had it deactivated goes back as it was found.
                if (tracker.HiddenTarget != null && tracker.HiddenTarget != tracker.target) ActivateHiddenTarget(tracker);
                if (tracker.target == null) continue;

                // Resolve part index (cached; rebuilt on asset reload via MarkPartTrackersDirty).
                if (tracker.ResolvedIndex == SpriteStudioPartTracker.ResolveDirty)
                    tracker.ResolvedIndex = internalPlayer.FindPartIndex(tracker.partName);
                int partIndex = tracker.ResolvedIndex;

                if (partIndex < 0)
                {
                    // Part absent from this animation: nothing to follow, so deactivate the target.
                    DeactivateTarget(tracker);
                    continue;
                }

                // A hidden part still has a valid matrix, so FollowAlways keeps tracking it; Deactivate hides.
                bool hasState = internalPlayer.TryGetPartState(partIndex, out bool hidden, out float lsx, out float lsy);
                if (hidden && tracker.onHide == SpriteStudioPartTrackerHide.Deactivate)
                {
                    DeactivateTarget(tracker);
                    continue;
                }

                if (!internalPlayer.TryGetPartTransform(partIndex, out Matrix4x4 partLocal)) continue;

                // Re-activate what this tracker deactivated (absent / hidden) on a previous frame.
                ActivateHiddenTarget(tracker);

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

        // Deactivate a tracker's target, recording that the tracker did so it can be activated again -- and
        // only then: a target already inactive is the user's to keep so. Play mode only: the edit-mode preview
        // would save the target inactive into the scene, and the record does not survive a domain reload to
        // undo it.
        private static void DeactivateTarget(SpriteStudioPartTracker tracker)
        {
            if (!Application.isPlaying || !tracker.target.gameObject.activeSelf) return;
            tracker.target.gameObject.SetActive(false);
            tracker.HiddenTarget = tracker.target;
        }

        private static void ActivateHiddenTarget(SpriteStudioPartTracker tracker)
        {
            var hidden = tracker.HiddenTarget;
            if (hidden == null) return;
            tracker.HiddenTarget = null;
            if (!hidden.gameObject.activeSelf) hidden.gameObject.SetActive(true);
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

        /// <summary>
        /// Remove all trackers whose target is the given Transform, activating it again if a tracker had
        /// deactivated it. Returns the number removed.
        /// </summary>
        public int UnregisterPartTracker(Transform target)
        {
            if (_partTrackers == null) return 0;
            return _partTrackers.RemoveAll(t =>
            {
                if (t == null || t.target != target) return false;
                ActivateHiddenTarget(t);
                return true;
            });
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

        /// <summary>
        /// The outline <paramref name="partName"/> is drawn with on the last frame built, in the player's local
        /// space as <see cref="TryGetPartTransform"/> reports it (Pixels Per Unit, flip and offset folded in).
        /// A part with a cell is its four drawn corners, skew and vertex deform included; a Mesh part is the
        /// axis-aligned box around its vertices; a Shape is its size around its pivot. False when the part is
        /// unknown or hidden, draws nothing of its own (null, bone, instance and effect parts), or no frame has
        /// been built; a part drawn at alpha 0 still has an outline. It builds nothing.
        /// </summary>
        public bool TryGetPartQuad(string partName, out SpriteStudioPartQuad quad)
        {
            quad = default;
            if (_core.Internal == null) return false;
            int idx = _core.Internal.FindPartIndex(partName);
            return idx >= 0 && _core.Internal.TryGetPartQuad(idx, out quad);
        }

        /// <inheritdoc cref="TryGetPartQuad(string, out SpriteStudioPartQuad)"/>
        public bool TryGetPartQuad(int partIndex, out SpriteStudioPartQuad quad)
        {
            quad = default;
            return _core.Internal != null && _core.Internal.TryGetPartQuad(partIndex, out quad);
        }

        /// <summary>
        /// The index of the front-most part whose <see cref="TryGetPartQuad(string, out SpriteStudioPartQuad)"/>
        /// outline holds <paramref name="point"/> on the last frame built, or -1 when none does — what
        /// <see cref="FindPartIndex"/> answers for a part that is not there. The point is in the player's local
        /// space. Parts drawn in front win.
        /// </summary>
        public int HitTestPart(Vector2 point) => _core.Internal?.HitTestPart(point) ?? -1;

        /// <inheritdoc cref="HitTestPart(Vector2)"/>
        /// <remarks>Only the parts named are tested; a name the asset does not have is skipped.</remarks>
        public int HitTestPart(Vector2 point, System.Collections.Generic.IReadOnlyList<string> parts)
            => _core.Internal?.HitTestPart(point, parts) ?? -1;

        /// <inheritdoc cref="HitTestPart(Vector2)"/>
        /// <remarks>Only the parts at these indices are tested.</remarks>
        public int HitTestPart(Vector2 point, System.Collections.Generic.IReadOnlyList<int> parts)
            => _core.Internal?.HitTestPart(point, parts) ?? -1;

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
        /// Skips reassignment while nothing that decides the material list has changed (the signature
        /// below). The mask-write SubMesh threshold (_MaskThreshold) can change every frame, so it is set
        /// separately via a MaterialPropertyBlock.
        /// </summary>
        private void UpdateSubmeshMaterials()
        {
            if (_animeAsset == null || _meshRenderer == null || _core.Internal == null) return;

            int subCount = _core.Internal.SubmeshCount;
            if (subCount <= 0) return;

            var submeshes = _core.Internal.Submeshes;

            // Layout signature. threshold is per-frame variable, so it is not included here and is passed via the MPB.
            uint signature = (uint)subCount;
            // Fold in the asset's revision: a reimport destroys the material variants in the list, and the
            // layout alone would keep drawing with them (the error shader) until the player was re-enabled.
            signature = signature * 31u + (uint)_animeAsset.Revision;
            // And the catalog's version: an edited catalog can resolve an AddOn part to a different shader.
            signature = signature * 31u + (uint)(_shaderCatalog != null ? _shaderCatalog.Version : 0);
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
                        // This path is only taken when the signature changes, so it does not spam.
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
        /// sampling this cellmap cuts against the replacement's alpha). Replace with a texture whose alpha
        /// silhouette matches if the cellmap is used as a mask.
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
        /// The current replacement texture for a cellmap name, or null when none is set (the replacement
        /// only, never the original texture).
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
                SetUpForTimeline(_animationName);
            }
        }

        public void SetTime(double time)
        {
            // Seek via Timeline: convert clip-local time (seconds) to a frame number and set it
            if (_core.Internal == null) return;
            SeekForTimeline(time);
#if UNITY_EDITOR
            if (!Application.isPlaying) DrawEditFrame();
#endif
        }

        // The Timeline sets the animation up itself rather than through SetAnimation, which would write the
        // serialized Animation Name. The runtime resets the loop count on every setup, so put this
        // component's playback settings back as SetAnimation does: a player that plays once after the
        // cutscene would otherwise loop forever.
        private bool SetUpForTimeline(string animationName)
        {
#if UNITY_EDITOR
            _editPreviewAnimation = null;
#endif
            if (!_core.SetAnimation(animationName, this)) return false;
            ApplyPlaybackSettings();
            return true;
        }

        // A clip's seeks leave a report of the last frame due on the next update, which is the player's own once
        // the clip lets go. Under a clip no event fires, and that frame is the clip's.
        private void DropTimelineLanding()
        {
            if (_timelineControlled) _core.DropLandingReport();
        }

        // Clip-local time counts from the animation's first frame, not from frame 0.
        private void SeekForTimeline(double clipLocalTime)
        {
            int fps = _core.Internal.GetFrameRate();
            if (fps <= 0) fps = 60;
            _core.Internal.SetFrame(_core.Internal.GetAnimationSectionStart() + (float)(clipLocalTime * fps));
        }

        public void OnControlTimeStop()
        {
            DropTimelineLanding();
            _timelineControlled = false;
#if UNITY_EDITOR
            if (!Application.isPlaying) _editPreviewDirty = true;
#endif
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
        /// In Play mode the UpdateManager renders what this seeks; in Edit Mode it is drawn here, so scrubbing
        /// the Timeline window moves the player.
        /// </summary>
        internal void EvaluateTimelineClip(string animationName, double clipLocalTime)
        {
            if (_core.Internal == null) return;
            _timelineControlled = true;

            if (!string.IsNullOrEmpty(animationName) && animationName != _timelineCurrentAnim)
            {
                if (SetUpForTimeline(animationName))
                    _timelineCurrentAnim = animationName;
            }

            SeekForTimeline(clipLocalTime);
            // Rendering is done by the UpdateManager via PrepareFrame(advance=false) -> ApplyAndRender (same path as SetTime).
#if UNITY_EDITOR
            if (!Application.isPlaying) DrawEditFrame();
#endif
        }

        /// <summary>Releases Timeline control (on mixer graph teardown / track deactivation).</summary>
        internal void ReleaseTimelineControl()
        {
            DropTimelineLanding();
            _timelineControlled = false;
            _timelineCurrentAnim = null;
#if UNITY_EDITOR
            if (!Application.isPlaying) _editPreviewDirty = true;
#endif
        }

        #endregion
    }
}
