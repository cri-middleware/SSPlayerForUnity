using System;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Layer 2: 共通ロジック層 (Internal Player)
    /// ネイティブ (Rust) 側との FFI コンテキスト (void*) を保持し、
    /// アニメーションの Tick と Job System を用いたメッシュ構築を管理します。
    /// ライフサイクル管理（IDisposable）を厳格に行い、メモリリークを防ぎます。
    /// </summary>
    public class SpriteStudioInternalPlayer : IDisposable
    {
        // ネイティブポインタを安全に扱うための ECS ラッパー
        private SsPlayerHandle _handle;

        // 実行中の Burst Job のハンドル（破棄時や次フレーム更新時の同期に必須）
        private JobHandle _updateJobHandle;

        // SSAB データの固定用（Rust 側にメモリを Borrow させるため）
        private System.Runtime.InteropServices.GCHandle _ssabPin;
        private unsafe void* _resourceHandle;

        private struct EffectSlotState
        {
            public System.IntPtr EffectSlot;
        }
        private EffectSlotState[] _effectSlots;

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

        public delegate byte[] AssetResolver(uint packHash, uint nameHash);
        private AssetResolver _assetResolver;

        private struct InstanceChildState
        {
            public System.IntPtr InstanceSlot;
            public SpriteStudioInternalPlayer Player;
            public float PreviousFrame;
        }
        private InstanceChildState[] _instanceChildren;
        private NativeArray<InstanceMeshPointer> _instanceMeshes;
        
        public JobHandle CurrentUpdateJobHandle => _updateJobHandle;

        /// <summary>
        /// 頂点位置に掛ける一様スケール (= 1 / PixelsPerUnit)。既定 1 (= ピクセル等倍)。
        /// トップレベルプレイヤーのみ設定し、Instance 子は 1 のまま据え置く
        /// (子は親に px のまま取り込まれ、親側で一括スケールされるため。二重スケール防止)。
        /// </summary>
        public float VertexScale { get; set; } = 1f;

        public SpriteStudioInternalPlayer()
        {
            unsafe
            {
                _handle = new SsPlayerHandle { ptr = NativeMethods.ss_runtime_create() };
                // 既定値 (YUp / Direct) に依存せず明示設定し、SDK 既定が将来変わっても巻き込まれないようにする。
                //  coordinate_system=0(YUp): Unity ワールドは Y-up。get_local_vertices は flip 適用済みで
                //    返るためワールド行列側で Y を再反転しない (二重反転防止)。
                //  render_mode=0(Direct): SoA ゲッター経路 (本実装が前提とする経路) を使う。
                if (_handle.IsValid)
                {
                    NativeMethods.ss_context_set_coordinate_system(_handle.ptr, 0);
                    NativeMethods.ss_runtime_set_render_mode(_handle.ptr, (byte)0);
                }
            }
        }

        /// <summary>
        /// SsabData (バイト配列) を読み込んでリソースをバインドする
        /// </summary>
        // Instance パーツの入れ子上限。万一データに循環参照があっても無限再帰でハングしないための保険。
        private const int MaxInstanceDepth = 16;
        private int _instanceDepth;

        public void LoadSsab(byte[] ssabData, AssetResolver resolver = null)
        {
            LoadSsabInternal(ssabData, resolver, 0);
        }

        /// <summary>LoadSsab の実体。depth は Instance 入れ子の深さ (循環参照ガード用)。</summary>
        private void LoadSsabInternal(byte[] ssabData, AssetResolver resolver, int depth)
        {
            _assetResolver = resolver;
            _instanceDepth = depth;
            if (!_handle.IsValid || ssabData == null || ssabData.Length == 0) return;

            // 既存のピン留めがあれば解除
            if (_ssabPin.IsAllocated)
            {
                _ssabPin.Free();
            }

            // バイト配列のメモリを固定して Rust 側へ Borrow させる（ゼロコピー）
            _ssabPin = System.Runtime.InteropServices.GCHandle.Alloc(ssabData, System.Runtime.InteropServices.GCHandleType.Pinned);

            unsafe
            {
                if (_resourceHandle != null)
                {
                    NativeMethods.ss_resource_destroy(_resourceHandle);
                    _resourceHandle = null;
                }
                byte* ptr = (byte*)_ssabPin.AddrOfPinnedObject();
                _resourceHandle = NativeMethods.ss_resource_create_borrow(ptr, (nuint)ssabData.Length);
                NativeMethods.ss_runtime_load_ssab_borrow(_handle.ptr, ptr, (nuint)ssabData.Length);
            }
            
            var bb = new Google.FlatBuffers.ByteBuffer(ssabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
            SetupSlots(ssab);
        }

        private void SetupSlots(ss.format.SsAnimeBinary binary)
        {
            unsafe
            {
                if (_effectSlots != null)
                {
                    for (int i = 0; i < _effectSlots.Length; i++)
                    {
                        if (_effectSlots[i].EffectSlot != System.IntPtr.Zero)
                        {
                            NativeMethods.ss_effect_slot_destroy((void*)_effectSlots[i].EffectSlot);
                        }
                    }
                    _effectSlots = null;
                }
                
                if (_instanceChildren != null)
                {
                    for (int i = 0; i < _instanceChildren.Length; i++)
                    {
                        if (_instanceChildren[i].InstanceSlot != System.IntPtr.Zero)
                        {
                            NativeMethods.ss_instance_slot_destroy((void*)_instanceChildren[i].InstanceSlot);
                        }
                        if (_instanceChildren[i].Player != null)
                        {
                            _instanceChildren[i].Player.Dispose();
                        }
                    }
                    _instanceChildren = null;
                }

                if (_resourceHandle == null) return;
                
                int partsLength = binary.PartsLength;
                if (partsLength == 0) return;

                _effectSlots = new EffectSlotState[partsLength];
                _instanceChildren = new InstanceChildState[partsLength];
                
                for (int i = 0; i < partsLength; i++)
                {
                    var pObj = binary.Parts(i);
                    if (!pObj.HasValue) continue;
                    var p = pObj.Value;
                    
                    if (p.PartTypeType == ss.format.PartType.PartTypeEffect)
                    {
                        var pt = p.PartTypeAsPartTypeEffect();
                        uint refHash = pt.RefEffectNameHash;
                        uint seed = (uint)i ^ refHash;
                        void* slot = NativeMethods.ss_effect_slot_create(_resourceHandle, refHash, seed);
                        _effectSlots[i].EffectSlot = (System.IntPtr)slot;
                    }
                    else if (p.PartTypeType == ss.format.PartType.PartTypeInstance)
                    {
                        if (_instanceDepth + 1 >= MaxInstanceDepth)
                        {
                            UnityEngine.Debug.LogWarning($"[SpriteStudioInternalPlayer] Instance nesting exceeded {MaxInstanceDepth} levels at part {i}; skipping to avoid cyclic references.");
                            continue;
                        }

                        var pt = p.PartTypeAsPartTypeInstance();
                        uint packHash = pt.RefAnimePackHash;
                        uint nameHash = pt.RefAnimeNameHash;

                        // 子アニメの SSAB を解決する。同一パック内なら自分自身の SSAB、別パックなら
                        // 外部参照の SSAB が返る (SpriteStudioAnimeAsset.ResolveExternalSsab 参照)。
                        // 実行時はファイルを読まず、インポート時に確定したハードリファレンスを辿るだけ。
                        byte[] childSsab = _assetResolver?.Invoke(packHash, nameHash);
                        if (childSsab == null)
                        {
                            UnityEngine.Debug.LogWarning($"[SpriteStudioInternalPlayer] Instance part {i}: referenced animation (pack=0x{packHash:X8}, name=0x{nameHash:X8}) could not be resolved. Check the AnimeAsset's external references / pack hash.");
                            continue;
                        }

                        var childPlayer = new SpriteStudioInternalPlayer { SubFrameEnabled = SubFrameEnabled };
                        childPlayer.LoadSsabInternal(childSsab, _assetResolver, _instanceDepth + 1);

                        if (!childPlayer.SetupAnimationByHash(nameHash))
                        {
                            UnityEngine.Debug.LogWarning($"[SpriteStudioInternalPlayer] Instance part {i}: animation hash 0x{nameHash:X8} not found in the resolved SSAB.");
                            childPlayer.Dispose();
                            continue;
                        }
                        childPlayer.Stop(); // 親に駆動されるまで停止状態で待機 (可視性は step が制御)

                        void* slot = NativeMethods.ss_instance_slot_create();
                        _instanceChildren[i] = new InstanceChildState
                        {
                            InstanceSlot = (System.IntPtr)slot,
                            Player = childPlayer,
                            PreviousFrame = 0f
                        };
                    }
                }
            }
        }

        public bool SetupAnimationByHash(uint animationNameHash)
        {
            if (!_handle.IsValid) return false;
            unsafe
            {
                return NativeMethods.ss_runtime_setup_animation_by_hash(_handle.ptr, animationNameHash);
            }
        }

        #region Playback Control
        public bool IsPlaying() { unsafe { return _handle.IsValid && NativeMethods.ss_runtime_is_playing(_handle.ptr); } }
        public void Play(float startFrame = -1.0f)
        {
            unsafe
            {
                if (!_handle.IsValid) return;
                if (startFrame < 0.0f) NativeMethods.ss_runtime_play(_handle.ptr);
                else NativeMethods.ss_runtime_play_with_start_frame(_handle.ptr, startFrame);
            }
        }
        public bool IsPausing() { unsafe { return _handle.IsValid && NativeMethods.ss_runtime_is_pausing(_handle.ptr); } }
        public void Pause() { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_pause(_handle.ptr); } }
        public void Stop() { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_stop(_handle.ptr); } }

        public void SetSpeed(float speed) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_animation_speed(_handle.ptr, speed); } }

        public void SetFrame(float frame) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_frame_no(_handle.ptr, frame); } }
        public float GetFrame() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_frame_no(_handle.ptr) : 0.0f; } }
        public int GetTotalFrames() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_end_frame(_handle.ptr) + 1 : 0; } }

        public void SetFrameRate(int fps) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_frame_rate(_handle.ptr, fps); } }
        public int GetFrameRate() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_fps(_handle.ptr) : 0; } }

        public void SetAnimationSection(int start, int end) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_animation_section(_handle.ptr, start, end); } }
        public int GetAnimationSectionStart() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_start_frame(_handle.ptr) : 0; } }
        public int GetAnimationSectionEnd() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_end_frame(_handle.ptr) : 0; } }

        public void SetPlaybackDirection(int direction, int style) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_playback_direction(_handle.ptr, direction, style); } }
        public int GetPlaybackDirection() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_playback_direction(_handle.ptr) : 0; } }
        public int GetPlaybackStyle() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_playback_style(_handle.ptr) : 0; } }

        public void SetLoop(int count) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_loop(_handle.ptr, count); } }
        public int GetLoop() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_loops(_handle.ptr) : 0; } }

        public void SetSkipFrames(bool skip) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_skip_frames(_handle.ptr, skip); } }
        public bool IsSkipFrames() { unsafe { return _handle.IsValid && NativeMethods.ss_runtime_get_skip_frames(_handle.ptr); } }

        public bool SubFrameEnabled { get; set; } = false;
        #endregion

        public bool SetupAnimation(string animationName)
        {
            if (!_handle.IsValid || string.IsNullOrEmpty(animationName)) return false;
            unsafe
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(animationName + "\0");
                fixed (byte* p = bytes)
                {
                    return NativeMethods.ss_runtime_setup_animation(_handle.ptr, p);
                }
            }
        }

        public void SetPlaybackStyle(int direction, int style)
        {
            if (!_handle.IsValid) return;
            unsafe { NativeMethods.ss_runtime_set_playback_direction(_handle.ptr, direction, style); }
        }

        /// <summary>
        /// 毎フレームのアニメーション更新処理
        /// </summary>
        /// <param name="deltaTime">経過時間</param>
        public void Tick(float deltaTime, bool subFrameEnabled)
        {
            // 前回フレームで投げた Job が終わるのを確実に待機する (純粋なゼロコピーパイプラインのため)
            _updateJobHandle.Complete();

            if (!_handle.IsValid) return;

            unsafe
            {
                // Tick is executed strictly sequentially on the main thread (state mutability boundary)
                // ss_runtime_update は delta を「ミリ秒」で受け取る (update_interval = 1000/fps)。
                // Unity の Time.deltaTime は秒なので 1000 倍する (Godot 実装も delta_seconds * 1000)。
                NativeMethods.ss_runtime_update(_handle.ptr, deltaTime * 1000.0f);
            }
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

        // index アップロードのキャッシュ用。トポロジ (バッチ構造) が不変なら ApplyToMesh で
        // index/submesh の再アップロードを省き、頂点のみ更新する。
        private ulong _topologySignature;
        private bool _meshUploaded;
        private int _lastMeshId;
        private ulong _uploadedTopologySignature;
        private int _uploadedVertexCount;
        private int _uploadedIndexCount;
        private bool _uploadedPerBatchSubmesh;

        // バッチ単位の SubMesh 情報 (MeshRenderer 経路のマルチマテリアル用)。
        // Job では使わず main thread でのみ参照するため managed 配列で保持する。
        public struct SubmeshInfo { public int IndexStart; public int IndexCount; public uint TextureHash; }
        private SubmeshInfo[] _submeshes;
        private int _submeshCount;

        public int VertexCount => _vertexCount;
        public int IndexCount => _indexCount;
        public int DrawBatchCount => _drawBatchCount;
        public int SubmeshCount => _submeshCount;
        public SubmeshInfo[] Submeshes => _submeshes;
        public NativeArray<DrawBatchData> DrawBatches => _drawBatches;
        public NativeArray<SsVertex> MeshVertices => _meshVertices;
        public NativeArray<uint> MeshIndices => _meshIndices;

        /// <summary>直近の ScheduleMeshUpdate で生成された描画バッチ (= SubMesh) の数。</summary>
        // public int SubmeshCount => _submeshCount;

        /// <summary>指定 SubMesh のテクスチャハッシュ (DrawBatch.TextureHash)。</summary>
        public uint GetSubmeshTextureHash(int index) => _submeshes[index].TextureHash;

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
        /// Update実行後、FFIからSoA配列のポインタを取得してメッシュ構築ジョブを発行する
        /// </summary>
        public void ScheduleMeshUpdate(float deltaTime)
        {
            if (!_handle.IsValid) return;

            // 永続バッファを書き換える前に、前フレームのジョブ完了を保証する
            _updateJobHandle.Complete();

            float drawFrameNo = SubFrameEnabled ? GetFrame() : UnityEngine.Mathf.Floor(GetFrame());

            unsafe
            {
                byte* frameDataPtr = null;
                nuint frameDataLen = 0;
                NativeMethods.ss_runtime_get_frame_data(_handle.ptr, drawFrameNo, &frameDataPtr, &frameDataLen);

                if (frameDataPtr == null || frameDataLen == 0)
                {
                    _vertexCount = 0;
                    _indexCount = 0;
                    return;
                }

                int len = (int)frameDataLen;
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

                NativeMethods.ss_runtime_get_world_matrices(_handle.ptr, &frame.WorldMatrices, &frame.WorldMatricesLen);
                NativeMethods.ss_runtime_get_local_uvs(_handle.ptr, &frame.LocalUvs, &frame.LocalUvsLen);
                NativeMethods.ss_runtime_get_local_vertices(_handle.ptr, &frame.LocalVertices, &frame.LocalVerticesLen);

                NativeMethods.ss_runtime_get_shape_vertices(_handle.ptr, &frame.ShapeVertices, &frame.ShapeVerticesLen);
                NativeMethods.ss_runtime_get_shape_vertex_box_coords(_handle.ptr, &frame.ShapeBoxCoords, &frame.ShapeBoxCoordsLen);
                NativeMethods.ss_runtime_get_shape_vertex_counts(_handle.ptr, &frame.ShapeVertexCounts, &frame.ShapeVertexCountsLen);

                NativeMethods.ss_runtime_get_mesh_vertices_x(_handle.ptr, &frame.MeshVerticesX, &frame.MeshVerticesXLen);
                NativeMethods.ss_runtime_get_mesh_vertices_y(_handle.ptr, &frame.MeshVerticesY, &frame.MeshVerticesYLen);
                NativeMethods.ss_runtime_get_mesh_vertex_offsets(_handle.ptr, &frame.MeshVertexOffsets, &frame.MeshVertexOffsetsLen);
                NativeMethods.ss_runtime_get_mesh_uvs(_handle.ptr, &frame.MeshUvs, &frame.MeshUvsLen);
                NativeMethods.ss_runtime_get_mesh_indices(_handle.ptr, &frame.MeshIndices, &frame.MeshIndicesLen);
                NativeMethods.ss_runtime_get_mesh_index_offsets(_handle.ptr, &frame.MeshIndexOffsets, &frame.MeshIndexOffsetsLen);

                int drawBatchesLength = frameData.DrawBatchesLength;
                int drawOrderLength = frameData.DrawOrderLength;
                int partsLength = frameData.PartsLength;
                int partsColorLength = frameData.PartsColorLength;

                EnsureCapacity(ref _effectPlans, partsLength);
                EnsureCapacity(ref _instanceMeshes, partsLength);

                bool parentLooped = NativeMethods.ss_runtime_is_looped(_handle.ptr);
                float fps = GetFrameRate();
                if (fps <= 0f) fps = 60f;

                NativeArray<JobHandle> childHandles = new NativeArray<JobHandle>(partsLength, Allocator.Temp);
                int childHandleCount = 0;

                // エフェクト結合バッファを毎フレーム先頭から詰め直す。
                _effectCommandCount = 0;
                _effectVertFloatCount = 0;
                _effectUvFloatCount = 0;
                _effectColorFloatCount = 0;
                _effectIndexCount = 0;

                for (int i = 0; i < partsLength; i++)
                {
                    if (_effectSlots != null && i < _effectSlots.Length && _effectSlots[i].EffectSlot != System.IntPtr.Zero)
                    {
                        var ev = NativeMethods.ss_runtime_get_active_effect_event(_handle.ptr, (uint)i);
                        var step = NativeMethods.ss_effect_slot_step((void*)_effectSlots[i].EffectSlot, ev, drawFrameNo, deltaTime, fps, parentLooped, false, 3);
                        
                        if (step.visible && step.draw_plan_len > 0)
                        {
                            _effectPlans[i] = AppendEffectDrawPlan(step.draw_plan_buf, (int)step.draw_plan_len);
                        }
                        else
                        {
                            _effectPlans[i] = default;
                        }
                    }
                    else
                    {
                        _effectPlans[i] = default;
                    }
                    
                    if (_instanceChildren != null && i < _instanceChildren.Length && _instanceChildren[i].InstanceSlot != System.IntPtr.Zero)
                    {
                        var childPlayer = _instanceChildren[i].Player;
                        if (childPlayer != null)
                        {
                            var ev = NativeMethods.ss_runtime_get_active_event_instance(_handle.ptr, (uint)i);
                            var step = NativeMethods.ss_instance_slot_step((void*)_instanceChildren[i].InstanceSlot, ev, childPlayer._handle.ptr, _instanceChildren[i].PreviousFrame, drawFrameNo, deltaTime, parentLooped);
                            
                            if (step.transitioned)
                            {
                                childPlayer.Play(); // Trigger side effects (UnityEvents) if needed
                            }
                            
                            if (step.visible)
                            {
                                float childDrawFrame = childPlayer.SubFrameEnabled ? step.child_frame_no : UnityEngine.Mathf.Floor(step.child_frame_no);
                                _instanceChildren[i].PreviousFrame = childDrawFrame;
                                childPlayer.SetFrame(step.child_frame_no);
                                childPlayer.ScheduleMeshUpdate(deltaTime);
                                
                                childHandles[childHandleCount++] = childPlayer.CurrentUpdateJobHandle;
                                
                                _instanceMeshes[i] = new InstanceMeshPointer {
                                    Vertices = (SsVertex*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(childPlayer.MeshVertices),
                                    Indices = (uint*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(childPlayer.MeshIndices),
                                    VertexCount = childPlayer.VertexCount,
                                    IndexCount = childPlayer.IndexCount
                                };
                            }
                            else
                            {
                                _instanceMeshes[i] = default;
                            }
                        }
                    }
                    else
                    {
                        _instanceMeshes[i] = default;
                    }
                }

                int maxBatches = drawBatchesLength;
                for (int i = 0; i < partsLength; i++)
                {
                    if (_effectPlans[i].CommandCount > 0)
                        maxBatches += _effectPlans[i].CommandCount;

                    if (_instanceMeshes[i].Vertices != null)
                    {
                        // Instance は子メッシュ全体を 1 draw batch として取り込むが、テクスチャ単位の
                        // SubMesh は子の submesh 数ぶん発生する。maxBatches は _drawBatches と _submeshes の
                        // 両方をサイズ確保するため、submesh 数 + 1 (draw batch ぶん) を見込む。
                        var child = _instanceChildren[i].Player;
                        maxBatches += child.SubmeshCount + 1;
                    }
                }

                // 永続バッファを grow-only で確保 (毎フレーム new しない)
                EnsureCapacity(ref _drawBatches, maxBatches);
                EnsureCapacity(ref _drawOrder, drawOrderLength);
                EnsureCapacity(ref _partStates, partsLength);
                EnsureCapacity(ref _partColors, partsColorLength);

                _drawBatchCount = 0; // Will be incremented dynamically
                _partCount = partsLength;
                _partColorCount = partsColorLength;

                int totalVertices = 0;
                int totalIndices = 0;
                _submeshCount = 0;
                if (_submeshes == null || _submeshes.Length < maxBatches)
                {
                    _submeshes = new SubmeshInfo[maxBatches < 1 ? 1 : maxBatches];
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
                            int p_idx = frameData.DrawOrder(rank);
                            if (p_idx < 0 || p_idx >= partsLength) continue;

                            var meshPtr = _instanceMeshes[p_idx];
                            if (meshPtr.Vertices != null && meshPtr.VertexCount > 0)
                            {
                                var childPlayer = _instanceChildren[p_idx].Player;
                                int cvc = childPlayer.VertexCount;
                                int cic = childPlayer.IndexCount;

                                // 子メッシュは頂点/インデックスが子バッファ上で連続しているため、1 つの
                                // Instance バッチとして丸ごと取り込む (Job の BuildInstance が meshPtr 全体を
                                // 1 回でコピーする)。子が複数バッチ (複数テクスチャ) でも、ここで複数の
                                // DrawBatchData を積むと BuildInstance が子メッシュをバッチ数ぶん重複コピーして
                                // バッファ溢れ・描画破綻を起こすため不可。テクスチャ単位の分割は下の SubmeshInfo が担う。
                                _drawBatches[_drawBatchCount++] = new DrawBatchData
                                {
                                    Kind = SsDrawBatchKind.Instance,
                                    EffectPartIndex = p_idx,
                                    VertexCount = cvc,
                                    IndexCount = cic
                                };

                                for (int sm = 0; sm < childPlayer.SubmeshCount; sm++)
                                {
                                    var childSubmesh = childPlayer.Submeshes[sm];
                                    _submeshes[_submeshCount++] = new SubmeshInfo
                                    {
                                        IndexStart = totalIndices + childSubmesh.IndexStart,
                                        IndexCount = childSubmesh.IndexCount,
                                        TextureHash = childSubmesh.TextureHash
                                    };
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
                            int p_idx = frameData.DrawOrder(rank);
                            if (p_idx < 0 || p_idx >= partsLength) continue;
                            
                            var plan = _effectPlans[p_idx];
                            if (plan.CommandCount == 0) continue;

                            for (int c = 0; c < plan.CommandCount; c++)
                            {
                                var cmd = _effectCommands[plan.CommandStart + c];
                                if (cmd.QuadCount > 0)
                                {
                                    int cmdVc = (int)cmd.QuadCount * 4;
                                    int cmdIc = (int)cmd.QuadCount * 6;

                                    _drawBatches[_drawBatchCount++] = new DrawBatchData
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
                                    
                                    _submeshes[_submeshCount++] = new SubmeshInfo
                                    {
                                        IndexStart = totalIndices,
                                        IndexCount = cmdIc,
                                        TextureHash = cmd.CellmapHash
                                    };
                                    
                                    totalVertices += cmdVc;
                                    totalIndices += cmdIc;
                                }
                            }
                        }
                    }
                    else if (b.Kind == ss.runtime.DrawBatchKind.Normal || b.Kind == ss.runtime.DrawBatchKind.Shape || b.Kind == ss.runtime.DrawBatchKind.Mesh)
                    {
                        for (int k = 0; k < b.Count; k++)
                        {
                            int rank = b.StartRank + k;
                            int p_idx = frameData.DrawOrder(rank);
                            if (p_idx < 0 || p_idx >= partsLength) continue;

                            if (b.Kind == ss.runtime.DrawBatchKind.Normal)
                            {
                                var p = frameData.Parts(p_idx).Value;
                                bool needsCenter = ((ulong)p.UpdateFlag & SsUpdateFlag.NeedsCenterMask) != 0;
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
                            _drawBatches[_drawBatchCount++] = new DrawBatchData
                            {
                                StartRank = (ushort)b.StartRank,
                                Count = (ushort)b.Count,
                                VertexCount = vc,
                                IndexCount = ic,
                                Kind = (byte)b.Kind
                            };

                            _submeshes[_submeshCount++] = new SubmeshInfo
                            {
                                IndexStart = totalIndices,
                                IndexCount = ic,
                                TextureHash = b.TextureHash
                            };

                            totalVertices += vc;
                            totalIndices += ic;
                        }
                    }
                }

                for (int i = 0; i < drawOrderLength; i++)
                {
                    _drawOrder[i] = frameData.DrawOrder(i);
                }

                for (int i = 0; i < partsLength; i++)
                {
                    var p = frameData.Parts(i).Value;
                    _partStates[i] = new PartStateData
                    {
                        UpdateFlag = (ulong)p.UpdateFlag,
                        Alpha = p.Alpha,
                        PartColorIndex = p.PartColor
                    };
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
                        var b = _drawBatches[i];
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
                    deps = JobHandle.CombineDependencies(childHandles.GetSubArray(0, childHandleCount));
                }
                childHandles.Dispose();

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
                    InvScale = VertexScale,

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
                    Indices = _meshIndices
                }.Schedule(deps);
            }
        }

        /// <summary>
        /// 発行済みのメッシュ構築ジョブの完了を待ち、UnityのMeshへ反映する
        /// </summary>
        public void ApplyToMesh(UnityEngine.Mesh targetMesh, bool perBatchSubmesh = false)
        {
            // Meshに適用する前に Job の完了を待機
            _updateJobHandle.Complete();

            if (_vertexCount == 0 || _indexCount == 0 || !_meshVertices.IsCreated || !_meshIndices.IsCreated)
            {
                targetMesh.Clear();
                _meshUploaded = false; // 次回はフルアップロード (キャッシュ無効化)
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
                && perBatchSubmesh == _uploadedPerBatchSubmesh;

            if (topoUnchanged)
            {
                targetMesh.SetVertexBufferData(_meshVertices, 0, 0, _vertexCount, 0, updateFlags);
                targetMesh.RecalculateBounds();
                return;
            }

            // --- フルアップロード (初回 or トポロジ変化) ---
            // バッファ再宣言時の領域外参照を避けるため一旦 submesh を空にする。
            targetMesh.SetSubMesh(0, new UnityEngine.Rendering.SubMeshDescriptor(0, 0), updateFlags);
            targetMesh.subMeshCount = 1;

            targetMesh.SetVertexBufferParams(_vertexCount,
                new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Position, UnityEngine.Rendering.VertexAttributeFormat.Float32, 3),
                new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Color, UnityEngine.Rendering.VertexAttributeFormat.UNorm8, 4),
                new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.TexCoord0, UnityEngine.Rendering.VertexAttributeFormat.Float32, 2),
                new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.TexCoord1, UnityEngine.Rendering.VertexAttributeFormat.Float32, 4)
            );
            targetMesh.SetIndexBufferParams(_indexCount, UnityEngine.Rendering.IndexFormat.UInt32);

            targetMesh.SetVertexBufferData(_meshVertices, 0, 0, _vertexCount, 0, updateFlags);
            targetMesh.SetIndexBufferData(_meshIndices, 0, 0, _indexCount, updateFlags);

            if (perBatchSubmesh && _submeshCount > 0)
            {
                // Allocator.Temp を用いてゼロアロケーションで NativeArray を作成し、SetSubMeshes で一括設定する。
                // これにより逐次設定時の Overlap 警告を完全に回避できる。
                var descArray = new Unity.Collections.NativeArray<UnityEngine.Rendering.SubMeshDescriptor>(_submeshCount, Unity.Collections.Allocator.Temp);
                for (int s = 0; s < _submeshCount; s++)
                {
                    var info = _submeshes[s];
                    descArray[s] = new UnityEngine.Rendering.SubMeshDescriptor(info.IndexStart, info.IndexCount, UnityEngine.MeshTopology.Triangles);
                }
                targetMesh.SetSubMeshes(descArray, 0, _submeshCount, updateFlags);
                descArray.Dispose();
            }
            else
            {
                targetMesh.subMeshCount = 1;
                var subMeshDescriptor = new UnityEngine.Rendering.SubMeshDescriptor(0, _indexCount, UnityEngine.MeshTopology.Triangles);
                targetMesh.SetSubMesh(0, subMeshDescriptor, updateFlags);
            }
            targetMesh.RecalculateBounds();

            // アップロード済みトポロジを記録 (次フレーム以降の index 再送スキップ判定に使う)。
            _meshUploaded = true;
            _lastMeshId = meshId;
            _uploadedTopologySignature = _topologySignature;
            _uploadedVertexCount = _vertexCount;
            _uploadedIndexCount = _indexCount;
            _uploadedPerBatchSubmesh = perBatchSubmesh;

            // バッファは破棄せず再利用する (Dispose() でのみ解放)
        }

        public void Dispose()
        {
            // 【最重要】ネイティブポインタを解放したりNativeArrayを破棄する直前に、
            // 必ず実行中の Burst Job を同期（完了待機）してセグフォールトを防ぐ。
            _updateJobHandle.Complete();

            if (_handle.IsValid)
            {
                unsafe
                {
                    NativeMethods.ss_runtime_destroy(_handle.ptr);
                    _handle.ptr = null;
                }
            }
            if (_ssabPin.IsAllocated)
            {
                _ssabPin.Free();
            }
            _updateJobHandle.Complete();
            
            unsafe
            {
                if (_effectSlots != null)
                {
                    for (int i = 0; i < _effectSlots.Length; i++)
                    {
                        if (_effectSlots[i].EffectSlot != System.IntPtr.Zero)
                        {
                            NativeMethods.ss_effect_slot_destroy((void*)_effectSlots[i].EffectSlot);
                        }
                    }
                    _effectSlots = null;
                }
                
                if (_instanceChildren != null)
                {
                    for (int i = 0; i < _instanceChildren.Length; i++)
                    {
                        if (_instanceChildren[i].InstanceSlot != System.IntPtr.Zero)
                        {
                            NativeMethods.ss_instance_slot_destroy((void*)_instanceChildren[i].InstanceSlot);
                        }
                        if (_instanceChildren[i].Player != null)
                        {
                            _instanceChildren[i].Player.Dispose();
                        }
                    }
                    _instanceChildren = null;
                }
                
                if (_resourceHandle != null)
                {
                    NativeMethods.ss_resource_destroy(_resourceHandle);
                    _resourceHandle = null;
                }
            }
            
            if (_effectPlans.IsCreated) _effectPlans.Dispose();
            if (_effectCommands.IsCreated) _effectCommands.Dispose();
            if (_effectVerts.IsCreated) _effectVerts.Dispose();
            if (_effectUvs.IsCreated) _effectUvs.Dispose();
            if (_effectColors.IsCreated) _effectColors.Dispose();
            if (_effectIndices.IsCreated) _effectIndices.Dispose();
            if (_instanceMeshes.IsCreated) _instanceMeshes.Dispose();
            if (_meshVertices.IsCreated) _meshVertices.Dispose();
            if (_meshIndices.IsCreated) _meshIndices.Dispose();
            if (_drawBatches.IsCreated) _drawBatches.Dispose();
            if (_drawOrder.IsCreated) _drawOrder.Dispose();
            if (_partStates.IsCreated) _partStates.Dispose();
            if (_partColors.IsCreated) _partColors.Dispose();
        }

        // エフェクト draw_plan (FlatBuffers, ネイティブ) を frame-data と同じ流儀で読み、
        // ジオメトリを結合バッファへ追記する。戻り値は結合バッファ上の位置情報。
        // 手書き vtable walk は廃し、生成アクセサ ss.runtime.EffectDrawPlan を使う。
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

            // ジオメトリを結合バッファへ追記 (grow は内容保持)。indices 値は command-local のまま保持する。
            EnsureCapacityPreserve(ref _effectVerts, _effectVertFloatCount + vLen);
            for (int j = 0; j < vLen; j++) _effectVerts[_effectVertFloatCount + j] = plan.Verts(j);
            _effectVertFloatCount += vLen;

            EnsureCapacityPreserve(ref _effectUvs, _effectUvFloatCount + uLen);
            for (int j = 0; j < uLen; j++) _effectUvs[_effectUvFloatCount + j] = plan.Uvs(j);
            _effectUvFloatCount += uLen;

            EnsureCapacityPreserve(ref _effectColors, _effectColorFloatCount + cLen);
            for (int j = 0; j < cLen; j++) _effectColors[_effectColorFloatCount + j] = plan.Colors(j);
            _effectColorFloatCount += cLen;

            EnsureCapacityPreserve(ref _effectIndices, _effectIndexCount + iLen);
            for (int j = 0; j < iLen; j++) _effectIndices[_effectIndexCount + j] = plan.Indices(j);
            _effectIndexCount += iLen;

            // commands を抽出 (描画順ループでテクスチャ/ブレンド/範囲を参照する)。
            EnsureCapacityPreserve(ref _effectCommands, _effectCommandCount + cmdLen);
            int cmdStart = _effectCommandCount;
            for (int c = 0; c < cmdLen; c++)
            {
                var cc = plan.Commands(c);
                if (!cc.HasValue) continue;
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
    }
}
