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
    }
}
