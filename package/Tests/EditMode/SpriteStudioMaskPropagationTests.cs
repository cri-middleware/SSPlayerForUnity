using NUnit.Framework;
using UnityEngine.Rendering;
using SpriteStudio.Runtime;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// Regression guard for how masking composes down an instance chain.
    ///
    /// SS7.5 composes the two masking flags as a caller/callee pair: visibleInsideMask accumulates with
    /// OR, maskInfluence chains with AND. The builder used to do neither - it drew each instance child's
    /// submeshes with their own stencil role and only patched the role-less ones to the calling instance
    /// part's role, so a child part carrying a role overrode its caller while a role-less one was forced
    /// to the caller's polarity even when its own said otherwise.
    ///
    /// These tests pin the rules themselves (SpriteStudioInternalPlayer's three statics), which is where
    /// every decision site now goes, rather than the geometry they produce.
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

        // ---- maskInfluence chains with AND --------------------------------------------------------

        [Test]
        public void MaskInfluence_ChainsWithAnd_SoAnUnmaskedCallerUnmasksItsWholeSubtree()
        {
            var callerNotInfluenced = Part(maskInfluence: false, visibleInsideMask: false);
            SpriteStudioInternalPlayer.ComposeInheritedContext(callerNotInfluenced, RootInfluence, RootVisibleInside,
                out bool masked, out bool influence, out _);

            Assert.IsFalse(masked, "an instance part with maskInfluence=0 and no MaskWrite is not a mask target");
            Assert.IsFalse(influence, "and it hands 'not masked' down");

            // A callee that would be a target on its own no longer is.
            var calleeWantsMask = Part(maskInfluence: true, visibleInsideMask: false);
            Assert.IsFalse(SpriteStudioInternalPlayer.IsMaskTarget(calleeWantsMask, influence),
                "maskInfluence composes with AND, so the callee cannot re-enable masking the caller switched off");
        }

        [Test]
        public void MaskInfluence_AnInfluencedCaller_LetsEachCalleeDecideForItself()
        {
            var caller = Part(maskInfluence: true, visibleInsideMask: false);
            SpriteStudioInternalPlayer.ComposeInheritedContext(caller, RootInfluence, RootVisibleInside,
                out bool masked, out bool influence, out _);

            Assert.IsTrue(masked);
            Assert.IsTrue(influence, "AND with true is the callee's own value");
            Assert.IsTrue(SpriteStudioInternalPlayer.IsMaskTarget(Part(true, false), influence));
            Assert.IsFalse(SpriteStudioInternalPlayer.IsMaskTarget(Part(false, false), influence));
        }

        [Test]
        public void MaskInfluence_OnceFalse_StaysFalseThroughDeeperNesting()
        {
            // caller (influence=0) -> middle (influence=1) -> leaf (influence=1)
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(false, false), RootInfluence, RootVisibleInside,
                out _, out bool afterCaller, out bool visibleAfterCaller);
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(true, false), afterCaller, visibleAfterCaller,
                out bool middleMasked, out bool afterMiddle, out _);

            Assert.IsFalse(middleMasked, "the middle instance is inside an unmasked subtree");
            Assert.IsFalse(afterMiddle, "AND cannot climb back to true");
            Assert.IsFalse(SpriteStudioInternalPlayer.IsMaskTarget(Part(true, false), afterMiddle));
        }

        // ---- visibleInsideMask accumulates with OR ------------------------------------------------

        [Test]
        public void VisibleInsideMask_AccumulatesWithOr_SoACallerAskingForInsideWins()
        {
            // The first row of the reported mismatch: caller visibleInsideMask=true drawing a callee part
            // that is a mask target with visibleInsideMask=false. OR makes it draw INSIDE the mask; the old
            // fallback kept the callee's own role and drew it outside.
            var caller = Part(maskInfluence: true, visibleInsideMask: true);
            SpriteStudioInternalPlayer.ComposeInheritedContext(caller, RootInfluence, RootVisibleInside,
                out _, out bool influence, out bool visibleInside);

            var callee = Part(maskInfluence: true, visibleInsideMask: false);
            Assert.IsTrue(SpriteStudioInternalPlayer.IsMaskTarget(callee, influence));
            Assert.IsTrue(SpriteStudioInternalPlayer.IsDrawnInsideMask(callee, visibleInside),
                "visibleInsideMask composes with OR, so the caller's 'inside' reaches the callee");
        }

        [Test]
        public void VisibleInsideMask_ACalleeAskingForInside_IsNotOverriddenByAnOutsideCaller()
        {
            var caller = Part(maskInfluence: true, visibleInsideMask: false);
            SpriteStudioInternalPlayer.ComposeInheritedContext(caller, RootInfluence, RootVisibleInside,
                out _, out _, out bool visibleInside);

            Assert.IsTrue(SpriteStudioInternalPlayer.IsDrawnInsideMask(Part(true, true), visibleInside),
                "OR keeps the callee's own 'inside'; the caller cannot force it outside");
        }

        [Test]
        public void VisibleInsideMask_OnceTrue_StaysTrueThroughDeeperNesting()
        {
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(true, true), RootInfluence, RootVisibleInside,
                out _, out bool afterCaller, out bool visibleAfterCaller);
            SpriteStudioInternalPlayer.ComposeInheritedContext(Part(true, false), afterCaller, visibleAfterCaller,
                out _, out _, out bool afterMiddle);

            Assert.IsTrue(afterMiddle, "OR cannot drop back to false");
            Assert.IsTrue(SpriteStudioInternalPlayer.IsDrawnInsideMask(Part(true, false), afterMiddle));
        }

        // ---- MaskWrite is outside the composition -------------------------------------------------

        [Test]
        public void MaskWrite_MakesAPartATarget_WithoutBeingInherited()
        {
            // MaskWrite says "this part writes a mask of its own" - a local fact, not one a descendant
            // takes on. It still opts the part itself into being tested.
            var clippingCaller = Part(maskInfluence: false, visibleInsideMask: false, maskWrite: true);
            SpriteStudioInternalPlayer.ComposeInheritedContext(clippingCaller, RootInfluence, RootVisibleInside,
                out bool masked, out bool influence, out _);

            Assert.IsTrue(masked, "a clipping writer is itself a mask target");
            Assert.IsFalse(influence, "but maskInfluence=0 still chains 'not masked' to its subtree");

            Assert.IsTrue(SpriteStudioInternalPlayer.IsMaskTarget(Part(false, false, maskWrite: true), influence),
                "a callee that writes its own mask is a target regardless of the inherited context");
        }

        // ---- writer op / plane composes with inherited maskInfluence ------------------------------
        // A mask writer's op (and therefore its stencil bit plane) follows its EFFECTIVE maskInfluence -
        // own AND inherited - so a mask inside a maskInfluence=0 instance moves from the parity plane
        // (Invert, XOR) to the counter plane (Incr/Decr, union). That is what makes an instance-internal
        // mask compose with an outer mask as a union instead of cancelling it (the reported bug: an MI=x
        // instance's child MI=v mask XOR-cancelling against an outer Hole on the shared parity plane).

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

            // Standalone / under a maskInfluence=1 instance: parity plane (its own influence stands).
            bool influenceAtRoot = SpriteStudioInternalPlayer.EffectiveMaskInfluence(writer, RootInfluence);
            Assert.AreEqual(SpriteStudioMaterial.MaskStencilMaskParity, PlaneFor(influenceAtRoot),
                "a maskInfluence=1 writer with no MI=0 ancestor writes on the parity plane");

            // Inside a maskInfluence=0 instance: the composed influence is 0, so it moves to the counter plane.
            bool influenceInherited0 = SpriteStudioInternalPlayer.EffectiveMaskInfluence(writer, inheritedMaskInfluence: false);
            Assert.AreEqual(SpriteStudioMaterial.MaskStencilMaskCount, PlaneFor(influenceInherited0),
                "inside a maskInfluence=0 instance the same writer moves to the counter plane (union, not XOR)");

            Assert.AreNotEqual(PlaneFor(influenceAtRoot), PlaneFor(influenceInherited0),
                "the inherited maskInfluence must actually change which plane the writer lands on");
        }
    }
}
