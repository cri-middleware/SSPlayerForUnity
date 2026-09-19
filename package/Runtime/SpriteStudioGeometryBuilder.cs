using System;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using SpriteStudio.SsRuntime;
using System.Runtime.InteropServices;

namespace SpriteStudio.Runtime
{
    public class SpriteStudioGeometryBuilder : IDisposable
    {
        private readonly SpriteStudioInternalPlayer _player;

        // --- Effect: EffectPlanBuffer owns the draw_plan reads and the merged geometry ---
        private readonly SpriteStudioEffectPlanBuffer _effectBuffer = new SpriteStudioEffectPlanBuffer();

        // --- Mask: MaskPlanner owns the writer list / per-part (texHash,blend) / vc-ic computation ---
        // (the actual draw batch / submesh emission = EmitMaskStep / Phase A/B touches shared state, so it stays in the builder)
        private readonly SpriteStudioMaskPlanner _maskPlanner = new SpriteStudioMaskPlanner();
        // --- Instance ---
internal NativeArray<InstanceMeshPointer> _instanceMeshes;
        // Instance-internal clipping masks carry past the instance boundary (SS7.5): the child's Phase C
        // restores are collected here and flushed at THIS player's Phase C, after its own parts, instead of
        // being drawn at the child's boundary. Grow-only; reset each ConsumeFrameData.
        private NativeArray<SubmeshInfo> _deferredCloses;
        private int _deferredCloseCount;
        // True once a visible instance child this frame left an open (carried-over) clip. Parts drawn after
        // that instance must then be emitted as stencil tests even when this asset has no mask writers of
        // its own, because the instance's coverage reaches them.
        private bool _instanceCoverageOpen;
        // Dependency array bundling the child Instances' UpdateMeshJob handles. Persistent, grow-only (avoids a per-frame Temp allocation).
        internal NativeArray<JobHandle> _childHandles;
        public float _lastBuiltDrawFrameNo = float.NaN;
        public float _pendingDrawFrame;
        public bool _frameSkipped;
        public JobHandle _updateJobHandle;

        public JobHandle CurrentUpdateJobHandle => _updateJobHandle;

        // FrameData vtable offsets (parts / parts_color fields). Match the generated code's Parts(j)=__offset(4) /
        // PartsColor(j)=__offset(8). Single source of truth for the fill and the layout check (SpriteStudioRawLayoutCheck).
        internal const int PartsVtoff = 4;
        internal const int PartsColorVtoff = 8;

        public SpriteStudioGeometryBuilder(SpriteStudioInternalPlayer player)
        {
            _player = player;
        }

        public void Dispose()
        {
            _updateJobHandle.Complete();
            _effectBuffer.Dispose();
            if (_instanceMeshes.IsCreated) _instanceMeshes.Dispose();
            if (_deferredCloses.IsCreated) _deferredCloses.Dispose();
            if (_childHandles.IsCreated) _childHandles.Dispose();
            if (_meshVertices.IsCreated) _meshVertices.Dispose();
            if (_meshIndices.IsCreated) _meshIndices.Dispose();
            _batchBuilder.Dispose();
            if (_drawOrder.IsCreated) _drawOrder.Dispose();
            if (_partStates.IsCreated) _partStates.Dispose();
            if (_partColors.IsCreated) _partColors.Dispose();
            if (_meshBounds.IsCreated) _meshBounds.Dispose();
            
            _meshUploader.Dispose();
            _maskPlanner.Dispose();
        }

        public unsafe struct DrawFrame
        {
            public float FrameNo;
            public float DeltaSeconds;

            public float* WorldMatrices;
            public nuint WorldMatricesLen;

            public float* LocalUvs;
            public nuint LocalUvsLen;

            public float* LocalVertices;
            public nuint LocalVerticesLen;

            public float* ShapeVertices;
            public nuint ShapeVerticesLen;

            public float* ShapeBoxCoords;
            public nuint ShapeBoxCoordsLen;

            public int* ShapeVertexCounts;
            public nuint ShapeVertexCountsLen;

            public float* MeshVerticesX;
            public nuint MeshVerticesXLen;

            public float* MeshVerticesY;
            public nuint MeshVerticesYLen;

            public uint* MeshVertexOffsets;
            public nuint MeshVertexOffsetsLen;

            public float* MeshUvs;
            public nuint MeshUvsLen;

            public int* MeshIndices;
            public nuint MeshIndicesLen;

            public uint* MeshIndexOffsets;
            public nuint MeshIndexOffsetsLen;
        }

        private Google.FlatBuffers.ByteBuffer _frameDataByteBuffer;
        // Last consumed frame's FrameData view, retained so part tracking can read per-part PartState
        // (hide / localScale) at ApplyAndRender time. Valid until the next ConsumeFrameData.
        private ss.runtime.FrameData _lastFrameData;
        private bool _hasFrameData;
#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        // Zero-copy: place the per-context frame_data native buffer directly into the ByteBuffer.
        private NativeByteBufferAllocator _frameDataAllocator;
#else
        private byte[] _frameDataArray;
#endif

        // For zero-allocation, the Job input/output buffers are persistently allocated and reused grow-only.
        // NativeArray.Length is the allocated capacity; the logical count is tracked separately in *_Count.
        private NativeArray<SsVertex> _meshVertices;
        // Indices are 32-bit, so the vertex count exceeding 65535 (from an effect's many particles or the
        // mesh/instance total) does not break (ApplyToMesh uses IndexFormat.UInt32).
        private NativeArray<uint> _meshIndices;
        private SpriteStudioBatchBuilder _batchBuilder = new SpriteStudioBatchBuilder();

        private NativeArray<ushort> _drawOrder;
        private NativeArray<PartStateData> _partStates;
        private NativeArray<PartColorData> _partColors;
        private int _partCount;
        private int _partColorCount;
        private int _vertexCount;
        private int _indexCount;
        private ulong _topologySignature;
        private readonly SpriteStudioMeshUploader _meshUploader = new SpriteStudioMeshUploader();
        // Mesh bounds (element 0=min, 1=max). UpdateMeshJob writes them and MeshUploader applies them to Mesh.bounds.
        private NativeArray<Vector3> _meshBounds;

        public int VertexCount => _vertexCount;
        public int IndexCount => _indexCount;
        public ulong TopologySignature => _topologySignature;
        public int DrawBatchCount => _batchBuilder.DrawBatchCount;
        /// <summary>Number of draw batches (= SubMeshes) produced by the most recent ScheduleMeshUpdate.</summary>
        public int SubmeshCount => _batchBuilder.SubmeshCount;
        public NativeArray<SubmeshInfo> Submeshes => _batchBuilder.Submeshes;
        public NativeArray<DrawBatchData> DrawBatches => _batchBuilder.DrawBatches;
        public NativeArray<SsVertex> MeshVertices => _meshVertices;
        public NativeArray<uint> MeshIndices => _meshIndices;

        /// <summary>
        /// Per-part state for the last consumed frame (used by part tracking). Returns false if no frame
        /// has been consumed yet or the index is out of range; outputs default to visible / unit scale.
        /// </summary>
        public bool TryGetPartState(int partIndex, out bool hidden, out float localScaleX, out float localScaleY)
        {
            hidden = false;
            localScaleX = 1f;
            localScaleY = 1f;
            if (!_hasFrameData || partIndex < 0 || partIndex >= _lastFrameData.PartsLength) return false;
            var p = _lastFrameData.Parts(partIndex);
            if (!p.HasValue) return false;
            var ps = p.Value;
            hidden = ps.Hide;
            localScaleX = ps.LocalScaleX;
            localScaleY = ps.LocalScaleY;
            return true;
        }

        /// <summary>
        /// Resolved colour of a part on the last consumed frame — the blend type and the four corners as
        /// the mesh job sees them, so any per-part colour override is already folded in. False when no
        /// frame has been consumed, the index is out of range, or the part carries no colour this frame.
        /// The render path reads the colours in bulk; this is a per-part inspection hook.
        /// </summary>
        internal bool TryGetPartColor(int partIndex, out ss.runtime.PartAttributePartColor partColor)
        {
            partColor = default;
            if (!_hasFrameData || partIndex < 0 || partIndex >= _lastFrameData.PartsLength) return false;
            var p = _lastFrameData.Parts(partIndex);
            if (!p.HasValue) return false;
            short colorIndex = p.Value.PartColor;
            if (colorIndex < 0 || colorIndex >= _lastFrameData.PartsColorLength) return false;
            var c = _lastFrameData.PartsColor(colorIndex);
            if (!c.HasValue) return false;
            partColor = c.Value;
            return true;
        }

        /// <summary>
        /// Wait for the previous frame's Job to complete, advance by one frame when advance is set, and
        /// finalize the draw frame number and frame-skip. When the out handle is Zero, this player is
        /// excluded from the parallel get_frame_data / ConsumeFrameData pass this frame (invalid handle, or skip).
        /// advance=false is for frame-driven use (child Instance / Timeline control).
        /// </summary>
        public void PrepareFrameData(float deltaTime, bool advance, out System.IntPtr handle, out float drawFrame)
        {
            handle = System.IntPtr.Zero;
            drawFrame = 0f;
            if (!_player._handle.IsValid) return;

            // Ensure the previous frame's job is complete before overwriting the persistent buffers.
            _updateJobHandle.Complete();

            // ss_runtime_update takes delta in milliseconds (update_interval = 1000/fps).
            if (advance)
            {
                unsafe { NativeMethods.ss_runtime_update(_player._handle.ptr, deltaTime * 1000.0f); }
                _player.PollEvents();
            }

            drawFrame = _player.SubFrameEnabled ? _player.GetFrame() : UnityEngine.Mathf.Floor(_player.GetFrame());
            // When SubFrame is enabled the value is evaluated as a fraction, so a floating-point number that
            // overshoots the end frame (end_frame) (e.g. 59.99) is treated as outside the SSAB valid range and
            // causes every part to disappear. Clamp so it always stays within the defined frame range.
            drawFrame = UnityEngine.Mathf.Clamp(drawFrame, _player.GetAnimationSectionStart(), _player.GetAnimationSectionEnd());

            // With subFrame disabled and no effect/instance parts, if the integer frame matches the previous
            // build the SoA output is exactly identical, so skip the FFI, aggregation and Job and reuse the
            // previous frame's geometry as-is. Effects (particles advance every frame) and instances (child
            // animations advance independently) are excluded.
            _frameSkipped = false;
            if (!_player.SubFrameEnabled && !_player._hasEffectParts && !_player._hasInstanceParts
                && _vertexCount > 0 && drawFrame == _lastBuiltDrawFrameNo)
            {
                _frameSkipped = true;
                return;
            }

            _pendingDrawFrame = drawFrame;
            unsafe { handle = (System.IntPtr)_player._handle.ptr; }
        }

        /// <summary>
        /// Invalidate the frame-skip cache so the next PrepareFrameData rebuilds geometry even when the draw
        /// frame number is unchanged. Must be called after setup_animation: that resets the native world
        /// matrices to identity, so a new animation whose start frame number matches the last built frame
        /// would otherwise be skipped, leaving matrices/geometry stale (seen as part trackers snapping to
        /// the local origin, and a stale mesh preview).
        /// </summary>
        public void InvalidateFrameCache() => _lastBuiltDrawFrameNo = float.NaN;

        /// <summary>
        /// Serial convenience wrapper. Fetches get_frame_data synchronously without advancing and builds the
        /// mesh, all in one method. Used by child Instances (frame-driven) and the edit-mode preview. Top-level
        /// Play mode is split by SpriteStudioUpdateManager into PrepareFrameData -> parallel get_frame_data -> ConsumeFrameData.
        /// </summary>
        public void ScheduleMeshUpdate(float deltaTime)
        {
            PrepareFrameData(deltaTime, false, out var handle, out _);
            if (handle == System.IntPtr.Zero) return;
            unsafe
            {
                byte* p = null;
                nuint l = 0;
                NativeMethods.ss_runtime_get_frame_data((void*)handle, _pendingDrawFrame, &p, &l);
                ConsumeFrameData((System.IntPtr)p, (int)l, deltaTime);
            }
        }

        /// <summary>
        /// Consume the frame-data pointer obtained by the parallel GetFrameDataJob (its job output), read out
        /// the SoA, and dispatch the mesh-building Burst Job. ptr==Zero / len==0 is treated as no geometry.
        /// </summary>
        public void ConsumeFrameData(System.IntPtr frameDataPtrRaw, int len, float deltaTime)
        {
            _hasFrameData = false;
            if (!_player._handle.IsValid) return;

            unsafe
            {
                byte* frameDataPtr = (byte*)frameDataPtrRaw;

                if (frameDataPtr == null || len == 0)
                {
                    _vertexCount = 0;
                    _indexCount = 0;
                    return;
                }

                float drawFrameNo = _pendingDrawFrame;
#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
                // Zero-copy: place the per-context frame_data native buffer directly into the ByteBuffer.
                // Reading completes within this method (synchronously). A child's get_frame_data writes to a
                // different buffer in a different context, so it does not corrupt the parent buffer
                // (SDK: get_frame_data borrows &mut self = per-context).
                if (_frameDataAllocator == null)
                {
                    _frameDataAllocator = new NativeByteBufferAllocator();
                    _frameDataByteBuffer = new Google.FlatBuffers.ByteBuffer(_frameDataAllocator, 0);
                }
                _frameDataAllocator.Reset(frameDataPtr, len);
                _frameDataByteBuffer.Position = 0;
#else
                if (_frameDataArray == null || _frameDataArray.Length < len)
                {
                    _frameDataArray = new byte[UnityEngine.Mathf.Max(len, 4096)];
                    _frameDataByteBuffer = new Google.FlatBuffers.ByteBuffer(_frameDataArray);
                }
                // Zero-GC: copy memory from the pointer into the existing byte[] (fallback)
                System.Runtime.InteropServices.Marshal.Copy((System.IntPtr)frameDataPtr, _frameDataArray, 0, len);
                _frameDataByteBuffer.Position = 0; // Reset ByteBuffer offset
#endif

                var frameData = ss.runtime.FrameData.GetRootAsFrameData(_frameDataByteBuffer);
                _lastFrameData = frameData;
                _hasFrameData = true;

                // Table view of the FrameData table. Take the start position of the contiguous parts /
                // parts_color / draw_order vectors (tFrame.__vector(vtoff)), shared by the raw-pointer direct
                // reads and the existing drawOrder copy.
                var tFrame = new Google.FlatBuffers.Table(
                    _frameDataByteBuffer.GetInt(_frameDataByteBuffer.Position) + _frameDataByteBuffer.Position, _frameDataByteBuffer);

                DrawFrame frame = new DrawFrame
                {
                    FrameNo = drawFrameNo,
                    DeltaSeconds = deltaTime
                };

                NativeMethods.ss_runtime_get_world_matrices(_player._handle.ptr, &frame.WorldMatrices, &frame.WorldMatricesLen);
                NativeMethods.ss_runtime_get_local_uvs(_player._handle.ptr, &frame.LocalUvs, &frame.LocalUvsLen);
                NativeMethods.ss_runtime_get_local_vertices(_player._handle.ptr, &frame.LocalVertices, &frame.LocalVerticesLen);

                NativeMethods.ss_runtime_get_shape_vertices(_player._handle.ptr, &frame.ShapeVertices, &frame.ShapeVerticesLen);
                NativeMethods.ss_runtime_get_shape_vertex_box_coords(_player._handle.ptr, &frame.ShapeBoxCoords, &frame.ShapeBoxCoordsLen);
                NativeMethods.ss_runtime_get_shape_vertex_counts(_player._handle.ptr, &frame.ShapeVertexCounts, &frame.ShapeVertexCountsLen);

                NativeMethods.ss_runtime_get_mesh_vertices_x(_player._handle.ptr, &frame.MeshVerticesX, &frame.MeshVerticesXLen);
                NativeMethods.ss_runtime_get_mesh_vertices_y(_player._handle.ptr, &frame.MeshVerticesY, &frame.MeshVerticesYLen);
                NativeMethods.ss_runtime_get_mesh_vertex_offsets(_player._handle.ptr, &frame.MeshVertexOffsets, &frame.MeshVertexOffsetsLen);
                NativeMethods.ss_runtime_get_mesh_uvs(_player._handle.ptr, &frame.MeshUvs, &frame.MeshUvsLen);
                NativeMethods.ss_runtime_get_mesh_indices(_player._handle.ptr, &frame.MeshIndices, &frame.MeshIndicesLen);
                NativeMethods.ss_runtime_get_mesh_index_offsets(_player._handle.ptr, &frame.MeshIndexOffsets, &frame.MeshIndexOffsetsLen);

                int drawBatchesLength = frameData.DrawBatchesLength;
                int drawOrderLength = frameData.DrawOrderLength;
                int partsLength = frameData.PartsLength;
                int partsColorLength = frameData.PartsColorLength;

                SpriteStudioNativeBuffers.EnsureCapacity(ref _instanceMeshes, partsLength);

                bool parentLooped = NativeMethods.ss_runtime_just_looped(_player._handle.ptr);
                float fps = _player.GetFrameRate();
                if (fps <= 0f) fps = 60f;

                // Allocate the effect plan table, clear it to default, and reset the merged buffer's count, all together.
                _effectBuffer.BeginFrame(partsLength);

                // Process only active instance parts sparsely (indices finalized in SetupSlots). Since the
                // accounting loop and the Burst Job may read the default from inactive slots, MemClear the whole
                // thing up front (far cheaper than writing partsLength individual defaults, and safe regardless
                // of which parts get read).
                if (partsLength > 0)
                {
                    Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(
                        Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_instanceMeshes),
                        (long)partsLength * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<InstanceMeshPointer>());
                }

                // Step the active effect/instance slots and fill the merged buffer / child mesh pointers.
                StepEffects(drawFrameNo, deltaTime, fps, parentLooped);
                int childHandleCount = StepInstances(drawFrameNo, deltaTime, parentLooped, partsLength);

                // maxBatches also aggregates only active effect/instance parts (inactive contribute default=0, so unneeded).
                int maxBatches = drawBatchesLength;
                for (int k = 0; k < _player._effectPartCount; k++)
                {
                    int i = _player._effectPartIndices[k];
                    if (_effectBuffer.GetPlan(i).CommandCount > 0)
                        maxBatches += _effectBuffer.GetPlan(i).CommandCount;
                }
                for (int k = 0; k < _player._instancePartCount; k++)
                {
                    int i = _player._instancePartIndices[k];
                    if (_instanceMeshes[i].Vertices != null)
                    {
                        // An Instance takes the whole child mesh in as one draw batch, but the per-texture
                        // SubMeshes number as many as the child's submesh count. Budget submesh count + 1 (for the draw batch).
                        maxBatches += _player._instanceChildren[i].Player.SubmeshCount + 1;
                    }
                }

                // Masking emits per-part submeshes (no batch merge) plus extra write/restore steps, so
                // grow the batch/submesh budget. Each part contributes up to ~2 steps (test+write or
                // colour-suppressed restore); Phase A pre-writes and Phase C clipping-closes each add at
                // most one per writer. (EmitMaskStep self-grows, so an under-estimate is never fatal.)
                if (_player.MaskAffectsParts) maxBatches += partsLength * 2 + _maskPlanner.WriterCount * 2;

                // Allocate the persistent buffers grow-only (do not new every frame)
                _batchBuilder.BeginFrame(maxBatches);
                // Reset the carried-up clipping restores / instance-coverage flag for this frame.
                _deferredCloseCount = 0;
                _instanceCoverageOpen = false;
                SpriteStudioNativeBuffers.EnsureCapacity(ref _drawOrder, drawOrderLength);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _partStates, partsLength);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _partColors, partsColorLength);

                
                _partCount = partsLength;
                _partColorCount = partsColorLength;

                // Fill PartState first so the batch aggregation below (Normal's needsCenter test) can read
                // UpdateFlag from _partStates instead of re-decoding it from the FlatBuffer.
                var pStates = (PartStateData*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_partStates);
                // Eliminate the per-element accessor (frameData.Parts(i)) and read the contiguous parts
                // struct-vector (fixed struct at vtable offset 4, 144B stride) directly via the RawPartState mirror.
                int partsVecOff = tFrame.__offset(PartsVtoff);
                if (partsVecOff != 0)
                {
                    RawPartState* rawParts = (RawPartState*)(frameDataPtr + tFrame.__vector(partsVecOff));
                    for (int i = 0; i < partsLength; i++)
                    {
                        ref readonly RawPartState p = ref rawParts[i];
                        pStates[i] = new PartStateData
                        {
                            UpdateFlag = p.UpdateFlag,
                            Alpha = p.Alpha,
                            PartColorIndex = p.PartColor
                        };
                    }
                }

                // Fill DrawOrder ([ushort] vector) first too, so the batch aggregation loop reads from _drawOrder.
                // It is a contiguous scalar vector, so memcpy the whole thing rather than per-element (as with effect geometry).
                // draw_order is FrameData field 6 -> vtable offset 16.
                SpriteStudioNativeBuffers.CopyFbVector(tFrame, 16, frameDataPtr, _drawOrder, 0, drawOrderLength);

                // Build the per-frame mask writer list (mirrors _build_mask_writers). Behaviour-neutral
                // until the stencil submesh pipeline consumes it.
                _maskPlanner.BuildWriters(frameData, _drawOrder, _player._hasMaskParts, _player._maskPartData, drawOrderLength, partsLength,
                    _player._inheritedMaskInfluence);

                int totalVertices = 0;
                int totalIndices = 0;

                // Mask Phase A: harvest per-part textures, then pre-write every pure mask so the stencil
                // reflects all of them before any body part is drawn. Each pure mask's coverage is
                // removed again at its own rank in the main loop (Phase B), yielding the "mask affects
                // lower-priority parts" scope.
                if (_player.MaskActive)
                {
                    _maskPlanner.BuildPartLookup(frameData, _drawOrder, drawBatchesLength, drawOrderLength, partsLength);
                    for (int wi = 0; wi < _maskPlanner.WriterCount; wi++)
                    {
                        var w = _maskPlanner.GetWriter(wi);
                        if (w.IsClipping) continue; // clipping writes at its own rank (Phase B), not pre-written
                        int wp = w.PartIndex;
                        if (wp < 0 || wp >= partsLength) continue;
                        var md = _player._maskPartData[wp];
                        if (!_maskPlanner.MaskPartVcIc(wp, md.DrawKind, frame, _partStates, out int wvc, out int wic)) continue;
                        byte op = w.OpInvert ? StencilOpInvert : StencilOpIncr;
                        float threshold = md.DrawKind == SsDrawBatchKind.Shape ? -1f : w.Threshold;
                        _batchBuilder.EmitMaskStep(md.DrawKind, w.DrawRank, wvc, wic, _maskPlanner.GetPartTexHash(wp), _maskPlanner.GetPartBlend(wp),
                            StencilRole.Write, op, 0, threshold, ref totalVertices, ref totalIndices);
                    }
                }

                for (int i = 0; i < drawBatchesLength; i++)
                {
                    var b = frameData.DrawBatches(i).Value;

                    if (b.Kind == ss.runtime.DrawBatchKind.Instance)
                    {
                        ProcessInstanceBatch(b, partsLength, ref totalVertices, ref totalIndices);
                    }
                    else if (b.Kind == ss.runtime.DrawBatchKind.Effect)
                    {
                        ProcessEffectBatch(b, partsLength, ref totalVertices, ref totalIndices);
                    }
                    else if (b.Kind == ss.runtime.DrawBatchKind.Normal || b.Kind == ss.runtime.DrawBatchKind.Shape || b.Kind == ss.runtime.DrawBatchKind.Mesh)
                    {
                        ProcessNormalShapeMeshBatch(b, partsLength, ref frame, frameData, ref totalVertices, ref totalIndices);
                    }
                    else if (b.Kind == ss.runtime.DrawBatchKind.Mask && _player.MaskActive)
                    {
                        ProcessMaskBatch(b, partsLength, ref frame, frameData, ref totalVertices, ref totalIndices);
                    }
                }

                // Mask Phase C: close every clipping mask's scope. A clipping mask writes its stencil at
                // its own rank (Phase B) to affect all parts drawn after it, but - unlike a pure mask,
                // which is pre-written in Phase A and removed at its own rank - it has no removal step, so
                // its isolated stencil bits would stay set past the object's draw and leak into other SS
                // objects sharing the stencil buffer this frame. Emit the inverse write at the very end of
                // the draw (after every clipped part, ColorMask 0 so nothing is drawn) to return the bits
                // to zero. This mirrors the pure-mask Phase A/B pair, making every writer balanced.
                if (_player.MaskActive)
                {
                    for (int wi = 0; wi < _maskPlanner.WriterCount; wi++)
                    {
                        var w = _maskPlanner.GetWriter(wi);
                        if (!w.IsClipping) continue;
                        int wp = w.PartIndex;
                        if (wp < 0 || wp >= partsLength) continue;
                        var md = _player._maskPartData[wp];
                        if (!_maskPlanner.MaskPartVcIc(wp, md.DrawKind, frame, _partStates, out int wvc, out int wic)) continue;
                        // Inverse of the Phase B clipping write (wop = MaskInfluence ? Invert : Incr).
                        byte rop = w.OpInvert ? StencilOpInvert : StencilOpDecr;
                        float threshold = md.DrawKind == SsDrawBatchKind.Shape ? -1f : w.Threshold;
                        _batchBuilder.EmitMaskStep(md.DrawKind, w.DrawRank, wvc, wic, _maskPlanner.GetPartTexHash(wp), _maskPlanner.GetPartBlend(wp),
                            StencilRole.Write, rop, 0, threshold, ref totalVertices, ref totalIndices);
                        // Tag as a deferrable close: if THIS player is itself an instance child, its parent
                        // carries this restore past the instance boundary (SS7.5) instead of closing here.
                        _batchBuilder.MarkLastSubmeshDeferrableClose();
                    }
                }

                // Flush the clipping restores carried up from visible instance children (their Phase C,
                // deferred to ours). Emitting them after every part of this player is what lets an
                // instance-internal mask clip this player's later parts too. They stay tagged deferrable so
                // an enclosing instance carries them up again (nesting). The geometry already exists in the
                // merged mesh (built when the child mesh was copied in), so this only adds submeshes - no
                // vertices/indices. Runs regardless of MaskActive: a child can have masks this player lacks.
                for (int di = 0; di < _deferredCloseCount; di++)
                {
                    var s = _deferredCloses[di];
                    _batchBuilder.EmitSubmesh(s.IndexStart, s.IndexCount, s.TextureHash, s.BlendType,
                        s.Role, s.StencilOp, s.StencilComp, s.StencilRef, s.StencilReadMask, s.StencilWriteMask, s.MaskThreshold);
                    _batchBuilder.MarkLastSubmeshDeferrableClose();
                }

                FillPartColors(frameDataPtr, tFrame, partsColorLength);

                if (totalVertices == 0 || totalIndices == 0)
                {
                    _vertexCount = 0;
                    _indexCount = 0;
                    return;
                }

                SpriteStudioNativeBuffers.EnsureCapacity(ref _meshVertices, totalVertices);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _meshIndices, totalIndices);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _meshBounds, 2); // the Job writes min/max
                _vertexCount = totalVertices;
                _indexCount = totalIndices;

                // Topology signature for the index upload cache (if identical to the previous frame, the
                // index/submesh need no re-upload). For effects, QuadCount/Offset change every frame and
                // naturally mismatch = not cached.
                _topologySignature = _batchBuilder.ComputeTopologySignature(totalVertices, totalIndices);

                // Fold each visible instance child's own topology signature into ours. EmitInstanceBatch records
                // a child as a single opaque block (kind + total vertex/index count), so the batch signature is
                // blind to a child-internal reorder (e.g. a PRIO keyframe): the child's merged index values move
                // while its totals stay put, and the index-upload cache would wrongly keep the stale GPU indices
                // (seen as a torn instanced mesh part). The child's own signature includes its draw ranks, so
                // mixing it in makes our cache re-upload exactly when the child's geometry actually changed.
                if (_player._hasInstanceParts)
                {
                    const ulong prime = 1099511628211UL;
                    for (int k = 0; k < _player._instancePartCount; k++)
                    {
                        int i = _player._instancePartIndices[k];
                        if (_instanceMeshes[i].Vertices == null) continue; // not visible this frame
                        var child = _player._instanceChildren[i].Player;
                        if (child == null) continue;
                        _topologySignature = (_topologySignature ^ child.TopologySignature) * prime;
                    }
                }

                // Keep at least 1 element in the merged arrays because the Job references them even with no effects.
                _effectBuffer.EnsureGeometryMinimum();

                JobHandle deps = default;
                if (childHandleCount > 0)
                {
                    deps = JobHandle.CombineDependencies(_childHandles.GetSubArray(0, childHandleCount));
                }

                _updateJobHandle = new UpdateMeshJob
                {
                    WorldMatrices = frame.WorldMatrices,
                    LocalUvs = frame.LocalUvs,
                    LocalVertices = frame.LocalVertices,
                    ShapeVertices = frame.ShapeVertices,
                    ShapeBoxCoords = frame.ShapeBoxCoords,
                    ShapeVertexCounts = frame.ShapeVertexCounts,

                    MeshVerticesX = frame.MeshVerticesX,
                    MeshVerticesY = frame.MeshVerticesY,
                    MeshVertexOffsets = frame.MeshVertexOffsets,
                    MeshUvs = frame.MeshUvs,
                    MeshIndices = frame.MeshIndices,
                    MeshIndexOffsets = frame.MeshIndexOffsets,

                    BatchCount = _batchBuilder.DrawBatchCount,
                    PartCount = _partCount,
                    PartColorCount = _partColorCount,
                    RootScale = _player.RootScale,
                    RootOffset = _player.VertexOffset,

                    DrawBatches = _batchBuilder.DrawBatches,
                    DrawOrder = _drawOrder,
                    PartStates = _partStates,
                    PartColors = _partColors,
                    EffectVerts = _effectBuffer.Verts,
                    EffectUvs = _effectBuffer.Uvs,
                    EffectColors = _effectBuffer.Colors,
                    EffectIndices = _effectBuffer.Indices,
                    InstanceMeshes = _instanceMeshes,

                    Vertices = _meshVertices,
                    Indices = _meshIndices,
                    Bounds = _meshBounds
                }.Schedule(deps);

                // Baseline for skipping the rebuild next frame if that integer frame matches this one.
                _lastBuiltDrawFrameNo = drawFrameNo;
            }
        }

        /// <summary>
        /// Wait for the dispatched mesh-building job to complete and apply the built geometry to Unity's Mesh.
        /// The actual upload (vertices/index/submesh/bounds and the re-upload-skip cache) is delegated to
        /// <see cref="SpriteStudioMeshUploader"/>.
        /// </summary>
        /// <param name="use16BitIndices">Canvas path: route to the high-level 16-bit upload
        /// (CanvasRenderer rejects the low-level mesh on Windows/D3D12 -> device removed). World path = false.</param>
        public void ApplyToMesh(UnityEngine.Mesh targetMesh, bool perBatchSubmesh = false, bool use16BitIndices = false)
        {
            // Wait for the Job to complete before applying to the Mesh (the Job is owned by the builder).
            _updateJobHandle.Complete();

            _meshUploader.Apply(targetMesh, perBatchSubmesh, use16BitIndices, new SpriteStudioMeshUploader.UploadData(
                _frameSkipped, _vertexCount, _indexCount, _meshVertices, _meshIndices,
                _batchBuilder.Submeshes, _batchBuilder.SubmeshCount, _topologySignature, _meshBounds));
        }

        /// <summary>Step the active effect slots and append the visible ones' draw_plan into the merged buffer.</summary>
        private unsafe void StepEffects(float drawFrameNo, float deltaTime, float fps, bool parentLooped)
        {
            for (int k = 0; k < _player._effectPartCount; k++)
            {
                int i = _player._effectPartIndices[k];
                var ev = NativeMethods.ss_runtime_get_active_effect_event(_player._handle.ptr, (uint)i);
                var step = NativeMethods.ss_effect_slot_step((void*)_player._effectSlots[i].EffectSlot, ev, drawFrameNo, deltaTime, fps, parentLooped, false, 3);
                if (step.visible && step.draw_plan_len > 0)
                    _effectBuffer.SetPlan(i, _effectBuffer.Append(step.draw_plan_buf, (int)step.draw_plan_len));
                // Non-visible stays at the MemClear'd default
            }
        }

        /// <summary>Step the active instance slots and collect visible children's mesh pointers / Job handles.
        /// Returns the number of child Job handles to bundle as dependencies (for CombineDependencies).</summary>
        private unsafe int StepInstances(float drawFrameNo, float deltaTime, bool parentLooped, int partsLength)
        {
            int childHandleCount = 0;
            // Writes to _childHandles only for visible instances. Persistent grow-only to avoid a per-frame Temp allocation.
            if (_player._hasInstanceParts)
                SpriteStudioNativeBuffers.EnsureCapacity(ref _childHandles, partsLength);

            for (int k = 0; k < _player._instancePartCount; k++)
            {
                int i = _player._instancePartIndices[k];
                var childPlayer = _player._instanceChildren[i].Player;
                if (childPlayer == null) continue; // _instanceMeshes[i] stays at the MemClear'd default

                var ev = NativeMethods.ss_runtime_get_active_event_instance(_player._handle.ptr, (uint)i);
                var step = NativeMethods.ss_instance_slot_step((void*)_player._instanceChildren[i].InstanceSlot, ev, childPlayer._handle.ptr, drawFrameNo, deltaTime, parentLooped);

                if (step.transitioned)
                {
                    childPlayer.Play(); // Trigger side effects (UnityEvents) if needed
                }

                if (step.visible)
                {
                    // For an independent instance the Rust-side controller is the sole frame truth, and
                    // SetFrame(=set_frame_no) rewrites that controller directly. The next frame's
                    // child_ctx.update() advances by delta from the written-back current frame, so writing back a
                    // Floor/Clamp'd value here truncates every frame the fractional progress of an independent
                    // child that advances 1tick<1frame, so it cannot cross an integer frame and freezes. Hence
                    // write the raw child_frame_no back to the controller (irrelevant for non-independent, which
                    // recomputes from parent_frame via set_frame_relative).
                    // The display-side frame-range clamp is unneeded here because the child's own PrepareFrameData does it before get_frame_data.
                    // Hand this instance part's mask context down before the child builds: the child bakes
                    // the composed roles into its own submeshes, so the parent never patches them afterwards.
                    PropagateMaskContext(i, childPlayer);
                    childPlayer.SetFrame(step.child_frame_no);
                    childPlayer.ScheduleMeshUpdate(deltaTime);

                    _childHandles[childHandleCount++] = childPlayer.CurrentUpdateJobHandle;

                    _instanceMeshes[i] = new InstanceMeshPointer {
                        Vertices = (SsVertex*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(childPlayer.MeshVertices),
                        Indices = (uint*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(childPlayer.MeshIndices),
                        VertexCount = childPlayer.VertexCount,
                        IndexCount = childPlayer.IndexCount
                    };
                }
                // Non-visible stays at the MemClear'd default
            }
            return childHandleCount;
        }

        /// <summary>Fill _partColors from frameData's PartsColor (the 4 corner vertex colors).</summary>
        private unsafe void FillPartColors(byte* frameDataPtr, Google.FlatBuffers.Table tFrame, int partsColorLength)
        {
            // Eliminate the per-element nested accessor (frameData.PartsColor(i)) and read the contiguous
            // parts_color struct-vector (vtable offset 8, 36B stride) directly via the RawPartColor mirror.
            // U8Color is in ARGB byte order, so reorder into Color32(R,G,B,A) (identical to the prior behavior).
            int vecOff = tFrame.__offset(PartsColorVtoff);
            if (vecOff == 0) return;
            RawPartColor* raw = (RawPartColor*)(frameDataPtr + tFrame.__vector(vecOff));
            for (int i = 0; i < partsColorLength; i++)
            {
                ref readonly RawPartColor pc = ref raw[i];
                _partColors[i] = new PartColorData
                {
                    BlendType = pc.BlendType,
                    Lt = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Lt.R, pc.Lt.G, pc.Lt.B, pc.Lt.A), Rate = pc.Lt.Rate },
                    Rt = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Rt.R, pc.Rt.G, pc.Rt.B, pc.Rt.A), Rate = pc.Rt.Rate },
                    Lb = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Lb.R, pc.Lb.G, pc.Lb.B, pc.Lb.A), Rate = pc.Lb.Rate },
                    Rb = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Rb.R, pc.Rb.G, pc.Rb.B, pc.Rb.A), Rate = pc.Rb.Rate }
                };
            }
        }

        /// <summary>
        /// Compute the FNV-1a signature for the index-upload cache decision from the current draw batch /
        /// submesh structure. Call after _vertexCount / _indexCount are finalized.
        /// </summary>
        

        // Stencil op / compare values used by the mask path (UnityEngine.Rendering enums as bytes).
        private const byte StencilOpIncr = (byte)UnityEngine.Rendering.StencilOp.IncrementWrap;   // 7
        private const byte StencilOpDecr = (byte)UnityEngine.Rendering.StencilOp.DecrementWrap;   // 8
        private const byte StencilOpInvert = (byte)UnityEngine.Rendering.StencilOp.Invert;        // 5
        private const byte StencilCompEqual = (byte)UnityEngine.Rendering.CompareFunction.Equal;       // 3 (draw outside mask)
        private const byte StencilCompNotEqual = (byte)UnityEngine.Rendering.CompareFunction.NotEqual; // 6 (draw inside mask)

        /// <summary>
        /// Emit one mask render step: a single-part geometry batch (built by the mesh job) plus a
        /// submesh carrying its stencil role/params. Geometry is rebuilt per step, so a writer that
        /// both writes and later restores costs two builds (cheap; masks are few).
        /// </summary>

        /// <summary>
        /// Hand this player's composed mask context to an instance child, before the child builds. The
        /// rules live on the player (SpriteStudioInternalPlayer.ComposeInheritedContext); a part index
        /// that resolves to no mask data leaves the child at the identity context (= unmasked).
        /// </summary>
        private void PropagateMaskContext(int instancePartIndex, SpriteStudioInternalPlayer childPlayer)
        {
            bool masked = false, influence = true, visibleInside = false;
            var md = _player._maskPartData;
            if (_player.MaskActive && md != null && instancePartIndex >= 0 && instancePartIndex < md.Length)
            {
                SpriteStudioInternalPlayer.ComposeInheritedContext(md[instancePartIndex],
                    _player._inheritedMaskInfluence, _player._inheritedVisibleInsideMask,
                    out masked, out influence, out visibleInside);
            }
            childPlayer._inheritedMasked = masked;
            childPlayer._inheritedMaskInfluence = influence;
            childPlayer._inheritedVisibleInsideMask = visibleInside;
        }

        /// <summary>
        /// Effective mask targeting for one of this player's parts, composed against the context inherited
        /// from the instance chain (see <see cref="PropagateMaskContext"/>). A root player's inherited
        /// values are the identity elements, so this reduces to the part's own setting.
        /// <paramref name="comp"/> receives the stencil compare for the resulting polarity.
        /// </summary>
        private bool MaskTargetOf(in SpriteStudioInternalPlayer.MaskPartData md, out byte comp)
        {
            comp = SpriteStudioInternalPlayer.IsDrawnInsideMask(md, _player._inheritedVisibleInsideMask)
                ? StencilCompNotEqual : StencilCompEqual;
            // Whether this part's own COLOUR is clipped by masks. A clipping part draws its pixels, then
            // writes its clip below ("pixels, then mask"), so its own MaskWrite must not gate its colour -
            // it is still clipped by OTHER masks purely on its real maskInfluence. IsMaskTarget adds
            // "|| MaskWrite" for the propagation/target sense; using it here made a clipping writer test
            // its own colour, so a visibleInsideMask writer with no covering mask (NotEqual) vanished when
            // it should draw (maskInfluence=0), and one with maskInfluence=1 still resolves the same way.
            return md.MaskInfluence && _player._inheritedMaskInfluence;
        }

        private unsafe void ProcessInstanceBatch(ss.runtime.DrawBatch b, int partsLength, ref int totalVertices, ref int totalIndices)
        {
            for (int k = 0; k < b.Count; k++)
            {
                int rank = b.StartRank + k;
                int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
                if (p_idx < 0 || p_idx >= partsLength) continue;

                var meshPtr = _instanceMeshes[p_idx];
                if (meshPtr.Vertices != null && meshPtr.VertexCount > 0)
                {
                    var childPlayer = _player._instanceChildren[p_idx].Player;
                    int cvc = childPlayer.VertexCount;
                    int cic = childPlayer.IndexCount;

                    // Base index of this child's block in the merged mesh. The mesh job places the whole
                    // child index buffer contiguously here (BuildInstance), preserving per-index positions,
                    // so a child submesh at within-child IndexStart lands at instanceIndexBase + IndexStart.
                    int instanceIndexBase = totalIndices;
                    _batchBuilder.EmitInstanceBatch(p_idx, cvc, cic);

                    // The child already resolved each submesh's stencil role against the context handed to
                    // it in PropagateMaskContext, so take them verbatim. (This used to patch every
                    // role-less child submesh to the instance part's own role instead, which is neither
                    // the OR of visibleInsideMask nor the AND of maskInfluence that SS7.5 composes: a child
                    // part carrying its own role overrode the caller, and a role-less one was forced to the
                    // caller's polarity even when its own said otherwise.)
                    for (int sm = 0; sm < childPlayer.SubmeshCount; sm++)
                    {
                        var cs = childPlayer.Submeshes[sm];
                        if (cs.DeferrableClose)
                        {
                            // A clipping-mask restore inside the child: don't close it here at the instance
                            // boundary. Carry it (in this player's index space) to our Phase C so the
                            // child's mask keeps clipping this player's parts drawn after the instance, and
                            // flag that coverage is now open for those parts (see ProcessNormalShapeMeshBatch).
                            StashDeferredClose(cs, instanceIndexBase + cs.IndexStart);
                            _instanceCoverageOpen = true;
                            continue;
                        }
                        _batchBuilder.EmitSubmesh(instanceIndexBase + cs.IndexStart, cs.IndexCount, cs.TextureHash, cs.BlendType,
                            cs.Role, cs.StencilOp, cs.StencilComp, cs.StencilRef, cs.StencilReadMask, cs.StencilWriteMask, cs.MaskThreshold);
                    }

                    totalVertices += cvc;
                    totalIndices += cic;
                }
            }
        }

        /// <summary>
        /// Record a clipping-mask restore carried up from an instance child, rewritten into this player's
        /// index space (parentIndexStart). Flushed at this player's Phase C. Grow-only, preserving prior
        /// entries so this frame's earlier instances keep their stashed closes.
        /// </summary>
        private unsafe void StashDeferredClose(in SubmeshInfo cs, int parentIndexStart)
        {
            if (!_deferredCloses.IsCreated || _deferredCloseCount >= _deferredCloses.Length)
            {
                int newSize = !_deferredCloses.IsCreated ? 8 : _deferredCloses.Length * 2;
                if (newSize < _deferredCloseCount + 1) newSize = _deferredCloseCount + 1;
                SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _deferredCloses, newSize);
            }
            var s = cs;
            s.IndexStart = parentIndexStart;
            ((SubmeshInfo*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_deferredCloses))[_deferredCloseCount++] = s;
        }

        private unsafe void ProcessEffectBatch(ss.runtime.DrawBatch b, int partsLength, ref int totalVertices, ref int totalIndices)
        {
            for (int k = 0; k < b.Count; k++)
            {
                int rank = b.StartRank + k;
                int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
                if (p_idx < 0 || p_idx >= partsLength) continue;
                
                var plan = _effectBuffer.GetPlan(p_idx);
                if (plan.CommandCount == 0) continue;

                StencilRole effRole = StencilRole.None;
                byte effComp = 0;
                if (_player.MaskActive)
                {
                    // Particles have no mask flags of their own, so the effect part's effective targeting
                    // (its own flags composed with the inherited instance context) applies to all of them.
                    if (MaskTargetOf(_player._maskPartData[p_idx], out byte c))
                    {
                        effRole = StencilRole.Test;
                        effComp = c;
                    }
                }

                for (int c = 0; c < plan.CommandCount; c++)
                {
                    var cmd = _effectBuffer.GetCommand(plan.CommandStart + c);
                    if (cmd.QuadCount > 0)
                    {
                        int cmdVc = (int)cmd.QuadCount * 4;
                        int cmdIc = (int)cmd.QuadCount * 6;

                        _batchBuilder.EmitEffectBatch(cmdVc, cmdIc, cmd.Blend, (uint)plan.QuadBase, cmd.ParticleOffset, cmd.QuadCount, p_idx);
                        
                        _batchBuilder.EmitSubmesh(totalIndices, cmdIc, cmd.CellmapHash, SpriteStudioMaterial.BlendMix,
                            effRole, 0, effComp, 0, effRole == StencilRole.Test ? SpriteStudioMaterial.MaskStencilMask : (byte)0, 0, 0f);
                        
                        totalVertices += cmdVc;
                        totalIndices += cmdIc;
                    }
                }
            }
        }

        private unsafe void ProcessNormalShapeMeshBatch(ss.runtime.DrawBatch b, int partsLength, ref DrawFrame frame, ss.runtime.FrameData frameData, ref int totalVertices, ref int totalIndices)
        {
            int vc = 0;
            int ic = 0;

            // Also taken when this player has no mask parts of its own but an instance ancestor's mask
            // reaches it (MaskAffectsParts), or a visible instance child EARLIER this frame left an open
            // clip that carries up (_instanceCoverageOpen): those parts still need one submesh each to
            // carry their own stencil role. The instance batch is processed before this one (draw order),
            // so parts drawn before any such instance correctly see the flag still false.
            if ((_player.MaskAffectsParts || _instanceCoverageOpen) && _player._maskPartData != null)
            {
                byte kind = (byte)b.Kind;
                for (int k = 0; k < b.Count; k++)
                {
                    int rank = b.StartRank + k;
                    int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
                    if (p_idx < 0 || p_idx >= partsLength) continue;
                    if (!_maskPlanner.MaskPartVcIc(p_idx, kind, frame, _partStates, out int pvc, out int pic)) continue;
                    var md = _player._maskPartData[p_idx];
                    // Canvas suppression (uGUI): drop pure masks, draw everything else unmasked. Within the
                    // MaskAffectsParts branch this is exactly !MaskActive; testing the suppress flag directly
                    // also routes the "coverage from an instance child, no own masks" case to the full logic
                    // below (where MaskActive is false but suppression is off) instead of this Canvas branch.
                    if (_player._suppressInternalMask)
                    {
                        // Canvas suppression: drop pure masks (no colour), draw everything else unmasked.
                        if (md.PureMask) continue;
                        _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                            StencilRole.None, 0, 0, 0f, ref totalVertices, ref totalIndices);
                        continue;
                    }
                    // A writer's op/plane follows its EFFECTIVE maskInfluence (own AND inherited from the
                    // instance chain), so a mask inside a maskInfluence=0 instance lands on the counter
                    // plane (union) instead of the parity plane (XOR) - matching SS7.5's mixed-influence union.
                    bool effInfluence = SpriteStudioInternalPlayer.EffectiveMaskInfluence(md, _player._inheritedMaskInfluence);
                    if (md.PureMask)
                    {
                        // The removal half of the Phase A/B pair, so it runs only for a writer Phase A
                        // actually wrote: a switched-off or hidden pure mask writes nothing, and decrementing
                        // (or inverting) for it would take the plane past where it started. Skipping it emits
                        // no submesh, which is what a pure mask contributes anyway - it never draws a colour.
                        if (_maskPlanner.IsWriterActive(p_idx))
                        {
                            byte rop = effInfluence ? StencilOpInvert : StencilOpDecr;
                            float th = kind == SsDrawBatchKind.Shape ? -1f : (255f - frameData.Parts(p_idx).Value.Mask) / 255f;
                            _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                                StencilRole.Write, rop, 0, th, ref totalVertices, ref totalIndices);
                        }
                    }
                    else
                    {
                        if (MaskTargetOf(md, out byte comp))
                        {
                            _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                                StencilRole.Test, 0, comp, 0f, ref totalVertices, ref totalIndices);
                        }
                        else
                        {
                            _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                                StencilRole.None, 0, 0, 0f, ref totalVertices, ref totalIndices);
                        }
                        // Its own colour is already emitted above, whatever the mask says: a clipping part
                        // draws either way, and only the stencil write is switched off. Phase C closes this
                        // write off the writer list, so both halves have to agree on which writers are live.
                        if (md.IsClipping && _maskPlanner.IsWriterActive(p_idx))
                        {
                            byte wop = effInfluence ? StencilOpInvert : StencilOpIncr;
                            float th = kind == SsDrawBatchKind.Shape ? -1f : (255f - frameData.Parts(p_idx).Value.Mask) / 255f;
                            _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                                StencilRole.Write, wop, 0, th, ref totalVertices, ref totalIndices);
                        }
                    }
                }
                return;
            }

            // Per-part AddOn shader dispatch (non-mask path). When the frame carries shader attributes
            // and a catalog is bound, parts whose shader id resolves to an is-per-part AddOn are carved
            // out as their own Count=1 submesh (stamped with the shader id hash); the rest accumulate
            // into shared rank-runs, mirroring the mask per-part split. Vertex order is unchanged, so the
            // mesh job fills exactly the same geometry — only the submesh granularity differs.
            if (frameData.ShadersLength > 0)
            {
                // Built-in AddOns resolve with no project setup; the (optional) user catalog overrides.
                var userCatalog = _player.ShaderCatalog;
                byte sKind = (byte)b.Kind;
                int runStart = b.StartRank;   // first rank of the current shared run (rank-span based)
                int runVc = 0, runIc = 0;
                for (int k = 0; k < b.Count; k++)
                {
                    int rank = b.StartRank + k;
                    int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
                    int pvc = 0, pic = 0;
                    bool valid = p_idx >= 0 && p_idx < partsLength
                                 && _maskPlanner.MaskPartVcIc(p_idx, sKind, frame, _partStates, out pvc, out pic);
                    if (!valid) continue; // skipped parts add no geometry; their rank stays in the run span

                    uint shaderHash = 0;
                    float ap0 = 0, ap1 = 0, ap2 = 0, ap3 = 0, ap4 = 0, ap5 = 0, ap6 = 0, ap7 = 0;
                    var ps = frameData.Parts(p_idx);
                    if (ps.HasValue)
                    {
                        int sIdx = ps.Value.Shader;
                        if (sIdx >= 0 && sIdx < frameData.ShadersLength)
                        {
                            var shAttr = frameData.Shaders(sIdx).Value;
                            uint idHash = shAttr.IdHash;
                            if (idHash != 0 && SpriteStudioAddOnRegistry.TryGetIsPerPart(idHash, userCatalog, out bool isPerPart) && isPerPart)
                            {
                                shaderHash = idHash;
                                ap0 = shAttr.Param0; ap1 = shAttr.Param1; ap2 = shAttr.Param2; ap3 = shAttr.Param3;
                                ap4 = shAttr.Param4; ap5 = shAttr.Param5; ap6 = shAttr.Param6; ap7 = shAttr.Param7;
                            }
                        }
                    }

                    if (shaderHash != 0)
                    {
                        // Flush the pending shared run [runStart, rank), then emit this part on its own.
                        int runCount = rank - runStart;
                        if (runCount > 0 && runIc > 0)
                            _batchBuilder.EmitDrawBatch((ushort)runStart, (ushort)runCount, runVc, runIc, sKind, b.TextureHash, (byte)b.BlendType, 0u, ref totalVertices, ref totalIndices);
                        _batchBuilder.EmitDrawBatch((ushort)rank, 1, pvc, pic, sKind, b.TextureHash, (byte)b.BlendType, shaderHash, ref totalVertices, ref totalIndices);
                        _batchBuilder.SetLastSubmeshAddOnParams(ap0, ap1, ap2, ap3, ap4, ap5, ap6, ap7);
                        // Cell rectangle (pixel UVs) for cell-rect AddOns (ss-circle / ss-spot). Only Normal
                        // parts carry a cell quad in LocalUvs (layout [lt.u,lt.v, rt, lb, rb.u@6,rb.v@7, center]);
                        // Shape/Mesh leave it 0 so those AddOns degenerate (discard).
                        if (sKind == SsDrawBatchKind.Normal && frame.LocalUvs != null && (p_idx * 10 + 7) < (int)frame.LocalUvsLen)
                        {
                            float* uvp = frame.LocalUvs + p_idx * 10;
                            _batchBuilder.SetLastSubmeshCellRect(uvp[0], uvp[1], uvp[6], uvp[7]);
                        }
                        runStart = rank + 1; runVc = 0; runIc = 0;
                    }
                    else
                    {
                        runVc += pvc; runIc += pic;
                    }
                }
                // Flush the trailing shared run [runStart, batchEnd).
                int tailCount = (b.StartRank + b.Count) - runStart;
                if (tailCount > 0 && runIc > 0)
                    _batchBuilder.EmitDrawBatch((ushort)runStart, (ushort)tailCount, runVc, runIc, sKind, b.TextureHash, (byte)b.BlendType, 0u, ref totalVertices, ref totalIndices);
                return;
            }

            for (int k = 0; k < b.Count; k++)
            {
                int rank = b.StartRank + k;
                int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
                if (p_idx < 0 || p_idx >= partsLength) continue;

                if (b.Kind == ss.runtime.DrawBatchKind.Normal)
                {
                    bool needsCenter = (_partStates[p_idx].UpdateFlag & SsUpdateFlag.NeedsCenterMask) != 0;
                    vc += needsCenter ? 5 : 4;
                    ic += needsCenter ? 12 : 6;
                }
                else if (b.Kind == ss.runtime.DrawBatchKind.Shape)
                {
                    int svc = frame.ShapeVertexCounts[p_idx];
                    if (svc >= 3)
                    {
                        vc += svc;
                        ic += (svc - 2) * 3;
                    }
                }
                else if (b.Kind == ss.runtime.DrawBatchKind.Mesh)
                {
                    uint v0 = frame.MeshVertexOffsets[p_idx];
                    uint v1 = frame.MeshVertexOffsets[p_idx + 1];
                    uint i0 = frame.MeshIndexOffsets[p_idx];
                    uint i1 = frame.MeshIndexOffsets[p_idx + 1];
                    if (v1 > v0 && i1 > i0 && (i1 - i0) % 3 == 0)
                    {
                        vc += (int)(v1 - v0);
                        ic += (int)(i1 - i0);
                    }
                }
            }

            if (vc > 0 && ic > 0)
            {
                _batchBuilder.EmitDrawBatch((ushort)b.StartRank, (ushort)b.Count, vc, ic, (byte)b.Kind, b.TextureHash, (byte)b.BlendType, 0u, ref totalVertices, ref totalIndices);
            }
        }

        private unsafe void ProcessMaskBatch(ss.runtime.DrawBatch b, int partsLength, ref DrawFrame frame, ss.runtime.FrameData frameData, ref int totalVertices, ref int totalIndices)
        {
            for (int k = 0; k < b.Count; k++)
            {
                int rank = b.StartRank + k;
                int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
                if (p_idx < 0 || p_idx >= partsLength) continue;
                var md = _player._maskPartData[p_idx];
                if (!md.IsMaskPart) continue;
                // Phase B for a Mask part: the removal that pairs with Phase A's write, so a writer Phase A
                // left out - switched off with mask == 0, or hidden - must be left out here too.
                if (!_maskPlanner.IsWriterActive(p_idx)) continue;
                if (!_maskPlanner.MaskPartVcIc(p_idx, SsDrawBatchKind.Normal, frame, _partStates, out int mvc, out int mic)) continue;
                // Op/plane follows the effective maskInfluence (own AND inherited); see ProcessNormalShapeMeshBatch.
                byte rop = SpriteStudioInternalPlayer.EffectiveMaskInfluence(md, _player._inheritedMaskInfluence) ? StencilOpInvert : StencilOpDecr;
                float th = (255f - frameData.Parts(p_idx).Value.Mask) / 255f;
                _batchBuilder.EmitMaskStep(SsDrawBatchKind.Normal, rank, mvc, mic, b.TextureHash, (byte)b.BlendType,
                    StencilRole.Write, rop, 0, th, ref totalVertices, ref totalIndices);
            }
        }

        
    }

    // === Raw layout mirrors for reading FlatBuffer fixed struct-vectors directly, without a per-element accessor ===
    // Byte-compatible with framedata_generated.cs's PartState(144B) / PartAttributePartColor(36B) / U8Rect.
    // Warning: if you regenerate the .fbs and the layout changes, update these too (the startup sizeof assert detects total-size drift).
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    internal struct RawCorner   // U8Color(ARGB) + Rate (SsAttributePartColorKeyValueColor)
    {
        [FieldOffset(0)] public byte A;
        [FieldOffset(1)] public byte R;
        [FieldOffset(2)] public byte G;
        [FieldOffset(3)] public byte B;
        [FieldOffset(4)] public float Rate;
    }

    [StructLayout(LayoutKind.Explicit, Size = 36)]
    internal struct RawPartColor   // PartAttributePartColor
    {
        [FieldOffset(0)]  public byte BlendType;
        [FieldOffset(4)]  public RawCorner Lt;
        [FieldOffset(12)] public RawCorner Rt;
        [FieldOffset(20)] public RawCorner Lb;
        [FieldOffset(28)] public RawCorner Rb;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    internal struct RawPartState   // PartState (only the used fields are declared; the rest is reserved via Size)
    {
        [FieldOffset(0)]   public ulong UpdateFlag;
        [FieldOffset(48)]  public float Alpha;
        [FieldOffset(128)] public short PartColor;
    }

    internal static class SpriteStudioRawLayoutCheck
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static unsafe void Validate()
        {
            // (1) Catch total-size drift (fields added/removed).
            UnityEngine.Debug.Assert(Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<RawPartState>() == 144,
                "RawPartState size != 144: framedata PartState layout changed - update the mirror struct");
            UnityEngine.Debug.Assert(Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<RawPartColor>() == 36,
                "RawPartColor size != 36: framedata PartAttributePartColor layout changed - update the mirror struct");
            UnityEngine.Debug.Assert(Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<RawCorner>() == 8,
                "RawCorner size != 8: framedata color-corner layout changed - update the mirror struct");

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // (2) Roundtrip that also catches same-size reorderings: write a known sentinel struct with the
            //     generated builder, then read the same buffer via both the generated accessor and the mirror
            //     and confirm they match. The moment a .fbs regeneration shifts a field offset / vtable offset,
            //     this goes red (= automatic detection of silent drift).
            var b = new Google.FlatBuffers.FlatBufferBuilder(256);

            ss.runtime.FrameData.StartPartsVector(b, 1);
            ss.runtime.PartState.CreatePartState(b,
                (ss.runtime.UpdateAttributeFlags)0x1122334455667788UL, // UpdateFlag (sentinel)
                0,0,0,0,0, 0,0,0,0,0,                                  // PositionX..LocalScaleY
                0.5f,                                                  // Alpha (sentinel)
                0,0,0,0,0, 0,0,0,0,0, 0,0,0,0,0, 0,0,                  // LocalAlpha..SkewY
                0,                                                     // Texture
                0,0,                                                   // PartIndex, Cell
                (short)0x4321,                                         // PartColor (sentinel)
                0,0,0,                                                 // Shader, Vertex, Deform
                false,false,false,false,false);                       // FlipH..ImgFlipV
            var partsVec = b.EndVector();

            ss.runtime.FrameData.StartPartsColorVector(b, 1);
            ss.runtime.PartAttributePartColor.CreatePartAttributePartColor(b,
                (ss.runtime.BlendType)3,
                1,2,3,4, 0.5f,      // Lt: A,R,G,B,Rate
                5,6,7,8, 1.5f,      // Rt
                9,10,11,12, 2.5f,   // Lb
                13,14,15,16, 3.5f); // Rb
            var pcVec = b.EndVector();

            // Satisfy FrameData's required fields (draw_order / draw_batches / mask_indices) with empty vectors
            // (EndFrameData requires their presence; the contents are unused in this check).
            ss.runtime.FrameData.StartDrawOrderVector(b, 0);
            var drawOrderVec = b.EndVector();
            ss.runtime.FrameData.StartDrawBatchesVector(b, 0);
            var drawBatchesVec = b.EndVector();
            ss.runtime.FrameData.StartMaskIndicesVector(b, 0);
            var maskIndicesVec = b.EndVector();

            ss.runtime.FrameData.StartFrameData(b);
            ss.runtime.FrameData.AddParts(b, partsVec);
            ss.runtime.FrameData.AddPartsColor(b, pcVec);
            ss.runtime.FrameData.AddDrawOrder(b, drawOrderVec);
            ss.runtime.FrameData.AddDrawBatches(b, drawBatchesVec);
            ss.runtime.FrameData.AddMaskIndices(b, maskIndicesVec);
            b.Finish(ss.runtime.FrameData.EndFrameData(b).Value);

            byte[] arr = b.DataBuffer.ToSizedArray();
            var bb = new Google.FlatBuffers.ByteBuffer(arr);
            var fd = ss.runtime.FrameData.GetRootAsFrameData(bb);
            var pAcc = fd.Parts(0).Value;
            var cAcc = fd.PartsColor(0).Value;

            fixed (byte* basePtr = arr)
            {
                var t = new Google.FlatBuffers.Table(bb.GetInt(bb.Position) + bb.Position, bb);

                RawPartState* mp = (RawPartState*)(basePtr + t.__vector(t.__offset(SpriteStudioGeometryBuilder.PartsVtoff)));
                UnityEngine.Debug.Assert(
                    mp->UpdateFlag == (ulong)pAcc.UpdateFlag && mp->Alpha == pAcc.Alpha && mp->PartColor == pAcc.PartColor,
                    "RawPartState field offsets do not match the framedata PartState accessor - verify UpdateFlag@0/Alpha@48/PartColor@128 and update the mirror struct");

                RawPartColor* mc = (RawPartColor*)(basePtr + t.__vector(t.__offset(SpriteStudioGeometryBuilder.PartsColorVtoff)));
                bool colorOk =
                    mc->BlendType == (byte)cAcc.BlendType &&
                    mc->Lt.A == cAcc.Lt.Rgba.A && mc->Lt.R == cAcc.Lt.Rgba.R && mc->Lt.G == cAcc.Lt.Rgba.G && mc->Lt.B == cAcc.Lt.Rgba.B && mc->Lt.Rate == cAcc.Lt.Rate &&
                    mc->Rt.A == cAcc.Rt.Rgba.A && mc->Rt.R == cAcc.Rt.Rgba.R && mc->Rt.G == cAcc.Rt.Rgba.G && mc->Rt.B == cAcc.Rt.Rgba.B && mc->Rt.Rate == cAcc.Rt.Rate &&
                    mc->Lb.A == cAcc.Lb.Rgba.A && mc->Lb.R == cAcc.Lb.Rgba.R && mc->Lb.G == cAcc.Lb.Rgba.G && mc->Lb.B == cAcc.Lb.Rgba.B && mc->Lb.Rate == cAcc.Lb.Rate &&
                    mc->Rb.A == cAcc.Rb.Rgba.A && mc->Rb.R == cAcc.Rb.Rgba.R && mc->Rb.G == cAcc.Rb.Rgba.G && mc->Rb.B == cAcc.Rb.Rgba.B && mc->Rb.Rate == cAcc.Rb.Rate;
                UnityEngine.Debug.Assert(colorOk,
                    "RawPartColor field offsets do not match the framedata PartAttributePartColor accessor - verify BlendType@0/Lt@4/Rt@12/Lb@20/Rb@28 and U8Color A@0/R@1/G@2/B@3+Rate@4, then update the mirror struct");
            }
#endif
        }
    }
}
