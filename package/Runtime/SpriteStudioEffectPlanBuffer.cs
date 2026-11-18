using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// A collaborator of <see cref="SpriteStudioGeometryBuilder"/> whose responsibility is to
    /// read the effect draw_plan (native FlatBuffer) straight from its bytes and pack every effect part's
    /// geometry (verts/uvs/colors/indices) into concatenated buffers. The Burst Job reads the
    /// concatenated buffers.
    ///
    /// The update manager's parallel pass copies the plans on the worker that steps them, into the buffers as
    /// they stand (<see cref="WorkerTarget"/>); the main thread appends, growing them, what does not fit.
    ///
    /// For zero-allocation the buffers are persistently allocated and reused grow-only. Logical counts are
    /// held in the *_Count fields.
    /// </summary>
    internal sealed class SpriteStudioEffectPlanBuffer : IDisposable
    {
        // draw_plan result indexed by part index (CommandStart/Count/QuadBase). MemClear'd default = not visible.
        private NativeArray<EffectDrawPlanData> _effectPlans;

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
        /// One slot's draw_plan, read straight from its bytes as the generated accessor reads it, so a worker
        /// can read it without a managed ByteBuffer. The table's fields in .fbs order: commands (a vector of
        /// <see cref="CommandSize"/>-byte EffectDrawCommand structs), vert_stride, verts, uvs, colors, indices.
        /// </summary>
        internal readonly unsafe struct PlanView
        {
            internal const int CommandsVtoff = 4;
            internal const int VertsVtoff = 8;
            internal const int UvsVtoff = 10;
            internal const int ColorsVtoff = 12;
            internal const int IndicesVtoff = 14;
            // EffectDrawCommand: cellmap_hash u32 @0, quad_count u32 @4, particle_offset u32 @8, blend u8 @12.
            internal const int CommandSize = 16;

            public readonly byte* Commands;
            public readonly int CommandCount;
            public readonly float* Verts;
            public readonly int VertCount;
            public readonly float* Uvs;
            public readonly int UvCount;
            public readonly float* Colors;
            public readonly int ColorCount;
            public readonly int* Indices;
            public readonly int IndexCount;

            public PlanView(byte* fb)
            {
                Commands = SpriteStudioNativeBuffers.RootVector(fb, CommandsVtoff, out CommandCount);
                Verts = (float*)SpriteStudioNativeBuffers.RootVector(fb, VertsVtoff, out VertCount);
                Uvs = (float*)SpriteStudioNativeBuffers.RootVector(fb, UvsVtoff, out UvCount);
                Colors = (float*)SpriteStudioNativeBuffers.RootVector(fb, ColorsVtoff, out ColorCount);
                Indices = (int*)SpriteStudioNativeBuffers.RootVector(fb, IndicesVtoff, out IndexCount);
            }
        }

        /// <summary>
        /// The merged buffers as raw pointers, how much each holds and how much of it is filled: what
        /// <see cref="CopyPlan"/> copies a plan into. It fills the buffers but cannot grow them.
        /// </summary>
        internal unsafe struct CopyTarget
        {
            public float* Verts;
            public float* Uvs;
            public float* Colors;
            public int* Indices;
            public EffectDrawCommandData* Commands;
            public int VertCapacity, UvCapacity, ColorCapacity, IndexCapacity, CommandCapacity;
            public int VertCount, UvCount, ColorCount, IndexCount, CommandCount;
            /// <summary>False once a plan did not fit: what was copied does not count, and the main thread
            /// appends every plan of the frame instead.</summary>
            public bool Fits;
        }

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

            var plan = new PlanView(fb);
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectVerts, _effectVertFloatCount + plan.VertCount);
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectUvs, _effectUvFloatCount + plan.UvCount);
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectColors, _effectColorFloatCount + plan.ColorCount);
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectIndices, _effectIndexCount + plan.IndexCount);
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectCommands, _effectCommandCount + plan.CommandCount);

            var target = Target();
            var data = CopyPlan(plan, ref target);
            TakeCopied(target);
            return data;
        }

        /// <summary>
        /// The buffers as they stand, empty, for a worker to copy this frame's plans into before BeginFrame;
        /// <see cref="TakeCopied"/> then counts in what it copied.
        /// </summary>
        public CopyTarget WorkerTarget()
        {
            var target = Target();
            target.VertCount = target.UvCount = target.ColorCount = target.IndexCount = target.CommandCount = 0;
            return target;
        }

        /// <summary>Make what was copied into <paramref name="target"/> this frame's geometry.</summary>
        public void TakeCopied(in CopyTarget target)
        {
            _effectVertFloatCount = target.VertCount;
            _effectUvFloatCount = target.UvCount;
            _effectColorFloatCount = target.ColorCount;
            _effectIndexCount = target.IndexCount;
            _effectCommandCount = target.CommandCount;
        }

        // The buffers as they stand, filled up to this frame's counts.
        private unsafe CopyTarget Target() => new CopyTarget
        {
            Verts = Pointer(_effectVerts),
            Uvs = Pointer(_effectUvs),
            Colors = Pointer(_effectColors),
            Indices = Pointer(_effectIndices),
            Commands = Pointer(_effectCommands),
            VertCapacity = Capacity(_effectVerts),
            UvCapacity = Capacity(_effectUvs),
            ColorCapacity = Capacity(_effectColors),
            IndexCapacity = Capacity(_effectIndices),
            CommandCapacity = Capacity(_effectCommands),
            VertCount = _effectVertFloatCount,
            UvCount = _effectUvFloatCount,
            ColorCount = _effectColorFloatCount,
            IndexCount = _effectIndexCount,
            CommandCount = _effectCommandCount,
            Fits = true,
        };

        private static unsafe T* Pointer<T>(NativeArray<T> array) where T : unmanaged
            => array.IsCreated ? (T*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(array) : null;

        private static int Capacity<T>(NativeArray<T> array) where T : struct => array.IsCreated ? array.Length : 0;

        /// <summary>
        /// Copy one slot's draw_plan after what <paramref name="target"/> holds and return the slot's plan
        /// metadata (CommandStart/Count/QuadBase); or, when it does not fit, clear <see cref="CopyTarget.Fits"/>
        /// and return default. Touches nothing but the target's memory, so a worker can call it.
        /// </summary>
        internal static unsafe EffectDrawPlanData CopyPlan(in PlanView plan, ref CopyTarget target)
        {
            if (target.VertCount + plan.VertCount > target.VertCapacity
                || target.UvCount + plan.UvCount > target.UvCapacity
                || target.ColorCount + plan.ColorCount > target.ColorCapacity
                || target.IndexCount + plan.IndexCount > target.IndexCapacity
                || target.CommandCount + plan.CommandCount > target.CommandCapacity)
            {
                target.Fits = false;
                return default;
            }

            // This slot's global quad base (= number of quads packed so far = indices/6).
            int quadBase = target.IndexCount / 6;

            if (plan.VertCount > 0) UnsafeUtility.MemCpy(target.Verts + target.VertCount, plan.Verts, (long)plan.VertCount * sizeof(float));
            target.VertCount += plan.VertCount;
            if (plan.UvCount > 0) UnsafeUtility.MemCpy(target.Uvs + target.UvCount, plan.Uvs, (long)plan.UvCount * sizeof(float));
            target.UvCount += plan.UvCount;
            if (plan.ColorCount > 0) UnsafeUtility.MemCpy(target.Colors + target.ColorCount, plan.Colors, (long)plan.ColorCount * sizeof(float));
            target.ColorCount += plan.ColorCount;
            if (plan.IndexCount > 0) UnsafeUtility.MemCpy(target.Indices + target.IndexCount, plan.Indices, (long)plan.IndexCount * sizeof(int));
            target.IndexCount += plan.IndexCount;

            // The commands the draw-order loop references texture/blend/range from.
            int commandStart = target.CommandCount;
            for (int c = 0; c < plan.CommandCount; c++)
            {
                byte* command = plan.Commands + c * PlanView.CommandSize;
                target.Commands[commandStart + c] = new EffectDrawCommandData
                {
                    CellmapHash = *(uint*)command,
                    QuadCount = *(uint*)(command + 4),
                    ParticleOffset = *(uint*)(command + 8),
                    Blend = command[12],
                };
            }
            target.CommandCount += plan.CommandCount;

            return new EffectDrawPlanData
            {
                CommandStart = commandStart,
                CommandCount = plan.CommandCount,
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
