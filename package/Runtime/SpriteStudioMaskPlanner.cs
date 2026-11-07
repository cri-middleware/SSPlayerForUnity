using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Collaborator of <see cref="SpriteStudioGeometryBuilder"/> whose responsibility is computing
    /// the mask (stencil) draw metadata. It builds the current frame's "mask writer list" and "per-part
    /// (texture hash, blend)", and computes each part's vertex/index counts in a form that matches the mesh
    /// job's builder.
    ///
    /// The Phase A/B loops that emit the draw batches and submeshes stay on the builder side. This class
    /// handles only the "computation"; the builder consumes the results via accessors.
    /// </summary>
    internal sealed class SpriteStudioMaskPlanner : System.IDisposable
    {
        internal struct MaskWriter
        {
            public int PartIndex;
            public ushort DrawRank;  // index into draw_order: the writer's scope pivot
            public bool OpInvert;    // false = increment / true = invert (effective maskInfluence)
            public bool IsClipping;  // true = masks parts drawn AFTER (rank > self) / false (pure) = BEFORE
            public float Threshold;  // (255 - PartState.Mask)/255; texel.a <= threshold is discarded
        }

        // Mask writers for the current frame (rebuilt by BuildWriters).
        private NativeArray<MaskWriter> _maskWriters;
        private int _maskWriterCount;
        // Per-part (texture hash, blend) for the current frame, harvested from the draw batches.
        private NativeArray<uint> _maskPartTexHash;
        private NativeArray<byte> _maskPartBlend;
        // Per part index: 1 where a writing part actually writes this frame
        // (mask != 0 and not hidden). Filled by BuildWriters for every part that Writes, whether or not
        // it made the writer list, because the write and its removal are emitted from different loops:
        // Phase A and Phase C walk the writer list, while a pure mask's removal and a clipping mask's
        // own-rank write are driven by the draw batches off static MaskPartData. The pairs have to agree
        // on which writers are live, or a removal lands with no write behind it and unbalances the plane.
        private NativeArray<byte> _maskWriterActive;

        public int WriterCount { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _maskWriterCount; }

        // Inlined and unchecked: these run per part in the build loop.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe MaskWriter GetWriter(int i)
            => ((MaskWriter*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskWriters))[i];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe uint GetPartTexHash(int partIndex)
            => ((uint*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartTexHash))[partIndex];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe byte GetPartBlend(int partIndex)
            => ((byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartBlend))[partIndex];
        /// <summary>Whether the writing part at this index writes to the stencil this frame. Meaningful
        /// only for a part whose static MaskPartData says it Writes; false for everything else.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool IsWriterActive(int partIndex)
            => ((byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskWriterActive))[partIndex] != 0;

        /// <summary>Build the current frame's mask writer list, and record
        /// which writing parts write at all this frame for <see cref="IsWriterActive"/>.</summary>
        /// <param name="inheritedMaskInfluence">maskInfluence of the instance part directly above this player
        /// (one level, not chained). A writer's op/plane follows its EFFECTIVE maskInfluence, not just its
        /// own: an instance part with maskInfluence=0 puts its child's writers on the counter plane (union),
        /// which is what SpriteStudio 7.6 does when maskInfluence is mixed. Root players pass the AND identity (true),
        /// so this is a no-op there.</param>
        public unsafe void BuildWriters(RawPartState* parts, NativeArray<ushort> drawOrder,
            bool hasMaskParts, SpriteStudioInternalPlayer.MaskPartData[] maskPartData, int drawOrderLength, int partsLength,
            bool inheritedMaskInfluence)
        {
            _maskWriterCount = 0;
            // Cleared ahead of the early return: a writer that stopped writing this frame must not be read
            // as live off the last frame's flags.
            if (!_maskWriterActive.IsCreated || _maskWriterActive.Length < partsLength)
            {
                SpriteStudioNativeBuffers.EnsureCapacity(ref _maskWriterActive, partsLength < 1 ? 1 : partsLength);
            }
            UnsafeUtility.MemClear(
                NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskWriterActive),
                (long)partsLength * sizeof(byte));
            if (!hasMaskParts || maskPartData == null) return;
            SpriteStudioNativeBuffers.EnsureCapacity(ref _maskWriters, drawOrderLength < 1 ? 1 : drawOrderLength);
            for (int rank = 0; rank < drawOrderLength; rank++)
            {
                int p_idx = ((ushort*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(drawOrder))[rank];
                if (p_idx < 0 || p_idx >= partsLength || p_idx >= maskPartData.Length) continue;
                var md = maskPartData[p_idx];
                if (!md.Writes) continue;
                ref readonly RawPartState ps = ref parts[p_idx];
                // A writer writes only where its per-frame mask is non-zero and it is not hidden.
                // Both live in FrameData, so this is the half of the classification
                // MaskPartData cannot answer: an authored MASK of 0 is how a writer is switched off over a
                // range of frames, and Threshold cannot stand in for it - a shape mask samples no texture,
                // so every emit path pins its threshold to -1 and its coverage is its whole geometry.
                if (ps.Mask == 0f || ps.Hide != 0) continue;
                ((byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskWriterActive))[p_idx] = 1;
                var pWriter = (MaskWriter*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskWriters);
                pWriter[_maskWriterCount++] = new MaskWriter
                {
                    PartIndex = p_idx,
                    DrawRank = (ushort)rank,
                    // Effective maskInfluence = own AND the instance part's above: the op/plane follows it.
                    OpInvert = SpriteStudioInternalPlayer.EffectiveMaskInfluence(md, inheritedMaskInfluence),
                    IsClipping = md.IsClipping,
                    Threshold = (255f - ps.Mask) / 255f,
                };
            }
        }

        /// <summary>
        /// Collect each part's (texture hash, blend) from the draw batches. A Mask twin has texture 0, and a
        /// following color batch overwrites it with the real texture. PartTypeMask only has a Mask batch (real texture).
        /// </summary>
        public unsafe void BuildPartLookup(RawDrawBatch* drawBatches, NativeArray<ushort> drawOrder,
            int drawBatchesLength, int drawOrderLength, int partsLength)
        {
            if (!_maskPartTexHash.IsCreated || _maskPartTexHash.Length < partsLength)
            {
                SpriteStudioNativeBuffers.EnsureCapacity(ref _maskPartTexHash, partsLength < 1 ? 1 : partsLength);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _maskPartBlend, partsLength < 1 ? 1 : partsLength);
            }
            UnsafeUtility.MemClear(
                NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartTexHash),
                (long)partsLength * sizeof(uint));
            UnsafeUtility.MemClear(
                NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartBlend),
                (long)partsLength * sizeof(byte));

            ushort* order = (ushort*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(drawOrder);
            uint* texHash = (uint*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartTexHash);
            byte* blend = (byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartBlend);
            for (int bi = 0; bi < drawBatchesLength; bi++)
            {
                ref readonly RawDrawBatch b = ref drawBatches[bi];
                byte k = b.Kind;
                if (k != SsDrawBatchKind.Normal && k != SsDrawBatchKind.Shape
                    && k != SsDrawBatchKind.Mesh && k != SsDrawBatchKind.Mask) continue;
                int startRank = b.StartRank, count = b.Count;
                uint batchTexHash = b.TextureHash;
                byte batchBlend = b.BlendType;
                for (int j = 0; j < count; j++)
                {
                    int rank = startRank + j;
                    if (rank < 0 || rank >= drawOrderLength) continue;
                    int p_idx = order[rank];
                    if (p_idx < 0 || p_idx >= partsLength) continue;
                    texHash[p_idx] = batchTexHash;
                    blend[p_idx] = batchBlend;
                }
            }
        }

        /// <summary>
        /// The vertex/index count a part contributes for the given mesh-job kind (exactly matches the job's builder).
        /// Normal: 4/5 (with center), Shape: fan, Mesh: CSR slice. false = degenerate (no draw).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool MaskPartVcIc(int p_idx, byte kind, in SpriteStudioGeometryBuilder.DrawFrame frame,
            NativeArray<PartStateData> partStates, out int vc, out int ic)
        {
            vc = 0; ic = 0;
            if (kind == SsDrawBatchKind.Normal)
            {
                bool needsCenter = (partStates[p_idx].UpdateFlag & SsUpdateFlag.NeedsCenterMask) != 0;
                vc = needsCenter ? 5 : 4;
                ic = needsCenter ? 12 : 6;
                return true;
            }
            if (kind == SsDrawBatchKind.Shape)
            {
                int svc = frame.ShapeVertexCounts[p_idx];
                if (svc < 3) return false;
                vc = svc; ic = (svc - 2) * 3;
                return true;
            }
            if (kind == SsDrawBatchKind.Mesh)
            {
                uint v0 = frame.MeshVertexOffsets[p_idx];
                uint v1 = frame.MeshVertexOffsets[p_idx + 1];
                uint i0 = frame.MeshIndexOffsets[p_idx];
                uint i1 = frame.MeshIndexOffsets[p_idx + 1];
                if (!(v1 > v0 && i1 > i0 && (i1 - i0) % 3 == 0)) return false;
                vc = (int)(v1 - v0); ic = (int)(i1 - i0);
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_maskWriters.IsCreated) _maskWriters.Dispose();
            if (_maskPartTexHash.IsCreated) _maskPartTexHash.Dispose();
            if (_maskPartBlend.IsCreated) _maskPartBlend.Dispose();
            if (_maskWriterActive.IsCreated) _maskWriterActive.Dispose();
        }
    }
}
