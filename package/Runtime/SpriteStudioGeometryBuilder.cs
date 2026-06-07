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

        // FrameData の vtable offset (parts / parts_color フィールド)。生成コードの Parts(j)=__offset(4) /
        // PartsColor(j)=__offset(8) と一致。fill とレイアウト検証 (SpriteStudioRawLayoutCheck) の単一ソース。
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
        // メッシュ境界 (要素 0=min, 1=max)。UpdateMeshJob が書き込み、MeshUploader が Mesh.bounds へ適用する。
        private NativeArray<Vector3> _meshBounds;

        public int VertexCount => _vertexCount;
        public int IndexCount => _indexCount;
        public int DrawBatchCount => _batchBuilder.DrawBatchCount;
        public int SubmeshCount => _batchBuilder.SubmeshCount;
        public NativeArray<SubmeshInfo> Submeshes => _batchBuilder.Submeshes;
        public NativeArray<DrawBatchData> DrawBatches => _batchBuilder.DrawBatches;
        public NativeArray<SsVertex> MeshVertices => _meshVertices;
        public NativeArray<uint> MeshIndices => _meshIndices;

        /// <summary>直近の ScheduleMeshUpdate で生成された描画バッチ (= SubMesh) の数。</summary>
        // public int SubmeshCount => _batchBuilder.SubmeshCount;

        /// <summary>指定 SubMesh のテクスチャハッシュ (DrawBatch.TextureHash)。</summary>
        public uint GetSubmeshTextureHash(int index) => _batchBuilder.Submeshes[index].TextureHash;

        /// <summary>SubMesh's ssab BlendType (0..7), used to resolve the per-blend material variant.</summary>
        public byte GetSubmeshBlendType(int index) => _batchBuilder.Submeshes[index].BlendType;

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

                // FrameData テーブルの Table ビュー。parts / parts_color / draw_order の連続 vector の先頭位置
                // (tFrame.__vector(vtoff)) を取り、生ポインタ直読みと既存 drawOrder コピーで共用する。
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
                _batchBuilder.BeginFrame(maxBatches);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _drawOrder, drawOrderLength);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _partStates, partsLength);
                SpriteStudioNativeBuffers.EnsureCapacity(ref _partColors, partsColorLength);

                
                _partCount = partsLength;
                _partColorCount = partsColorLength;

                // PartState を先に充填し、下のバッチ集計 (Normal の needsCenter 判定) が
                // UpdateFlag を FlatBuffer から再デコードせず _partStates から読めるようにする。
                var pStates = (PartStateData*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_partStates);
                // per-element accessor (frameData.Parts(i)) を撲滅し、parts の連続 struct-vector
                // (vtable offset 4・144B stride の固定 struct) を RawPartState ミラーで直読みする。
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

                // DrawOrder ([ushort] vector) も先に充填し、バッチ集計ループは _drawOrder から読む。
                // 連続 scalar vector なので per-element でなく一括 memcpy する (effect ジオメトリと同様)。
                // draw_order は FrameData の field 6 → vtable offset 16。
                SpriteStudioNativeBuffers.CopyFbVector(tFrame, 16, frameDataPtr, _drawOrder, 0, drawOrderLength);

                // Build the per-frame mask writer list (mirrors _build_mask_writers). Behaviour-neutral
                // until the stencil submesh pipeline consumes it.
                _maskPlanner.BuildWriters(frameData, _drawOrder, _player._hasMaskParts, _player._maskPartData, drawOrderLength, partsLength);

                int totalVertices = 0;
                int totalIndices = 0;
                

                // Mask Phase A: harvest per-part textures, then pre-write every pure mask so the stencil
                // reflects all of them before any body part is drawn. Each pure mask's coverage is
                // removed again at its own rank in the main loop (Phase B), yielding the "mask affects
                // lower-priority parts" scope (mirrors SS6PU's pre-draw + slot decrement).
                // Suppressed (Canvas/uGUI) players skip the pre-write entirely: pure masks draw nothing
                // and everything else draws unmasked (handled per-part in ProcessNormalShapeMeshBatch).
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
                FillPartColors(frameDataPtr, tFrame, partsColorLength);

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
                _topologySignature = _batchBuilder.ComputeTopologySignature(totalVertices, totalIndices);

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

                    BatchCount = _batchBuilder.DrawBatchCount,
                    PartCount = _partCount,
                    PartColorCount = _partColorCount,
                    InvScale = _player.VertexScale,

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

                // 次フレームこの整数フレームと一致すれば再構築を省く基準。
                _lastBuiltDrawFrameNo = drawFrameNo;
            }
        }

        /// <summary>
        /// 発行済みのメッシュ構築ジョブの完了を待ち、構築済みジオメトリを Unity の Mesh へ反映する。
        /// 実際のアップロード (頂点/index/submesh/bounds と再送スキップのキャッシュ) は
        /// <see cref="SpriteStudioMeshUploader"/> へ委譲する。
        /// </summary>
        /// <param name="use16BitIndices">
        /// Canvas/uGUI path: upload via the high-level Mesh API with 16-bit indices. CanvasRenderer
        /// rejects the low-level SetVertexBufferData mesh on Windows/D3D12 (it issues an invalid GPU
        /// draw that removes the device). The world (MeshRenderer) path leaves this false for the fast
        /// low-level upload. See SpriteStudioMeshUploader and project_canvas_single_material_resume.
        /// </param>
        public void ApplyToMesh(UnityEngine.Mesh targetMesh, bool perBatchSubmesh = false, bool use16BitIndices = false)
        {
            // Mesh に適用する前に Job の完了を待機 (Job はビルダーが所有)。
            _updateJobHandle.Complete();

            _meshUploader.Apply(targetMesh, perBatchSubmesh, use16BitIndices, new SpriteStudioMeshUploader.UploadData(
                _frameSkipped, _vertexCount, _indexCount, _meshVertices, _meshIndices,
                _batchBuilder.Submeshes, _batchBuilder.SubmeshCount, _topologySignature, _meshBounds));
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
        private unsafe void FillPartColors(byte* frameDataPtr, Google.FlatBuffers.Table tFrame, int partsColorLength)
        {
            // per-element 入れ子 accessor (frameData.PartsColor(i)) を撲滅し、parts_color の連続
            // struct-vector (vtable offset 8・36B stride) を RawPartColor ミラーで直読みする。
            // U8Rect は ARGB バイト順なので Color32(R,G,B,A) へ並べ替える (従来挙動と同一)。
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
        /// 現在の draw batch / submesh 構造から index アップロードキャッシュ判定用の FNV-1a シグネチャを算出する。
        /// _vertexCount / _indexCount 確定後に呼ぶこと。
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

                    _batchBuilder.EmitInstanceBatch(p_idx, cvc, cic);

                    StencilRole instRole = StencilRole.None;
                    byte instComp = 0;
                    if (_player.MaskActive)
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
                        _batchBuilder.EmitSubmesh(totalIndices + cs.IndexStart, cs.IndexCount, cs.TextureHash, cs.BlendType,
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
                if (_player.MaskActive)
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
                    if (!_player.MaskActive)
                    {
                        // Canvas/uGUI suppression: a pure mask contributes no colour (drop it); every
                        // other part draws as plain colour (Role None, merged downstream by EmitSubmesh).
                        // The stencil buffer is left to uGUI's own Mask/RectMask2D. See project_unity_canvas_plan.
                        if (md.PureMask) continue;
                        _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                            StencilRole.None, 0, 0, 0f, ref totalVertices, ref totalIndices);
                        continue;
                    }
                    if (md.PureMask)
                    {
                        byte rop = md.MaskInfluence ? StencilOpInvert : StencilOpDecr;
                        float th = kind == SsDrawBatchKind.Shape ? -1f : (255f - frameData.Parts(p_idx).Value.Mask) / 255f;
                        _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                            StencilRole.Write, rop, 0, th, ref totalVertices, ref totalIndices);
                    }
                    else
                    {
                        bool maskTarget = md.MaskInfluence || md.MaskWrite;
                        if (maskTarget)
                        {
                            byte comp = md.VisibleInsideMask ? StencilCompNotEqual : StencilCompEqual;
                            _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                                StencilRole.Test, 0, comp, 0f, ref totalVertices, ref totalIndices);
                        }
                        else
                        {
                            _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                                StencilRole.None, 0, 0, 0f, ref totalVertices, ref totalIndices);
                        }
                        if (md.IsClipping)
                        {
                            byte wop = md.MaskInfluence ? StencilOpInvert : StencilOpIncr;
                            float th = kind == SsDrawBatchKind.Shape ? -1f : (255f - frameData.Parts(p_idx).Value.Mask) / 255f;
                            _batchBuilder.EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
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
                _batchBuilder.EmitDrawBatch((ushort)b.StartRank, (ushort)b.Count, vc, ic, (byte)b.Kind, b.TextureHash, (byte)b.BlendType, ref totalVertices, ref totalIndices);
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
                _batchBuilder.EmitMaskStep(SsDrawBatchKind.Normal, rank, mvc, mic, b.TextureHash, (byte)b.BlendType,
                    StencilRole.Write, rop, 0, th, ref totalVertices, ref totalIndices);
            }
        }

        
    }

    // === FlatBuffer の固定 struct-vector を per-element accessor 無しで直読みするための生レイアウトミラー ===
    // framedata_generated.cs の PartState(144B) / PartAttributePartColor(36B) / U8Rect とバイト互換。
    // ⚠ .fbs を再生成してレイアウトが変わったらここも更新する (起動時 sizeof assert で総量 drift を検出)。
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    internal struct RawCorner   // U8Rect(ARGB) + Rate (SsAttributePartColorKeyValueColor)
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
    internal struct RawPartState   // PartState (使用フィールドのみ宣言・残りは Size で確保)
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
            // (1) 総量 drift (フィールド増減) を捕捉。
            UnityEngine.Debug.Assert(Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<RawPartState>() == 144,
                "RawPartState size != 144: framedata PartState layout changed - update the mirror struct");
            UnityEngine.Debug.Assert(Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<RawPartColor>() == 36,
                "RawPartColor size != 36: framedata PartAttributePartColor layout changed - update the mirror struct");
            UnityEngine.Debug.Assert(Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<RawCorner>() == 8,
                "RawCorner size != 8: framedata color-corner layout changed - update the mirror struct");

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // (2) 同サイズの並べ替え等も捕捉する roundtrip: 生成ビルダーで既知の sentinel struct を書き、
            //     生成 accessor とミラーの両方で同一バッファを読んで一致を確認する。.fbs を再生成して
            //     field offset / vtable offset がズレた瞬間にここが赤くなる (= サイレント drift の自動検出)。
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

            // FrameData の required フィールド (draw_order / draw_batches / mask_indices) を空ベクタで満たす
            // (EndFrameData が presence を要求する。中身は本検証では未使用)。
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
                    "RawPartColor field offsets do not match the framedata PartAttributePartColor accessor - verify BlendType@0/Lt@4/Rt@12/Lb@20/Rb@28 and U8Rect A@0/R@1/G@2/B@3+Rate@4, then update the mirror struct");
            }
#endif
        }
    }
}
