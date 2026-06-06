using System;
using Unity.Collections;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    public partial class SpriteStudioInternalPlayer
    {
        /// <summary>
        /// Static per-part mask metadata, derived once at load from ssab PartData. Mirrors the
        /// static inputs of SSPlayerForGodot's _build_mask_writers (ss_internal_player.cpp:659):
        /// the per-frame inputs (draw-order rank for scope, threshold via PartState.Mask, hide)
        /// are resolved later in the draw loop, not here.
        /// </summary>
        internal struct MaskPartData
        {
            public bool IsMaskPart;        // part_type == PartTypeMask
            public bool DrawAsMask;        // unified pure-mask flag (shape/text/nines mask)
            public bool MaskWrite;         // clipping writer flag
            public bool MaskInfluence;     // writer OP (false=increment / true=invert); on a drawn target = mask opt-in
            public bool VisibleInsideMask; // target polarity (draw where masked == VisibleInsideMask)
            // Derived (static), mirroring _build_mask_writers:
            public bool PureMask;          // IsMaskPart || DrawAsMask: draws no colour, masks parts drawn BEFORE it
            public bool Writes;            // PureMask || MaskWrite: contributes stencil coverage
            public bool IsClipping;        // MaskWrite && !PureMask: draws normally, masks parts drawn AFTER it
            // Geometry kind the mesh job uses to build this part's stencil-write geometry. PartTypeMask
            // has no colour batch, so it is built as a Normal quad from local vertices (like Godot's
            // _render_mask_coverage, which routes Mask through _build_normal).
            public byte DrawKind;          // SsDrawBatchKind: Normal / Shape / Mesh
        }
        // Per-part static mask metadata (index-aligned with ssab parts). Null until SetupSlots runs.
        private MaskPartData[] _maskPartData;
        // True when this ssab has at least one mask writer (pure mask or clipping). When false the
        // whole mask path is skipped, like _build_mask_writers returning empty.
        private bool _hasMaskParts;

        /// <summary>
        /// Per-frame mask writer, rebuilt each frame from the draw order (rank = scope pivot).
        /// Mirrors SSPlayerForGodot's MaskWriter / _build_mask_writers (ss_internal_player.cpp:659).
        /// </summary>
        internal struct MaskWriter
        {
            public int PartIndex;
            public ushort DrawRank;  // index into draw_order: the writer's scope pivot
            public bool OpInvert;    // false = increment / true = invert (PartData.MaskInfluence)
            public bool IsClipping;  // true = masks parts drawn AFTER (rank > self) / false (pure) = BEFORE
            public float Threshold;  // (255 - PartState.Mask)/255; texel.a <= threshold is discarded
        }
        // Mask writers for the current frame (rebuilt by BuildMaskWriters). Managed: consumed on the
        // main thread when assigning stencil-write submeshes / materials.
        private MaskWriter[] _maskWriters;
        private int _maskWriterCount;
        // Per-part (texture hash, blend) for the current frame, harvested from the draw batches so the
        // mask path can resolve a writer's texture (Mask twins carry texture 0; the part's colour batch
        // carries the real one). Index-aligned with parts; only filled when _hasMaskParts.
        private uint[] _maskPartTexHash;
        private byte[] _maskPartBlend;
        /// <summary>
        /// Rebuilds <see cref="_maskWriters"/> for the current frame from the draw order, mirroring
        /// SSPlayerForGodot's _build_mask_writers: a writer is any part whose static MaskPartData
        /// marks it as writing (pure mask or clipping). Hidden masks are already excluded from the
        /// draw order by the runtime, so iterating draw_order is sufficient. The per-frame threshold
        /// comes from PartState.Mask: (255 - mask) / 255 (texel alpha &lt;= threshold is cut out).
        /// </summary>
        private void BuildMaskWriters(ss.runtime.FrameData frameData, int drawOrderLength, int partsLength)
        {
            _maskWriterCount = 0;
            if (!_hasMaskParts || _maskPartData == null) return;
            if (_maskWriters == null || _maskWriters.Length < drawOrderLength)
            {
                _maskWriters = new MaskWriter[drawOrderLength < 1 ? 1 : drawOrderLength];
            }
            for (int rank = 0; rank < drawOrderLength; rank++)
            {
                int p_idx = _drawOrder[rank];
                if (p_idx < 0 || p_idx >= partsLength || p_idx >= _maskPartData.Length) continue;
                var md = _maskPartData[p_idx];
                if (!md.Writes) continue;
                var ps = frameData.Parts(p_idx);
                if (!ps.HasValue) continue;
                _maskWriters[_maskWriterCount++] = new MaskWriter
                {
                    PartIndex = p_idx,
                    DrawRank = (ushort)rank,
                    OpInvert = md.MaskInfluence,
                    IsClipping = md.IsClipping,
                    Threshold = (255f - ps.Value.Mask) / 255f,
                };
            }
        }

        // Stencil op / compare values used by the mask path (UnityEngine.Rendering enums as bytes).
        private const byte StencilOpIncr = (byte)UnityEngine.Rendering.StencilOp.IncrementWrap;   // 7
        private const byte StencilOpDecr = (byte)UnityEngine.Rendering.StencilOp.DecrementWrap;   // 8
        private const byte StencilOpInvert = (byte)UnityEngine.Rendering.StencilOp.Invert;        // 5
        private const byte StencilCompEqual = (byte)UnityEngine.Rendering.CompareFunction.Equal;       // 3 (draw outside mask)
        private const byte StencilCompNotEqual = (byte)UnityEngine.Rendering.CompareFunction.NotEqual; // 6 (draw inside mask)

        /// <summary>
        /// Harvest each part's (texture hash, blend) for the current frame from the draw batches.
        /// A Mask twin carries texture 0; the part's colour batch (emitted after the twin) carries the
        /// real texture and overwrites it. PartTypeMask parts have only a Mask batch (real texture).
        /// </summary>
        private void BuildMaskPartLookup(ss.runtime.FrameData frameData, int drawBatchesLength, int drawOrderLength, int partsLength)
        {
            if (_maskPartTexHash == null || _maskPartTexHash.Length < partsLength)
            {
                _maskPartTexHash = new uint[partsLength < 1 ? 1 : partsLength];
                _maskPartBlend = new byte[partsLength < 1 ? 1 : partsLength];
            }
            for (int i = 0; i < partsLength; i++) { _maskPartTexHash[i] = 0; _maskPartBlend[i] = 0; }

            for (int bi = 0; bi < drawBatchesLength; bi++)
            {
                var b = frameData.DrawBatches(bi).Value;
                var k = b.Kind;
                if (k != ss.runtime.DrawBatchKind.Normal && k != ss.runtime.DrawBatchKind.Shape
                    && k != ss.runtime.DrawBatchKind.Mesh && k != ss.runtime.DrawBatchKind.Mask) continue;
                for (int j = 0; j < b.Count; j++)
                {
                    int rank = b.StartRank + j;
                    if (rank < 0 || rank >= drawOrderLength) continue;
                    int p_idx = _drawOrder[rank];
                    if (p_idx < 0 || p_idx >= partsLength) continue;
                    _maskPartTexHash[p_idx] = b.TextureHash;
                    _maskPartBlend[p_idx] = (byte)b.BlendType;
                }
            }
        }

        /// <summary>
        /// Vertex / index count a part contributes for the given mesh-job kind, matching the job's
        /// builders exactly (Normal: 4/5 with centre, Shape: fan, Mesh: CSR slice). False = degenerate.
        /// </summary>
        private unsafe bool MaskPartVcIc(int p_idx, byte kind, in DrawFrame frame, out int vc, out int ic)
        {
            vc = 0; ic = 0;
            if (kind == SsDrawBatchKind.Normal)
            {
                bool needsCenter = (_partStates[p_idx].UpdateFlag & SsUpdateFlag.NeedsCenterMask) != 0;
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

        /// <summary>
        /// Emit one mask render step: a single-part geometry batch (built by the mesh job) plus a
        /// submesh carrying its stencil role/params. Geometry is rebuilt per step, so a writer that
        /// both writes and later restores costs two builds (cheap; masks are few).
        /// </summary>
        private void EmitMaskStep(byte kind, int rank, int vc, int ic, uint texHash, byte blend,
            StencilRole role, byte stencilOp, byte stencilComp, float threshold,
            ref int totalVertices, ref int totalIndices)
        {
            _drawBatches[_drawBatchCount++] = new DrawBatchData
            {
                StartRank = (ushort)rank,
                Count = 1,
                VertexCount = vc,
                IndexCount = ic,
                Kind = kind
            };
            // Route through EmitSubmesh for the auto-grow path. Mask submeshes (role != None) are never
            // merged, so each step stays its own submesh and keeps its stencil ordering.
            EmitSubmesh(totalIndices, ic, texHash, blend, role, stencilOp, stencilComp, 0,
                SpriteStudioMaterial.MaskStencilMask,
                role == StencilRole.Write ? SpriteStudioMaterial.MaskStencilMask : (byte)0, threshold);
            totalVertices += vc;
            totalIndices += ic;
        }
    }
}
