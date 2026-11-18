using NUnit.Framework;
using UnityEngine.Rendering;
using SpriteStudio.Runtime;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// Regression guard for how masking composes across an instance boundary.
    ///
    /// SpriteStudio 7.6 composes a part's maskInfluence (AND) and visibleInsideMask (OR) with the instance part
    /// DIRECTLY above it - one level only, and whether or not the caller is masked, so a maskInfluence=0
    /// instance exempts only its own sub-animation's parts.
    ///
    /// These tests pin the rules themselves (SpriteStudioInternalPlayer's statics, and the gate that
    /// decides whether a player tests its parts at all), which is where every decision site goes, rather
    /// than the geometry they produce.
    /// </summary>
    public class SpriteStudioMaskPropagationTests
    {
        // Identity context: what a root player - drawn by no instance - composes against.
        private const bool RootInfluence = true;    // AND identity
        private const bool RootVisibleInside = false; // OR identity

        private static SpriteStudioInternalPlayer.MaskPartData Part(bool maskInfluence, bool visibleInsideMask, bool maskWrite = false)
            => new SpriteStudioInternalPlayer.MaskPartData
            {
                MaskInfluence = maskInfluence,
                VisibleInsideMask = visibleInsideMask,
                MaskWrite = maskWrite,
            };

        // ---- identity: a root player sees each part's own settings --------------------------------

        [Test]
        public void RootContext_LeavesEachPartAtItsOwnSettings()
        {
            var target = Part(maskInfluence: true, visibleInsideMask: true);
            var plain = Part(maskInfluence: false, visibleInsideMask: false);

            Assert.IsTrue(SpriteStudioInternalPlayer.IsMaskTarget(target, RootInfluence));
            Assert.IsTrue(SpriteStudioInternalPlayer.IsDrawnInsideMask(target, RootVisibleInside));
            Assert.IsFalse(SpriteStudioInternalPlayer.IsMaskTarget(plain, RootInfluence));
            Assert.IsFalse(SpriteStudioInternalPlayer.IsDrawnInsideMask(plain, RootVisibleInside));
        }

        // ---- the instance part hands down its own flags -------------------------------------------

        [Test]
        public void MaskInfluence_AnUnmaskedCaller_ExemptsItsOwnSubAnimation()
        {
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(maskInfluence: false, visibleInsideMask: false),
                out bool influence, out _);

            Assert.IsFalse(influence, "an instance part with maskInfluence=0 hands 'not masked' to its child");
            Assert.IsFalse(SpriteStudioInternalPlayer.IsMaskTarget(Part(true, false), influence),
                "maskInfluence composes with AND, so the callee cannot re-enable masking the caller switched off");
        }

        [Test]
        public void MaskInfluence_AnInfluencedCaller_LetsEachCalleeDecideForItself()
        {
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(maskInfluence: true, visibleInsideMask: false),
                out bool influence, out _);

            Assert.IsTrue(influence, "AND with true is the callee's own value");
            Assert.IsTrue(SpriteStudioInternalPlayer.IsMaskTarget(Part(true, false), influence));
            Assert.IsFalse(SpriteStudioInternalPlayer.IsMaskTarget(Part(false, false), influence));
        }

        [Test]
        public void MaskInfluence_DoesNotChain_AGrandchildIsJudgedByTheInstancePartThatDrawsIt()
        {
            // caller (maskInfluence=0) -> middle instance part (maskInfluence=1) -> leaf part (maskInfluence=1).
            // SpriteStudio 7.6 (Tired3_NoHole): the Source01 checker inside an MI=0 Tired3 block is still cut, because
            // the Source01 instance part above it has MI=1.
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(false, false), out bool afterCaller, out _);
            Assert.IsFalse(afterCaller, "the middle animation's own parts are exempt");

            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(true, false), out bool afterMiddle, out _);
            Assert.IsTrue(afterMiddle, "what the caller handed the middle animation is not passed on");
            Assert.IsTrue(SpriteStudioInternalPlayer.IsMaskTarget(Part(true, false), afterMiddle));
        }

        [Test]
        public void VisibleInsideMask_ComposesWithOr_SoACallerAskingForInsideWins()
        {
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(maskInfluence: true, visibleInsideMask: true),
                out bool influence, out bool visibleInside);

            var callee = Part(maskInfluence: true, visibleInsideMask: false);
            Assert.IsTrue(SpriteStudioInternalPlayer.IsMaskTarget(callee, influence));
            Assert.IsTrue(SpriteStudioInternalPlayer.IsDrawnInsideMask(callee, visibleInside),
                "visibleInsideMask composes with OR, so the caller's 'inside' reaches the callee");
        }

        [Test]
        public void VisibleInsideMask_ACalleeAskingForInside_IsNotOverriddenByAnOutsideCaller()
        {
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(maskInfluence: true, visibleInsideMask: false),
                out _, out bool visibleInside);

            Assert.IsTrue(SpriteStudioInternalPlayer.IsDrawnInsideMask(Part(true, true), visibleInside),
                "OR keeps the callee's own 'inside'; the caller cannot force it outside");
        }

        [Test]
        public void VisibleInsideMask_DoesNotChain()
        {
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(true, true), out _, out bool afterCaller);
            Assert.IsTrue(afterCaller);

            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(true, false), out _, out bool afterMiddle);
            Assert.IsFalse(afterMiddle, "a grandchild takes the polarity of the instance part that draws it");
            Assert.IsFalse(SpriteStudioInternalPlayer.IsDrawnInsideMask(Part(true, false), afterMiddle));
        }

        // ---- MaskWrite is outside the composition -------------------------------------------------

        [Test]
        public void MaskWrite_NeitherTargetsAPartNorReachesItsChild()
        {
            // A clipping part draws its pixels before it writes its clip, so it is tested only by other
            // masks, on its maskInfluence - MaskWrite itself makes nothing a target.
            Assert.IsFalse(SpriteStudioInternalPlayer.IsMaskTarget(Part(false, false, maskWrite: true), RootInfluence));
            Assert.IsTrue(SpriteStudioInternalPlayer.IsMaskTarget(Part(true, false, maskWrite: true), RootInfluence));

            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(maskInfluence: false, visibleInsideMask: false, maskWrite: true),
                out bool influence, out _);
            Assert.IsFalse(influence, "an instance part's MaskWrite does not hand 'masked' to its child");
        }

        // ---- writer op / plane: own maskInfluence AND the instance part's above -------------------
        // A mask writer's op (and therefore its stencil bit plane) follows its EFFECTIVE maskInfluence,
        // so a mask directly inside a maskInfluence=0 instance moves from the parity plane (Invert, XOR) to
        // the counter plane (Incr/Decr, union). SpriteStudio 7.6 (S01_Overlap / OV_Top) takes this from one level too.

        private static byte PlaneFor(bool influence)
            => SpriteStudioMaterial.MaskWriteMaskForOp(
                influence ? (byte)StencilOp.Invert : (byte)StencilOp.IncrementWrap);

        [Test]
        public void EffectiveMaskInfluence_ComposesWithAnd()
        {
            var mi = Part(maskInfluence: true, visibleInsideMask: false);
            var noMi = Part(maskInfluence: false, visibleInsideMask: false);

            Assert.IsTrue(SpriteStudioInternalPlayer.EffectiveMaskInfluence(mi, RootInfluence),
                "own=1, inherited=1 -> 1");
            Assert.IsFalse(SpriteStudioInternalPlayer.EffectiveMaskInfluence(mi, inheritedMaskInfluence: false),
                "own=1, inherited=0 -> 0: an MI=0 instance forces its child writer's influence off");
            Assert.IsFalse(SpriteStudioInternalPlayer.EffectiveMaskInfluence(noMi, RootInfluence),
                "own=0, inherited=1 -> 0");
            Assert.IsFalse(SpriteStudioInternalPlayer.EffectiveMaskInfluence(noMi, inheritedMaskInfluence: false),
                "own=0, inherited=0 -> 0");
        }

        [Test]
        public void WriterInsideAMaskInfluenceZeroInstance_MovesToTheCounterPlane()
        {
            var writer = Part(maskInfluence: true, visibleInsideMask: false, maskWrite: true);

            bool influenceAtRoot = SpriteStudioInternalPlayer.EffectiveMaskInfluence(writer, RootInfluence);
            Assert.AreEqual(SpriteStudioMaterial.MaskStencilMaskParity, PlaneFor(influenceAtRoot),
                "a maskInfluence=1 writer with no MI=0 instance part above it writes on the parity plane");

            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(false, false), out bool underMi0, out _);
            bool influenceUnderMi0 = SpriteStudioInternalPlayer.EffectiveMaskInfluence(writer, underMi0);
            Assert.AreEqual(SpriteStudioMaterial.MaskStencilMaskCount, PlaneFor(influenceUnderMi0),
                "directly inside a maskInfluence=0 instance the same writer moves to the counter plane (union, not XOR)");
        }

        [Test]
        public void WriterTwoLevelsBelowAMaskInfluenceZeroInstance_StaysOnTheParityPlane()
        {
            // OV_Top: MI=0 at the top, MI=1 on the instance part that draws the writer. The overlap of two
            // inverting clips still cancels in SpriteStudio 7.6, so the writer keeps inverting.
            var writer = Part(maskInfluence: true, visibleInsideMask: false, maskWrite: true);
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(false, false), out _, out _);
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(true, false), out bool middleInfluence, out _);

            Assert.AreEqual(SpriteStudioMaterial.MaskStencilMaskParity,
                PlaneFor(SpriteStudioInternalPlayer.EffectiveMaskInfluence(writer, middleInfluence)));
        }

        // ---- whether a player tests its parts at all ----------------------------------------------

        private static SpriteStudioInternalPlayer PlayerWith(bool influenced, bool inside)
        {
            var p = new SpriteStudioInternalPlayer();
            p._maskPartData = new SpriteStudioInternalPlayer.MaskPartData[1];
            p._hasInfluencedTargets = influenced;
            p._hasInsideTargets = inside;
            return p;
        }

        [Test]
        public void AnInsideTarget_IsTestedEvenWithNoMaskAnywhere()
        {
            // SpriteStudio 7.6 (S01_NoWriters): a visibleInsideMask=1 part draws only where a mask covers it, so with
            // no mask at all it is hidden - it has to go through the stencil test, not be drawn untested.
            using (var p = PlayerWith(influenced: true, inside: true))
            {
                Assert.IsTrue(p.MaskAffectsParts);
                Assert.IsTrue(p.MaskActive);
            }
        }

        [Test]
        public void OutsideTargets_AreNotTested_WithNoMaskAnywhere()
        {
            // Nothing can cover them, and testing would force the per-part path for nothing.
            using (var p = PlayerWith(influenced: true, inside: false))
                Assert.IsFalse(p.MaskAffectsParts);
        }

        [Test]
        public void OutsideTargets_AreTested_UnderCoverageFromOutside()
        {
            using (var p = PlayerWith(influenced: true, inside: false))
            {
                p._outerMaskCoverage = true;
                Assert.IsTrue(p.MaskAffectsParts);
            }
        }

        [Test]
        public void ACallerAskingForInside_MakesInfluencedPartsInsideTargets()
        {
            using (var p = PlayerWith(influenced: true, inside: false))
            {
                p._inheritedVisibleInsideMask = true;
                Assert.IsTrue(p.MaskAffectsParts);
            }
        }

        [Test]
        public void AnUnmaskedCaller_LeavesNothingToTest()
        {
            using (var p = PlayerWith(influenced: true, inside: true))
            {
                p._outerMaskCoverage = true;
                p._inheritedMaskInfluence = false;
                Assert.IsFalse(p.MaskAffectsParts, "effective maskInfluence is 0 on every part");
            }
        }

        [Test]
        public void CanvasSuppression_KeepsTheGateButDropsTheStencil()
        {
            using (var p = PlayerWith(influenced: true, inside: true))
            {
                p._suppressInternalMask = true;
                Assert.IsTrue(p.MaskAffectsParts, "the per-part path still runs to drop pure masks");
                Assert.IsFalse(p.MaskActive);
            }
        }
    }
}
