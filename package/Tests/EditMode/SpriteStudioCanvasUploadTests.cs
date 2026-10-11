using NUnit.Framework;
using SpriteStudio.Runtime;
using Unity.Collections;
using UnityEngine;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// Guard for the Canvas upload (<see cref="SpriteStudioMeshUploader.ApplyCanvas"/>), which leaves the mesh's
    /// indices in place while the topology is unchanged. A frame that only moved the vertices must still get
    /// its new vertex streams, and a frame with a new topology must replace the indices, with fewer vertices than
    /// the frame before as with more.
    /// </summary>
    public class SpriteStudioCanvasUploadTests
    {
        private static readonly Color32 White = new Color32(255, 255, 255, 255);
        private static readonly int[] Quad = { 0, 1, 2, 1, 3, 2 };

        private NativeArray<SsVertex> _vertices;
        private NativeArray<uint> _indices;
        private NativeArray<SubmeshInfo> _submeshes;
        private NativeArray<Vector3> _bounds;
        private Mesh _mesh;
        private SpriteStudioMeshUploader _uploader;

        [SetUp]
        public void SetUp()
        {
            _vertices = new NativeArray<SsVertex>(4, Allocator.Persistent);
            _indices = new NativeArray<uint>(6, Allocator.Persistent);
            _submeshes = new NativeArray<SubmeshInfo>(1, Allocator.Persistent);
            _bounds = new NativeArray<Vector3>(2, Allocator.Persistent);
            _mesh = new Mesh();
            _uploader = new SpriteStudioMeshUploader();
        }

        [TearDown]
        public void TearDown()
        {
            _uploader.Dispose();
            Object.DestroyImmediate(_mesh);
            _vertices.Dispose();
            _indices.Dispose();
            _submeshes.Dispose();
            _bounds.Dispose();
        }

        // A unit quad shifted right by x, every corner the given colour, drawn with the given indices.
        private void SetQuad(float x, Color32 colour, int[] indices)
        {
            for (int i = 0; i < 4; i++)
            {
                _vertices[i] = new SsVertex
                {
                    Position = new Vector3(x + (i & 1), i >> 1, 0f),
                    Color = colour,
                    TexCoord0 = new Vector2(i & 1, i >> 1),
                    TexCoord1 = new Vector4(0.5f, 2f, 0f, i),
                };
            }
            for (int i = 0; i < 6; i++) _indices[i] = (uint)indices[i];
            _bounds[0] = new Vector3(x, 0f, 0f);
            _bounds[1] = new Vector3(x + 1f, 1f, 0f);
        }

        private void Upload(ulong topology, Color32 tint, bool frameSkipped = false, int vertexCount = 4, int indexCount = 6)
        {
            _uploader.ApplyCanvas(_mesh, tint, new SpriteStudioMeshUploader.UploadData(
                frameSkipped, vertexCount, indexCount, _vertices, _indices, _submeshes, 0, topology, _bounds));
        }

        [Test]
        public void Channels_CarryTheTintThePartAlphaAndThePartColour()
        {
            SetQuad(0f, new Color32(51, 102, 153, 128), Quad);
            Upload(1, new Color32(255, 128, 0, 200));

            var colours = _mesh.colors32;
            Assert.AreEqual(new Color32(255, 128, 0, 200 * 128 / 255), colours[3], "the tint times the part's alpha");
            var uv2 = new System.Collections.Generic.List<Vector3>();
            _mesh.GetUVs(2, uv2);
            Assert.That(uv2[3].x, Is.EqualTo(51f / 255f).Within(1e-6f), "the part colour in TEXCOORD2");
            Assert.That(uv2[3].y, Is.EqualTo(102f / 255f).Within(1e-6f));
            Assert.That(uv2[3].z, Is.EqualTo(153f / 255f).Within(1e-6f));
            var uv1 = new System.Collections.Generic.List<Vector4>();
            _mesh.GetUVs(1, uv1);
            Assert.AreEqual(new Vector4(0.5f, 2f, 0f, 3f), uv1[3]);
            Assert.AreEqual(new Vector3(1f, 1f, 0f), _mesh.vertices[3]);
            CollectionAssert.AreEqual(Quad, _mesh.GetIndices(0));
            Assert.AreEqual(new Bounds(new Vector3(0.5f, 0.5f, 0f), new Vector3(1f, 1f, 0f)), _mesh.bounds,
                "the bounds the mesh job computed");
        }

        [Test]
        public void SameTopology_TakesTheNewVertices_AndKeepsTheIndices()
        {
            SetQuad(0f, White, Quad);
            Upload(1, White);

            // The topology says nothing changed, so these indices are not the ones the frame draws with.
            SetQuad(10f, new Color32(255, 255, 255, 51), new[] { 3, 3, 3, 3, 3, 3 });
            Upload(1, White);

            Assert.AreEqual(new Vector3(11f, 1f, 0f), _mesh.vertices[3]);
            Assert.AreEqual(51, _mesh.colors32[3].a);
            CollectionAssert.AreEqual(Quad, _mesh.GetIndices(0));
            Assert.AreEqual(10.5f, _mesh.bounds.center.x, 1e-5f);
        }

        [Test]
        public void NewTopology_ReplacesTheIndices()
        {
            SetQuad(0f, White, Quad);
            Upload(1, White);

            var reordered = new[] { 1, 3, 2, 0, 1, 2 };
            SetQuad(0f, White, reordered);
            Upload(2, White);

            CollectionAssert.AreEqual(reordered, _mesh.GetIndices(0));
        }

        [Test]
        public void NewTopology_WithFewerVertices_ReplacesTheIndices()
        {
            SetQuad(0f, White, Quad);
            Upload(1, White);

            // The quad's first triangle alone: the quad's indices reference a vertex this frame no longer has.
            var triangle = new[] { 0, 1, 2 };
            for (int i = 0; i < triangle.Length; i++) _indices[i] = (uint)triangle[i];
            Upload(2, White, vertexCount: 3, indexCount: 3);

            Assert.AreEqual(3, _mesh.vertexCount);
            CollectionAssert.AreEqual(triangle, _mesh.GetIndices(0));

            SetQuad(0f, White, Quad);
            Upload(3, White);

            Assert.AreEqual(4, _mesh.vertexCount);
            CollectionAssert.AreEqual(Quad, _mesh.GetIndices(0));
        }

        [Test]
        public void SkippedFrame_WithANewTint_UploadsTheColours()
        {
            SetQuad(0f, White, Quad);
            Upload(1, White);

            Upload(1, new Color32(0, 0, 255, 255), frameSkipped: true);

            Assert.AreEqual(new Color32(0, 0, 255, 255), _mesh.colors32[0]);
            CollectionAssert.AreEqual(Quad, _mesh.GetIndices(0));
        }
    }
}
