using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpriteStudio.Runtime;
using SpriteStudio.UI;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// The rules that turn a UI state into a section. No test pack carries the state labels, so the tables are
    /// built by hand, as on the other Players.
    /// </summary>
    public class SpriteStudioUiStateTests
    {
        private const string Button = "button";
        private const string Other = "other";

        // Labels handed over out of frame order, as the pack keeps them.
        private static SpriteStudioLabelTable Table(int endFrame, params (string name, int frame)[] labels)
            => new SpriteStudioLabelTable(labels.Select(l => new SpriteStudioLabel(l.name, l.frame)), 0, endFrame);

        private static readonly SpriteStudioLabelTable Full = Table(59,
            ("pressed", 20), ("normal", 0), ("reduced_motion", 40), ("hover", 10), ("note", 30),
            ("selected_normal", 50));

        private static SpriteStudioUiSection Resolve(SpriteStudioUiState state, SpriteStudioLabelTable table,
                                                     bool selected = false, bool reduced = false,
                                                     IReadOnlyList<SpriteStudioUiClip> clips = null,
                                                     List<SpriteStudioUiClip> missing = null)
        {
            var tables = new Dictionary<string, SpriteStudioLabelTable> { [Button] = table, [Other] = Table(9, ("x", 4)) };
            Assert.IsTrue(SpriteStudioUiStates.TryResolve(state, selected, reduced, Button,
                name => tables.TryGetValue(name, out var t) ? t : null, clips, c => missing?.Add(c), out var section));
            return section;
        }

        private static void AssertSection(SpriteStudioUiSection s, string animation, int start, int end, bool hold)
        {
            Assert.AreEqual(animation, s.Animation);
            Assert.AreEqual(start, s.StartFrame, "start");
            Assert.AreEqual(end, s.EndFrame, "end");
            Assert.AreEqual(hold, s.Hold, "hold");
        }

        [Test]
        public void Of_PutsDisabledOverPressedOverHoverOverFocused()
        {
            Assert.AreEqual(SpriteStudioUiState.Disabled, SpriteStudioUiStates.Of(true, true, true, true));
            Assert.AreEqual(SpriteStudioUiState.Pressed, SpriteStudioUiStates.Of(false, true, true, true));
            Assert.AreEqual(SpriteStudioUiState.Hover, SpriteStudioUiStates.Of(false, false, true, true));
            Assert.AreEqual(SpriteStudioUiState.Focused, SpriteStudioUiStates.Of(false, false, false, true));
            Assert.AreEqual(SpriteStudioUiState.Normal, SpriteStudioUiStates.Of(false, false, false, false));
        }

        [Test]
        public void AState_RunsFromItsLabel_ToTheFrameBeforeTheNextLabelOfAnyName()
        {
            AssertSection(Resolve(SpriteStudioUiState.Normal, Full), Button, 0, 9, false);
            AssertSection(Resolve(SpriteStudioUiState.Pressed, Full), Button, 20, 29, false);
        }

        [Test]
        public void TheLastLabel_RunsToTheEndOfTheAnimation()
        {
            Assert.IsTrue(SpriteStudioUiStates.TryGetLabelSection(Full, "selected_normal", out int start, out int end));
            Assert.AreEqual((50, 59), (start, end));
        }

        [Test]
        public void AMissingLabel_FallsBack()
        {
            var normalOnly = Table(29, ("normal", 0), ("hover_typo", 10));
            AssertSection(Resolve(SpriteStudioUiState.Hover, normalOnly), Button, 0, 9, false);
            AssertSection(Resolve(SpriteStudioUiState.Disabled, normalOnly), Button, 0, 9, false);
            AssertSection(Resolve(SpriteStudioUiState.Focused, normalOnly), Button, 0, 9, false);

            var hover = Table(29, ("normal", 0), ("hover", 10));
            AssertSection(Resolve(SpriteStudioUiState.Pressed, hover), Button, 10, 29, false);

            AssertSection(Resolve(SpriteStudioUiState.Pressed, Table(29)), Button, 0, 29, false);
        }

        [Test]
        public void Selected_TriesEverySelectedLabelFirst_ButAPressGoesToPressed()
        {
            CollectionAssert.AreEqual(new[] { "selected_hover", "selected_normal", "hover", "normal" },
                SpriteStudioUiStates.Chain(SpriteStudioUiState.Hover, true).Select(l => SpriteStudioUiStates.LabelOf(l.State, l.Selected)));
            CollectionAssert.AreEqual(new[] { "selected_pressed", "pressed", "selected_hover", "selected_normal", "hover", "normal" },
                SpriteStudioUiStates.Chain(SpriteStudioUiState.Pressed, true).Select(l => SpriteStudioUiStates.LabelOf(l.State, l.Selected)));

            AssertSection(Resolve(SpriteStudioUiState.Hover, Full, selected: true), Button, 50, 59, false);
            AssertSection(Resolve(SpriteStudioUiState.Pressed, Full, selected: true), Button, 20, 29, false);
        }

        [Test]
        public void AClip_ReplacesTheLabel_AndServesTheStatesThatFallBackToIt()
        {
            var clips = new[] { new SpriteStudioUiClip(SpriteStudioUiState.Hover, false, Other, "") };
            var hoverless = Table(29, ("normal", 0));

            AssertSection(Resolve(SpriteStudioUiState.Hover, hoverless, clips: clips), Other, 0, 9, false);
            AssertSection(Resolve(SpriteStudioUiState.Pressed, hoverless, clips: clips), Other, 0, 9, false);
            AssertSection(Resolve(SpriteStudioUiState.Normal, hoverless, clips: clips), Button, 0, 29, false);

            var label = new[] { new SpriteStudioUiClip(SpriteStudioUiState.Normal, false, "", "note") };
            AssertSection(Resolve(SpriteStudioUiState.Normal, Full, clips: label), Button, 30, 39, false);
        }

        [Test]
        public void AClipThePackDoesNotHave_IsReported_AndTheStateGoesOnWithoutIt()
        {
            var missing = new List<SpriteStudioUiClip>();
            var clips = new[]
            {
                new SpriteStudioUiClip(SpriteStudioUiState.Hover, false, "no_such_animation", ""),
                new SpriteStudioUiClip(SpriteStudioUiState.Normal, false, "", "no_such_label"),
            };

            AssertSection(Resolve(SpriteStudioUiState.Hover, Full, clips: clips, missing: missing), Button, 10, 19, false);
            Assert.AreEqual(1, missing.Count);
            AssertSection(Resolve(SpriteStudioUiState.Normal, Full, clips: clips, missing: missing), Button, 0, 9, false);
            Assert.AreEqual(2, missing.Count);
        }

        [Test]
        public void ReducedMotion_HoldsEachState_ButNormalPlaysTheReducedMotionLabel()
        {
            AssertSection(Resolve(SpriteStudioUiState.Normal, Full, reduced: true), Button, 40, 49, false);
            AssertSection(Resolve(SpriteStudioUiState.Focused, Full, reduced: true), Button, 40, 49, false);
            AssertSection(Resolve(SpriteStudioUiState.Pressed, Full, reduced: true), Button, 20, 29, true);
            AssertSection(Resolve(SpriteStudioUiState.Normal, Full, selected: true, reduced: true), Button, 50, 59, true);
            AssertSection(Resolve(SpriteStudioUiState.Normal, Table(29, ("normal", 0)), reduced: true), Button, 0, 29, true);
        }

        [Test]
        public void AnAnimationThePackDoesNotHave_ResolvesToNothing()
        {
            Assert.IsFalse(SpriteStudioUiStates.TryResolve(SpriteStudioUiState.Normal, false, false, "missing",
                _ => null, null, null, out _));
        }
    }
}
