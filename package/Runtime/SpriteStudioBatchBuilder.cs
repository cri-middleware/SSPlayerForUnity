using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    // BlendType is the ssab BlendType (0..7) used to resolve the per-blend material variant.
    // Effect submeshes keep Mix here (their additive path is handled in-shader for now).
    // Stencil role of a submesh. None = a plain colour submesh (existing behaviour). Write =
    // the SpriteStudio/MaskWrite pass (ColorMask 0) that increments/decrements/inverts the
    // isolated stencil bits (covers both the pure-mask "write" and its "restore"; the op
    // distinguishes them). Test = a colour submesh that is kept/discarded by a stencil compare.
    public enum StencilRole : byte { None = 0, Write = 1, Test = 2 }

    public struct SubmeshInfo
    {
        public int IndexStart;
        public int IndexCount;
        public uint TextureHash;
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
            byte stencilReadMask = 0, byte stencilWriteMask = 0, float maskThreshold = 0f)
        {
            if (role == StencilRole.None && _submeshCount > 0)
            {
                var pSub = (SubmeshInfo*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_submeshes);
                ref var last = ref pSub[_submeshCount - 1];
                if (last.Role == StencilRole.None && last.TextureHash == textureHash && last.BlendType == blendType)
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

        public unsafe void EmitMaskStep(byte kind, int rank, int vc, int ic, uint texHash, byte blend,
            StencilRole role, byte stencilOp, byte stencilComp, float threshold,
            ref int totalVertices, ref int totalIndices)
        {
            ((DrawBatchData*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[_drawBatchCount++] = new DrawBatchData
            {
                StartRank = (ushort)rank,
                Count = 1,
                VertexCount = vc,
                IndexCount = ic,
                Kind = kind
            };
            
            EmitSubmesh(totalIndices, ic, texHash, blend, role, stencilOp, stencilComp, 0,
                SpriteStudioMaterial.MaskStencilMask,
                role == StencilRole.Write ? SpriteStudioMaterial.MaskStencilMask : (byte)0, threshold);
            totalVertices += vc;
            totalIndices += ic;
        }

        public unsafe void EmitInstanceBatch(int partIndex, int vertexCount, int indexCount)
        {
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

        public unsafe void EmitDrawBatch(ushort startRank, ushort count, int vc, int ic, byte kind, uint textureHash, byte blendType, ref int totalVertices, ref int totalIndices)
        {
            ((DrawBatchData*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[_drawBatchCount++] = new DrawBatchData
            {
                StartRank = startRank,
                Count = count,
                VertexCount = vc,
                IndexCount = ic,
                Kind = kind
            };
            EmitSubmesh(totalIndices, ic, textureHash, blendType);
            totalVertices += vc;
            totalIndices += ic;
        }

        public unsafe ulong ComputeTopologySignature(int vertexCount, int indexCount)
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
            }
            sig = (sig ^ (ulong)(uint)_submeshCount) * prime;
            for (int i = 0; i < _submeshCount; i++)
            {
                var s = _submeshes[i];
                sig = (sig ^ ((ulong)(uint)s.IndexStart | ((ulong)(uint)s.IndexCount << 32))) * prime;
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
