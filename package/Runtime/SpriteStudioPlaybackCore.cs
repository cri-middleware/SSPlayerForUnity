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

        // The asset whose ssab is currently loaded into Internal, and the data it held then. Used to detect
        // an inspector asset swap so EnsureCreated reloads (the inspector writes the serialized field
        // directly, bypassing the setter), and a reimport, which keeps the asset and replaces its data.
        private SpriteStudioAnimeAsset _loadedAsset;
        private byte[] _loadedSsab;

        /// <summary>
        /// Creates the internal player if there is none, and loads the asset's ssab when it is not the one
        /// loaded. Returns true when it loaded one: the animation set up before is gone, and whatever the
        /// caller built from the previous pack has to be built again.
        /// </summary>
        public bool EnsureCreated(SpriteStudioAnimeAsset asset, bool subFrameEnabled)
        {
            if (Internal == null)
            {
                Internal = new SpriteStudioInternalPlayer { SubFrameEnabled = subFrameEnabled };
                HookEvents();
            }

            // Load the ssab when (a) none is loaded yet, (b) the referenced asset changed, or (c) the asset's
            // data changed. (a) is the self-heal case: on an Editor restart the referenced .ssab can still be
            // re-importing when OnEnable first runs (asset / SsabData momentarily null), so we retry until
            // it is ready. (b) handles an inspector asset swap, which bypasses the C# property setter. (c) is
            // a reimport. LoadSsab is safe to call repeatedly.
            if (asset != null && asset.SsabData != null
                && (!Internal.HasSsab || !ReferenceEquals(asset, _loadedAsset) || !ReferenceEquals(asset.SsabData, _loadedSsab)))
            {
                LoadAsset(asset);
                return true;
            }
            return false;
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

        // The one place a pack reaches the Internal, so the one place the "already reported" memory has to
        // be dropped: the names and frame ranges that are valid have just changed.
        private void LoadAsset(SpriteStudioAnimeAsset asset)
        {
            Internal.LoadSsab(asset.SsabData, asset.ResolveExternalSsab);
            _loadedAsset = asset;
            _loadedSsab = asset.SsabData;
            _reportedMissingAnimation = null;
            _reportedRefusedSection = null;
            _reportedLabelSection = null;
        }

        public void ReloadAsset(SpriteStudioAnimeAsset asset)
        {
            if (Internal != null && asset != null && asset.SsabData != null)
            {
                LoadAsset(asset);
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
            _loadedSsab = null;
        }

        /// <summary>
        /// Whether <paramref name="animationName"/> is the animation the native runtime already has set
        /// up. The owning component checks this before doing anything, so that re-asserting the current
        /// animation costs nothing — see its SetAnimation for why that matters.
        /// </summary>
        public bool IsAnimationSetUp(string animationName)
            => Internal != null && !string.IsNullOrEmpty(animationName) && Internal.CurrentAnimationName == animationName;

        // The animation name already reported missing. The owning component's early return compares against
        // the name the RUNTIME has set up, and a failed setup leaves that alone -- so the early return cannot
        // absorb a repeat, and a state machine asserting a typo every frame would warn every frame. Keyed on
        // the name, so a second wrong one still gets its own line. Cleared on a successful setup, and by
        // LoadAsset (a different pack has a different set of names).
        private string _reportedMissingAnimation;

        /// <summary>
        /// Sets up the data for the specified animation (posed at the first frame). Does not start playback.
        /// Call <see cref="Play()"/> separately to start.
        /// This is the bare setup: the Rust setup_animation resets the loop count and the playback section
        /// (both are per-transition — the transition is what states them) and keeps speed, frame skip, the
        /// frame-rate override and the direction / style. Nothing is re-applied here; the owning component
        /// pushes its own configuration back in its SetAnimation, which is where the loop count is restored.
        /// Returns true if setup succeeded.
        /// </summary>
        /// <param name="context">
        /// The owning component. It names the warning, and being handed to Unity's logger it makes the console
        /// entry select the offending GameObject when clicked. May be null where there is no component behind
        /// the core (a test driving it directly); the warning then names this class.
        /// </param>
        public bool SetAnimation(string animationName, Object context)
        {
            if (Internal == null || string.IsNullOrEmpty(animationName)) return false;

            if (!Internal.SetupAnimation(animationName))
            {
                // Name the pack as well as the name asked for: it is what separates a typo from a name
                // that belongs to a different asset. The names that
                // WOULD have worked are not listed -- AnimationNames is public on both components, and the
                // inspector's Animation Name dropdown is built from it.
                if (_reportedMissingAnimation != animationName)
                {
                    _reportedMissingAnimation = animationName;
                    // The runtime's own reason, verbatim. This used to say "has no animation", which is the
                    // common case and not the only one: the same call fails with "SSAB data is not loaded"
                    // on a player still waiting for its asset to finish importing, and telling that reader
                    // the name is missing sends them to look at the wrong thing.
                    string reason = Internal.LastErrorMessage;
                    string because = reason == null ? "" : $" -- {reason}";
                    Debug.LogWarning(
                        $"[{Who(context)}] '{PackName}' could not select animation '{animationName}'{because}. " +
                        "Check AnimationNames for what it does have.", context);
                }
                return false;
            }
            _reportedMissingAnimation = null;
            return true;
        }

        // The section already reported refused, keyed on the pair it was given, for the same reason as
        // _reportedMissingAnimation: a refused section changes nothing, so one re-stated every frame would
        // warn every frame. Cleared on a section that is taken, and by LoadAsset.
        private (int start, int end)? _reportedRefusedSection;

        /// <summary>
        /// Limits playback to [<paramref name="startFrame"/>, <paramref name="endFrame"/>]. Returns false,
        /// changing neither the section nor the playhead, when the runtime refuses it: both ends inside the
        /// animation and the end before the start. <paramref name="context"/> is as for SetAnimation.
        /// </summary>
        public bool SetAnimationSection(int startFrame, int endFrame, Object context)
        {
            if (Internal == null) return false;

            if (!Internal.SetAnimationSection(startFrame, endFrame))
            {
                if (_reportedRefusedSection != (startFrame, endFrame))
                {
                    _reportedRefusedSection = (startFrame, endFrame);
                    Debug.LogWarning(
                        $"[{Who(context)}] Animation section ({startFrame}, {endFrame}) was refused: its end is before its start. " +
                        "The section and the playhead stay as they were.", context);
                }
                return false;
            }
            _reportedRefusedSection = null;
            return true;
        }

        // The by-label section already reported, keyed on the animation and everything the caller passed, for
        // the same reason as _reportedRefusedSection. Cleared on a section that is taken, and by LoadAsset.
        private string _reportedLabelSection;

        /// <summary>
        /// <see cref="SetAnimationSection"/> with the ends named by labels of the animation set up: from
        /// <paramref name="startLabel"/>'s frame plus <paramref name="startOffset"/> to
        /// <paramref name="endLabel"/>'s frame plus <paramref name="endOffset"/>. Returns false, changing
        /// neither the section nor the playhead, when the animation has no such label or the frames are a
        /// section the runtime refuses; before an animation is set up it returns false and says nothing.
        /// <paramref name="context"/> is as for SetAnimation.
        /// </summary>
        public bool SetAnimationSectionByLabel(string startLabel, string endLabel, int startOffset, int endOffset, Object context)
        {
            var table = Internal?.GetLabelTable();
            if (table == null) return false;

            string animation = Internal.CurrentAnimationName;
            string key = $"{animation}\0{startLabel}\0{endLabel}\0{startOffset}\0{endOffset}";
            bool hasStart = table.TryGetFrame(startLabel, out int start);
            bool hasEnd = table.TryGetFrame(endLabel, out int end);
            if (!hasStart || !hasEnd)
            {
                if (_reportedLabelSection != key)
                {
                    _reportedLabelSection = key;
                    // Only the labels that are missing: the one that was found is not the mistake.
                    string missing = !hasStart && !hasEnd && startLabel != endLabel
                        ? $"'{startLabel}' or '{endLabel}'"
                        : $"'{(hasStart ? endLabel : startLabel)}'";
                    Debug.LogWarning(
                        $"[{Who(context)}] Animation '{animation}' of '{PackName}' has no label {missing}. " +
                        "The section and the playhead stay as they were.", context);
                }
                return false;
            }

            int from = start + startOffset;
            int to = end + endOffset;
            if (!Internal.SetAnimationSection(from, to))
            {
                if (_reportedLabelSection != key)
                {
                    _reportedLabelSection = key;
                    Debug.LogWarning(
                        $"[{Who(context)}] Animation section from label '{startLabel}' to '{endLabel}' lands on frames " +
                        $"({from}, {to}) and was refused: its end is before its start. " +
                        "The section and the playhead stay as they were.", context);
                }
                return false;
            }
            _reportedLabelSection = null;
            return true;
        }

        private static string Who(Object context) => context != null ? context.GetType().Name : nameof(SpriteStudioPlaybackCore);

        private string PackName => _loadedAsset != null ? _loadedAsset.name : "the loaded pack";

        /// <summary>Starts playing the currently set-up animation from the beginning.</summary>
        public void Play()
        {
            if (Internal == null) return;
            _seekPending = false;
            Internal.Play();
            OnAnimationStarted?.Invoke();
        }

        /// <summary>Starts playing the currently set-up animation from the specified frame.</summary>
        public void Play(float startFrame)
        {
            if (Internal == null) return;
            _seekPending = false;
            Internal.Play(startFrame);
            OnAnimationStarted?.Invoke();
        }

        // A seek, playing or not, reports the landing frame's keys on the next update, though the playhead
        // crossed none of them: their sounds are not played, and their notifications still fire. Play() starts
        // from its own frame, whose keys do sound, so it drops a seek made before it.
        private bool _seekPending;
        private int _seekLandingFrame;

        /// <summary>Moves the playhead, remembering where it lands.</summary>
        public void Seek(float frame)
        {
            var p = Internal;
            if (p == null) return;
            p.SetFrame(frame);
            _seekPending = true;
            _seekLandingFrame = Mathf.FloorToInt(p.GetFrame());
        }

        /// <summary>Whether an audio key on <paramref name="frameNo"/> is a seek's landing frame, not one crossed.</summary>
        public bool IsSeekLanding(int frameNo) => _seekPending && frameNo == _seekLandingFrame;

        /// <summary>Called after each update: the landing frame's keys have been reported by then.</summary>
        public void EndUpdate() => _seekPending = false;

        /// <summary>Advances the animation and schedules the mesh-build job (does not wait for the Job to complete).</summary>
        public void TickAndSchedule(float deltaTime, bool subFrameEnabled)
        {
            if (Internal == null) return;
            Internal.Tick(deltaTime, subFrameEnabled);
            Internal.ScheduleMeshUpdate(deltaTime);
        }
    }
}
