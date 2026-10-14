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
                _canvasMaterialDirty = true;
            }
        }

        [Header("Audio")]
        [Tooltip("Play the sounds of audio keys (ADIO) with the built-in playback. OFF: the audio events still fire, for a host that plays them itself.")]
        [SerializeField] private bool _playAudio = true;
        [Tooltip("Volume of the built-in playback.")]
        [Range(0f, 1f)]
        [SerializeField] private float _audioVolume = 1f;

        private SpriteStudioAudioController _audioController;

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
            set => _core.Seek(value);
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
        /// <see cref="FrameSkipEnabled"/>). It lives in the runtime, not in a serialized field: a value set while
        /// the component is disabled is dropped, and enabling the component starts it Forward again.
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
        /// animation walking outward rather than mid-bounce. It lives in the runtime, not in a serialized field: a
        /// value set while the component is disabled is dropped, and enabling the component starts it Normal again.
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
        // Mirror of SpriteStudioPlayer's per-part override API (colour / cell / visibility) for the
        // Canvas path. See SpriteStudioPlayer for the semantics; the runtime owns the override table
        // and its lifecycle, so nothing is cached or re-applied here.

        /// <summary>Resolve a part name to its index, or -1 if the part is not in the loaded asset.</summary>
        public int FindPartIndex(string partName) => _core.Internal?.FindPartIndex(partName) ?? -1;

        /// <summary>
        /// Is the named part a bone-skinned mesh? Its vertices are drawn by bound bones while the part's own
        /// transform stays at its setup node, so it is not a reliable target to pin an object to. (Part tracking
        /// itself is a `SpriteStudioPlayer` feature; this query is provided for parity.) Returns false when the
        /// part is unknown / not loaded; a rigid (deform-only) mesh reports false.
        /// </summary>
        public bool TryGetPartSkinnedMesh(string partName, out bool isSkinnedMesh)
        {
            isSkinnedMesh = false;
            if (_core.Internal == null) return false;
            int idx = _core.Internal.FindPartIndex(partName);
            return idx >= 0 && _core.Internal.TryGetPartSkinnedMesh(idx, out isSkinnedMesh);
        }

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
        /// Override a Normal part's colour with a single value (all four corners). The colour comes
        /// first and <paramref name="blendOp"/> trails with a default;
        /// the C ABI's order is the other way round.
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

        /// <summary>How strongly a track is laid over the ones below it, 0 to 1.</summary>
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
        public bool SetTrackFrame(int track, float frameNo) => _core.Internal?.SetTrackFrame(track, frameNo) ?? false;

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
            p.SetLoop(_loopCount);
        }

        #endregion

        protected override void Start()
        {
            base.Start();
            _started = true;
            StartPlayback();
        }

        // Whether Start has run. OnDisable disposes the native player, so a graphic enabled again comes back
        // with nothing set up; from the second enable on, OnEnable repeats what Start did, as an Animator
        // returns to its default state and a Play On Awake particle system plays again.
        [System.NonSerialized] private bool _started;

        private void StartPlayback()
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

        // The mesh handed to the CanvasRenderer, exposed for tests that check what reached it.
        internal Mesh CanvasMesh => _sharedMesh;

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
            base.OnEnable();

            if (_sharedMesh == null)
            {
                _sharedMesh = new Mesh();
                _sharedMesh.name = "SpriteStudio_CanvasMesh";
                // Same contract as SpriteStudioPlayer's mesh: runtime-only, destroyed by the owner
                // in OnDisable. This path hands the mesh to canvasRenderer.SetMesh rather than to a
                // serialized field, so it is not written into scenes the way the MeshFilter one was
                // - the flag is here so both meshes state the same lifetime instead of leaving the
                // difference to be rediscovered.
                _sharedMesh.hideFlags = HideFlags.HideAndDontSave;
                _sharedMesh.MarkDynamic();
            }

            _core.EnsureCreated(_animeAsset, _subFrameEnabled);
            // Canvas path: drop SpriteStudio internal masking; a parent uGUI Mask clips this graphic
            // read-only via the stencil wrap below, and a parent RectMask2D through the shader's clip rect.
            if (_core.Internal != null) _core.Internal.SuppressInternalMask = true;
            // Resolve the serialized cellmap texture replacements against the asset (applied on the next build).
            _textureOverride.Rebuild(_animeAsset, _cellmapTextureOverrides);
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

            // Release this graphic's material clone and invalidate the material signature so OnEnable
            // re-resolves from scratch. (base.OnDisable released MaskableGraphic's stencil variant.)
            ReleaseAtlasMaterial();
            _lastMaterialSignature = 0;
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
            // ssab up once it is available. Without this an empty Internal created at OnEnable would never
            // get its ssab, so SetupAnimation fails forever. A reimported .ssab keeps this reference and
            // brings new data, which EnsureCreated loads the same way; what came from the previous pack is
            // rebuilt either way.
            if (_core.EnsureCreated(_animeAsset, _subFrameEnabled))
            {
                _textureOverride.Rebuild(_animeAsset, _cellmapTextureOverrides);
                _canvasMaterialDirty = true;
                _editPreviewNeedsSetup = true;
            }
            if (_core.Internal == null || !_core.Internal.HasSsab) return;
            if (_editPreviewNeedsSetup)
            {
                // Same fallback as the world player's edit preview: with no animation chosen yet, preview
                // the asset's first one so a graphic placed without a name is visible. Not written back to
                // _animationName, so nothing is serialized and Play mode still honours the empty name.
                string previewAnimation = string.IsNullOrEmpty(_animationName)
                    ? _core.Internal.GetAnimationName(0)
                    : _animationName;
                if (string.IsNullOrEmpty(previewAnimation) || !_core.Internal.SetupAnimation(previewAnimation)) return;
                _core.Internal.SetFrame(_core.Internal.GetAnimationSectionStart());
                _editPreviewNeedsSetup = false;
            }
            _core.Internal.ScheduleMeshUpdate(0f); // Static: build the start frame without advancing
            ApplyAndRender();
        }

        // An Animator, or a Timeline Animation Track, writes keyed fields straight into the component -- as the
        // Inspector does, bypassing the property setters -- and calls this after each evaluation. Graphic's own
        // override marks the graphic dirty for a keyed Color; push the playback fields as well.
        protected override void OnDidApplyAnimationProperties()
        {
            base.OnDidApplyAnimationProperties();
            var p = _core.Internal;
            if (p == null) return;
            p.SetSpeed(_speedScale);
            p.SetSkipFrames(_skipFrames);
            p.SubFrameEnabled = _subFrameEnabled;
            // SetLoop re-arms the loops left, and this runs on every evaluation: write it only when the keyed
            // count differs from the runtime's, which keeps 0 and below as -1.
            if (p.GetLoop() != (_loopCount > 0 ? _loopCount : -1)) p.SetLoop(_loopCount);
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
            // Reflect inspector edits to the replacement list (or an asset swap) into the draw-time map.
            _textureOverride.Rebuild(_animeAsset, _cellmapTextureOverrides);
            _canvasMaterialDirty = true;
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
            _core.EndUpdate();
        }

        /// <inheritdoc/>
        // Explicit implementation: only the update manager needs this, and a raw native pointer must not
        // become part of the package's public API.
        System.IntPtr ISpriteStudioRenderable.GetLiveHandle() => _core.Internal != null ? _core.Internal.NativeHandle : System.IntPtr.Zero;

        public void ConsumeFrame(System.IntPtr frameDataPtr, int len, float deltaTime)
        {
            if (_core.Internal == null) return;
            _core.Internal.ConsumeFrameData(frameDataPtr, len, deltaTime);
            // The upload's conversion runs on a worker while the other players are consumed. ApplyAndRender
            // converts again only if Graphic.color has changed by then.
            if (_sharedMesh != null) _core.Internal.ScheduleCanvasConversion(_sharedMesh, color);
        }

        SpriteStudioInternalPlayer ISpriteStudioRenderable.InternalPlayer => _core.Internal;

        float ISpriteStudioRenderable.ConsumeDeltaTime(float deltaTime) => deltaTime;

        public void ApplyAndRender()
        {
            if (_core.Internal == null || _sharedMesh == null) return;

            // Single-material Canvas: build the whole graphic as ONE submesh and drive it with one
            // material; MIX/ADD are split per-vertex in-shader. Graphic.color goes into the vertex colour,
            // as an Image puts it there, and the CanvasRenderer's own colour is left to uGUI: the Button
            // transitions and CrossFadeColor / CrossFadeAlpha animate it, and the canvas multiplies it in.
            _core.Internal.ApplyToCanvasMesh(_sharedMesh, color);
            EnsureCanvasShaderChannels();
            UpdateCanvasMaterials();
            canvasRenderer.SetMesh(_sharedMesh);
        }

        // --- Single-material Canvas resolution (one atlas + one fixed PMA blend) ---
        // The whole graphic renders as ONE submesh with ONE material on the SpriteStudio/Canvas shader.
        // MIX vs ADD is handled per-vertex in-shader (a=0 trick); other blends collapse to MIX. SpriteStudio
        // internal masking is suppressed. The material reaches the CanvasRenderer the way
        // Graphic.UpdateMaterial sends an Image's: materialForRendering, which starts from the Material field
        // (the atlas material, as defaultMaterial, when that is empty) and runs every IMaterialModifier on
        // this GameObject -- MaskableGraphic's stencil test for a parent Mask, a Mask's own stencil write for
        // its children, anything else -- with mainTexture as the texture.
        // RectMask2D needs nothing here: MaskableGraphic hands the shader _ClipRect and UNITY_UI_CLIP_RECT
        // through CanvasRenderer.EnableRectClipping, and SpriteStudio/Canvas clips with them.
        private Material _atlasMaterial;        // the atlas material, or this graphic's texture-replacement clone of it
        private uint _lastMaterialSignature;    // resolved atlas texture hash + 1 (0 = nothing resolved)
        private int _materialAssetRevision;     // the asset's Revision when the atlas material was resolved
        private bool _canvasMaterialDirty;
        private bool _warnedMultiTexture;

        /// <summary>
        /// The material the animation draws with while <see cref="Graphic.material"/> is empty: SpriteStudio/Canvas
        /// carrying the atlas the animation samples. A material assigned instead needs a shader built from
        /// SpriteStudio/Canvas, the only kind that reads the vertex layout this graphic emits.
        /// </summary>
        public override Material defaultMaterial => _atlasMaterial != null ? _atlasMaterial : base.defaultMaterial;

        /// <summary>The atlas texture, handed to whichever material draws the graphic, as an Image hands over its sprite's.</summary>
        public override Texture mainTexture
            => _atlasMaterial != null && _atlasMaterial.mainTexture != null ? _atlasMaterial.mainTexture : base.mainTexture;

        private void UpdateCanvasMaterials()
        {
            if (_animeAsset == null || _core.Internal == null) return;

            int subCount = _core.Internal.SubmeshCount;
            if (subCount <= 0)
            {
                if (_lastMaterialSignature != 0)
                {
                    ReleaseAtlasMaterial();
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

            uint signature = texHash + 1u; // +1 so a valid hash never collides with the empty sentinel 0
            // A reimport destroys the asset's canvas materials: a new revision re-resolves the atlas material,
            // which the layout alone would keep drawing with (the error shader) until the graphic was re-enabled.
            if (signature != _lastMaterialSignature || _canvasMaterialDirty || _animeAsset.Revision != _materialAssetRevision)
            {
                RebuildCanvasMaterial(texHash);
                _lastMaterialSignature = signature;
                _materialAssetRevision = _animeAsset.Revision;
                _canvasMaterialDirty = false;
            }
        }

        private void RebuildCanvasMaterial(uint texHash)
        {
            ReleaseAtlasMaterial();

            Material baseMat = _animeAsset.ResolveCanvasMaterial(texHash, 0);
            if (baseMat == null)
            {
                Debug.LogError($"[SpriteStudioGraphic] Canvas material for texture {texHash} could not be resolved.", this);
                canvasRenderer.materialCount = 0;
                return;
            }

            // Runtime cellmap texture replacement (Player-side override): clone the asset-shared canvas
            // material and swap its texture so the replacement is per-player. Cloned (never mutated) and
            // released with the atlas material. ReleaseAtlasMaterial above already freed any prior clone.
            if (_textureOverride.HasAny && _textureOverride.TryGet(texHash, out var ovrTex))
            {
                _canvasTexOverride = new Material(baseMat) { hideFlags = HideFlags.HideAndDontSave, name = baseMat.name + "_TexOverride" };
                _canvasTexOverride.mainTexture = ovrTex;
                baseMat = _canvasTexOverride;
            }
            _atlasMaterial = baseMat;

            canvasRenderer.materialCount = 1;
            canvasRenderer.SetMaterial(materialForRendering, 0);
            canvasRenderer.SetTexture(mainTexture);
        }

        // The mesh packs per-vertex [rate, blend_idx, isNormalized, isAdditive] into TEXCOORD1 and the
        // part colour into TEXCOORD2. A Canvas only emits the shader channels listed in
        // additionalShaderChannels, and neither is included by default, so without this the data is
        // dropped/garbage and the part-color blend breaks (renders black). Enable both on the canvas we
        // render to.
        private void EnsureCanvasShaderChannels()
        {
            var c = canvas;
            if (c == null) return;
            const AdditionalCanvasShaderChannels needed = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2;
            if ((c.additionalShaderChannels & needed) != needed)
            {
                c.additionalShaderChannels |= needed;
            }
        }

        // Per-player canvas material clone holding a replaced texture (null when no replacement is active).
        // Released with the atlas material, so every ReleaseAtlasMaterial site (rebuild start, empty-mesh
        // path, OnDisable) frees it too.
        private Material _canvasTexOverride;

        // The stencil variants materialForRendering produced are MaskableGraphic's and Mask's, released by
        // them; only the texture-replacement clone is this graphic's.
        private void ReleaseAtlasMaterial()
        {
            _atlasMaterial = null;
            if (_canvasTexOverride != null)
            {
                if (Application.isPlaying) Destroy(_canvasTexOverride); else DestroyImmediate(_canvasTexOverride);
                _canvasTexOverride = null;
            }
        }

        #region Runtime texture replacement (Player-side override)

        /// <summary>
        /// Replace, at runtime, the texture bound for a cellmap (or external-texture) name for THIS graphic
        /// only — the asset and other players are untouched. Pass <paramref name="texture"/> = null to
        /// restore the original. This is a Player-side override, separate from the Override Layer API.
        ///
        /// The Canvas (uGUI) path is single-atlas: only the first atlas is drawn, so a replacement targeting
        /// a name that resolves to a non-first atlas will not show. Otherwise identical to the world player:
        /// keyed by name, persists across an asset swap, does not propagate into Instance children.
        /// </summary>
        /// <param name="cellmapName">Cellmap name (e.g. "common") or external-texture name (e.g. "common.png").</param>
        /// <param name="texture">Replacement texture, or null to restore the original.</param>
        public void SetCellmapTexture(string cellmapName, Texture2D texture)
        {
            if (string.IsNullOrEmpty(cellmapName)) return;

            SpriteStudioTextureOverride.SetEntry(_cellmapTextureOverrides, cellmapName, texture);
            _textureOverride.Rebuild(_animeAsset, _cellmapTextureOverrides);
            // Flag a re-resolve; the next ApplyAndRender (every managed frame in play mode, every editor
            // frame in edit mode) rebuilds the canvas material. Not resolved synchronously here so a caller
            // invoking this off-cycle cannot race the mesh-update job UpdateCanvasMaterials reads from.
            _canvasMaterialDirty = true;
        }

        /// <summary>
        /// The current replacement texture for a cellmap name, or null when none is set (returns only the
        /// override value, never the original texture).
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
            // SetMaterial. Instead flag a re-send, so the next ApplyAndRender hands the CanvasRenderer a
            // fresh materialForRendering: uGUI raises this for a new Material, for a Mask added, removed or
            // toggled, and for a reparent under or out of one (MaskableGraphic.RecalculateMasking).
            _canvasMaterialDirty = true;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            // Disabled: do not let it convert to UI Vertex on its own
            vh.Clear();
        }
    }
}
