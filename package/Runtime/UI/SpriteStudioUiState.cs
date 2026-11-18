using System;
using System.Collections.Generic;
using SpriteStudio.Runtime;

namespace SpriteStudio.UI
{
    /// <summary>
    /// A state a SpriteStudio button shows. Each plays, by default, the section of one animation from the label
    /// of the same name (lower case) to the frame before the next label.
    /// </summary>
    public enum SpriteStudioUiState
    {
        Normal = 0,
        Hover = 1,
        Pressed = 2,
        Disabled = 3,
        Focused = 4,
    }

    /// <summary>
    /// What a state plays in place of the label named after it: the whole of <see cref="Animation"/>, or its
    /// section from <see cref="Label"/>. An empty <see cref="Animation"/> is the animation the states are read
    /// from; an empty <see cref="Label"/> is the whole animation.
    /// </summary>
    [Serializable]
    public struct SpriteStudioUiClip
    {
        public SpriteStudioUiState State;
        /// <summary>For the state of a selected toggle — the <c>selected_</c> labels.</summary>
        public bool Selected;
        public string Animation;
        public string Label;

        public SpriteStudioUiClip(SpriteStudioUiState state, bool selected, string animation, string label)
        {
            State = state;
            Selected = selected;
            Animation = animation;
            Label = label;
        }
    }

    /// <summary>A section a state plays, and whether to hold its first frame rather than play it.</summary>
    public readonly struct SpriteStudioUiSection
    {
        public readonly string Animation;
        public readonly int StartFrame;
        public readonly int EndFrame;
        public readonly bool Hold;

        public SpriteStudioUiSection(string animation, int startFrame, int endFrame, bool hold)
        {
            Animation = animation;
            StartFrame = startFrame;
            EndFrame = endFrame;
            Hold = hold;
        }
    }

    /// <summary>
    /// The rules that turn a UI state into a section of an animation: which state wins, the labels a state
    /// falls back to, and what reduced motion holds. Pure, so every branch is testable with no player.
    /// </summary>
    public static class SpriteStudioUiStates
    {
        /// <summary>The label a calm loop plays from under reduced motion, in place of <c>normal</c>.</summary>
        public const string ReducedMotionLabel = "reduced_motion";

        /// <summary>What a selected toggle's labels start with: <c>selected_normal</c>, <c>selected_hover</c>, …</summary>
        public const string SelectedPrefix = "selected_";

        private static readonly string[] Names = { "normal", "hover", "pressed", "disabled", "focused" };

        /// <summary>The label a state is read from: its name in lower case, with <c>selected_</c> when selected.</summary>
        public static string LabelOf(SpriteStudioUiState state, bool selected = false)
            => selected ? SelectedPrefix + Names[(int)state] : Names[(int)state];

        /// <summary>
        /// The one state to show when several hold at once: disabled over pressed over hover over focused, and
        /// normal when none does.
        /// </summary>
        public static SpriteStudioUiState Of(bool disabled, bool pressed, bool hovered, bool focused)
        {
            if (disabled) return SpriteStudioUiState.Disabled;
            if (pressed) return SpriteStudioUiState.Pressed;
            if (hovered) return SpriteStudioUiState.Hover;
            if (focused) return SpriteStudioUiState.Focused;
            return SpriteStudioUiState.Normal;
        }

        private static readonly SpriteStudioUiState[][] Chains =
        {
            new[] { SpriteStudioUiState.Normal },
            new[] { SpriteStudioUiState.Hover, SpriteStudioUiState.Normal },
            new[] { SpriteStudioUiState.Pressed, SpriteStudioUiState.Hover, SpriteStudioUiState.Normal },
            new[] { SpriteStudioUiState.Disabled, SpriteStudioUiState.Normal },
            new[] { SpriteStudioUiState.Focused, SpriteStudioUiState.Normal },
        };

        /// <summary>
        /// The labels a state is read from, in order: its own, then those it falls back to when the animation
        /// lacks it. A selected toggle tries every <c>selected_</c> label first, so one that is on still looks on
        /// while hovered — except that a press goes straight to <c>pressed</c>, being brief and the sign the press
        /// was taken.
        /// </summary>
        public static IReadOnlyList<(SpriteStudioUiState State, bool Selected)> Chain(SpriteStudioUiState state, bool selected)
        {
            var chain = new List<(SpriteStudioUiState, bool)>(7);
            var links = Chains[(int)state];
            if (selected && state == SpriteStudioUiState.Pressed)
            {
                chain.Add((SpriteStudioUiState.Pressed, true));
                chain.Add((SpriteStudioUiState.Pressed, false));
                links = Chains[(int)SpriteStudioUiState.Hover];
            }
            if (selected)
            {
                foreach (var s in links) chain.Add((s, true));
            }
            foreach (var s in links) chain.Add((s, false));
            return chain;
        }

        /// <summary>
        /// The section from <paramref name="label"/> in <paramref name="table"/> to the frame before the next
        /// label — any label, the state names or not — or to the end of the animation. False when the table has
        /// no such label.
        /// </summary>
        public static bool TryGetLabelSection(SpriteStudioLabelTable table, string label, out int startFrame, out int endFrame)
        {
            startFrame = endFrame = 0;
            if (table == null || !table.TryGetFrame(label, out int start)) return false;
            int end = table.EndFrame;
            foreach (var l in table.Labels)
            {
                if (l.Frame > start)
                {
                    end = l.Frame - 1;
                    break;
                }
            }
            startFrame = start;
            endFrame = end;
            return true;
        }

        /// <summary>
        /// The section <paramref name="state"/> plays in <paramref name="animation"/>: the first of its
        /// <see cref="Chain"/> that has a clip in <paramref name="clips"/> or a label of its own name, or else the
        /// whole animation. False when <paramref name="tableOf"/> knows no <paramref name="animation"/>.
        /// <para>
        /// Under <paramref name="reducedMotion"/> the section is held, except where it came from <c>normal</c>
        /// (or the whole animation <c>normal</c> falls back to) and the animation has a <c>reduced_motion</c> label:
        /// that label's section plays instead.
        /// </para>
        /// A clip that names an animation or a label the pack does not have is handed to
        /// <paramref name="onMissingClip"/>, and the state goes on as if it had none.
        /// </summary>
        public static bool TryResolve(SpriteStudioUiState state, bool selected, bool reducedMotion, string animation,
                                      Func<string, SpriteStudioLabelTable> tableOf, IReadOnlyList<SpriteStudioUiClip> clips,
                                      Action<SpriteStudioUiClip> onMissingClip, out SpriteStudioUiSection section)
        {
            section = default;
            var table = tableOf(animation);
            if (table == null) return false;

            string name = animation;
            int start = table.StartFrame, end = table.EndFrame;
            bool found = false;
            (SpriteStudioUiState State, bool Selected) source = default;
            foreach (var link in Chain(state, selected))
            {
                if (TryFindClip(clips, link.State, link.Selected, out var clip))
                {
                    string clipName = string.IsNullOrEmpty(clip.Animation) ? animation : clip.Animation;
                    var clipTable = clipName == animation ? table : tableOf(clipName);
                    if (clipTable != null && string.IsNullOrEmpty(clip.Label))
                    {
                        start = clipTable.StartFrame;
                        end = clipTable.EndFrame;
                        found = true;
                    }
                    else
                    {
                        found = TryGetLabelSection(clipTable, clip.Label, out start, out end);
                    }
                    if (found)
                    {
                        name = clipName;
                        source = link;
                        break;
                    }
                    onMissingClip?.Invoke(clip);
                }
                if (TryGetLabelSection(table, LabelOf(link.State, link.Selected), out start, out end))
                {
                    found = true;
                    source = link;
                    break;
                }
            }
            if (!found)
            {
                start = table.StartFrame;
                end = table.EndFrame;
            }
            if (reducedMotion && (!found || (source.State == SpriteStudioUiState.Normal && !source.Selected))
                && TryGetLabelSection(table, ReducedMotionLabel, out int calmStart, out int calmEnd))
            {
                section = new SpriteStudioUiSection(animation, calmStart, calmEnd, false);
                return true;
            }
            section = new SpriteStudioUiSection(name, start, end, reducedMotion);
            return true;
        }

        private static bool TryFindClip(IReadOnlyList<SpriteStudioUiClip> clips, SpriteStudioUiState state, bool selected,
                                        out SpriteStudioUiClip clip)
        {
            clip = default;
            if (clips == null) return false;
            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                if (c.State != state || c.Selected != selected) continue;
                if (string.IsNullOrEmpty(c.Animation) && string.IsNullOrEmpty(c.Label)) continue;
                clip = c;
                return true;
            }
            return false;
        }
    }
}
