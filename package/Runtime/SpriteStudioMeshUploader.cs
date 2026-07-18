using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Collaborator of <see cref="SpriteStudioGeometryBuilder"/> whose responsibility is uploading
    /// the built geometry (vertices / indices / SubMesh / bounds) to Unity's <see cref="Mesh"/>.
    ///
    /// Internally holds an "index cache": while the topology (index/submesh structure) matches the previous
    /// upload, only the vertices are updated and the index/submesh re-upload is skipped. The cache state is
    /// fully owned by this class.
    /// </summary>
    internal sealed class SpriteStudioMeshUploader : IDisposable
    {
        /// <summary>The full set of built geometry to upload (references to buffers owned by the builder).</summary>
        internal readonly struct UploadData
        {
            public readonly bool FrameSkipped;
            public readonly int VertexCount;
            public readonly int IndexCount;
            public readonly NativeArray<SsVertex> Vertices;
            public readonly NativeArray<uint> Indices;
            public readonly NativeArray<SubmeshInfo> Submeshes;
            public readonly int SubmeshCount;
            public readonly ulong TopologySignature;
            public readonly NativeArray<Vector3> Bounds;

            public UploadData(bool frameSkipped, int vertexCount, int indexCount,
                NativeArray<SsVertex> vertices, NativeArray<uint> indices,
                NativeArray<SubmeshInfo> submeshes, int submeshCount,
                ulong topologySignature, NativeArray<Vector3> bounds)
            {
                FrameSkipped = frameSkipped;
                VertexCount = vertexCount;
                IndexCount = indexCount;
                Vertices = vertices;
                Indices = indices;
                Submeshes = submeshes;
                SubmeshCount = submeshCount;
                TopologySignature = topologySignature;
                Bounds = bounds;
            }
        }

        // Vertex layout (Position / Color / TexCoord0 / TexCoord1). Reused for SetVertexBufferParams.
        private static readonly VertexAttributeDescriptor[] s_vertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
        };

        // Persistent buffer for SetSubMeshes (reused grow-only).
        private NativeArray<SubMeshDescriptor> _submeshDescriptors;

        // For the index-upload cache. If the topology (batch structure) is unchanged, skip re-uploading the index/submesh.
        private bool _meshUploaded;
        private int _lastMeshId;
        private ulong _uploadedTopologySignature;
        private int _uploadedVertexCount;
        private int _uploadedIndexCount;
        private bool _uploadedPerBatchSubmesh;

        // Canvas (uGUI) high-level upload scratch (see ApplyCanvas). De-interleaved channels + 16-bit
        // indices, reused grow-only.
        private NativeArray<Vector3> _canvasPositions;
        private NativeArray<Color32> _canvasColors;
        private NativeArray<Vector2> _canvasUv0;
        private NativeArray<Vector4> _canvasUv1;
        private NativeArray<ushort> _meshIndices16;
        private bool _warned16BitOverflow;

        /// <summary>
        /// Apply the built geometry to <paramref name="targetMesh"/>. Assumes the caller (the builder) has
        /// already waited for the Job to complete. On a frame-skip the previous Mesh is correct, so do nothing.
        /// </summary>
        public void Apply(Mesh targetMesh, bool perBatchSubmesh, bool use16BitIndices, in UploadData data)
        {
            // On a frame where ScheduleMeshUpdate skipped the rebuild, the Mesh is still correct from last time, so skip the re-upload too.
            if (data.FrameSkipped) return;

            if (data.VertexCount == 0 || data.IndexCount == 0 || !data.Vertices.IsCreated || !data.Indices.IsCreated)
            {
                targetMesh.Clear();
                _meshUploaded = false; // next time is a full upload (cache invalidated)
                return;
            }

            // Canvas path: high-level 16-bit upload (CanvasRenderer rejects the low-level mesh on D3D12).
            if (use16BitIndices)
            {
                ApplyCanvas(targetMesh, data);
                return;
            }

            // Flags to bypass Unity's strict validation (Overlap and out-of-range references).
            var updateFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

            // If the topology (index/submesh structure) exactly matches the previous upload, update only the
            // per-frame moving vertices and skip re-uploading the index/submesh. If the Mesh instance changed
            // (shared/preview, etc.), fall back to a full upload for safety.
            int meshId = targetMesh.GetInstanceID();
            bool topoUnchanged = _meshUploaded
                && meshId == _lastMeshId
                && data.TopologySignature == _uploadedTopologySignature
                && data.VertexCount == _uploadedVertexCount
                && data.IndexCount == _uploadedIndexCount
                && perBatchSubmesh == _uploadedPerBatchSubmesh;

            if (topoUnchanged)
            {
                targetMesh.SetVertexBufferData(data.Vertices, 0, 0, data.VertexCount, 0, updateFlags);
                ApplyJobComputedBounds(targetMesh, data.Bounds);
                return;
            }

            // --- Full upload (first time or topology change) ---
            // Empty the submesh once to avoid out-of-range references while re-declaring the buffers.
            targetMesh.SetSubMesh(0, new SubMeshDescriptor(0, 0), updateFlags);
            targetMesh.subMeshCount = 1;

            targetMesh.SetVertexBufferParams(data.VertexCount, s_vertexLayout);
            targetMesh.SetIndexBufferParams(data.IndexCount, IndexFormat.UInt32);

            targetMesh.SetVertexBufferData(data.Vertices, 0, 0, data.VertexCount, 0, updateFlags);
            targetMesh.SetIndexBufferData(data.Indices, 0, 0, data.IndexCount, updateFlags);

            if (perBatchSubmesh && data.SubmeshCount > 0)
            {
                // Set them all at once via SetSubMeshes (avoids the Overlap warning from setting sequentially).
                EnsureDescriptors(data.SubmeshCount);
                for (int s = 0; s < data.SubmeshCount; s++)
                {
                    var info = data.Submeshes[s];
                    _submeshDescriptors[s] = new SubMeshDescriptor(info.IndexStart, info.IndexCount, MeshTopology.Triangles);
                }
                targetMesh.SetSubMeshes(_submeshDescriptors, 0, data.SubmeshCount, updateFlags);
            }
            else
            {
                targetMesh.subMeshCount = 1;
                targetMesh.SetSubMesh(0, new SubMeshDescriptor(0, data.IndexCount, MeshTopology.Triangles), updateFlags);
            }
            ApplyJobComputedBounds(targetMesh, data.Bounds);

            // Record the uploaded topology (used from the next frame on to decide whether to skip the index re-upload).
            _meshUploaded = true;
            _lastMeshId = meshId;
            _uploadedTopologySignature = data.TopologySignature;
            _uploadedVertexCount = data.VertexCount;
            _uploadedIndexCount = data.IndexCount;
            _uploadedPerBatchSubmesh = perBatchSubmesh;
        }

        /// <summary>
        /// Apply the min/max computed by UpdateMeshJob to Mesh.bounds. If not computed, RecalculateBounds.
        /// </summary>
        private static void ApplyJobComputedBounds(Mesh targetMesh, NativeArray<Vector3> bounds)
        {
            if (!bounds.IsCreated || bounds.Length < 2)
            {
                targetMesh.RecalculateBounds();
                return;
            }
            Vector3 mn = bounds[0];
            Vector3 mx = bounds[1];
            targetMesh.bounds = new Bounds((mn + mx) * 0.5f, mx - mn);
        }

        /// <summary>
        /// Canvas (uGUI) upload via the high-level Mesh API. Caution: CanvasRenderer rejects a low-level
        /// SetVertexBufferData mesh on Windows/D3D12 (invalid GPU draw -> device removed). De-interleaves
        /// the packed SsVertex stream into per-channel scratch and narrows indices to 16-bit. Full rebuild
        /// each call (UI animates per frame); the low-level topology cache is bypassed.
        /// </summary>
        private unsafe void ApplyCanvas(Mesh targetMesh, in UploadData data)
        {
            // Index VALUES must fit in ushort, i.e. vertexCount <= 65535 (the index COUNT is unconstrained).
            if (data.VertexCount > 65535 && !_warned16BitOverflow)
            {
                Debug.LogWarning("[SpriteStudio] Canvas mesh exceeds 65535 vertices; CanvasRenderer requires 16-bit indices so geometry will wrap. Reduce part/particle count or use the world (MeshRenderer) player.");
                _warned16BitOverflow = true;
            }

            SpriteStudioNativeBuffers.EnsureCapacity(ref _canvasPositions, data.VertexCount);
            SpriteStudioNativeBuffers.EnsureCapacity(ref _canvasColors, data.VertexCount);
            SpriteStudioNativeBuffers.EnsureCapacity(ref _canvasUv0, data.VertexCount);
            SpriteStudioNativeBuffers.EnsureCapacity(ref _canvasUv1, data.VertexCount);
            SpriteStudioNativeBuffers.EnsureCapacity(ref _meshIndices16, data.IndexCount);

            var src = (SsVertex*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(data.Vertices);
            var pos = (Vector3*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_canvasPositions);
            var col = (Color32*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_canvasColors);
            var uv0 = (Vector2*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_canvasUv0);
            var uv1 = (Vector4*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_canvasUv1);
            for (int i = 0; i < data.VertexCount; i++)
            {
                var v = src[i];
                pos[i] = v.Position;
                col[i] = v.Color;
                uv0[i] = v.TexCoord0;
                uv1[i] = v.TexCoord1;
            }
            var si = (uint*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(data.Indices);
            var di = (ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_meshIndices16);
            for (int i = 0; i < data.IndexCount; i++) di[i] = (ushort)si[i];

            // High-level upload (per channel). SetVertices first so the colour/uv channels size to it.
            targetMesh.indexFormat = IndexFormat.UInt16;
            targetMesh.SetVertices(_canvasPositions, 0, data.VertexCount);
            targetMesh.SetColors(_canvasColors, 0, data.VertexCount);
            targetMesh.SetUVs(0, _canvasUv0, 0, data.VertexCount);
            targetMesh.SetUVs(1, _canvasUv1, 0, data.VertexCount);
            targetMesh.SetIndices(_meshIndices16, 0, data.IndexCount, MeshTopology.Triangles, 0, false);
            ApplyJobComputedBounds(targetMesh, data.Bounds);

            _meshUploaded = false; // Canvas path bypasses the low-level topology cache.
        }

        private void EnsureDescriptors(int needed)
        {
            if (_submeshDescriptors.IsCreated && _submeshDescriptors.Length >= needed) return;
            if (_submeshDescriptors.IsCreated) _submeshDescriptors.Dispose();
            _submeshDescriptors = new NativeArray<SubMeshDescriptor>(needed < 1 ? 1 : needed,
                Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        public void Dispose()
        {
            if (_submeshDescriptors.IsCreated) _submeshDescriptors.Dispose();
            if (_canvasPositions.IsCreated) _canvasPositions.Dispose();
            if (_canvasColors.IsCreated) _canvasColors.Dispose();
            if (_canvasUv0.IsCreated) _canvasUv0.Dispose();
            if (_canvasUv1.IsCreated) _canvasUv1.Dispose();
            if (_meshIndices16.IsCreated) _meshIndices16.Dispose();
            _meshUploaded = false;
        }
    }
}
