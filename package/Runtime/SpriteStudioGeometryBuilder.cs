using System;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using SpriteStudio.SsRuntime;
using System.Runtime.InteropServices;

namespace SpriteStudio.Runtime
{
    internal class SpriteStudioGeometryBuilder : IDisposable
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

        // FrameData vtable offsets (parts / parts_color / draw_batches fields). Match the generated code's
        // Parts(j)=__offset(4) / PartsColor(j)=__offset(8) / DrawBatches(j)=__offset(18). Single source of truth for
        // the raw reads and the layout check (SpriteStudioRawLayoutCheck).
        internal const int PartsVtoff = 4;
        internal const int PartsColorVtoff = 8;
        internal const int DrawBatchesVtoff = 18;

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
                _player.UpdateRuntime(deltaTime);
                _player.PollEvents();
                // An event handler disposed this player: it disabled it, destroyed it with DestroyImmediate, or set
                // its asset to null.
                if (!_player._handle.IsValid) return;
            }

            drawFrame = _player.SubFrameEnabled ? _player.GetFrame() : UnityEngine.Mathf.Floor(_player.GetFrame());
            // When SubFrame is enabled the value is evaluated as a fraction, so a floating-point number that
            // overshoots the end frame (end_frame) (e.g. 59.99) is treated as outside the SSAB valid range and
            // causes every part to disappear. Clamp so it always stays within the defined frame range.
            drawFrame = UnityEngine.Mathf.Clamp(drawFrame, _player.GetAnimationSectionStart(), _player.GetAnimationSectionEnd());

            // With subFrame disabled and no effect/instance parts, if the integer frame matches the previous
            // build the SoA output is exactly identical, so skip the FFI, aggregation and Job and reuse the
            // previous frame's geometry as-is. Effects (particles advance every frame) and instances (child
            // animations advance independently) are excluded, and so is an update that moved the track
            // stack, whose overlays and fades move the pose while track 0's frame stands still.
            _frameSkipped = false;
            if (!_player.SubFrameEnabled && !_player._hasEffectParts && !_player._hasInstanceParts
                && !_player.StackMoved
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
        /// the local origin, and a stale mesh preview). The root transform (scale, flip, offset) is folded
        /// into the geometry, so a change to it calls this too.
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
            // Effect steps the parallel pass took are for this call only, whether or not it gets as far as
            // drawing them.
            int steppedEffects = _steppedEffectsStart;
            _steppedEffectsStart = -1;
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

                int drawOrderLength = frameData.DrawOrderLength;
                int partsLength = frameData.PartsLength;
                int partsColorLength = frameData.PartsColorLength;
                int shadersLength = frameData.ShadersLength;
                RawDrawBatch* drawBatches = (RawDrawBatch*)SpriteStudioNativeBuffers.RootVector(frameDataPtr, DrawBatchesVtoff, out int drawBatchesLength);
                RawPartState* rawParts = PartsOf(frameDataPtr, out _);

                SpriteStudioNativeBuffers.EnsureCapacity(ref _instanceMeshes, partsLength);

                bool parentLooped = NativeMethods.ss_runtime_just_looped(_player._handle.ptr);
                float fps = EffectFps();

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
                StepEffects(frameDataPtr, drawFrameNo, deltaTime, fps, parentLooped, steppedEffects);
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

                // Fill DrawOrder ([ushort] vector) first, so the batch aggregation loop reads from _drawOrder.
                // It is a contiguous scalar vector, so memcpy the whole thing rather than per-element (as with effect geometry).
                // draw_order is FrameData field 6 -> vtable offset 16.
                SpriteStudioNativeBuffers.CopyFbVector(tFrame, 16, frameDataPtr, _drawOrder, 0, drawOrderLength);

                // Fill PartState before the batch aggregation below (Normal's needsCenter test) reads UpdateFlag
                // from _partStates, for the parts in the draw order only: the batches and the mesh job reach a part
                // state through the draw order and nothing else, so the other entries are never read and keep
                // whatever they held.
                var pStates = (PartStateData*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_partStates);
                ushort* order = (ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder);
                if (rawParts != null)
                {
                    for (int r = 0; r < drawOrderLength; r++)
                    {
                        int i = order[r];
                        if (i >= partsLength) continue;
                        ref readonly RawPartState p = ref rawParts[i];
                        pStates[i] = new PartStateData
                        {
                            UpdateFlag = p.UpdateFlag,
                            Alpha = p.Alpha,
                            PartColorIndex = p.PartColor
                        };
                    }
                }

                // Build the per-frame mask writer list (mirrors _build_mask_writers). Behaviour-neutral
                // until the stencil submesh pipeline consumes it.
                _maskPlanner.BuildWriters(rawParts, _drawOrder, _player._hasMaskParts, _player._maskPartData, drawOrderLength, partsLength,
                    _player._inheritedMaskInfluence);

                int totalVertices = 0;
                int totalIndices = 0;

                // Mask Phase A: harvest per-part textures, then pre-write every pure mask so the stencil
                // reflects all of them before any body part is drawn. Each pure mask's coverage is
                // removed again at its own rank in the main loop (Phase B), yielding the "mask affects
                // lower-priority parts" scope.
                if (_player.MaskActive)
                {
                    _maskPlanner.BuildPartLookup(drawBatches, _drawOrder, drawBatchesLength, drawOrderLength, partsLength);
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
                    ref readonly RawDrawBatch b = ref drawBatches[i];
                    byte kind = b.Kind;

                    if (kind == SsDrawBatchKind.Instance)
                    {
                        ProcessInstanceBatch(b, partsLength, ref totalVertices, ref totalIndices);
                    }
                    else if (kind == SsDrawBatchKind.Effect)
                    {
                        ProcessEffectBatch(b, partsLength, ref totalVertices, ref totalIndices);
                    }
                    else if (kind == SsDrawBatchKind.Normal || kind == SsDrawBatchKind.Shape || kind == SsDrawBatchKind.Mesh)
                    {
                        ProcessNormalShapeMeshBatch(b, partsLength, ref frame, frameData, rawParts, shadersLength, ref totalVertices, ref totalIndices);
                    }
                    else if (kind == SsDrawBatchKind.Mask && _player.MaskActive)
                    {
                        ProcessMaskBatch(b, partsLength, ref frame, rawParts, ref totalVertices, ref totalIndices);
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
        public void ApplyToMesh(UnityEngine.Mesh targetMesh, bool perBatchSubmesh = false)
        {
            // Wait for the Job to complete before applying to the Mesh (the Job is owned by the builder).
            _updateJobHandle.Complete();

            _meshUploader.Apply(targetMesh, perBatchSubmesh, BuildUploadData());
        }

        /// <summary>
        /// The Canvas (uGUI) counterpart of <see cref="ApplyToMesh"/>: the high-level 16-bit upload that
        /// CanvasRenderer accepts (it rejects the low-level mesh on Windows/D3D12 -> device removed), with
        /// <paramref name="tint"/> (Graphic.color) as the vertex colour. See
        /// <see cref="SpriteStudioMeshUploader.ApplyCanvas"/>.
        /// </summary>
        public void ApplyToCanvasMesh(UnityEngine.Mesh targetMesh, UnityEngine.Color32 tint)
        {
            _updateJobHandle.Complete();

            _meshUploader.ApplyCanvas(targetMesh, tint, BuildUploadData());
        }

        /// <summary>
        /// Start <see cref="ApplyToCanvasMesh"/>'s conversion on a worker, behind the mesh job ConsumeFrameData
        /// scheduled, so that ApplyToCanvasMesh with the same tint only uploads. See
        /// <see cref="SpriteStudioMeshUploader.ScheduleCanvas"/>.
        /// </summary>
        internal void ScheduleCanvasConversion(UnityEngine.Mesh targetMesh, UnityEngine.Color32 tint)
        {
            _updateJobHandle = _meshUploader.ScheduleCanvas(targetMesh, tint, BuildUploadData(), _updateJobHandle);
        }

        private SpriteStudioMeshUploader.UploadData BuildUploadData() => new SpriteStudioMeshUploader.UploadData(
            _frameSkipped, _vertexCount, _indexCount, _meshVertices, _meshIndices,
            _batchBuilder.Submeshes, _batchBuilder.SubmeshCount, _topologySignature, _meshBounds);

        /// <summary>One effect slot to step, as the update manager's parallel pass hands it to its job.</summary>
        internal struct EffectStep
        {
            public System.IntPtr Slot;
            public uint PartIndex;
            public float DeltaSeconds;
            public float Fps;
        }

        // Effect steps the update manager's parallel pass took on a worker, for the next ConsumeFrameData:
        // this player's first result in _steppedEffects, or -1 to step the slots there. The worker also copied
        // the visible plans into the merged buffers, at _steppedPlans, unless they did not fit.
        private NativeArray<ss_effect_step_result> _steppedEffects;
        private NativeArray<EffectDrawPlanData> _steppedPlans;
        private SpriteStudioEffectPlanBuffer.CopyTarget _steppedCopies;
        private int _steppedEffectsStart = -1;

        /// <summary>
        /// Write this player's effect slots (<c>_effectPartCount</c> of them) at <paramref name="start"/>, as
        /// ConsumeFrameData would step them with <paramref name="deltaTime"/>.
        /// </summary>
        internal void WriteEffectSteps(float deltaTime, NativeArray<EffectStep> into, int start)
        {
            float fps = EffectFps();
            for (int k = 0; k < _player._effectPartCount; k++)
            {
                int i = _player._effectPartIndices[k];
                into[start + k] = new EffectStep
                {
                    Slot = _player._effectSlots[i].EffectSlot,
                    PartIndex = (uint)i,
                    DeltaSeconds = deltaTime,
                    Fps = fps,
                };
            }
        }

        /// <summary>
        /// The merged effect buffers, for the parallel pass's worker to copy this player's plans into as it
        /// steps <see cref="WriteEffectSteps"/>'s slots.
        /// </summary>
        internal SpriteStudioEffectPlanBuffer.CopyTarget EffectCopyTarget() => _effectBuffer.WorkerTarget();

        /// <summary>
        /// Hand over what the parallel pass got stepping <see cref="WriteEffectSteps"/>'s slots, starting at
        /// <paramref name="start"/>, and the plans it copied into <see cref="EffectCopyTarget"/>; the next
        /// ConsumeFrameData draws these instead of stepping the slots.
        /// </summary>
        internal void UseSteppedEffects(NativeArray<ss_effect_step_result> results, NativeArray<EffectDrawPlanData> plans,
            in SpriteStudioEffectPlanBuffer.CopyTarget copies, int start)
        {
            _steppedEffects = results;
            _steppedPlans = plans;
            _steppedCopies = copies;
            _steppedEffectsStart = start;
        }

        /// <summary>
        /// Step one effect slot of the player whose runtime is <paramref name="context"/>, against the parts of
        /// the frame just built (<see cref="PartsOf"/>). Both StepEffects and the parallel pass's worker make
        /// this call, so the two cannot step a slot differently.
        /// <para>
        /// A part the frame hides is not drawn -- the runtime leaves it out of the draw order -- so its slot
        /// steps without building a draw plan: the clock and the transitions still move, which is all an
        /// independently playing effect needs while hidden, and the first drawn step after it builds the plan
        /// it would have built anyway.
        /// </para>
        /// </summary>
        internal static unsafe ss_effect_step_result StepEffectSlot(void* context, in EffectStep step, float drawFrameNo, bool parentLooped,
            RawPartState* parts, int partCount)
        {
            var ev = NativeMethods.ss_runtime_get_active_effect_event(context, step.PartIndex);
            bool draw = step.PartIndex < (uint)partCount && parts[step.PartIndex].Hide == 0;
            return NativeMethods.ss_effect_slot_step((void*)step.Slot, ev, drawFrameNo, step.DeltaSeconds, step.Fps, parentLooped, false, 3, draw);
        }

        /// <summary>
        /// The parts vector of a FrameData buffer, read straight from the bytes as the fill in
        /// ConsumeFrameData reads it (root table, then field <see cref="PartsVtoff"/>), so a worker can read it
        /// without a managed ByteBuffer. Null with a zero count when the frame carries none.
        /// </summary>
        internal static unsafe RawPartState* PartsOf(byte* frameData, out int count)
        {
            count = 0;
            if (frameData == null) return null;
            return (RawPartState*)SpriteStudioNativeBuffers.RootVector(frameData, PartsVtoff, out count);
        }

        // The frame rate effects step at: the animation's, or 60 when it reports none.
        private float EffectFps()
        {
            float fps = _player.GetFrameRate();
            return fps > 0f ? fps : 60f;
        }

        /// <summary>
        /// Step the active effect slots -- or take the steps the parallel pass took, from
        /// <paramref name="steppedEffects"/> on -- and append the visible ones' draw_plan into the merged buffer,
        /// unless the parallel pass copied them there already.
        /// </summary>
        private unsafe void StepEffects(byte* frameDataPtr, float drawFrameNo, float deltaTime, float fps, bool parentLooped, int steppedEffects)
        {
            if (steppedEffects >= 0 && _steppedCopies.Fits)
            {
                _effectBuffer.TakeCopied(_steppedCopies);
                for (int k = 0; k < _player._effectPartCount; k++)
                    _effectBuffer.SetPlan(_player._effectPartIndices[k], _steppedPlans[steppedEffects + k]);
                return;
            }
            // Only the serial path steps here; the parallel pass read the parts on its worker.
            int partCount = 0;
            RawPartState* parts = steppedEffects >= 0 ? null : PartsOf(frameDataPtr, out partCount);
            for (int k = 0; k < _player._effectPartCount; k++)
            {
                int i = _player._effectPartIndices[k];
                var step = steppedEffects >= 0
                    ? _steppedEffects[steppedEffects + k]
                    : StepEffectSlot(_player._handle.ptr, new EffectStep
                    {
                        Slot = _player._effectSlots[i].EffectSlot,
                        PartIndex = (uint)i,
                        DeltaSeconds = deltaTime,
                        Fps = fps,
                    }, drawFrameNo, parentLooped, parts, partCount);
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

            // Whether a child may be drawn under coverage written outside its own asset: this player's own
            // masks, coverage that already reaches this player, or a clipping mask in any instance here -
            // an earlier one's clip stays open for the instances drawn after it.
            bool outerCoverage = _player._instancePartCount > 0
                && (_player._outerMaskCoverage || _player._hasMaskParts || _player.SubtreeHasClippingParts);

            for (int k = 0; k < _player._instancePartCount; k++)
            {
                int i = _player._instancePartIndices[k];
                var childPlayer = _player._instanceChildren[i].Player;
                if (childPlayer == null) continue; // _instanceMeshes[i] stays at the MemClear'd default

                var ev = NativeMethods.ss_runtime_get_active_event_instance(_player._handle.ptr, (uint)i);
                var step = NativeMethods.ss_instance_slot_step((void*)_player._instanceChildren[i].InstanceSlot, ev, childPlayer._handle.ptr, drawFrameNo, deltaTime, parentLooped);

                if (step.transitioned)
                {
                    // Starts the child. ss_instance_slot_step configured it for the new key
                    // (section, direction, loop count) but does not play it, and an independent
                    // child never advances until it plays.
                    childPlayer.Play();
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
                    PropagateMaskContext(i, childPlayer, outerCoverage);
                    childPlayer.SetFrame(step.child_frame_no);
                    // The slot advanced the child at its key's speed, so the child's own independent
                    // instances and effects must too, or they drift from it. Read the speed the slot used,
                    // not ev.speed: while the slot ignores the implicit frame-0 key ev says 1.0 but the
                    // child keeps running at the last real key's speed.
                    float childDelta = deltaTime * UnityEngine.Mathf.Abs(
                        NativeMethods.ss_instance_slot_get_speed((void*)_player._instanceChildren[i].InstanceSlot));
                    childPlayer.ScheduleMeshUpdate(childDelta);

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
        /// Hand an instance child the mask context it is drawn in, before the child builds: the flags of
        /// the instance part that draws it (SpriteStudioInternalPlayer.ComposeInheritedContext - one level,
        /// always, whether or not this player is masked), and whether coverage from outside the child may
        /// be live (<paramref name="outerCoverage"/>). A part index that resolves to no mask data leaves
        /// the flags at the identity context.
        /// </summary>
        private void PropagateMaskContext(int instancePartIndex, SpriteStudioInternalPlayer childPlayer, bool outerCoverage)
        {
            bool influence = true, visibleInside = false;
            var md = _player._maskPartData;
            if (md != null && instancePartIndex >= 0 && instancePartIndex < md.Length)
            {
                SpriteStudioInternalPlayer.ComposeInheritedContext(md[instancePartIndex],
                    out influence, out visibleInside);
            }
            childPlayer._outerMaskCoverage = outerCoverage;
            childPlayer._inheritedMaskInfluence = influence;
            childPlayer._inheritedVisibleInsideMask = visibleInside;
        }

        /// <summary>
        /// Effective mask targeting for one of this player's parts, composed against the flags of the
        /// instance part that draws this player (see <see cref="PropagateMaskContext"/>). A root player's
        /// inherited values are the identity elements, so this reduces to the part's own setting.
        /// <paramref name="comp"/> receives the stencil compare for the resulting polarity.
        /// </summary>
        private bool MaskTargetOf(in SpriteStudioInternalPlayer.MaskPartData md, out byte comp)
        {
            comp = SpriteStudioInternalPlayer.IsDrawnInsideMask(md, _player._inheritedVisibleInsideMask)
                ? StencilCompNotEqual : StencilCompEqual;
            return SpriteStudioInternalPlayer.IsMaskTarget(md, _player._inheritedMaskInfluence);
        }

        private unsafe void ProcessInstanceBatch(in RawDrawBatch b, int partsLength, ref int totalVertices, ref int totalIndices)
        {
            ushort* drawOrder = (ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder);
            int startRank = b.StartRank, count = b.Count;
            for (int k = 0; k < count; k++)
            {
                int rank = startRank + k;
                int p_idx = drawOrder[rank];
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

        private unsafe void ProcessEffectBatch(in RawDrawBatch b, int partsLength, ref int totalVertices, ref int totalIndices)
        {
            ushort* drawOrder = (ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder);
            int startRank = b.StartRank, count = b.Count;
            for (int k = 0; k < count; k++)
            {
                int rank = startRank + k;
                int p_idx = drawOrder[rank];
                if (p_idx < 0 || p_idx >= partsLength) continue;
                
                var plan = _effectBuffer.GetPlan(p_idx);
                if (plan.CommandCount == 0) continue;

                StencilRole effRole = StencilRole.None;
                byte effComp = 0;
                // Same gate as the per-part path: an instance child earlier this frame may have left a
                // clip open (_instanceCoverageOpen) even when this player has no masks of its own.
                if ((_player.MaskActive || _instanceCoverageOpen) && !_player._suppressInternalMask && _player._maskPartData != null)
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

        private unsafe void ProcessNormalShapeMeshBatch(in RawDrawBatch b, int partsLength, ref DrawFrame frame, ss.runtime.FrameData frameData,
            RawPartState* rawParts, int shadersLength, ref int totalVertices, ref int totalIndices)
        {
            int vc = 0;
            int ic = 0;
            ushort* drawOrder = (ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder);
            int startRank = b.StartRank, count = b.Count;
            byte kind = b.Kind, blendType = b.BlendType;
            uint textureHash = b.TextureHash;

            // Also taken when this player has no mask parts of its own but some part still needs a stencil
            // test (MaskAffectsParts: coverage from outside may reach it, or it draws inside the mask and
            // must be hidden with no mask over it), or a visible instance child EARLIER this frame left an open
            // clip that carries up (_instanceCoverageOpen): those parts still need one submesh each to
            // carry their own stencil role. The instance batch is processed before this one (draw order),
            // so parts drawn before any such instance correctly see the flag still false.
            if ((_player.MaskAffectsParts || _instanceCoverageOpen) && _player._maskPartData != null)
            {
                for (int k = 0; k < count; k++)
                {
                    int rank = startRank + k;
                    int p_idx = drawOrder[rank];
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
                        _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, textureHash, blendType,
                            StencilRole.None, 0, 0, 0f, ref totalVertices, ref totalIndices);
                        continue;
                    }
                    // A pure mask never draws a colour, and its removal (the Phase B half of the Phase A/B
                    // pair) is emitted from its Mask batch twin at the same rank (ProcessMaskBatch), so its
                    // colour batch contributes nothing here.
                    if (md.PureMask) continue;
                    if (MaskTargetOf(md, out byte comp))
                    {
                        _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, textureHash, blendType,
                            StencilRole.Test, 0, comp, 0f, ref totalVertices, ref totalIndices);
                    }
                    else
                    {
                        _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, textureHash, blendType,
                            StencilRole.None, 0, 0, 0f, ref totalVertices, ref totalIndices);
                    }
                    // Its own colour is already emitted above, whatever the mask says: a clipping part
                    // draws either way, and only the stencil write is switched off. Phase C closes this
                    // write off the writer list, so both halves have to agree on which writers are live.
                    if (md.IsClipping && _maskPlanner.IsWriterActive(p_idx))
                    {
                        // The op/plane follows the writer's EFFECTIVE maskInfluence (own AND the instance
                        // part's above), so a mask directly inside a maskInfluence=0 instance lands on the
                        // counter plane (union) instead of the parity plane (XOR) - SS7.5's mixed-influence union.
                        byte wop = SpriteStudioInternalPlayer.EffectiveMaskInfluence(md, _player._inheritedMaskInfluence)
                            ? StencilOpInvert : StencilOpIncr;
                        float th = kind == SsDrawBatchKind.Shape ? -1f : (255f - rawParts[p_idx].Mask) / 255f;
                        _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, textureHash, blendType,
                            StencilRole.Write, wop, 0, th, ref totalVertices, ref totalIndices);
                    }
                }
                return;
            }

            // Per-part AddOn shader dispatch (non-mask path). When the frame carries shader attributes
            // and a catalog is bound, parts whose shader id resolves to an is-per-part AddOn are carved
            // out as their own Count=1 submesh (stamped with the shader id hash); the rest accumulate
            // into shared rank-runs, mirroring the mask per-part split. Vertex order is unchanged, so the
            // mesh job fills exactly the same geometry — only the submesh granularity differs.
            if (shadersLength > 0)
            {
                // Built-in AddOns resolve with no project setup; the (optional) user catalog overrides.
                var userCatalog = _player.ShaderCatalog;
                int runStart = startRank;   // first rank of the current shared run (rank-span based)
                int runVc = 0, runIc = 0;
                for (int k = 0; k < count; k++)
                {
                    int rank = startRank + k;
                    int p_idx = drawOrder[rank];
                    int pvc = 0, pic = 0;
                    bool valid = p_idx >= 0 && p_idx < partsLength
                                 && _maskPlanner.MaskPartVcIc(p_idx, kind, frame, _partStates, out pvc, out pic);
                    if (!valid) continue; // skipped parts add no geometry; their rank stays in the run span

                    uint shaderHash = 0;
                    float ap0 = 0, ap1 = 0, ap2 = 0, ap3 = 0, ap4 = 0, ap5 = 0, ap6 = 0, ap7 = 0;
                    int sIdx = rawParts[p_idx].Shader;
                    if (sIdx >= 0 && sIdx < shadersLength)
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

                    if (shaderHash != 0)
                    {
                        // Flush the pending shared run [runStart, rank), then emit this part on its own.
                        int runCount = rank - runStart;
                        if (runCount > 0 && runIc > 0)
                            _batchBuilder.EmitDrawBatch((ushort)runStart, (ushort)runCount, runVc, runIc, kind, textureHash, blendType, 0u, ref totalVertices, ref totalIndices);
                        _batchBuilder.EmitDrawBatch((ushort)rank, 1, pvc, pic, kind, textureHash, blendType, shaderHash, ref totalVertices, ref totalIndices);
                        _batchBuilder.SetLastSubmeshAddOnParams(ap0, ap1, ap2, ap3, ap4, ap5, ap6, ap7);
                        // Cell rectangle (pixel UVs) for cell-rect AddOns (ss-circle / ss-spot). Only Normal
                        // parts carry a cell quad in LocalUvs (layout [lt.u,lt.v, rt, lb, rb.u@6,rb.v@7, center]);
                        // Shape/Mesh leave it 0 so those AddOns degenerate (discard).
                        if (kind == SsDrawBatchKind.Normal && frame.LocalUvs != null && (p_idx * 10 + 7) < (int)frame.LocalUvsLen)
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
                int tailCount = (startRank + count) - runStart;
                if (tailCount > 0 && runIc > 0)
                    _batchBuilder.EmitDrawBatch((ushort)runStart, (ushort)tailCount, runVc, runIc, kind, textureHash, blendType, 0u, ref totalVertices, ref totalIndices);
                return;
            }

            PartStateData* partStates = (PartStateData*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_partStates);
            for (int k = 0; k < count; k++)
            {
                int p_idx = drawOrder[startRank + k];
                if (p_idx < 0 || p_idx >= partsLength) continue;

                if (kind == SsDrawBatchKind.Normal)
                {
                    bool needsCenter = (partStates[p_idx].UpdateFlag & SsUpdateFlag.NeedsCenterMask) != 0;
                    vc += needsCenter ? 5 : 4;
                    ic += needsCenter ? 12 : 6;
                }
                else if (kind == SsDrawBatchKind.Shape)
                {
                    int svc = frame.ShapeVertexCounts[p_idx];
                    if (svc >= 3)
                    {
                        vc += svc;
                        ic += (svc - 2) * 3;
                    }
                }
                else if (kind == SsDrawBatchKind.Mesh)
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
                _batchBuilder.EmitDrawBatch((ushort)startRank, (ushort)count, vc, ic, kind, textureHash, blendType, 0u, ref totalVertices, ref totalIndices);
            }
        }

        private unsafe void ProcessMaskBatch(in RawDrawBatch b, int partsLength, ref DrawFrame frame, RawPartState* rawParts, ref int totalVertices, ref int totalIndices)
        {
            ushort* drawOrder = (ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder);
            int startRank = b.StartRank, count = b.Count;
            for (int k = 0; k < count; k++)
            {
                int rank = startRank + k;
                int p_idx = drawOrder[rank];
                if (p_idx < 0 || p_idx >= partsLength) continue;
                var md = _player._maskPartData[p_idx];
                // Every pure mask has a Mask batch at its own rank: the only batch of a Mask part, and the
                // twin the runtime emits ahead of a draw_as_mask part's colour batch (kept even when that
                // part is hidden). Removing here rather than at the colour batch covers every kind alike -
                // a Nines draw_as_mask has no colour path in this builder, so Phase A's write used to stay
                // set past its rank.
                if (!md.PureMask) continue;
                // Phase B: the removal that pairs with Phase A's write, so a writer Phase A left out -
                // switched off with mask == 0, or hidden - must be left out here too.
                if (!_maskPlanner.IsWriterActive(p_idx)) continue;
                if (!_maskPlanner.MaskPartVcIc(p_idx, md.DrawKind, frame, _partStates, out int mvc, out int mic)) continue;
                // Op/plane follows the effective maskInfluence (own AND the instance part's above).
                byte rop = SpriteStudioInternalPlayer.EffectiveMaskInfluence(md, _player._inheritedMaskInfluence) ? StencilOpInvert : StencilOpDecr;
                // Same geometry, texture and threshold as the Phase A write it undoes.
                float th = md.DrawKind == SsDrawBatchKind.Shape ? -1f : (255f - rawParts[p_idx].Mask) / 255f;
                _batchBuilder.EmitMaskStep(md.DrawKind, rank, mvc, mic, _maskPlanner.GetPartTexHash(p_idx), _maskPlanner.GetPartBlend(p_idx),
                    StencilRole.Write, rop, 0, th, ref totalVertices, ref totalIndices);
            }
        }

        
    }

    // === Raw layout mirrors for reading FlatBuffer fixed struct-vectors directly, without a per-element accessor ===
    // Byte-compatible with framedata_generated.cs's PartState(144B) / PartAttributePartColor(36B) / DrawBatch(16B) / U8Rect.
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
        [FieldOffset(108)] public float Mask;
        [FieldOffset(128)] public short PartColor;
        [FieldOffset(130)] public short Shader;
        [FieldOffset(138)] public byte Hide;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct RawDrawBatch   // DrawBatch (VertexCount / IndexCount / Flags are not read)
    {
        [FieldOffset(0)]  public ushort StartRank;
        [FieldOffset(2)]  public ushort Count;
        [FieldOffset(4)]  public uint TextureHash;
        [FieldOffset(12)] public byte BlendType;
        [FieldOffset(13)] public byte Kind;
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
            UnityEngine.Debug.Assert(Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<RawDrawBatch>() == 16,
                "RawDrawBatch size != 16: framedata DrawBatch layout changed - update the mirror struct");

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
                0,0,0,0,0, 0,0,0,0,0, 0,0,0,0,                         // LocalAlpha..BoundingRadius
                0.25f,                                                 // Mask (sentinel)
                0,0,                                                   // SkewX, SkewY
                0,                                                     // Texture
                0,0,                                                   // PartIndex, Cell
                (short)0x4321,                                         // PartColor (sentinel)
                (short)0x1357,                                         // Shader (sentinel)
                0,0,                                                   // Vertex, Deform
                false,false,true,false,false);                        // FlipH, FlipV, Hide (sentinel), ImgFlipH, ImgFlipV
            var partsVec = b.EndVector();

            ss.runtime.FrameData.StartPartsColorVector(b, 1);
            ss.runtime.PartAttributePartColor.CreatePartAttributePartColor(b,
                (ss.runtime.BlendType)3,
                1,2,3,4, 0.5f,      // Lt: A,R,G,B,Rate
                5,6,7,8, 1.5f,      // Rt
                9,10,11,12, 2.5f,   // Lb
                13,14,15,16, 3.5f); // Rb
            var pcVec = b.EndVector();

            // Two sentinel batches, so the stride is checked too.
            ss.runtime.FrameData.StartDrawBatchesVector(b, 2);
            ss.runtime.DrawBatch.CreateDrawBatch(b, 0x0102, 0x0304, 0x05060708u, 0x090A, 0x0B0C,
                ss.runtime.BlendType.Screen, ss.runtime.DrawBatchKind.Mask, 0x0D);
            ss.runtime.DrawBatch.CreateDrawBatch(b, 0x1112, 0x1314, 0x15161718u, 0x191A, 0x1B1C,
                ss.runtime.BlendType.Sub, ss.runtime.DrawBatchKind.Mesh, 0x1D);
            var drawBatchesVec = b.EndVector();

            // Satisfy FrameData's other required fields (draw_order / mask_indices) with empty vectors
            // (EndFrameData requires their presence; the contents are unused in this check).
            ss.runtime.FrameData.StartDrawOrderVector(b, 0);
            var drawOrderVec = b.EndVector();
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
                    mp->UpdateFlag == (ulong)pAcc.UpdateFlag && mp->Alpha == pAcc.Alpha && mp->Mask == pAcc.Mask
                    && mp->PartColor == pAcc.PartColor && mp->Shader == pAcc.Shader && (mp->Hide != 0) == pAcc.Hide,
                    "RawPartState field offsets do not match the framedata PartState accessor - verify UpdateFlag@0/Alpha@48/Mask@108/PartColor@128/Shader@130/Hide@138 and update the mirror struct");

                RawPartColor* mc = (RawPartColor*)(basePtr + t.__vector(t.__offset(SpriteStudioGeometryBuilder.PartsColorVtoff)));
                bool colorOk =
                    mc->BlendType == (byte)cAcc.BlendType &&
                    mc->Lt.A == cAcc.Lt.Rgba.A && mc->Lt.R == cAcc.Lt.Rgba.R && mc->Lt.G == cAcc.Lt.Rgba.G && mc->Lt.B == cAcc.Lt.Rgba.B && mc->Lt.Rate == cAcc.Lt.Rate &&
                    mc->Rt.A == cAcc.Rt.Rgba.A && mc->Rt.R == cAcc.Rt.Rgba.R && mc->Rt.G == cAcc.Rt.Rgba.G && mc->Rt.B == cAcc.Rt.Rgba.B && mc->Rt.Rate == cAcc.Rt.Rate &&
                    mc->Lb.A == cAcc.Lb.Rgba.A && mc->Lb.R == cAcc.Lb.Rgba.R && mc->Lb.G == cAcc.Lb.Rgba.G && mc->Lb.B == cAcc.Lb.Rgba.B && mc->Lb.Rate == cAcc.Lb.Rate &&
                    mc->Rb.A == cAcc.Rb.Rgba.A && mc->Rb.R == cAcc.Rb.Rgba.R && mc->Rb.G == cAcc.Rb.Rgba.G && mc->Rb.B == cAcc.Rb.Rgba.B && mc->Rb.Rate == cAcc.Rb.Rate;
                UnityEngine.Debug.Assert(colorOk,
                    "RawPartColor field offsets do not match the framedata PartAttributePartColor accessor - verify BlendType@0/Lt@4/Rt@12/Lb@20/Rb@28 and U8Color A@0/R@1/G@2/B@3+Rate@4, then update the mirror struct");

                // Read through the same root-vector reader ConsumeFrameData uses.
                RawDrawBatch* mb = (RawDrawBatch*)SpriteStudioNativeBuffers.RootVector(basePtr, SpriteStudioGeometryBuilder.DrawBatchesVtoff, out int mbCount);
                bool batchOk = mbCount == 2 && mbCount == fd.DrawBatchesLength;
                for (int i = 0; batchOk && i < mbCount; i++)
                {
                    var bAcc = fd.DrawBatches(i).Value;
                    batchOk = mb[i].StartRank == bAcc.StartRank && mb[i].Count == bAcc.Count && mb[i].TextureHash == bAcc.TextureHash
                        && mb[i].BlendType == (byte)bAcc.BlendType && mb[i].Kind == (byte)bAcc.Kind;
                }
                UnityEngine.Debug.Assert(batchOk,
                    "RawDrawBatch does not match the framedata DrawBatch accessor - verify draw_batches@18 and StartRank@0/Count@2/TextureHash@4/BlendType@12/Kind@13, stride 16, then update the mirror struct");
            }

            // (3) The same roundtrip for the EffectDrawPlan SpriteStudioEffectPlanBuffer.PlanView reads raw: two
            //     sentinel commands (their stride too) and a different length for every vector.
            var pb = new Google.FlatBuffers.FlatBufferBuilder(256);
            var vertsVec = ss.runtime.EffectDrawPlan.CreateVertsVector(pb, new[] { 1f, 2f });
            var uvsVec = ss.runtime.EffectDrawPlan.CreateUvsVector(pb, new[] { 3f, 4f, 5f });
            var colorsVec = ss.runtime.EffectDrawPlan.CreateColorsVector(pb, new[] { 6f, 7f, 8f, 9f });
            var indicesVec = ss.runtime.EffectDrawPlan.CreateIndicesVector(pb, new[] { 10, 11, 12, 13, 14 });
            ss.runtime.EffectDrawPlan.StartCommandsVector(pb, 2);
            ss.runtime.EffectDrawCommand.CreateEffectDrawCommand(pb, 0x55667788u, 0x0E0F1011u, 0x12131415u, 0);
            ss.runtime.EffectDrawCommand.CreateEffectDrawCommand(pb, 0x11223344u, 0x0A0B0C0Du, 0x01020304u, 1);
            var commandsVec = pb.EndVector();
            ss.runtime.EffectDrawPlan.StartEffectDrawPlan(pb);
            ss.runtime.EffectDrawPlan.AddCommands(pb, commandsVec);
            ss.runtime.EffectDrawPlan.AddVertStride(pb, 2);
            ss.runtime.EffectDrawPlan.AddVerts(pb, vertsVec);
            ss.runtime.EffectDrawPlan.AddUvs(pb, uvsVec);
            ss.runtime.EffectDrawPlan.AddColors(pb, colorsVec);
            ss.runtime.EffectDrawPlan.AddIndices(pb, indicesVec);
            pb.Finish(ss.runtime.EffectDrawPlan.EndEffectDrawPlan(pb).Value);

            byte[] planArr = pb.DataBuffer.ToSizedArray();
            var plan = ss.runtime.EffectDrawPlan.GetRootAsEffectDrawPlan(new Google.FlatBuffers.ByteBuffer(planArr));
            fixed (byte* planPtr = planArr)
            {
                var view = new SpriteStudioEffectPlanBuffer.PlanView(planPtr);
                bool planOk = view.CommandCount == plan.CommandsLength && view.VertCount == plan.VertsLength
                    && view.UvCount == plan.UvsLength && view.ColorCount == plan.ColorsLength && view.IndexCount == plan.IndicesLength
                    && view.Verts[1] == plan.Verts(1) && view.Uvs[2] == plan.Uvs(2) && view.Colors[3] == plan.Colors(3)
                    && view.Indices[4] == plan.Indices(4);
                for (int c = 0; planOk && c < view.CommandCount; c++)
                {
                    byte* mcmd = view.Commands + c * SpriteStudioEffectPlanBuffer.PlanView.CommandSize;
                    var cmd = plan.Commands(c).Value;
                    planOk = *(uint*)mcmd == cmd.CellmapHash && *(uint*)(mcmd + 4) == cmd.QuadCount
                        && *(uint*)(mcmd + 8) == cmd.ParticleOffset && mcmd[12] == cmd.Blend;
                }
                UnityEngine.Debug.Assert(planOk,
                    "SpriteStudioEffectPlanBuffer.PlanView does not match the EffectDrawPlan accessor - verify the vtable offsets commands@4/verts@8/uvs@10/colors@12/indices@14 and EffectDrawCommand CellmapHash@0/QuadCount@4/ParticleOffset@8/Blend@12, stride 16");
            }
#endif
        }
    }
}
