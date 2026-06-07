using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// マスク (ステンシル) 描画のメタ情報を計算する責務を <see cref="SpriteStudioGeometryBuilder"/> から
    /// 分離したコラボレータ。現フレームの「マスク writer 一覧」と「パーツ毎の (texture hash, blend)」を
    /// 構築し、パーツ毎の頂点/インデックス数を mesh job の builder と一致する形で算出する。
    ///
    /// 実際の draw batch / submesh 発行 (EmitMaskStep / Phase A/B) は共有状態 (_drawBatches/_submeshes) を
    /// 触るためビルダー側に残す。本クラスは「計算」だけを担い、結果はアクセサ経由でビルダーが消費する。
    /// </summary>
    internal sealed class SpriteStudioMaskPlanner : System.IDisposable
    {
        internal struct MaskWriter
        {
            public int PartIndex;
            public ushort DrawRank;  // index into draw_order: the writer's scope pivot
            public bool OpInvert;    // false = increment / true = invert (PartData.MaskInfluence)
            public bool IsClipping;  // true = masks parts drawn AFTER (rank > self) / false (pure) = BEFORE
            public float Threshold;  // (255 - PartState.Mask)/255; texel.a <= threshold is discarded
        }

        // Mask writers for the current frame (rebuilt by BuildWriters).
        private NativeArray<MaskWriter> _maskWriters;
        private int _maskWriterCount;
        // Per-part (texture hash, blend) for the current frame, harvested from the draw batches.
        private NativeArray<uint> _maskPartTexHash;
        private NativeArray<byte> _maskPartBlend;

        public int WriterCount { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _maskWriterCount; }

        // ホットパス (mask Phase A) のアクセサは元実装の生ポインタ性能に合わせ、bounds-check を回避して inline する。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe MaskWriter GetWriter(int i)
            => ((MaskWriter*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskWriters))[i];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe uint GetPartTexHash(int partIndex)
            => ((uint*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartTexHash))[partIndex];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe byte GetPartBlend(int partIndex)
            => ((byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartBlend))[partIndex];

        /// <summary>現フレームの mask writer 一覧を構築する (mirrors _build_mask_writers)。</summary>
        public unsafe void BuildWriters(ss.runtime.FrameData frameData, NativeArray<ushort> drawOrder,
            bool hasMaskParts, SpriteStudioInternalPlayer.MaskPartData[] maskPartData, int drawOrderLength, int partsLength)
        {
            _maskWriterCount = 0;
            if (!hasMaskParts || maskPartData == null) return;
            SpriteStudioNativeBuffers.EnsureCapacity(ref _maskWriters, drawOrderLength < 1 ? 1 : drawOrderLength);
            for (int rank = 0; rank < drawOrderLength; rank++)
            {
                int p_idx = ((ushort*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(drawOrder))[rank];
                if (p_idx < 0 || p_idx >= partsLength || p_idx >= maskPartData.Length) continue;
                var md = maskPartData[p_idx];
                if (!md.Writes) continue;
                var ps = frameData.Parts(p_idx);
                if (!ps.HasValue) continue;
                var pWriter = (MaskWriter*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskWriters);
                pWriter[_maskWriterCount++] = new MaskWriter
                {
                    PartIndex = p_idx,
                    DrawRank = (ushort)rank,
                    OpInvert = md.MaskInfluence,
                    IsClipping = md.IsClipping,
                    Threshold = (255f - ps.Value.Mask) / 255f,
                };
            }
        }

        /// <summary>
        /// 各パーツの (texture hash, blend) を draw batch から収集する。Mask twin はテクスチャ 0 を持ち、
        /// 後続のカラーバッチが実テクスチャで上書きする。PartTypeMask は Mask バッチ (実テクスチャ) のみ。
        /// </summary>
        public unsafe void BuildPartLookup(ss.runtime.FrameData frameData, NativeArray<ushort> drawOrder,
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
                    int p_idx = ((ushort*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(drawOrder))[rank];
                    if (p_idx < 0 || p_idx >= partsLength) continue;
                    _maskPartTexHash[p_idx] = b.TextureHash;
                    _maskPartBlend[p_idx] = (byte)b.BlendType;
                }
            }
        }

        /// <summary>
        /// 指定 mesh-job kind でパーツが寄与する頂点/インデックス数 (job の builder と完全一致)。
        /// Normal: 4/5(中心あり), Shape: ファン, Mesh: CSR スライス。false = 退化 (描画なし)。
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
        }
    }
}
