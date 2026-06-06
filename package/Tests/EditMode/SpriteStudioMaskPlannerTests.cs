using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using SpriteStudio.Runtime;

namespace SpriteStudio.Runtime.EditModeTests
{
    public class SpriteStudioMaskPlannerTests
    {
        private SpriteStudioMaskPlanner _planner;

        [SetUp]
        public void SetUp()
        {
            _planner = new SpriteStudioMaskPlanner();
        }

        [TearDown]
        public void TearDown()
        {
            _planner.Dispose();
        }

        [Test]
        public unsafe void MaskPartVcIc_Normal_WithoutCenter_Returns4_6()
        {
            var frame = new SpriteStudioGeometryBuilder.DrawFrame();
            var partStates = new NativeArray<PartStateData>(1, Allocator.Temp);
            try
            {
                partStates[0] = new PartStateData { UpdateFlag = 0 };

                bool result = _planner.MaskPartVcIc(0, SsDrawBatchKind.Normal, frame, partStates, out int vc, out int ic);

                Assert.IsTrue(result);
                Assert.AreEqual(4, vc);
                Assert.AreEqual(6, ic);
            }
            finally
            {
                partStates.Dispose();
            }
        }

        [Test]
        public unsafe void MaskPartVcIc_Normal_WithCenter_Returns5_12()
        {
            var frame = new SpriteStudioGeometryBuilder.DrawFrame();
            var partStates = new NativeArray<PartStateData>(1, Allocator.Temp);
            try
            {
                partStates[0] = new PartStateData { UpdateFlag = (ulong)SsUpdateFlag.NeedsCenterMask };

                bool result = _planner.MaskPartVcIc(0, SsDrawBatchKind.Normal, frame, partStates, out int vc, out int ic);

                Assert.IsTrue(result);
                Assert.AreEqual(5, vc);
                Assert.AreEqual(12, ic);
            }
            finally
            {
                partStates.Dispose();
            }
        }

        [Test]
        public unsafe void MaskPartVcIc_Shape_SvcLessThan3_ReturnsFalse()
        {
            var frame = new SpriteStudioGeometryBuilder.DrawFrame();
            var partStates = new NativeArray<PartStateData>(1, Allocator.Temp);
            try
            {
                int* shapeCounts = stackalloc int[1] { 2 };
                frame.ShapeVertexCounts = shapeCounts;

                bool result = _planner.MaskPartVcIc(0, SsDrawBatchKind.Shape, frame, partStates, out int vc, out int ic);

                Assert.IsFalse(result);
            }
            finally
            {
                partStates.Dispose();
            }
        }

        [Test]
        public unsafe void MaskPartVcIc_Shape_SvcGreaterOrEqual3_ReturnsCalculated()
        {
            var frame = new SpriteStudioGeometryBuilder.DrawFrame();
            var partStates = new NativeArray<PartStateData>(1, Allocator.Temp);
            try
            {
                int* shapeCounts = stackalloc int[1] { 4 };
                frame.ShapeVertexCounts = shapeCounts;

                bool result = _planner.MaskPartVcIc(0, SsDrawBatchKind.Shape, frame, partStates, out int vc, out int ic);

                Assert.IsTrue(result);
                Assert.AreEqual(4, vc);
                Assert.AreEqual(6, ic); // (4 - 2) * 3
            }
            finally
            {
                partStates.Dispose();
            }
        }

        [Test]
        public unsafe void MaskPartVcIc_Mesh_Valid_ReturnsCalculated()
        {
            var frame = new SpriteStudioGeometryBuilder.DrawFrame();
            var partStates = new NativeArray<PartStateData>(1, Allocator.Temp);
            try
            {
                uint* vOffsets = stackalloc uint[2] { 10, 15 }; // 5 vertices
                uint* iOffsets = stackalloc uint[2] { 20, 29 }; // 9 indices (multiple of 3)
                
                frame.MeshVertexOffsets = vOffsets;
                frame.MeshIndexOffsets = iOffsets;

                bool result = _planner.MaskPartVcIc(0, SsDrawBatchKind.Mesh, frame, partStates, out int vc, out int ic);

                Assert.IsTrue(result);
                Assert.AreEqual(5, vc);
                Assert.AreEqual(9, ic);
            }
            finally
            {
                partStates.Dispose();
            }
        }

        [Test]
        public unsafe void MaskPartVcIc_Mesh_InvalidIndices_ReturnsFalse()
        {
            var frame = new SpriteStudioGeometryBuilder.DrawFrame();
            var partStates = new NativeArray<PartStateData>(1, Allocator.Temp);
            try
            {
                uint* vOffsets = stackalloc uint[2] { 10, 15 };
                uint* iOffsets = stackalloc uint[2] { 20, 28 }; // 8 indices (NOT multiple of 3)
                
                frame.MeshVertexOffsets = vOffsets;
                frame.MeshIndexOffsets = iOffsets;

                bool result = _planner.MaskPartVcIc(0, SsDrawBatchKind.Mesh, frame, partStates, out int vc, out int ic);

                Assert.IsFalse(result);
            }
            finally
            {
                partStates.Dispose();
            }
        }

        [Test]
        public unsafe void MaskPartVcIc_Mask_UnknownKind_ReturnsFalse()
        {
            var frame = new SpriteStudioGeometryBuilder.DrawFrame();
            var partStates = new NativeArray<PartStateData>(1, Allocator.Temp);
            try
            {
                // 0xFF is an unknown kind
                bool result = _planner.MaskPartVcIc(0, 0xFF, frame, partStates, out int vc, out int ic);

                Assert.IsFalse(result);
            }
            finally
            {
                partStates.Dispose();
            }
        }
    }
}
