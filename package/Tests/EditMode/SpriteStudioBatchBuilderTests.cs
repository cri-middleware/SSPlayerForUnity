using NUnit.Framework;
using SpriteStudio.Runtime;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// Guard for how <see cref="SpriteStudioBatchBuilder.EmitSubmesh"/> merges unmasked colour runs.
    ///
    /// An instance parent copies its child's submeshes in but carries the child's clipping restores to its
    /// own end, so the child's last colour run and the next thing the parent emits are no longer adjacent
    /// in the index buffer, and must not merge.
    /// </summary>
    public class SpriteStudioBatchBuilderTests
    {
        private const uint Tex = 0x1234u;
        private const byte Blend = 0;

        [Test]
        public void ContiguousColourRuns_Merge()
        {
            using (var b = new SpriteStudioBatchBuilder())
            {
                b.BeginFrame(4);
                b.EmitSubmesh(0, 6, Tex, Blend);
                b.EmitSubmesh(6, 6, Tex, Blend);

                Assert.AreEqual(1, b.SubmeshCount);
                Assert.AreEqual(0, b.Submeshes[0].IndexStart);
                Assert.AreEqual(12, b.Submeshes[0].IndexCount);
            }
        }

        [Test]
        public void ColourRunsWithAGapBetween_StaySeparate()
        {
            using (var b = new SpriteStudioBatchBuilder())
            {
                b.BeginFrame(4);
                b.EmitSubmesh(24, 6, Tex, Blend);
                // Indices 30..41 belong to restores carried to the end; the next run starts after them.
                b.EmitSubmesh(42, 6, Tex, Blend);

                Assert.AreEqual(2, b.SubmeshCount, "a merge would cover the restores' indices in between");
                Assert.AreEqual(24, b.Submeshes[0].IndexStart);
                Assert.AreEqual(6, b.Submeshes[0].IndexCount);
                Assert.AreEqual(42, b.Submeshes[1].IndexStart);
                Assert.AreEqual(6, b.Submeshes[1].IndexCount);
            }
        }

        // One batch of two parts, with totals that stay the same whatever the parts in it.
        private static unsafe ulong Signature(byte kind, ushort first, ushort second, ulong firstFlag = 0, ulong secondFlag = 0)
        {
            using (var b = new SpriteStudioBatchBuilder())
            {
                b.BeginFrame(4);
                int vertices = 0, indices = 0;
                b.EmitDrawBatch(0, 2, 30, 72, kind, Tex, Blend, 0u, ref vertices, ref indices);

                ushort* order = stackalloc ushort[2] { first, second };
                PartStateData* states = stackalloc PartStateData[2];
                states[0] = new PartStateData { UpdateFlag = firstFlag };
                states[1] = new PartStateData { UpdateFlag = secondFlag };
                int* shapeCounts = stackalloc int[2] { 4, 8 };
                return b.ComputeTopologySignature(vertices, indices, order, states, shapeCounts, 2);
            }
        }

        [Test]
        public void Signature_IsStable_ForTheSameParts()
        {
            Assert.AreEqual(Signature(SsDrawBatchKind.Mesh, 0, 1), Signature(SsDrawBatchKind.Mesh, 0, 1));
        }

        [TestCase(SsDrawBatchKind.Mesh)]
        [TestCase(SsDrawBatchKind.Shape)]
        public void Signature_Differs_WhenThePartsInABatchSwap(byte kind)
        {
            Assert.AreNotEqual(Signature(kind, 0, 1), Signature(kind, 1, 0));
        }

        [Test]
        public void Signature_Differs_WhenACentreVertexPassesToAnotherPart()
        {
            ulong firstHasIt = Signature(SsDrawBatchKind.Normal, 0, 1, firstFlag: SsUpdateFlag.AttributePartColor);
            ulong secondHasIt = Signature(SsDrawBatchKind.Normal, 0, 1, secondFlag: SsUpdateFlag.AttributePartColor);
            Assert.AreNotEqual(firstHasIt, secondHasIt);
        }

        [Test]
        public void Signature_IsTheSame_ForNormalPartsOfTheSameShape()
        {
            Assert.AreEqual(Signature(SsDrawBatchKind.Normal, 0, 1), Signature(SsDrawBatchKind.Normal, 1, 0),
                "their indices do not depend on which part is which, so nothing needs uploading");
        }
    }
}
