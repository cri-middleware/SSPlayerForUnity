using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using SpriteStudio.Runtime;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// Guards the mask stencil bit-plane split.
    ///
    /// Mask writers use one of two ops depending on the part's maskInfluence: Invert (influence=1) or
    /// IncrementWrap/DecrementWrap (influence=0). Each op is self-inverse on its own, but the two are
    /// NOT mutually commutative, so Invert stays on a parity plane (0x08) and Incr/Decr on a counter plane
    /// (0x07), where neither observes the other's bits. These tests pin that separation, the arithmetic
    /// properties each plane needs, and the URP coexistence bound (nothing may escape 0x0F).
    /// </summary>
    public class SpriteStudioMaskStencilPlaneTests
    {
        private const byte OpInvert = (byte)StencilOp.Invert;
        private const byte OpIncr = (byte)StencilOp.IncrementWrap;
        private const byte OpDecr = (byte)StencilOp.DecrementWrap;

        /// <summary>
        /// Model of a hardware stencil write: the op runs on the whole value, then the write mask
        /// decides which bits actually land (new = (op(old) &amp; WriteMask) | (old &amp; ~WriteMask)).
        /// This is what makes a plane "closed" - the carry/borrow leaving the plane is discarded.
        /// </summary>
        private static byte Write(byte value, byte op, byte writeMask)
        {
            int applied = op switch
            {
                OpInvert => ~value,
                OpIncr => value + 1,
                OpDecr => value - 1,
                _ => value,
            };
            return (byte)((applied & writeMask) | (value & ~writeMask));
        }

        /// <summary>Applies a writer the way the runtime does: the plane is derived from the op.</summary>
        private static byte WriteAsRuntime(byte value, byte op)
            => Write(value, op, SpriteStudioMaterial.MaskWriteMaskForOp(op));

        /// <summary>Coverage test: Ref 0 over ReadMask 0x0F, i.e. "any isolated bit set".</summary>
        private static bool IsCovered(byte stencil)
            => (stencil & SpriteStudioMaterial.MaskStencilMask) != 0;

        // ---- plane layout ----------------------------------------------------------------------

        [Test]
        public void Planes_AreDisjoint_AndExactlyCoverTheMaskBits()
        {
            byte parity = SpriteStudioMaterial.MaskStencilMaskParity;
            byte count = SpriteStudioMaterial.MaskStencilMaskCount;

            Assert.AreEqual(0, parity & count, "the two planes must not share a bit, or the ops corrupt each other again");
            Assert.AreEqual(SpriteStudioMaterial.MaskStencilMask, (byte)(parity | count),
                "together the planes must cover exactly the isolated mask bits, so the union coverage test still sees both");
        }

        [Test]
        public void Planes_StayWithinTheUrpUserReservedBits()
        {
            // Bit 4 is URP's light stencil, bits 5-6 material type, bit 7 reserved. Widening past 0x0F
            // would corrupt (and be corrupted by) URP - the counter plane must be grown some other way.
            Assert.AreEqual(0, SpriteStudioMaterial.MaskStencilMask & 0xF0,
                "the SS mask must stay inside URP's user-reserved low 4 bits");
        }

        [Test]
        public void ParityPlane_IsASingleBit()
        {
            byte parity = SpriteStudioMaterial.MaskStencilMaskParity;
            Assert.AreNotEqual(0, parity);
            Assert.AreEqual(0, parity & (parity - 1), "Invert must flip exactly one bit to behave as a parity toggle");
        }

        [Test]
        public void CounterPlane_IsContiguousFromBitZero()
        {
            byte count = SpriteStudioMaterial.MaskStencilMaskCount;
            Assert.AreNotEqual(0, count);
            Assert.AreEqual(0, count & (count + 1),
                "IncrementWrap/DecrementWrap are ordinary arithmetic, so the counter plane has to be the low contiguous bits");
        }

        // ---- op -> plane routing ---------------------------------------------------------------

        [Test]
        public void MaskWriteMaskForOp_RoutesInvertToParity_AndIncrDecrToCounter()
        {
            Assert.AreEqual(SpriteStudioMaterial.MaskStencilMaskParity, SpriteStudioMaterial.MaskWriteMaskForOp(OpInvert));
            Assert.AreEqual(SpriteStudioMaterial.MaskStencilMaskCount, SpriteStudioMaterial.MaskWriteMaskForOp(OpIncr));
            Assert.AreEqual(SpriteStudioMaterial.MaskStencilMaskCount, SpriteStudioMaterial.MaskWriteMaskForOp(OpDecr));
        }

        [Test]
        public void WriteAndRestore_UseTheSamePlane_AndTheTwoInfluencesUseDifferentOnes()
        {
            // maskInfluence=0 writes with Incr and restores with Decr, so the pair must share a plane or
            // the restore cannot undo the write. maskInfluence=1 uses Invert for both (self-inverse).
            Assert.AreEqual(SpriteStudioMaterial.MaskWriteMaskForOp(OpIncr), SpriteStudioMaterial.MaskWriteMaskForOp(OpDecr),
                "the Incr/Decr write-restore pair must land on one plane");
            Assert.AreNotEqual(SpriteStudioMaterial.MaskWriteMaskForOp(OpIncr), SpriteStudioMaterial.MaskWriteMaskForOp(OpInvert),
                "the two maskInfluence kinds must not share a plane - that is the whole point of the split");
        }

        // ---- the properties each plane must have -----------------------------------------------

        [Test]
        public void ParityPlane_ComposesAsParity_AndIsSelfInverse()
        {
            byte s = 0;
            s = WriteAsRuntime(s, OpInvert);
            Assert.IsTrue(IsCovered(s), "1 overlapping influence=1 mask covers");
            s = WriteAsRuntime(s, OpInvert);
            Assert.IsFalse(IsCovered(s), "2 overlapping influence=1 masks cancel (XOR)");
            s = WriteAsRuntime(s, OpInvert);
            Assert.IsTrue(IsCovered(s), "3 overlapping influence=1 masks cover again - parity, not 'exactly one'");
        }

        [Test]
        public void CounterPlane_CountsUpToSeven_ThenWraps()
        {
            byte s = 0;
            for (int i = 1; i <= 7; i++)
            {
                s = WriteAsRuntime(s, OpIncr);
                Assert.IsTrue(IsCovered(s), $"{i} overlapping influence=0 masks must stay covered (union)");
            }
            // Documented capacity limit of plane A: an 8th overlap wraps the 3-bit counter back to zero.
            // If this ever becomes a real constraint the planes have to be re-budgeted, not just widened
            // (see Planes_StayWithinTheUrpUserReservedBits).
            s = WriteAsRuntime(s, OpIncr);
            Assert.IsFalse(IsCovered(s), "the counter plane holds 7 overlaps; the 8th is the documented wrap point");
        }

        [Test]
        public void CounterPlane_CarryDoesNotLeakIntoTheParityPlane()
        {
            byte s = 0;
            for (int i = 0; i < 8; i++) s = WriteAsRuntime(s, OpIncr); // overflow the 3-bit counter
            Assert.AreEqual(0, s & SpriteStudioMaterial.MaskStencilMaskParity,
                "an IncrementWrap carry must be discarded by the write mask, not flip the parity bit");
        }

        [Test]
        public void CounterPlane_BorrowDoesNotLeakIntoTheParityPlane()
        {
            byte s = WriteAsRuntime(0, OpDecr); // underflow the 3-bit counter
            Assert.AreEqual(0, s & SpriteStudioMaterial.MaskStencilMaskParity,
                "a DecrementWrap borrow must be discarded by the write mask, not flip the parity bit");
        }

        // ---- plane isolation --------------------------------------------------------------------

        [Test]
        public void MixedInfluence_CoverageIsIndependentOfDrawOrder()
        {
            // The reported symptom: with a shared write mask these two orders disagreed
            // (Incr then Invert covered; Invert then Incr wrapped back to 0 and did not).
            byte incrFirst = WriteAsRuntime(WriteAsRuntime(0, OpIncr), OpInvert);
            byte invertFirst = WriteAsRuntime(WriteAsRuntime(0, OpInvert), OpIncr);

            Assert.AreEqual(incrFirst, invertFirst, "mixing maskInfluence must not make coverage depend on draw order");
            Assert.IsTrue(IsCovered(incrFirst), "an influence=0 and an influence=1 mask overlapping compose as a union");
        }

        [Test]
        public void MixedInfluence_EveryWriterRestoresToZero_WhateverIsInterleaved()
        {
            // Phase A pre-writes both pure masks; each removes its own coverage at its own rank, with the
            // other writer's op interleaved in between. The stencil has to end at exactly 0, or the
            // residue leaks onto parts drawn later (and into other SS objects sharing the buffer).
            byte s = 0;
            s = WriteAsRuntime(s, OpIncr);    // Phase A: influence=0 pre-write
            s = WriteAsRuntime(s, OpInvert);  // Phase A: influence=1 pre-write
            s = WriteAsRuntime(s, OpDecr);    // Phase B: influence=0 removes its coverage
            s = WriteAsRuntime(s, OpInvert);  // Phase B: influence=1 removes its coverage

            Assert.AreEqual(0, s, "an interleaved write/restore round must return the stencil to zero");
        }

        [Test]
        public void ThreeOverlappingInfluenceOneMasks_StayCovered_EvenWithACounterMaskInTheMix()
        {
            byte s = 0;
            s = WriteAsRuntime(s, OpInvert);
            s = WriteAsRuntime(s, OpInvert);
            s = WriteAsRuntime(s, OpInvert); // parity of 3 = covered
            Assert.IsTrue(IsCovered(s));

            byte withCounter = WriteAsRuntime(s, OpIncr);
            Assert.IsTrue(IsCovered(withCounter), "adding an influence=0 mask can only add coverage, never remove it");
        }

        // ---- material wiring -------------------------------------------------------------------

        [Test]
        public void CreateMaskWriteMaterial_WritesOnlyItsOwnPlane_AndReadsBoth()
        {
            var shader = Shader.Find("SpriteStudio/MaskWrite");
            if (shader == null)
            {
                Assert.Ignore("SpriteStudio/MaskWrite is not resolvable in this context - skipping the material wiring check.");
            }

            var byOp = new (byte op, byte expectedPlane)[]
            {
                (OpInvert, SpriteStudioMaterial.MaskStencilMaskParity),
                (OpIncr, SpriteStudioMaterial.MaskStencilMaskCount),
                (OpDecr, SpriteStudioMaterial.MaskStencilMaskCount),
            };

            foreach (var (op, expectedPlane) in byOp)
            {
                var m = SpriteStudioMaterial.CreateMaskWriteMaterial(shader, null, op);
                try
                {
                    Assert.IsNotNull(m);
                    Assert.AreEqual(expectedPlane, (byte)m.GetFloat("_StencilWriteMask"),
                        $"op {op} must write only its own plane");
                    Assert.AreEqual(SpriteStudioMaterial.MaskStencilMask, (byte)m.GetFloat("_StencilReadMask"),
                        "the read mask stays the union so coverage is the union of both planes");
                    Assert.AreEqual(0f, m.GetFloat("_StencilRef"), "coverage is 'any isolated bit set', so Ref stays 0");
                }
                finally
                {
                    if (m != null) Object.DestroyImmediate(m);
                }
            }
        }
    }
}
