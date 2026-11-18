using NUnit.Framework;
using SpriteStudio.Runtime;
using Unity.Collections;
using UnityEngine;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// The colours <see cref="UpdateMeshJob"/> writes for one part of each kind. The runtime has already
    /// multiplied the part's opacity, its parents' included, into a part colour's corners; only a part with no
    /// part colour takes it from the part state.
    /// </summary>
    public unsafe class SpriteStudioMeshJobTests
    {
        private const float PartAlpha = 0.5f;
        private const byte CornerAlpha = 128;
        private const byte PartAlphaByte = 127; // (byte)(0.5f * 255f)

        private static SsVertex BuildPart(byte kind, bool hasPartColor)
        {
            float* world = stackalloc float[16] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            float* quad = stackalloc float[10];
            float* shape = stackalloc float[24];
            int* shapeCounts = stackalloc int[1] { 3 };
            float* meshX = stackalloc float[3];
            float* meshY = stackalloc float[3];
            uint* offsets = stackalloc uint[2] { 0, 3 };
            float* meshUvs = stackalloc float[6];
            int* meshIndices = stackalloc int[3] { 0, 1, 2 };

            using (var batches = new NativeArray<DrawBatchData>(1, Allocator.Temp))
            using (var order = new NativeArray<ushort>(1, Allocator.Temp))
            using (var states = new NativeArray<PartStateData>(1, Allocator.Temp))
            using (var colors = new NativeArray<PartColorData>(1, Allocator.Temp))
            using (var vertices = new NativeArray<SsVertex>(8, Allocator.Temp))
            using (var indices = new NativeArray<uint>(16, Allocator.Temp))
            using (var bounds = new NativeArray<Vector3>(2, Allocator.Temp))
            {
                var batchArray = batches;
                batchArray[0] = new DrawBatchData { Kind = kind, Count = 1 };
                var stateArray = states;
                stateArray[0] = new PartStateData
                {
                    Alpha = PartAlpha,
                    PartColorIndex = (short)(hasPartColor ? 0 : -1),
                    UpdateFlag = hasPartColor ? SsUpdateFlag.AttributePartColor : 0UL,
                };
                var corner = new CornerColorData { Rgba = new Color32(10, 20, 30, CornerAlpha), Rate = 1f };
                var colorArray = colors;
                colorArray[0] = new PartColorData { Lt = corner, Rt = corner, Lb = corner, Rb = corner };

                new UpdateMeshJob
                {
                    WorldMatrices = world,
                    LocalUvs = quad,
                    LocalVertices = quad,
                    ShapeVertices = shape,
                    ShapeBoxCoords = shape,
                    ShapeVertexCounts = shapeCounts,
                    MeshVerticesX = meshX,
                    MeshVerticesY = meshY,
                    MeshVertexOffsets = offsets,
                    MeshUvs = meshUvs,
                    MeshIndices = meshIndices,
                    MeshIndexOffsets = offsets,
                    DrawBatches = batches,
                    DrawOrder = order,
                    PartStates = states,
                    PartColors = colors,
                    Vertices = vertices,
                    Indices = indices,
                    Bounds = bounds,
                    BatchCount = 1,
                    PartCount = 1,
                    PartColorCount = 1,
                    RootScale = Vector2.one,
                }.Execute();

                return vertices[0];
            }
        }

        [TestCase(SsDrawBatchKind.Normal)]
        [TestCase(SsDrawBatchKind.Shape)]
        [TestCase(SsDrawBatchKind.Mesh)]
        public void APartColoursAlpha_IsNotMultipliedByThePartsAlphaAgain(byte kind)
        {
            Assert.AreEqual(CornerAlpha, BuildPart(kind, hasPartColor: true).Color.a);
        }

        [TestCase(SsDrawBatchKind.Normal)]
        [TestCase(SsDrawBatchKind.Shape)]
        [TestCase(SsDrawBatchKind.Mesh)]
        public void APartWithNoPartColour_TakesThePartsAlpha(byte kind)
        {
            Assert.AreEqual(PartAlphaByte, BuildPart(kind, hasPartColor: false).Color.a);
        }

        [Test]
        public void AShape_IsMarkedAsHavingNoCell_AndAPartWithACellIsNot()
        {
            Assert.AreEqual(SsVertex.BlendIdxNoCell, BuildPart(SsDrawBatchKind.Shape, hasPartColor: false).TexCoord1.y);
            Assert.GreaterOrEqual(BuildPart(SsDrawBatchKind.Normal, hasPartColor: false).TexCoord1.y, 0f);
        }
    }
}
