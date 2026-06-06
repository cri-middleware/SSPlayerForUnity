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
        
        // --- Moved from Effect.cs ---
private NativeArray<EffectDrawPlanData> _effectPlans;

        // エフェクト draw_plan を frame-data と同じ流儀で読む (ネイティブ→byte[]→ByteBuffer→生成アクセサ)。
        // 全エフェクトパートのジオメトリを連結した結合バッファ。Burst Job はこれを読む。
        private Google.FlatBuffers.ByteBuffer _planByteBuffer;
#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        // ゼロコピー: ネイティブ draw_plan ポインタを直接 ByteBuffer に載せる読み取り専用アロケータ。
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

        // effect/instance スロットを持つパーツ index の疎なリスト (SetupSlots で確定)。
        
        // --- Moved from Mask.cs ---
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
        private NativeArray<MaskWriter> _maskWriters;
        private int _maskWriterCount;
        // Per-part (texture hash, blend) for the current frame, harvested from the draw batches so the
        // mask path can resolve a writer's texture (Mask twins carry texture 0; the part's colour batch
        // carries the real one). Index-aligned with parts; only filled when _player._hasMaskParts.
        private NativeArray<uint> _maskPartTexHash;
        private NativeArray<byte> _maskPartBlend;
        // --- Moved from Instance.cs ---
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
            if (_effectPlans.IsCreated) _effectPlans.Dispose();
            if (_effectCommands.IsCreated) _effectCommands.Dispose();
            if (_effectVerts.IsCreated) _effectVerts.Dispose();
            if (_effectUvs.IsCreated) _effectUvs.Dispose();
            if (_effectColors.IsCreated) _effectColors.Dispose();
            if (_effectIndices.IsCreated) _effectIndices.Dispose();
            if (_instanceMeshes.IsCreated) _instanceMeshes.Dispose();
            if (_childHandles.IsCreated) _childHandles.Dispose();
            if (_meshVertices.IsCreated) _meshVertices.Dispose();
            if (_meshIndices.IsCreated) _meshIndices.Dispose();
            if (_meshIndices16.IsCreated) _meshIndices16.Dispose();
            if (_canvasPositions.IsCreated) _canvasPositions.Dispose();
            if (_canvasColors.IsCreated) _canvasColors.Dispose();
            if (_canvasUv0.IsCreated) _canvasUv0.Dispose();
            if (_canvasUv1.IsCreated) _canvasUv1.Dispose();
            if (_drawBatches.IsCreated) _drawBatches.Dispose();
            if (_drawOrder.IsCreated) _drawOrder.Dispose();
            if (_partStates.IsCreated) _partStates.Dispose();
            if (_partColors.IsCreated) _partColors.Dispose();
            if (_submeshDescriptors.IsCreated) _submeshDescriptors.Dispose();
            if (_meshBounds.IsCreated) _meshBounds.Dispose();
            if (_submeshes.IsCreated) _submeshes.Dispose();
            if (_maskWriters.IsCreated) _maskWriters.Dispose();
            if (_maskPartTexHash.IsCreated) _maskPartTexHash.Dispose();
            if (_maskPartBlend.IsCreated) _maskPartBlend.Dispose();
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
        // 16-bit index copy built on upload for CanvasRenderer (which rejects 32-bit index meshes).
        // Only filled when ApplyToMesh is asked for 16-bit indices (the Canvas/uGUI path).
        private NativeArray<ushort> _meshIndices16;
        private bool _warned16BitOverflow;
        // De-interleaved scratch channels for the Canvas high-level Mesh upload (see ApplyToMeshCanvas).
        // CanvasRenderer cannot consume a low-level SetVertexBufferData mesh on Windows/D3D12, so the
        // Canvas path uploads per channel via the high-level API instead. Reused (grow-only) = zero alloc.
        private NativeArray<Vector3> _canvasPositions;
        private NativeArray<Color32> _canvasColors;
        private NativeArray<Vector2> _canvasUv0;
        private NativeArray<Vector4> _canvasUv1;
        private NativeArray<DrawBatchData> _drawBatches;
        private NativeArray<ushort> _drawOrder;
        private NativeArray<PartStateData> _partStates;
        private NativeArray<PartColorData> _partColors;

        private int _vertexCount;
        private int _indexCount;
        private int _drawBatchCount;
        private int _partCount;
        private int _partColorCount;

        // index アップロードのキャッシュ用。トポロジ (バッチ構造) が不変なら ApplyToMesh で
        // index/submesh の再アップロードを省き、頂点のみ更新する。
        private ulong _topologySignature;
        private bool _meshUploaded;
        private int _lastMeshId;
        private ulong _uploadedTopologySignature;
        private int _uploadedVertexCount;
        private int _uploadedIndexCount;
        private bool _uploadedPerBatchSubmesh;
        private bool _uploadedUse16BitIndices;

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
                EnsureCapacityPreserve(ref _submeshes, newSize);
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
        // SetSubMeshes 用の永続バッファ (grow-only で使い回す)。
        private NativeArray<UnityEngine.Rendering.SubMeshDescriptor> _submeshDescriptors;
        // メッシュ境界 (要素 0=min, 1=max)。UpdateMeshJob が書き込み、ApplyToMesh が Mesh.bounds へ適用する。
        private NativeArray<Vector3> _meshBounds;

        // 頂点レイアウト (Position / Color / TexCoord0 / TexCoord1)。SetVertexBufferParams へ使い回す。
        private static readonly UnityEngine.Rendering.VertexAttributeDescriptor[] s_vertexLayout =
        {
            new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Position, UnityEngine.Rendering.VertexAttributeFormat.Float32, 3),
            new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Color, UnityEngine.Rendering.VertexAttributeFormat.UNorm8, 4),
            new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.TexCoord0, UnityEngine.Rendering.VertexAttributeFormat.Float32, 2),
            new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.TexCoord1, UnityEngine.Rendering.VertexAttributeFormat.Float32, 4),
        };

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

        /// <summary>必要件数を満たすまで grow-only で確保する。容量が足りていれば再利用する。</summary>
        private static void EnsureCapacity<T>(ref NativeArray<T> arr, int needed) where T : struct
        {
            if (arr.IsCreated && arr.Length >= needed) return;
            if (arr.IsCreated) arr.Dispose();
            arr = new NativeArray<T>(needed < 1 ? 1 : needed, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        // grow 時に既存内容を保持する版 (エフェクト結合バッファを slot ごとに追記するため)。
        private static void EnsureCapacityPreserve<T>(ref NativeArray<T> arr, int needed) where T : struct
        {
            if (arr.IsCreated && arr.Length >= needed) return;
            int newLen = UnityEngine.Mathf.Max(needed, arr.IsCreated ? arr.Length * 2 : 256);
            var newArr = new NativeArray<T>(newLen, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            if (arr.IsCreated)
            {
                NativeArray<T>.Copy(arr, newArr, arr.Length);
                arr.Dispose();
            }
            arr = newArr;
        }

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

                EnsureCapacity(ref _effectPlans, partsLength);
                EnsureCapacity(ref _instanceMeshes, partsLength);

                bool parentLooped = NativeMethods.ss_runtime_is_looped(_player._handle.ptr);
                float fps = _player.GetFrameRate();
                if (fps <= 0f) fps = 60f;

                // _childHandles への書き込みは InstanceSlot != Zero のブランチ内だけで、それは
                // _player._hasInstanceParts が真のときしか成立しない。永続 grow-only で毎フレームの Temp 確保を避ける。
                int childHandleCount = 0;
                if (_player._hasInstanceParts)
                    EnsureCapacity(ref _childHandles, partsLength);

                // エフェクト結合バッファを毎フレーム先頭から詰め直す。
                _effectCommandCount = 0;
                _effectVertFloatCount = 0;
                _effectUvFloatCount = 0;
                _effectColorFloatCount = 0;
                _effectIndexCount = 0;

                // active な effect/instance パーツだけを sparse に処理する (index は SetupSlots で確定)。
                // 非 active 部 (通常パーツ／解決失敗した instance パーツ等) は会計ループ・Burst Job から default が
                // 読まれ得るため、事前に一括 MemClear しておく (partsLength 個別 default 書込みより遥かに安く、
                // 「どのパーツが読まれるか」に依存せず安全)。これで partsLength 全スキャンの空回りを回避する。
                if (partsLength > 0)
                {
                    Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(
                        Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_effectPlans),
                        (long)partsLength * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<EffectDrawPlanData>());
                    Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(
                        Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_instanceMeshes),
                        (long)partsLength * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<InstanceMeshPointer>());
                }

                for (int k = 0; k < _player._effectPartCount; k++)
                {
                    int i = _player._effectPartIndices[k];
                    var ev = NativeMethods.ss_runtime_get_active_effect_event(_player._handle.ptr, (uint)i);
                    var step = NativeMethods.ss_effect_slot_step((void*)_player._effectSlots[i].EffectSlot, ev, drawFrameNo, deltaTime, fps, parentLooped, false, 3);
                    if (step.visible && step.draw_plan_len > 0)
                        _effectPlans[i] = AppendEffectDrawPlan(step.draw_plan_buf, (int)step.draw_plan_len);
                    // 非 visible は MemClear 済の default のまま
                }

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

                // maxBatches も active な effect/instance パーツのみ集計 (非 active は default=0寄与なので不要)。
                int maxBatches = drawBatchesLength;
                for (int k = 0; k < _player._effectPartCount; k++)
                {
                    int i = _player._effectPartIndices[k];
                    if (_effectPlans[i].CommandCount > 0)
                        maxBatches += _effectPlans[i].CommandCount;
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

                // Internal masking is active only when the asset has mask writers AND this player is
                // not suppressing them. Canvas/uGUI suppresses (project_unity_canvas_plan); the world
                // path keeps it. When suppressed we still walk masked parts per-part (to drop pure-mask
                // parts), so the per-part budget below is needed in both cases.
                bool maskActive = _player._hasMaskParts && !_player._suppressInternalMask;

                // Masking emits per-part submeshes (no batch merge) plus extra write/restore steps, so
                // grow the batch/submesh budget. Each part contributes up to ~2 steps (test+write or
                // colour-suppressed restore); the pre-write phase adds at most one per writer.
                if (_player._hasMaskParts) maxBatches += partsLength * 2 + _maskWriterCount;

                // 永続バッファを grow-only で確保 (毎フレーム new しない)
                EnsureCapacity(ref _drawBatches, maxBatches);
                EnsureCapacity(ref _drawOrder, drawOrderLength);
                EnsureCapacity(ref _partStates, partsLength);
                EnsureCapacity(ref _partColors, partsColorLength);

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
                CopyFbVector(tFrame, 16, frameDataPtr, _drawOrder, 0, drawOrderLength);

                // Build the per-frame mask writer list (mirrors _build_mask_writers). Behaviour-neutral
                // until the stencil submesh pipeline consumes it.
                BuildMaskWriters(frameData, drawOrderLength, partsLength);

                int totalVertices = 0;
                int totalIndices = 0;
                _submeshCount = 0;
                EnsureCapacity(ref _submeshes, maxBatches < 1 ? 1 : maxBatches);

                // Mask Phase A: harvest per-part textures, then pre-write every pure mask so the stencil
                // reflects all of them before any body part is drawn. Each pure mask's coverage is
                // removed again at its own rank in the main loop (Phase B), yielding the "mask affects
                // lower-priority parts" scope (mirrors SS6PU's pre-draw + slot decrement).
                if (maskActive)
                {
                    BuildMaskPartLookup(frameData, drawBatchesLength, drawOrderLength, partsLength);
                    for (int wi = 0; wi < _maskWriterCount; wi++)
                    {
                        var pWriters = (MaskWriter*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskWriters);
                        var w = pWriters[wi];
                        if (w.IsClipping) continue; // clipping writes at its own rank (Phase B), not pre-written
                        int wp = w.PartIndex;
                        if (wp < 0 || wp >= partsLength) continue;
                        var md = _player._maskPartData[wp];
                        if (!MaskPartVcIc(wp, md.DrawKind, frame, out int wvc, out int wic)) continue;
                        byte op = w.OpInvert ? StencilOpInvert : StencilOpIncr;
                        float threshold = md.DrawKind == SsDrawBatchKind.Shape ? -1f : w.Threshold;
                        EmitMaskStep(md.DrawKind, w.DrawRank, wvc, wic, ((uint*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartTexHash))[wp], ((byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartBlend))[wp],
                            StencilRole.Write, op, 0, threshold, ref totalVertices, ref totalIndices);
                    }
                }

                for (int i = 0; i < drawBatchesLength; i++)
                {
                    var b = frameData.DrawBatches(i).Value;

                    // 描画対象は Normal(0)/Shape(1)/Instance(2)/Effect(3)/Mesh(4) のみ。
                    int vc = 0;
                    int ic = 0;
                    
                    if (b.Kind == ss.runtime.DrawBatchKind.Instance)
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

                                // 子メッシュは頂点/インデックスが子バッファ上で連続しているため、1 つの
                                // Instance バッチとして丸ごと取り込む (Job の BuildInstance が meshPtr 全体を
                                // 1 回でコピーする)。子が複数バッチ (複数テクスチャ) でも、ここで複数の
                                // DrawBatchData を積むと BuildInstance が子メッシュをバッチ数ぶん重複コピーして
                                // バッファ溢れ・描画破綻を起こすため不可。テクスチャ単位の分割は下の SubmeshInfo が担う。
                                ((DrawBatchData*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawBatches))[_drawBatchCount++] = new DrawBatchData
                                {
                                    Kind = SsDrawBatchKind.Instance,
                                    EffectPartIndex = p_idx,
                                    VertexCount = cvc,
                                    IndexCount = cic
                                };

                                // Parent-mask inheritance (single level): if this instance part is a
                                // mask target, stencil-test the whole child sub-animation against the
                                // shared coverage. Mirrors Godot's _emit_instance_slot mask path.
                                StencilRole instRole = StencilRole.None;
                                byte instComp = 0;
                                if (maskActive)
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
                                    // Preserve the child's own stencil role so its internal masks still
                                    // work (its write submeshes stay ColorMask-0 writes instead of leaking
                                    // as colour, and its test/restore sequence is kept verbatim/contiguous
                                    // = self-restoring). Only a plain child colour submesh (Role None)
                                    // inherits the parent's mask test (and stays mergeable via EmitSubmesh).
                                    // NOTE single level: a parent mask and a child mask share the isolated
                                    // stencil bits, so a child part that is itself masked is not additionally
                                    // parent-clipped, and simultaneous overlap of the two is a known limit.
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
                    else if (b.Kind == ss.runtime.DrawBatchKind.Effect)
                    {
                        for (int k = 0; k < b.Count; k++)
                        {
                            int rank = b.StartRank + k;
                            int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
                            if (p_idx < 0 || p_idx >= partsLength) continue;
                            
                            var plan = _effectPlans[p_idx];
                            if (plan.CommandCount == 0) continue;

                            // Parent-mask inheritance: stencil-test the effect quads when this effect
                            // part is a mask target (same single-level rule as Instance / Normal).
                            StencilRole effRole = StencilRole.None;
                            byte effComp = 0;
                            if (maskActive)
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
                                var cmd = _effectCommands[plan.CommandStart + c];
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
                                        // 結合ジオメトリ内のグローバル quad オフセット (= スロットベース + コマンド内オフセット)。
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
                    else if (b.Kind == ss.runtime.DrawBatchKind.Normal || b.Kind == ss.runtime.DrawBatchKind.Shape || b.Kind == ss.runtime.DrawBatchKind.Mesh)
                    {
                        if (_player._hasMaskParts)
                        {
                            // Phase B (rank order): emit each part per-part. When masking is active each
                            // part carries its stencil role; when suppressed (Canvas) pure-mask parts are
                            // dropped and everything else draws as plain colour (Role None, still merged
                            // by EmitSubmesh). See project_unity_canvas_plan.
                            byte kind = (byte)b.Kind;
                            for (int k = 0; k < b.Count; k++)
                            {
                                int rank = b.StartRank + k;
                                int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
                                if (p_idx < 0 || p_idx >= partsLength) continue;
                                var md = _player._maskPartData[p_idx];
                                if (!maskActive)
                                {
                                    // Canvas: a pure mask contributes no colour; draw nothing for it.
                                    if (md.PureMask) continue;
                                    if (!MaskPartVcIc(p_idx, kind, frame, out int cvc, out int cic)) continue;
                                    EmitMaskStep(kind, rank, cvc, cic, b.TextureHash, (byte)b.BlendType,
                                        StencilRole.None, 0, 0, 0f, ref totalVertices, ref totalIndices);
                                    continue;
                                }
                                if (!MaskPartVcIc(p_idx, kind, frame, out int pvc, out int pic)) continue;
                                if (md.PureMask)
                                {
                                    // draw_as_mask: suppress colour, restore (pre-written in Phase A).
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
                                        // Clipping also writes coverage at its own rank (no restore -> affects later parts).
                                        byte wop = md.MaskInfluence ? StencilOpInvert : StencilOpIncr;
                                        float th = kind == SsDrawBatchKind.Shape ? -1f : (255f - frameData.Parts(p_idx).Value.Mask) / 255f;
                                        EmitMaskStep(kind, rank, pvc, pic, b.TextureHash, (byte)b.BlendType,
                                            StencilRole.Write, wop, 0, th, ref totalVertices, ref totalIndices);
                                    }
                                }
                            }
                            continue; // masked per-part handling replaces the batch-merge below
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
                                Kind = (byte)b.Kind,
                                BlendType = (byte)b.BlendType
                            };

                            EmitSubmesh(totalIndices, ic, b.TextureHash, (byte)b.BlendType);

                            totalVertices += vc;
                            totalIndices += ic;
                        }
                    }
                    else if (b.Kind == ss.runtime.DrawBatchKind.Mask && maskActive)
                    {
                        // PartTypeMask parts arrive as a Mask batch (vc/ic = 0) and are built as a
                        // Normal quad. The write happened in Phase A; restore here at the mask's rank.
                        // Mask twins (texture 0) for draw_as_mask / clipping parts are skipped — those
                        // are handled via their colour batch in the Normal/Shape/Mesh branch above.
                        for (int k = 0; k < b.Count; k++)
                        {
                            int rank = b.StartRank + k;
                            int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
                            if (p_idx < 0 || p_idx >= partsLength) continue;
                            var md = _player._maskPartData[p_idx];
                            if (!md.IsMaskPart) continue;
                            if (!MaskPartVcIc(p_idx, SsDrawBatchKind.Normal, frame, out int mvc, out int mic)) continue;
                            byte rop = md.MaskInfluence ? StencilOpInvert : StencilOpDecr;
                            float th = (255f - frameData.Parts(p_idx).Value.Mask) / 255f;
                            EmitMaskStep(SsDrawBatchKind.Normal, rank, mvc, mic, b.TextureHash, (byte)b.BlendType,
                                StencilRole.Write, rop, 0, th, ref totalVertices, ref totalIndices);
                        }
                    }
                }

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

                if (totalVertices == 0 || totalIndices == 0)
                {
                    _vertexCount = 0;
                    _indexCount = 0;
                    return;
                }

                EnsureCapacity(ref _meshVertices, totalVertices);
                EnsureCapacity(ref _meshIndices, totalIndices);
                EnsureCapacity(ref _meshBounds, 2); // Job が min/max を書き込む
                _vertexCount = totalVertices;
                _indexCount = totalIndices;

                // index アップロードキャッシュ用のトポロジシグネチャ。バッチ構造は生成される index 値と
                // submesh 範囲を一意に決めるため、これが前フレームと同一なら index/submesh は再送不要。
                // (Effect は QuadCount/Offset が毎フレーム変わるので自然に不一致 = キャッシュされない)
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
                    _topologySignature = sig;
                }

                // エフェクトが無くても Job が参照するため、結合配列は最低 1 要素を確保しておく。
                EnsureCapacityPreserve(ref _effectVerts, 1);
                EnsureCapacityPreserve(ref _effectUvs, 1);
                EnsureCapacityPreserve(ref _effectColors, 1);
                EnsureCapacityPreserve(ref _effectIndices, 1);

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
                    EffectVerts = _effectVerts,
                    EffectUvs = _effectUvs,
                    EffectColors = _effectColors,
                    EffectIndices = _effectIndices,
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
        /// 発行済みのメッシュ構築ジョブの完了を待ち、UnityのMeshへ反映する
        /// </summary>
        public void ApplyToMesh(UnityEngine.Mesh targetMesh, bool perBatchSubmesh = false, bool use16BitIndices = false)
        {
            // Meshに適用する前に Job の完了を待機
            _updateJobHandle.Complete();

            // ScheduleMeshUpdate が再構築を省いたフレームは Mesh が前回のまま正しいので、再アップロードも省く。
            if (_frameSkipped) return;

            if (_vertexCount == 0 || _indexCount == 0 || !_meshVertices.IsCreated || !_meshIndices.IsCreated)
            {
                targetMesh.Clear();
                _meshUploaded = false; // 次回はフルアップロード (キャッシュ無効化)
                return;
            }

            // CanvasRenderer rejects low-level SetVertexBufferData meshes on Windows/D3D12 (it issues an
            // invalid GPU draw that removes the device). The Canvas path builds via the high-level Mesh
            // API instead; the world (MeshRenderer) path keeps the fast low-level upload below.
            if (use16BitIndices)
            {
                ApplyToMeshCanvas(targetMesh);
                return;
            }

            // Unity の厳格なバリデーション (Overlapや領域外参照) を回避するためのフラグ
            // DontValidateIndices = 8 (Unity 2021以降)
            var updateFlags = UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds | (UnityEngine.Rendering.MeshUpdateFlags)8;

            // トポロジ (index/submesh 構造) が前回アップロードと完全一致するなら、毎フレーム動く頂点だけ
            // 更新し、index/submesh の再アップロードを省く (考察の「index キャッシュ」)。Mesh インスタンスが
            // 変わった場合 (共有/プレビュー等) は安全側でフルアップロードに倒す。
            int meshId = targetMesh.GetInstanceID();
            bool topoUnchanged = _meshUploaded
                && meshId == _lastMeshId
                && _topologySignature == _uploadedTopologySignature
                && _vertexCount == _uploadedVertexCount
                && _indexCount == _uploadedIndexCount
                && perBatchSubmesh == _uploadedPerBatchSubmesh
                && use16BitIndices == _uploadedUse16BitIndices;

            if (topoUnchanged)
            {
                targetMesh.SetVertexBufferData(_meshVertices, 0, 0, _vertexCount, 0, updateFlags);
                ApplyJobComputedBounds(targetMesh);
                return;
            }

            // --- フルアップロード (初回 or トポロジ変化) ---
            // バッファ再宣言時の領域外参照を避けるため一旦 submesh を空にする。
            targetMesh.SetSubMesh(0, new UnityEngine.Rendering.SubMeshDescriptor(0, 0), updateFlags);
            targetMesh.subMeshCount = 1;

            targetMesh.SetVertexBufferParams(_vertexCount, s_vertexLayout);
            targetMesh.SetVertexBufferData(_meshVertices, 0, 0, _vertexCount, 0, updateFlags);

            // World (MeshRenderer) path is always 32-bit. The Canvas path returns earlier via
            // ApplyToMeshCanvas (high-level API), so use16BitIndices is always false here.
            targetMesh.SetIndexBufferParams(_indexCount, UnityEngine.Rendering.IndexFormat.UInt32);
            targetMesh.SetIndexBufferData(_meshIndices, 0, 0, _indexCount, updateFlags);

            if (perBatchSubmesh && _submeshCount > 0)
            {
                // SetSubMeshes で一括設定する (逐次設定時の Overlap 警告を回避)。
                EnsureCapacity(ref _submeshDescriptors, _submeshCount);
                for (int s = 0; s < _submeshCount; s++)
                {
                    var info = _submeshes[s];
                    _submeshDescriptors[s] = new UnityEngine.Rendering.SubMeshDescriptor(info.IndexStart, info.IndexCount, UnityEngine.MeshTopology.Triangles);
                }
                targetMesh.SetSubMeshes(_submeshDescriptors, 0, _submeshCount, updateFlags);
            }
            else
            {
                targetMesh.subMeshCount = 1;
                var subMeshDescriptor = new UnityEngine.Rendering.SubMeshDescriptor(0, _indexCount, UnityEngine.MeshTopology.Triangles);
                targetMesh.SetSubMesh(0, subMeshDescriptor, updateFlags);
            }
            ApplyJobComputedBounds(targetMesh);

            // アップロード済みトポロジを記録 (次フレーム以降の index 再送スキップ判定に使う)。
            _meshUploaded = true;
            _lastMeshId = meshId;
            _uploadedTopologySignature = _topologySignature;
            _uploadedVertexCount = _vertexCount;
            _uploadedIndexCount = _indexCount;
            _uploadedPerBatchSubmesh = perBatchSubmesh;
            _uploadedUse16BitIndices = use16BitIndices;

            // バッファは破棄せず再利用する (Dispose() でのみ解放)
        }

        /// <summary>
        /// Canvas (uGUI) mesh upload. CanvasRenderer does not reliably consume a mesh built with the
        /// low-level SetVertexBufferData path on Windows/D3D12 — it issues an invalid GPU draw that removes
        /// the device. The high-level Mesh API produces the same layout uGUI's VertexHelper.FillMesh uses,
        /// which CanvasRenderer accepts. The job's packed SsVertex stream is de-interleaved into reused
        /// scratch channels (zero managed alloc) and indices are narrowed to 16-bit (CanvasRenderer rejects
        /// 32-bit index meshes). A full rebuild runs each call — UI geometry animates per frame anyway — so
        /// the low-level topology cache is bypassed and invalidated.
        /// </summary>
        private void ApplyToMeshCanvas(UnityEngine.Mesh targetMesh)
        {
            // Index VALUES must fit in ushort, i.e. vertexCount <= 65535 (the index COUNT is unconstrained).
            if (_vertexCount > 65535 && !_warned16BitOverflow)
            {
                UnityEngine.Debug.LogWarning("[SpriteStudio] Canvas mesh exceeds 65535 vertices; CanvasRenderer requires 16-bit indices so geometry will wrap. Reduce part/particle count or use the world (MeshRenderer) player.");
                _warned16BitOverflow = true;
            }

            EnsureCapacity(ref _canvasPositions, _vertexCount);
            EnsureCapacity(ref _canvasColors, _vertexCount);
            EnsureCapacity(ref _canvasUv0, _vertexCount);
            EnsureCapacity(ref _canvasUv1, _vertexCount);
            EnsureCapacity(ref _meshIndices16, _indexCount);

            unsafe
            {
                var src = (SsVertex*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_meshVertices);
                var pos = (Vector3*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_canvasPositions);
                var col = (Color32*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_canvasColors);
                var uv0 = (Vector2*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_canvasUv0);
                var uv1 = (Vector4*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_canvasUv1);
                for (int i = 0; i < _vertexCount; i++)
                {
                    var v = src[i];
                    pos[i] = v.Position;
                    col[i] = v.Color;
                    uv0[i] = v.TexCoord0;
                    uv1[i] = v.TexCoord1;
                }
                var si = (uint*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_meshIndices);
                var di = (ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_meshIndices16);
                for (int i = 0; i < _indexCount; i++) di[i] = (ushort)si[i];
            }

            // High-level upload (per channel). SetVertices first so the colour/uv channels size to it.
            targetMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
            targetMesh.SetVertices(_canvasPositions, 0, _vertexCount);
            targetMesh.SetColors(_canvasColors, 0, _vertexCount);
            targetMesh.SetUVs(0, _canvasUv0, 0, _vertexCount);
            targetMesh.SetUVs(1, _canvasUv1, 0, _vertexCount);
            targetMesh.SetIndices(_meshIndices16, 0, _indexCount, UnityEngine.MeshTopology.Triangles, 0, false);
            ApplyJobComputedBounds(targetMesh);

            _meshUploaded = false; // Canvas path bypasses the low-level topology cache.
        }

        /// <summary>
        /// UpdateMeshJob が算出した min/max を Mesh.bounds へ適用する。
        /// 未算出時は Mesh.RecalculateBounds() にフォールバックする。
        /// </summary>
        private void ApplyJobComputedBounds(UnityEngine.Mesh targetMesh)
        {
            if (!_meshBounds.IsCreated || _meshBounds.Length < 2)
            {
                targetMesh.RecalculateBounds();
                return;
            }
            Vector3 mn = _meshBounds[0];
            Vector3 mx = _meshBounds[1];
            targetMesh.bounds = new Bounds((mn + mx) * 0.5f, mx - mn);
        }

        private unsafe EffectDrawPlanData AppendEffectDrawPlan(byte* fb, int len)
        {
            if (fb == null || len <= 0) return default;

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
            // ゼロコピー: ネイティブポインタを直接 ByteBuffer に載せる (allocator/ByteBuffer は使い回し)。
            // draw_plan は本メソッド内で全ジオメトリを結合 NativeArray へコピーし切るため寿命が完結し安全。
            if (_planAllocator == null)
            {
                _planAllocator = new NativeByteBufferAllocator();
                _planByteBuffer = new Google.FlatBuffers.ByteBuffer(_planAllocator, 0);
            }
            _planAllocator.Reset(fb, len);
            _planByteBuffer.Position = 0;
#else
            // ネイティブ → 再利用 byte[] へコピーして ByteBuffer に包む (フォールバック)。
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

            // このスロットのグローバル quad ベース (= これまでに詰めた quad 数 = indices/6)。
            int quadBase = _effectIndexCount / 6;

            // ジオメトリ (verts/uvs/colors/indices) は fbs で連続 scalar vector。旧実装の per-element 読み
            // (plan.Verts(j) を粒子数×要素ぶんループ) は実測で処理時間の ~49% を占めた。Table の public API で
            // vector 先頭の絶対 byte 位置を取り、生ポインタ fb から結合バッファへ一括 memcpy する (fbs コメントの
            // "one memcpy per command" 設計どおり)。fb と _planByteBuffer は同一バイト列なので位置は共通。
            // vtable offset = 4 + field_index*2、fbs 定義順: commands(0)/vert_stride(1)/verts(2)/uvs(3)/colors(4)/indices(5)。
            var t = new Google.FlatBuffers.Table(
                _planByteBuffer.GetInt(_planByteBuffer.Position) + _planByteBuffer.Position, _planByteBuffer);

            EnsureCapacityPreserve(ref _effectVerts, _effectVertFloatCount + vLen);
            CopyFbVector(t, 8, fb, _effectVerts, _effectVertFloatCount, vLen);
            _effectVertFloatCount += vLen;

            EnsureCapacityPreserve(ref _effectUvs, _effectUvFloatCount + uLen);
            CopyFbVector(t, 10, fb, _effectUvs, _effectUvFloatCount, uLen);
            _effectUvFloatCount += uLen;

            EnsureCapacityPreserve(ref _effectColors, _effectColorFloatCount + cLen);
            CopyFbVector(t, 12, fb, _effectColors, _effectColorFloatCount, cLen);
            _effectColorFloatCount += cLen;

            EnsureCapacityPreserve(ref _effectIndices, _effectIndexCount + iLen);
            CopyFbVector(t, 14, fb, _effectIndices, _effectIndexCount, iLen);
            _effectIndexCount += iLen;

            // commands を抽出 (描画順ループでテクスチャ/ブレンド/範囲を参照する)。
            EnsureCapacityPreserve(ref _effectCommands, _effectCommandCount + cmdLen);
            int cmdStart = _effectCommandCount;
            for (int c = 0; c < cmdLen; c++)
            {
                var cc = plan.Commands(c);
                // Zero-fill a missing command (QuadCount=0 makes the draw loop skip it reliably).
                // Skipping the fill with `continue` would leave last frame's stale value here,
                // causing the wrong quad range to be read.
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

        // FlatBuffer の scalar vector (vtable offset vtoff) を生バッファ fb から dst[dstOffset..] へ一括 memcpy する。
        // n は generated accessor 由来の要素数。vector データは fb 内に連続配置され、位置は Table の public API で取る。
        private static unsafe void CopyFbVector<T>(Google.FlatBuffers.Table t, int vtoff, byte* fb, NativeArray<T> dst, int dstOffset, int n) where T : unmanaged
        {
            if (n <= 0) return;
            int o = t.__offset(vtoff);
            if (o == 0) return; // フィールド欠落 (required のはずだが防御的に)
            UnityEngine.Debug.Assert(t.__vector_len(o) == n, "CopyFbVector: vtable offset と要素数が不一致 (offset 定義ズレの疑い)");
            int pos = t.__vector(o); // vector 先頭要素の絶対 byte 位置
            int elem = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<T>();
            byte* d = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(dst) + (long)dstOffset * elem;
            Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(d, fb + pos, (long)n * elem);
        }
private unsafe void BuildMaskWriters(ss.runtime.FrameData frameData, int drawOrderLength, int partsLength)
        {
            _maskWriterCount = 0;
            if (!_player._hasMaskParts || _player._maskPartData == null) return;
            EnsureCapacity(ref _maskWriters, drawOrderLength < 1 ? 1 : drawOrderLength);
            for (int rank = 0; rank < drawOrderLength; rank++)
            {
                int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
                if (p_idx < 0 || p_idx >= partsLength || p_idx >= _player._maskPartData.Length) continue;
                var md = _player._maskPartData[p_idx];
                if (!md.Writes) continue;
                var ps = frameData.Parts(p_idx);
                if (!ps.HasValue) continue;
                var pWriter = (MaskWriter*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskWriters);
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
        private unsafe void BuildMaskPartLookup(ss.runtime.FrameData frameData, int drawBatchesLength, int drawOrderLength, int partsLength)
        {
            if (!_maskPartTexHash.IsCreated || _maskPartTexHash.Length < partsLength)
            {
                EnsureCapacity(ref _maskPartTexHash, partsLength < 1 ? 1 : partsLength);
                EnsureCapacity(ref _maskPartBlend, partsLength < 1 ? 1 : partsLength);
            }
            Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(
                Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartTexHash),
                (long)partsLength * sizeof(uint));
            Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(
                Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_maskPartBlend),
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
                    int p_idx = ((ushort*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_drawOrder))[rank];
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
                Kind = kind,
                BlendType = blend
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
