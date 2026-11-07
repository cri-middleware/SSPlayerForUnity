using System.Collections.Generic;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.UI
{
    /// <summary>
    /// Plays the section a UI state calls for on a <see cref="SpriteStudioGraphic"/>, taking its playback over:
    /// it selects animations, sets sections and the loop count, and plays and stops. A new state plays its
    /// section from the first frame at once, looping; under reduced motion it is held on that frame, stopped, so
    /// no event fires.
    /// </summary>
    internal sealed class SpriteStudioUiStateDriver
    {
        private SpriteStudioInternalPlayer _shownPlayer;
        private bool _hasShown;
        private SpriteStudioUiState _state;
        private bool _selected;
        private bool _reduced;
        private string _shownAnimation;
        private int _shownStart;
        private int _shownEnd;
        private bool _shownHold;
        private string _base;
        private readonly HashSet<string> _reported = new HashSet<string>();

        /// <summary>Show the state again on the next call, as when the clips change.</summary>
        public void Invalidate() => _hasShown = false;

        /// <summary>
        /// Shows <paramref name="state"/> — or nothing before the graphic has a pack. Showing what is shown, on
        /// a graphic still playing what it was given, does nothing, so it can be called every frame; after the
        /// graphic loads another pack, or something else changes what it plays, the state is shown again.
        /// </summary>
        public void Show(SpriteStudioGraphic graphic, SpriteStudioUiState state, bool selected, bool reducedMotion,
                         IReadOnlyList<SpriteStudioUiClip> clips, Object context)
        {
            var p = graphic != null ? graphic.InternalPlayer : null;
            if (p == null || !p.HasSsab) return;
            string current = p.CurrentAnimationName;
            bool samePlayer = ReferenceEquals(p, _shownPlayer);
            bool running = p.IsPlaying() && !p.IsPausing();
            if (samePlayer && _hasShown && state == _state && selected == _selected && reducedMotion == _reduced
                && current == _shownAnimation && p.GetAnimationSectionStart() == _shownStart
                && p.GetAnimationSectionEnd() == _shownEnd && running != _shownHold)
                return;

            // A graphic enabled again comes back with a new runtime, set up on the animation last shown, which
            // may be a clip's; the animation the states are read from stays while the pack still has it.
            if (!samePlayer) _reported.Clear();
            // An animation the driver did not set was chosen by the scene or a script, and the states are read
            // from it now.
            if (_base == null || (current != null && current != _shownAnimation) || graphic.GetLabelTable(_base) == null)
            {
                _base = current;
                if (string.IsNullOrEmpty(_base)) _base = graphic.CurrentAnimation;
                if (string.IsNullOrEmpty(_base) || graphic.GetLabelTable(_base) == null) _base = p.GetAnimationName(0);
            }
            if (string.IsNullOrEmpty(_base)) return;

            if (!SpriteStudioUiStates.TryResolve(state, selected, reducedMotion, _base, graphic.GetLabelTable, clips,
                                                 clip => ReportMissing(clip, graphic, context), out var section))
                return;

            // The states' animation keeps the fit, so a clip that plays an animation with another canvas does not
            // resize the artwork mid-press.
            graphic.FitBox = p.TryGetCanvasRect(_base, out Rect box) ? box : (Rect?)null;
            graphic.SetAnimation(section.Animation);
            graphic.SetAnimationSection(section.StartFrame, section.EndFrame);
            if (section.Hold)
            {
                // Stopped rather than paused, so the held frame fires no events.
                graphic.Stop();
                graphic.FrameNo = section.StartFrame;
            }
            else
            {
                graphic.LoopCount = -1;
                graphic.Play();
            }

            _shownPlayer = p;
            _hasShown = true;
            _state = state;
            _selected = selected;
            _reduced = reducedMotion;
            _shownAnimation = p.CurrentAnimationName;
            _shownStart = p.GetAnimationSectionStart();
            _shownEnd = p.GetAnimationSectionEnd();
            _shownHold = section.Hold;
        }

        /// <summary>Hands the graphic back: it fits its own animation again.</summary>
        public void Release(SpriteStudioGraphic graphic)
        {
            if (graphic != null) graphic.FitBox = null;
            _shownPlayer = null;
            _hasShown = false;
        }

        private void ReportMissing(SpriteStudioUiClip clip, SpriteStudioGraphic graphic, Object context)
        {
            string key = $"{clip.State}\0{clip.Selected}\0{clip.Animation}\0{clip.Label}";
            if (!_reported.Add(key)) return;
            string what = string.IsNullOrEmpty(clip.Animation) ? "" : $"animation '{clip.Animation}'";
            if (!string.IsNullOrEmpty(clip.Label)) what += (what.Length > 0 ? " with " : "") + $"label '{clip.Label}'";
            string pack = graphic.AnimeAsset != null ? graphic.AnimeAsset.name : "the loaded pack";
            Debug.LogWarning(
                $"[{(context != null ? context.GetType().Name : nameof(SpriteStudioUiStateDriver))}] The state clip for " +
                $"'{SpriteStudioUiStates.LabelOf(clip.State, clip.Selected)}' names {what}, which '{pack}' does not have, " +
                "so the state plays as if it had no clip.", context);
        }
    }
}
