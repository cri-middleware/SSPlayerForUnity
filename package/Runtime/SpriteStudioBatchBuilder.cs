using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    // Stencil role of a submesh. None = a plain colour submesh. Write =
    // the SpriteStudio/MaskWrite pass (ColorMask 0) that increments/decrements/inverts the
    // isolated stencil bits (covers both the pure-mask "write" and its "restore"; the op
    // distinguishes them). Test = a colour submesh that is kept/discarded by a stencil compare.
    internal enum StencilRole : byte { None = 0, Write = 1, Test = 2 }

    internal struct SubmeshInfo
    {
        public int IndexStart;
        public int IndexCount;
        public uint TextureHash;

        // Per-part AddOn shader id hash (FrameData PartAttributeShader.IdHash). 0 = the shared default
        // path (SpriteStudio/Default). Non-zero = an individual per-part draw whose material uses the
        // AddOn shader resolved from the SpriteStudioShaderCatalog.
        public uint ShaderHash;
        // The ssab BlendType (0..7), which resolves the per-blend material variant. Effect submeshes keep
        // Mix here; their additive path is handled in-shader.
        public byte BlendType;

        // Stencil state (only meaningful when Role != None). Op/Comp hold the corresponding
        // UnityEngine.Rendering.StencilOp / CompareFunction values; the masks/ref are the
        // bit-isolated stencil values; MaskThreshold = (255 - mask)/255 for Write (<0 = no cutout).
        public StencilRole Role;
        public byte StencilOp;        // Write: StencilOp (IncrWrap/DecrWrap/Invert/...)
        public byte StencilComp;      // Test: CompareFunction (Equal/NotEqual/...)
        public byte StencilRef;
        public byte StencilReadMask;
        public byte StencilWriteMask;
        public float MaskThreshold;

        // A Phase C clipping-mask restore (ColorMask 0 stencil revert). An enclosing instance parent
        // relocates such a submesh to after its own parts - SpriteStudio 7.6 carries an instance-internal mask past
        // the instance boundary, so the restore waits until the parent animation's own restore at the very
        // end - instead of drawing it in place at the child's boundary. Data-only: the render path treats
        // it exactly like Role == Write.
        public bool DeferrableClose;

        public float P0, P1, P2, P3, P4, P5, P6, P7;  // Shader Params (for AddOn)
        public float CellL, CellT, CellR, CellB;      // cell rectangles / 0 for parts with no cell (Shape / Mesh)
    }

    internal sealed class SpriteStudioBatchBuilder : IDisposable
    {
        private NativeArray<DrawBatchData> _drawBatches;
        private int _drawBatchCount;
        
        private NativeArray<SubmeshInfo> _submeshes;
        private int _submeshCount;

        public int DrawBatchCount => _drawBatchCount;
        public int SubmeshCount => _submeshCount;
        public NativeArray<DrawBatchData> DrawBatches => _drawBatches;
        public NativeArray<SubmeshInfo> Submeshes => _submeshes;

        public void BeginFrame(int maxBatches)
        {
            SpriteStudioNativeBuffers.EnsureCapacity(ref _drawBatches, maxBatches);
            SpriteStudioNativeBuffers.EnsureCapacity(ref _submeshes, maxBatches < 1 ? 1 : maxBatches);
            _drawBatchCount = 0;
            _submeshCount = 0;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public unsafe void EmitSubmesh(int indexStart, int indexCount, uint textureHash, byte blendType,
            StencilRole role = StencilRole.None, byte stencilOp = 0, byte stencilComp = 0, byte stencilRef = 0,
            byte stencilReadMask = 0, byte stencilWriteMask = 0, float maskThreshold = 0f, uint shaderHash = 0)
        {
            if (role != StencilRole.Write && _submeshCount > 0)
            {
                var pSub = (SubmeshInfo*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_submeshes);
                ref var last = ref pSub[_submeshCount - 1];
                // Only merge shared (non-AddOn) runs.
                // A per-part AddOn part (shaderHash != 0) must stay its own submesh
                //   so it carries its own material instance and params — even two adjacent
                //   parts using the same AddOn shader can have different params, so they must not merge.
                // The two ranges must also be contiguous: growing the last one covers every index in between.
                //   An instance parent carries its child's clipping restores to its own end, so the child's
                //   last colour run and whatever the parent emits next are separated by those restores'
                //   indices. Merging across that gap would draw the restores as colour.
                // A Test submesh only reads the stencil, so the next one under the same stencil state
                //   joins it. A writer between them is a Write submesh of its own, which ends the run.
                if (last.Role == role && shaderHash == 0 && last.ShaderHash == 0
                    && last.TextureHash == textureHash && last.BlendType == blendType
                    && last.IndexStart + last.IndexCount == indexStart
                    && (role == StencilRole.None
                        || (last.StencilOp == stencilOp && last.StencilComp == stencilComp
                            && last.StencilRef == stencilRef && last.StencilReadMask == stencilReadMask
                            && last.StencilWriteMask == stencilWriteMask && last.MaskThreshold == maskThreshold)))
                {
                    last.IndexCount += indexCount;
                    return;
                }
            }
            if (!_submeshes.IsCreated || _submeshCount >= _submeshes.Length)
            {
                int newSize = !_submeshes.IsCreated ? 8 : _submeshes.Length * 2;
                if (newSize < _submeshCount + 1) newSize = _submeshCount + 1;
                SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _submeshes, newSize);
            }
            var ptr = (SubmeshInfo*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_submeshes);
            ptr[_submeshCount++] = new SubmeshInfo
            {
                IndexStart = indexStart,
                IndexCount = indexCount,
                TextureHash = textureHash,
                ShaderHash = shaderHash,
                BlendType = blendType,
                Role = role,
                StencilOp = stencilOp,
                StencilComp = stencilComp,
                StencilRef = stencilRef,
                StencilReadMask = stencilReadMask,
                StencilWriteMask = stencilWriteMask,
                MaskThreshold = maskThreshold
            };
        }

        // Stamp the per-part AddOn params onto the most recently emitted submesh (called right after the
        // per-part EmitDrawBatch, which always creates a fresh submesh since AddOn parts never merge).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public unsafe void SetLastSubmeshAddOnParams(float p0, float p1, float p2, float p3, float p4, float p5, float p6, float p7)
        {
            if (_submeshCount == 0) return;
            ref var s = ref ((SubmeshInfo*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_submeshes))[_submeshCount - 1];
            s.P0 = p0; s.P1 = p1; s.P2 = p2; s.P3 = p3; s.P4 = p4; s.P5 = p5; s.P6 = p6; s.P7 = p7;
        }

        // Tag the most recently emitted submesh as a deferrable clipping-mask restore. Called right after
        // a Phase C close (EmitMaskStep) or a flushed carried-up close (EmitSubmesh); both are Role == Write,
        // which never merges, so the last submesh is always the one just created.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public unsafe void MarkLastSubmeshDeferrableClose()
        {
            if (_submeshCount == 0) return;
            ((SubmeshInfo*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_submeshes))[_submeshCount - 1].DeferrableClose = true;
        }

        // Stamp the per-part cell rectangle (pixel UVs) onto the most recently emitted submesh.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public unsafe void SetLastSubmeshCellRect(float l, float t, float r, float b)
        {
            if (_submeshCount == 0) return;
            ref var s = ref ((SubmeshInfo*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_submeshes))[_submeshCount - 1];
            s.CellL = l; s.CellT = t; s.CellR = r; s.CellB = b;
        }

        // The draw-batch buffer is pre-sized by BeginFrame(maxBatches), but maxBatches is only a
        // hand-computed estimate (drawBatches + effect command counts + per-instance submeshes +
        // mask write/restore steps). Self-grow here so an under-estimate can never overrun the
        // unchecked GetUnsafeBufferPointerWithoutChecks writes below. Mirrors EmitSubmesh; growth
        // preserves the batches already emitted this frame.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void EnsureDrawBatchCapacity()
        {
            if (_drawBatches.IsCreated && _drawBatchCount < _drawBatches.Length) return;
            int newSize = !_drawBatches.IsCreated ? 8 : _drawBatches.Length * 2;
            if (newSize < _drawBatchCount + 1) newSize = _drawBatchCount + 1;
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _drawBatches, newSize);
        }

        public unsafe void EmitMaskStep(byte kind, int rank, int vc, int ic, uint texHash, byte blend,
            StencilRole role, byte stencilOp, byte stencilComp, float threshold,
            ref int totalVertices, ref int totalIndices, uint shaderHash = 0)
        {
            EnsureDrawBatchCapacity();
            ((DrawBatchData*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[_drawBatchCount++] = new DrawBatchData
            {
                StartRank = (ushort)rank,
                Count = 1,
                VertexCount = vc,
                IndexCount = ic,
                Kind = kind,
                BlendType = blend
            };

            // Writers only ever write their own op's bit plane (parity for Invert, counter for
            // Incr/Decr); a test writes nothing. Kept in sync with the material through the shared
            // helper - the two must agree or a writer would be undone on the wrong plane.
            EmitSubmesh(totalIndices, ic, texHash, blend, role, stencilOp, stencilComp, 0,
                SpriteStudioMaterial.MaskStencilMask,
                role == StencilRole.Write ? SpriteStudioMaterial.MaskWriteMaskForOp(stencilOp) : (byte)0, threshold, shaderHash);
            totalVertices += vc;
            totalIndices += ic;
        }

        public unsafe void EmitInstanceBatch(int partIndex, int vertexCount, int indexCount)
        {
            EnsureDrawBatchCapacity();
            ((DrawBatchData*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[_drawBatchCount++] = new DrawBatchData
            {
                Kind = SsDrawBatchKind.Instance,
                EffectPartIndex = partIndex,
                VertexCount = vertexCount,
                IndexCount = indexCount
            };
        }

        public unsafe void EmitEffectBatch(int cmdVc, int cmdIc, byte blend, uint quadBase, uint particleOffset, uint quadCount, int partIndex)
        {
            EnsureDrawBatchCapacity();
            ((DrawBatchData*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[_drawBatchCount++] = new DrawBatchData
            {
                StartRank = 0,
                Count = 0,
                VertexCount = cmdVc,
                IndexCount = cmdIc,
                Kind = SsDrawBatchKind.Effect,
                EffectBlend = blend,
                EffectParticleOffset = quadBase + particleOffset,
                EffectQuadCount = quadCount,
                EffectPartIndex = partIndex
            };
        }

        public unsafe void EmitDrawBatch(ushort startRank, ushort count, int vc, int ic, byte kind, uint textureHash, byte blendType, uint shaderHash, ref int totalVertices, ref int totalIndices)
        {
            EnsureDrawBatchCapacity();
            ((DrawBatchData*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[_drawBatchCount++] = new DrawBatchData
            {
                StartRank = startRank,
                Count = count,
                VertexCount = vc,
                IndexCount = ic,
                Kind = kind,
                BlendType = blendType
            };
            EmitSubmesh(totalIndices, ic, textureHash, blendType, shaderHash: shaderHash);
            totalVertices += vc;
            totalIndices += ic;
        }

        /// <summary>
        /// The signature that decides whether the indices already on the GPU are still right. Besides the
        /// batch and submesh layout it folds in what decides each part's indices: the batch totals do not say
        /// how the parts share them, so two Mesh parts swapped by a PRIO key, a Mesh hidden for another of the
        /// same size, or a centre vertex passing from one Normal part to another would leave them all alone.
        /// A Mesh part's indices are static per part, a Shape's fan follows its vertex count.
        /// </summary>
        public unsafe ulong ComputeTopologySignature(int vertexCount, int indexCount,
            ushort* drawOrder, PartStateData* partStates, int* shapeVertexCounts, int partCount)
        {
            ulong sig = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            sig = (sig ^ (ulong)(uint)_drawBatchCount) * prime;
            for (int i = 0; i < _drawBatchCount; i++)
            {
                var b = ((DrawBatchData*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[i];
                sig = (sig ^ ((ulong)b.Kind | ((ulong)b.Count << 8) | ((ulong)b.StartRank << 24))) * prime;
                sig = (sig ^ (ulong)(uint)b.VertexCount) * prime;
                sig = (sig ^ (ulong)(uint)b.IndexCount) * prime;
                sig = (sig ^ (ulong)(uint)b.EffectPartIndex) * prime;
                sig = (sig ^ ((ulong)b.EffectQuadCount | ((ulong)b.EffectParticleOffset << 32))) * prime;

                if (b.Kind != SsDrawBatchKind.Normal && b.Kind != SsDrawBatchKind.Shape && b.Kind != SsDrawBatchKind.Mesh) continue;
                for (int k = 0; k < b.Count; k++)
                {
                    int p = drawOrder[b.StartRank + k];
                    if (p >= partCount) continue;
                    ulong key = b.Kind == SsDrawBatchKind.Mesh ? (ulong)p
                        : b.Kind == SsDrawBatchKind.Shape ? (ulong)(uint)shapeVertexCounts[p]
                        : (partStates[p].UpdateFlag & SsUpdateFlag.NeedsCenterMask) != 0 ? 1UL : 0UL;
                    sig = (sig ^ key) * prime;
                }
            }
            sig = (sig ^ (ulong)(uint)_submeshCount) * prime;
            for (int i = 0; i < _submeshCount; i++)
            {
                var s = _submeshes[i];
                sig = (sig ^ ((ulong)(uint)s.IndexStart | ((ulong)(uint)s.IndexCount << 32))) * prime;

                // Include the AddOn shader axis so the material list re-resolves when a part's shader
                // assignment changes (the per-frame params are set separately, like the mask threshold).
                sig = (sig ^ (ulong)s.ShaderHash) * prime;
            }
            sig = (sig ^ (ulong)(uint)indexCount) * prime;
            sig = (sig ^ (ulong)(uint)vertexCount) * prime;
            return sig;
        }

        public void Dispose()
        {
            if (_drawBatches.IsCreated) _drawBatches.Dispose();
            if (_submeshes.IsCreated) _submeshes.Dispose();
        }
    }
}
