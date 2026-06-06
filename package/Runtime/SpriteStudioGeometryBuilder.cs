using System;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    public class SpriteStudioGeometryBuilder : IDisposable
    {
        private readonly SpriteStudioInternalPlayer _player;

        // Moved from InternalPlayer
        
        // --- Effect: draw_plan の読み取りと結合ジオメトリは EffectPlanBuffer が所有する ---
        private readonly SpriteStudioEffectPlanBuffer _effectBuffer = new SpriteStudioEffectPlanBuffer();

        // effect/instance スロットを持つパーツ index の疎なリスト (SetupSlots で確定)。

        // --- Mask: writer 一覧 / パーツ毎 (texHash,blend) / vc-ic 算出は MaskPlanner が所有する ---
        // (実際の draw batch / submesh 発行 = EmitMaskStep / Phase A/B は共有状態を触るためビルダーに残す)
        private readonly SpriteStudioMaskPlanner _maskPlanner = new SpriteStudioMaskPlanner();
        // --- Instance ---
internal NativeArray<InstanceMeshPointer> _instanceMeshes;
        // Instance 子の UpdateMeshJob ハンドルを束ねる依存配列。永続 grow-only (A3: 毎フレ Temp 確保を回避)。
        internal NativeArray<JobHandle> _childHandles;
        public float _lastBuiltDrawFrameNo = float.NaN;
        public float _pendingDrawFrame;
        public bool _frameSkipped;
        public JobHandle _updateJobHandle;

        public JobHandle CurrentUpdateJobHandle => _updateJobHandle;

        public SpriteStudioGeometryBuilder(SpriteStudioInternalPlayer player)
        {
            _player = player;
        }

        public void Dispose()
        {
            _updateJobHandle.Complete();
            _effectBuffer.Dispose();
            if (_instanceMeshes.IsCreated) _instanceMeshes.Dispose();
            if (_childHandles.IsCreated) _childHandles.Dispose();
            if (_meshVertices.IsCreated) _meshVertices.Dispose();
            if (_meshIndices.IsCreated) _meshIndices.Dispose();
            if (_drawBatches.IsCreated) _drawBatches.Dispose();
            if (_drawOrder.IsCreated) _drawOrder.Dispose();
            if (_partStates.IsCreated) _partStates.Dispose();
            if (_partColors.IsCreated) _partColors.Dispose();
            if (_meshBounds.IsCreated) _meshBounds.Dispose();
            if (_submeshes.IsCreated) _submeshes.Dispose();
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
#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        // ゼロコピー: per-context の frame_data ネイティブバッファを直接 ByteBuffer に載せる。
        private NativeByteBufferAllocator _frameDataAllocator;
#else
        private byte[] _frameDataArray;
#endif

        // ゼロアロケーションのため、Job 入出力バッファは永続確保し grow-only で再利用する。
        // NativeArray.Length は確保容量、論理件数は別途 *_Count で保持する。
        private NativeArray<SsVertex> _meshVertices;
        // インデックスは 32bit。エフェクトの大量パーティクルや mesh/instance 合算で
        // 頂点数が 65535 を超えても破綻しないようにする (ApplyToMesh は IndexFormat.UInt32)。
        private NativeArray<uint> _meshIndices;
        private NativeArray<DrawBatchData> _drawBatches;
        private NativeArray<ushort> _drawOrder;
        private NativeArray<PartStateData> _partStates;
        private NativeArray<PartColorData> _partColors;

        private int _vertexCount;
        private int _indexCount;
        private int _drawBatchCount;
        private int _partCount;
        private int _partColorCount;

        // index アップロードのキャッシュ判定に使うトポロジシグネチャ (ConsumeFrameData が算出)。
        // 実際の Mesh アップロードと再送スキップのキャッシュ状態は SpriteStudioMeshUploader が所有する。
        private ulong _topologySignature;
        private readonly SpriteStudioMeshUploader _meshUploader = new SpriteStudioMeshUploader();

        // バッチ単位の SubMesh 情報 (MeshRenderer 経路のマルチマテリアル用)。
        // Job では使わず main thread でのみ参照するため managed 配列で保持する。
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

        // Append a submesh, dynamically merging into the previous one to enable batching (origin/main
        // perf work). Merging is restricted to plain colour submeshes (Role None) of the same texture
        // + blend: mask submeshes carry per-submesh stencil state and a strict draw order, so they are
        // never merged. Auto-grows the buffer.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private unsafe void EmitSubmesh(int indexStart, int indexCount, uint textureHash, byte blendType,
            StencilRole role = StencilRole.None, byte stencilOp = 0, byte stencilComp = 0, byte stencilRef = 0,
            byte stencilReadMask = 0, byte stencilWriteMask = 0, float maskThreshold = 0f)
        {
            if (role == StencilRole.None && _submeshCount > 0)
            {
                var pSub = (SubmeshInfo*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_submeshes);
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
            var ptr = (SubmeshInfo*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_submeshes);
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
        private NativeArray<SubmeshInfo> _submeshes;
        private int _submeshCount;
        // メッシュ境界 (要素 0=min, 1=max)。UpdateMeshJob が書き込み、MeshUploader が Mesh.bounds へ適用する。
        private NativeArray<Vector3> _meshBounds;

        public int VertexCount => _vertexCount;
        public int IndexCount => _indexCount;
        public int DrawBatchCount => _drawBatchCount;
        public int SubmeshCount => _submeshCount;
        public NativeArray<SubmeshInfo> Submeshes => _submeshes;
        public NativeArray<DrawBatchData> DrawBatches => _drawBatches;
        public NativeArray<SsVertex> MeshVertices => _meshVertices;
        public NativeArray<uint> MeshIndices => _meshIndices;

        /// <summary>直近の ScheduleMeshUpdate で生成された描画バッチ (= SubMesh) の数。</summary>
        // public int SubmeshCount => _submeshCount;

        /// <summary>指定 SubMesh のテクスチャハッシュ (DrawBatch.TextureHash)。</summary>
        public uint GetSubmeshTextureHash(int index) => _submeshes[index].TextureHash;

        /// <summary>SubMesh's ssab BlendType (0..7), used to resolve the per-blend material variant.</summary>
        public byte GetSubmeshBlendType(int index) => _submeshes[index].BlendType;

        /// <summary>
        /// 前フレームの Job 完了を待ち、advance 指定時は 1 フレーム進め、
        /// 描画フレーム番号と frame-skip を確定する。out handle が Zero のときは、このフレームは
        /// このプレイヤーを並列 get_frame_data / ConsumeFrameData の対象から外す (ハンドル無効、または skip)。
        /// advance=false は frame 駆動 (子 Instance / Timeline 制御) 用。
        /// </summary>
        public void PrepareFrameData(float deltaTime, bool advance, out System.IntPtr handle, out float drawFrame)
        {
            handle = System.IntPtr.Zero;
            drawFrame = 0f;
            if (!_player._handle.IsValid) return;

            // 永続バッファを書き換える前に、前フレームのジョブ完了を保証する
            _updateJobHandle.Complete();

            // ss_runtime_update は delta を「ミリ秒」で受け取る (update_interval = 1000/fps)。
            if (advance)
            {
                unsafe { NativeMethods.ss_runtime_update(_player._handle.ptr, deltaTime * 1000.0f); }
            }

            drawFrame = _player.SubFrameEnabled ? _player.GetFrame() : UnityEngine.Mathf.Floor(_player.GetFrame());
            // SubFrame 有効時は小数のまま評価するため、終端フレーム (end_frame) を超過した浮動小数点数
            // (例: 59.99) が渡されると、SSABの有効範囲外として扱われ、全てのパーツが消える原因になります。
            // 常に定義されたフレーム範囲内に収まるようクランプします。
            drawFrame = UnityEngine.Mathf.Clamp(drawFrame, _player.GetAnimationSectionStart(), _player.GetAnimationSectionEnd());

            // subFrame 無効・エフェクト/インスタンス無しで整数フレームが前回ビルドと同一なら、SoA 出力は
            // 完全に同一になる。FFI・集計・Job を省いて前フレームのジオメトリをそのまま再利用する。
            // エフェクトは粒子が毎フレーム進み、インスタンスは子アニメが独立に進むため除外する。
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
        /// 直列の便宜ラッパ。advance せず get_frame_data を同期取得してメッシュ構築まで 1 メソッドで行う。
        /// 子 Instance (frame 駆動) と編集モードプレビューが使う。Play モードのトップレベルは
        /// SpriteStudioUpdateManager が PrepareFrameData → 並列 get_frame_data → ConsumeFrameData に分割する。
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
        /// 並列 GetFrameDataJob が取得した frame-data ポインタ (job 出力) を消費し、
        /// SoA を読み出してメッシュ構築 Burst Job を発行する。ptr==Zero / len==0 はジオメトリ無しとして扱う。
        /// </summary>
        public void ConsumeFrameData(System.IntPtr frameDataPtrRaw, int len, float deltaTime)
        {
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
                // ゼロコピー: per-context の frame_data ネイティブバッファを直接 ByteBuffer に載せる。
                // 読み取りは本メソッド内 (同期) で完結する。子の get_frame_data は別 context の別バッファに
                // 書くので親バッファを汚さない (SDK: get_frame_data は &mut self 借用 = context 毎)。
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
                // ゼロGC: 既存のbyte[]にポインタからメモリコピー (フォールバック)
                System.Runtime.InteropServices.Marshal.Copy((System.IntPtr)frameDataPtr, _frameDataArray, 0, len);
                _frameDataByteBuffer.Position = 0; // Reset ByteBuffer offset
#endif

                var frameData = ss.runtime.FrameData.GetRootAsFrameData(_frameDataByteBuffer);

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

                bool parentLooped = NativeMethods.ss_runtime_is_looped(_player._handle.ptr);
                float fps = _player.GetFrameRate();
                if (fps <= 0f) fps = 60f;

                // エフェクト plan テーブルの確保・default クリアと結合バッファの件数リセットをまとめて行う。
                _effectBuffer.BeginFrame(partsLength);

                // active な instance パーツだけを sparse に処理する (index は SetupSlots で確定)。非 active 部は
                // 会計ループ・Burst Job から default が読まれ得るため、事前に一括 MemClear しておく
                // (partsLength 個別 default 書込みより遥かに安く、「どのパーツが読まれるか」に依存せず安全)。
                if (partsLength > 0)
                {
                    Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(
                        Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_instanceMeshes),
                        (long)partsLength * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<InstanceMeshPointer>());
                }

                // active な effect/instance スロットを step し、結合バッファ / 子メッシュポインタを充填する。
                StepEffects(drawFrameNo, deltaTime, fps, parentLooped);
                int childHandleCount = StepInstances(drawFrameNo, deltaTime, parentLooped, partsLength);

                // maxBatches も active な effect/instance パーツのみ集計 (非 active は default=0寄与なので不要)。
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
                        // Instance は子メッシュ全体を 1 draw batch として取り込むが、テクスチャ単位の SubMesh は
                        // 子の submesh 数ぶん発生する。submesh 数 + 1 (draw batch ぶん) を見込む。
                        maxBatches += _player._instanceChildren[i].Player.SubmeshCount + 1;
                    }
                }

                // Masking emits per-part submeshes (no batch merge) plus extra write/restore steps, so
                // grow the batch/submesh budget. Each part contributes up to ~2 steps (test+write or
                // colour-suppressed restore); the pre-write phase adds at most one per writer.
                if (_player._hasMaskParts) maxBatches += partsLength * 2 + _maskPlanner.WriterCount;

                // 永続バッファを grow-only で確保 (毎フレーム new しない)
                SpriteStudioNativeBuffers.EnsureCapacity(ref _drawBatches, maxBatches);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _drawOrder, drawOrderLength);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _partStates, partsLength);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _partColors, partsColorLength);

                _drawBatchCount = 0; // Will be incremented dynamically
                _partCount = partsLength;
                _partColorCount = partsColorLength;

                // PartState を先に充填し、下のバッチ集計 (Normal の needsCenter 判定) が
                // UpdateFlag を FlatBuffer から再デコードせず _partStates から読めるようにする。
                var pStates = (PartStateData*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_partStates);
                for (int i = 0; i < partsLength; i++)
                {
                    var p = frameData.Parts(i).Value;
                    pStates[i] = new PartStateData
                    {
                        UpdateFlag = (ulong)p.UpdateFlag,
                        Alpha = p.Alpha,
                        PartColorIndex = p.PartColor
                    };
                }

                // DrawOrder ([ushort] vector) も先に充填し、バッチ集計ループは _drawOrder から読む。
                // 連続 scalar vector なので per-element でなく一括 memcpy する (effect ジオメトリと同様)。
                // draw_order は FrameData の field 6 → vtable offset 16。
                var tFrame = new Google.FlatBuffers.Table(
                    _frameDataByteBuffer.GetInt(_frameDataByteBuffer.Position) + _frameDataByteBuffer.Position, _frameDataByteBuffer);
                SpriteStudioNativeBuffers.CopyFbVector(tFrame, 16, frameDataPtr, _drawOrder, 0, drawOrderLength);

                // Build the per-frame mask writer list (mirrors _build_mask_writers). Behaviour-neutral
                // until the stencil submesh pipeline consumes it.
                _maskPlanner.BuildWriters(frameData, _drawOrder, _player._hasMaskParts, _player._maskPartData, drawOrderLength, partsLength);

                int totalVertices = 0;
                int totalIndices = 0;
                _submeshCount = 0;
                SpriteStudioNativeBuffers.EnsureCapacity(ref _submeshes, maxBatches < 1 ? 1 : maxBatches);

                // Mask Phase A: harvest per-part textures, then pre-write every pure mask so the stencil
                // reflects all of them before any body part is drawn. Each pure mask's coverage is
                // removed again at its own rank in the main loop (Phase B), yielding the "mask affects
                // lower-priority parts" scope (mirrors SS6PU's pre-draw + slot decrement).
                if (_player._hasMaskParts)
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
                        EmitMaskStep(md.DrawKind, w.DrawRank, wvc, wic, _maskPlanner.GetPartTexHash(wp), _maskPlanner.GetPartBlend(wp),
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
                    else if (b.Kind == ss.runtime.DrawBatchKind.Mask && _player._hasMaskParts)
                    {
                        ProcessMaskBatch(b, partsLength, ref frame, frameData, ref totalVertices, ref totalIndices);
                    }
                }

                FillPartColors(frameData, partsColorLength);

                if (totalVertices == 0 || totalIndices == 0)
                {
                    _vertexCount = 0;
                    _indexCount = 0;
                    return;
                }

                SpriteStudioNativeBuffers.EnsureCapacity(ref _meshVertices, totalVertices);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _meshIndices, totalIndices);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _meshBounds, 2); // Job が min/max を書き込む
                _vertexCount = totalVertices;
                _indexCount = totalIndices;

                // index アップロードキャッシュ用のトポロジシグネチャ (前フレームと同一なら index/submesh は
                // 再送不要)。Effect は QuadCount/Offset が毎フレーム変わり自然に不一致 = キャッシュされない。
                _topologySignature = ComputeTopologySignature();

                // エフェクトが無くても Job が参照するため、結合配列は最低 1 要素を確保しておく。
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

                    BatchCount = _drawBatchCount,
                    PartCount = _partCount,
                    PartColorCount = _partColorCount,
                    InvScale = _player.VertexScale,

                    DrawBatches = _drawBatches,
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

                // 次フレームこの整数フレームと一致すれば再構築を省く基準。
                _lastBuiltDrawFrameNo = drawFrameNo;
            }
        }

        /// <summary>
        /// 発行済みのメッシュ構築ジョブの完了を待ち、構築済みジオメトリを Unity の Mesh へ反映する。
        /// 実際のアップロード (頂点/index/submesh/bounds と再送スキップのキャッシュ) は
        /// <see cref="SpriteStudioMeshUploader"/> へ委譲する。
        /// </summary>
        public void ApplyToMesh(UnityEngine.Mesh targetMesh, bool perBatchSubmesh = false)
        {
            // Mesh に適用する前に Job の完了を待機 (Job はビルダーが所有)。
            _updateJobHandle.Complete();

            _meshUploader.Apply(targetMesh, perBatchSubmesh, new SpriteStudioMeshUploader.UploadData(
                _frameSkipped, _vertexCount, _indexCount, _meshVertices, _meshIndices,
                _submeshes, _submeshCount, _topologySignature, _meshBounds));
        }

        /// <summary>active な effect スロットを step し、可視なものの draw_plan を結合バッファへ追記する。</summary>
        private unsafe void StepEffects(float drawFrameNo, float deltaTime, float fps, bool parentLooped)
        {
            for (int k = 0; k < _player._effectPartCount; k++)
            {
                int i = _player._effectPartIndices[k];
                var ev = NativeMethods.ss_runtime_get_active_effect_event(_player._handle.ptr, (uint)i);
                var step = NativeMethods.ss_effect_slot_step((void*)_player._effectSlots[i].EffectSlot, ev, drawFrameNo, deltaTime, fps, parentLooped, false, 3);
                if (step.visible && step.draw_plan_len > 0)
                    _effectBuffer.SetPlan(i, _effectBuffer.Append(step.draw_plan_buf, (int)step.draw_plan_len));
                // 非 visible は MemClear 済の default のまま
            }
        }

        /// <summary>active な instance スロットを step し、可視な子の mesh ポインタ/Job ハンドルを収集する。
        /// 返り値は依存に束ねる子 Job ハンドル数 (CombineDependencies 用)。</summary>
        private unsafe int StepInstances(float drawFrameNo, float deltaTime, bool parentLooped, int partsLength)
        {
            int childHandleCount = 0;
            // _childHandles への書き込みは可視 instance のときだけ。永続 grow-only で毎フレームの Temp 確保を避ける。
            if (_player._hasInstanceParts)
                SpriteStudioNativeBuffers.EnsureCapacity(ref _childHandles, partsLength);

            for (int k = 0; k < _player._instancePartCount; k++)
            {
                int i = _player._instancePartIndices[k];
                var childPlayer = _player._instanceChildren[i].Player;
                if (childPlayer == null) continue; // _instanceMeshes[i] は MemClear 済 default

                var ev = NativeMethods.ss_runtime_get_active_event_instance(_player._handle.ptr, (uint)i);
                var step = NativeMethods.ss_instance_slot_step((void*)_player._instanceChildren[i].InstanceSlot, ev, childPlayer._handle.ptr, _player._instanceChildren[i].PreviousFrame, drawFrameNo, deltaTime, parentLooped);

                if (step.transitioned)
                {
                    childPlayer.Play(); // Trigger side effects (UnityEvents) if needed
                }

                if (step.visible)
                {
                    float childDrawFrame = childPlayer.SubFrameEnabled ? step.child_frame_no : UnityEngine.Mathf.Floor(step.child_frame_no);
                    childDrawFrame = UnityEngine.Mathf.Clamp(childDrawFrame, childPlayer.GetAnimationSectionStart(), childPlayer.GetAnimationSectionEnd());
                    _player._instanceChildren[i].PreviousFrame = childDrawFrame;
                    childPlayer.SetFrame(childDrawFrame);
                    childPlayer.ScheduleMeshUpdate(deltaTime);

                    _childHandles[childHandleCount++] = childPlayer.CurrentUpdateJobHandle;

                    _instanceMeshes[i] = new InstanceMeshPointer {
                        Vertices = (SsVertex*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(childPlayer.MeshVertices),
                        Indices = (uint*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(childPlayer.MeshIndices),
                        VertexCount = childPlayer.VertexCount,
                        IndexCount = childPlayer.IndexCount
                    };
                }
                // 非 visible は MemClear 済の default のまま
            }
            return childHandleCount;
        }

        /// <summary>frameData の PartsColor から _partColors を充填する (4 隅の頂点カラー)。</summary>
        private void FillPartColors(ss.runtime.FrameData frameData, int partsColorLength)
        {
            for (int i = 0; i < partsColorLength; i++)
            {
                var pc = frameData.PartsColor(i).Value;
                _partColors[i] = new PartColorData
                {
                    BlendType = (byte)pc.BlendType,
                    Lt = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Lt.Rgba.R, pc.Lt.Rgba.G, pc.Lt.Rgba.B, pc.Lt.Rgba.A), Rate = pc.Lt.Rate },
                    Rt = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Rt.Rgba.R, pc.Rt.Rgba.G, pc.Rt.Rgba.B, pc.Rt.Rgba.A), Rate = pc.Rt.Rate },
                    Lb = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Lb.Rgba.R, pc.Lb.Rgba.G, pc.Lb.Rgba.B, pc.Lb.Rgba.A), Rate = pc.Lb.Rate },
                    Rb = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Rb.Rgba.R, pc.Rb.Rgba.G, pc.Rb.Rgba.B, pc.Rb.Rgba.A), Rate = pc.Rb.Rate }
                };
            }
        }

        /// <summary>
        /// 現在の draw batch / submesh 構造から index アップロードキャッシュ判定用の FNV-1a シグネチャを算出する。
        /// _vertexCount / _indexCount 確定後に呼ぶこと。
        /// </summary>
        private unsafe ulong ComputeTopologySignature()
        {
            ulong sig = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            sig = (sig ^ (ulong)(uint)_drawBatchCount) * prime;
            for (int i = 0; i < _drawBatchCount; i++)
            {
                var b = ((DrawBatchData*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[i];
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
            sig = (sig ^ (ulong)(uint)_indexCount) * prime;
            sig = (sig ^ (ulong)(uint)_vertexCount) * prime;
            return sig;
        }

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

                    ((DrawBatchData*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[_drawBatchCount++] = new DrawBatchData
                    {
                        Kind = SsDrawBatchKind.Instance,
                        EffectPartIndex = p_idx,
                        VertexCount = cvc,
                        IndexCount = cic
                    };

                    StencilRole instRole = StencilRole.None;
                    byte instComp = 0;
                    if (_player._hasMaskParts)
                    {
                        var imd = _player._maskPartData[p_idx];
                        if (imd.MaskInfluence || imd.MaskWrite)
                        {
                            instRole = StencilRole.Test;
                            instComp = imd.VisibleInsideMask ? StencilCompNotEqual : StencilCompEqual;
                        }
                    }

                    for (int sm = 0; sm < childPlayer.SubmeshCount; sm++)
                    {
                        var cs = childPlayer.Submeshes[sm];
                        StencilRole cr = cs.Role;
                        byte cComp = cs.StencilComp, cRMask = cs.StencilReadMask;
                        if (cs.Role == StencilRole.None && instRole == StencilRole.Test)
                        {
                            cr = StencilRole.Test;
                            cComp = instComp;
                            cRMask = SpriteStudioMaterial.MaskStencilMask;
                        }
                        EmitSubmesh(totalIndices + cs.IndexStart, cs.IndexCount, cs.TextureHash, cs.BlendType,
                            cr, cs.StencilOp, cComp, cs.StencilRef, cRMask, cs.StencilWriteMask, cs.MaskThreshold);
                    }

                    totalVertices += cvc;
                    totalIndices += cic;
                }
            }
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
                if (_player._hasMaskParts)
                {
                    var emd = _player._maskPartData[p_idx];
                    if (emd.MaskInfluence || emd.MaskWrite)
                    {
                        effRole = StencilRole.Test;
                        effComp = emd.VisibleInsideMask ? StencilCompNotEqual : StencilCompEqual;
                    }
                }

                for (int c = 0; c < plan.CommandCount; c++)
                {
                    var cmd = _effectBuffer.GetCommand(plan.CommandStart + c);
                    if (cmd.QuadCount > 0)
                    {
                        int cmdVc = (int)cmd.QuadCount * 4;
                        int cmdIc = (int)cmd.QuadCount * 6;

                        ((DrawBatchData*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[_drawBatchCount++] = new DrawBatchData
                        {
                            StartRank = 0,
                            Count = 0,
                            VertexCount = cmdVc,
                            IndexCount = cmdIc,
                            Kind = SsDrawBatchKind.Effect,
                            EffectBlend = cmd.Blend,
                            EffectParticleOffset = (uint)plan.QuadBase + cmd.ParticleOffset,
                            EffectQuadCount = cmd.QuadCount,
                            EffectPartIndex = p_idx
                        };
                        
                        EmitSubmesh(totalIndices, cmdIc, cmd.CellmapHash, SpriteStudioMaterial.BlendMix,
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

            if (_player._hasMaskParts)
            {
                byte kind = (byte)b.Kind;
                for (int k = 0; k < b.Count; k++)
                {
                    int rank = b.StartRank + k;
                    int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
                    if (p_idx < 0 || p_idx >= partsLength) continue;
                    if (!_maskPlanner.MaskPartVcIc(p_idx, kind, frame, _partStates, out int pvc, out int pic)) continue;
                    var md = _player._maskPartData[p_idx];
                    if (md.PureMask)
                    {
                        byte rop = md.MaskInfluence ? StencilOpInvert : StencilOpDecr;
                        float th = kind == SsDrawBatchKind.Shape ? -1f : (255f - frameData.Parts(p_idx).Value.Mask) / 255f;
                        EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                            StencilRole.Write, rop, 0, th, ref totalVertices, ref totalIndices);
                    }
                    else
                    {
                        bool maskTarget = md.MaskInfluence || md.MaskWrite;
                        if (maskTarget)
                        {
                            byte comp = md.VisibleInsideMask ? StencilCompNotEqual : StencilCompEqual;
                            EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                                StencilRole.Test, 0, comp, 0f, ref totalVertices, ref totalIndices);
                        }
                        else
                        {
                            EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                                StencilRole.None, 0, 0, 0f, ref totalVertices, ref totalIndices);
                        }
                        if (md.IsClipping)
                        {
                            byte wop = md.MaskInfluence ? StencilOpInvert : StencilOpIncr;
                            float th = kind == SsDrawBatchKind.Shape ? -1f : (255f - frameData.Parts(p_idx).Value.Mask) / 255f;
                            EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                                StencilRole.Write, wop, 0, th, ref totalVertices, ref totalIndices);
                        }
                    }
                }
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
                ((DrawBatchData*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[_drawBatchCount++] = new DrawBatchData
                {
                    StartRank = (ushort)b.StartRank,
                    Count = (ushort)b.Count,
                    VertexCount = vc,
                    IndexCount = ic,
                    Kind = (byte)b.Kind
                };

                EmitSubmesh(totalIndices, ic, b.TextureHash, (byte)b.BlendType);

                totalVertices += vc;
                totalIndices += ic;
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
                if (!_maskPlanner.MaskPartVcIc(p_idx, SsDrawBatchKind.Normal, frame, _partStates, out int mvc, out int mic)) continue;
                byte rop = md.MaskInfluence ? StencilOpInvert : StencilOpDecr;
                float th = (255f - frameData.Parts(p_idx).Value.Mask) / 255f;
                EmitMaskStep(SsDrawBatchKind.Normal, rank, mvc, mic, b.TextureHash, (byte)b.BlendType,
                    StencilRole.Write, rop, 0, th, ref totalVertices, ref totalIndices);
            }
        }

        private unsafe void EmitMaskStep(byte kind, int rank, int vc, int ic, uint texHash, byte blend,
            StencilRole role, byte stencilOp, byte stencilComp, float threshold,
            ref int totalVertices, ref int totalIndices)
        {
            ((DrawBatchData*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[_drawBatchCount++] = new DrawBatchData
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
