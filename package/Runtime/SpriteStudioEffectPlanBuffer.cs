using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// A collaborator split out from <see cref="SpriteStudioGeometryBuilder"/> whose responsibility is to
    /// read the effect draw_plan (native FlatBuffer) in the same manner as frame-data and pack every effect
    /// part's geometry (verts/uvs/colors/indices) into concatenated buffers. The Burst Job reads the
    /// concatenated buffers.
    ///
    /// For zero-allocation the buffers are persistently allocated and reused grow-only. Logical counts are
    /// held in the *_Count fields.
    /// </summary>
    internal sealed class SpriteStudioEffectPlanBuffer : IDisposable
    {
        // draw_plan result indexed by part index (CommandStart/Count/QuadBase). MemClear'd default = not visible.
        private NativeArray<EffectDrawPlanData> _effectPlans;

        private Google.FlatBuffers.ByteBuffer _planByteBuffer;
#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        // Zero-copy: a read-only allocator that places the native draw_plan pointer directly into a ByteBuffer.
        private NativeByteBufferAllocator _planAllocator;
#else
        private byte[] _planBytes;
#endif
        private NativeArray<EffectDrawCommandData> _effectCommands;
        private NativeArray<float> _effectVerts;
        private NativeArray<float> _effectUvs;
        private NativeArray<float> _effectColors;
        private NativeArray<int> _effectIndices;
        private int _effectCommandCount;
        private int _effectVertFloatCount;
        private int _effectUvFloatCount;
        private int _effectColorFloatCount;
        private int _effectIndexCount;

        /// <summary>Concatenated geometry (read by the Burst Job).</summary>
        public NativeArray<float> Verts { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _effectVerts; }
        public NativeArray<float> Uvs { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _effectUvs; }
        public NativeArray<float> Colors { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _effectColors; }
        public NativeArray<int> Indices { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _effectIndices; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EffectDrawPlanData GetPlan(int partIndex) => _effectPlans[partIndex];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetPlan(int partIndex, EffectDrawPlanData plan) => _effectPlans[partIndex] = plan;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EffectDrawCommandData GetCommand(int index) => _effectCommands[index];

        /// <summary>
        /// At the start of a frame, allocate the plan table for partsLength entries, MemClear all plans to
        /// default (not visible), and reset the concatenated buffers' logical counts to 0.
        /// </summary>
        public unsafe void BeginFrame(int partsLength)
        {
            SpriteStudioNativeBuffers.EnsureCapacity(ref _effectPlans, partsLength);
            _effectCommandCount = 0;
            _effectVertFloatCount = 0;
            _effectUvFloatCount = 0;
            _effectColorFloatCount = 0;
            _effectIndexCount = 0;
            if (partsLength > 0)
            {
                UnsafeUtility.MemClear(
                    NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_effectPlans),
                    (long)partsLength * UnsafeUtility.SizeOf<EffectDrawPlanData>());
            }
        }

        /// <summary>
        /// Guarantee the concatenated arrays hold at least 1 element, since the Job references them even
        /// when there are no effects.
        /// </summary>
        public void EnsureGeometryMinimum()
        {
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectVerts, 1);
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectUvs, 1);
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectColors, 1);
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectIndices, 1);
        }

        /// <summary>
        /// Read one slot's draw_plan (native FlatBuffer fb/len), append its geometry to the concatenated
        /// buffers, and return this slot's plan metadata (CommandStart/Count/QuadBase).
        /// </summary>
        public unsafe EffectDrawPlanData Append(byte* fb, int len)
        {
            if (fb == null || len <= 0) return default;

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
            // Zero-copy: place the native pointer directly into the ByteBuffer (allocator/ByteBuffer are reused).
            // The draw_plan's lifetime is self-contained and safe because this method copies all of its
            // geometry into the concatenated NativeArrays within the call.
            if (_planAllocator == null)
            {
                _planAllocator = new NativeByteBufferAllocator();
                _planByteBuffer = new Google.FlatBuffers.ByteBuffer(_planAllocator, 0);
            }
            _planAllocator.Reset(fb, len);
            _planByteBuffer.Position = 0;
#else
            // Copy native -> a reused byte[] and wrap it in a ByteBuffer (fallback).
            if (_planBytes == null || _planBytes.Length < len)
            {
                _planBytes = new byte[UnityEngine.Mathf.Max(len, 4096)];
                _planByteBuffer = new Google.FlatBuffers.ByteBuffer(_planBytes);
            }
            System.Runtime.InteropServices.Marshal.Copy((System.IntPtr)fb, _planBytes, 0, len);
            _planByteBuffer.Position = 0;
#endif

            var plan = ss.runtime.EffectDrawPlan.GetRootAsEffectDrawPlan(_planByteBuffer);

            int vLen = plan.VertsLength;
            int uLen = plan.UvsLength;
            int cLen = plan.ColorsLength;
            int iLen = plan.IndicesLength;
            int cmdLen = plan.CommandsLength;

            // This slot's global quad base (= number of quads packed so far = indices/6).
            int quadBase = _effectIndexCount / 6;

            // The geometry is a contiguous scalar vector in fbs. Bulk-memcpy from the raw pointer fb into
            // the concatenated buffers. vtable offset = 4 + field_index*2; fbs declaration order:
            // commands(0)/vert_stride(1)/verts(2)/uvs(3)/colors(4)/indices(5).
            var t = new Google.FlatBuffers.Table(
                _planByteBuffer.GetInt(_planByteBuffer.Position) + _planByteBuffer.Position, _planByteBuffer);

            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectVerts, _effectVertFloatCount + vLen);
            SpriteStudioNativeBuffers.CopyFbVector(t, 8, fb, _effectVerts, _effectVertFloatCount, vLen);
            _effectVertFloatCount += vLen;

            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectUvs, _effectUvFloatCount + uLen);
            SpriteStudioNativeBuffers.CopyFbVector(t, 10, fb, _effectUvs, _effectUvFloatCount, uLen);
            _effectUvFloatCount += uLen;

            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectColors, _effectColorFloatCount + cLen);
            SpriteStudioNativeBuffers.CopyFbVector(t, 12, fb, _effectColors, _effectColorFloatCount, cLen);
            _effectColorFloatCount += cLen;

            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectIndices, _effectIndexCount + iLen);
            SpriteStudioNativeBuffers.CopyFbVector(t, 14, fb, _effectIndices, _effectIndexCount, iLen);
            _effectIndexCount += iLen;

            // Extract commands (the draw-order loop references texture/blend/range from them).
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectCommands, _effectCommandCount + cmdLen);
            int cmdStart = _effectCommandCount;
            for (int c = 0; c < cmdLen; c++)
            {
                var cc = plan.Commands(c);
                // Fill missing commands with default (QuadCount=0). Skipping via continue would leave the
                // previous frame's value and read a wrong quad range, so always fill.
                if (!cc.HasValue) { _effectCommands[cmdStart + c] = default; continue; }
                var cmd = cc.Value;
                _effectCommands[cmdStart + c] = new EffectDrawCommandData
                {
                    CellmapHash = cmd.CellmapHash,
                    QuadCount = cmd.QuadCount,
                    ParticleOffset = cmd.ParticleOffset,
                    Blend = cmd.Blend
                };
            }
            _effectCommandCount += cmdLen;

            return new EffectDrawPlanData
            {
                CommandStart = cmdStart,
                CommandCount = cmdLen,
                QuadBase = quadBase
            };
        }

        public void Dispose()
        {
            if (_effectPlans.IsCreated) _effectPlans.Dispose();
            if (_effectCommands.IsCreated) _effectCommands.Dispose();
            if (_effectVerts.IsCreated) _effectVerts.Dispose();
            if (_effectUvs.IsCreated) _effectUvs.Dispose();
            if (_effectColors.IsCreated) _effectColors.Dispose();
            if (_effectIndices.IsCreated) _effectIndices.Dispose();
        }
    }
}
