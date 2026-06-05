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
        // Instance 子の UpdateMeshJob ハンドルを束ねる依存配列。永続 grow-only (A3: 毎フレ Temp 確保を回避)。
        private NativeArray<JobHandle> _childHandles;
        // effect/instance スロットを持つパーツ index の疎なリスト (SetupSlots で確定)。毎フレーム partsLength
        // 全スキャンせず、これらだけを処理することで通常パーツ多数時 (例 448 中 effect 数個) の空回りを避ける。
        private int[] _effectPartIndices;
        private int _effectPartCount;
        private int[] _instancePartIndices;
        private int _instancePartCount;
        // この SSAB に Instance パートが 1 つでもあるか。
        private bool _hasInstanceParts;
        // この SSAB に Effect パートが 1 つでもあるか (フレーム重複スキップの可否判定に使う)。
        private bool _hasEffectParts;

        /// <summary>
        /// Static per-part mask metadata, derived once at load from ssab PartData. Mirrors the
        /// static inputs of SSPlayerForGodot's _build_mask_writers (ss_internal_player.cpp:659):
        /// the per-frame inputs (draw-order rank for scope, threshold via PartState.Mask, hide)
        /// are resolved later in the draw loop, not here.
        /// </summary>
        internal struct MaskPartData
        {
            public bool IsMaskPart;        // part_type == PartTypeMask
            public bool DrawAsMask;        // unified pure-mask flag (shape/text/nines mask)
            public bool MaskWrite;         // clipping writer flag
            public bool MaskInfluence;     // writer OP (false=increment / true=invert); on a drawn target = mask opt-in
            public bool VisibleInsideMask; // target polarity (draw where masked == VisibleInsideMask)
            // Derived (static), mirroring _build_mask_writers:
            public bool PureMask;          // IsMaskPart || DrawAsMask: draws no colour, masks parts drawn BEFORE it
            public bool Writes;            // PureMask || MaskWrite: contributes stencil coverage
            public bool IsClipping;        // MaskWrite && !PureMask: draws normally, masks parts drawn AFTER it
            // Geometry kind the mesh job uses to build this part's stencil-write geometry. PartTypeMask
            // has no colour batch, so it is built as a Normal quad from local vertices (like Godot's
            // _render_mask_coverage, which routes Mask through _build_normal).
            public byte DrawKind;          // SsDrawBatchKind: Normal / Shape / Mesh
        }
        // Per-part static mask metadata (index-aligned with ssab parts). Null until SetupSlots runs.
        private MaskPartData[] _maskPartData;
        // True when this ssab has at least one mask writer (pure mask or clipping). When false the
        // whole mask path is skipped, like _build_mask_writers returning empty.
        private bool _hasMaskParts;

        /// <summary>
        /// Per-frame mask writer, rebuilt each frame from the draw order (rank = scope pivot).
        /// Mirrors SSPlayerForGodot's MaskWriter / _build_mask_writers (ss_internal_player.cpp:659).
        /// </summary>
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
        private MaskWriter[] _maskWriters;
        private int _maskWriterCount;
        // 前回ビルドした整数フレーム (subFrame 無効時のフレーム重複スキップ判定用、未ビルドは NaN)。
        private float _lastBuiltDrawFrameNo = float.NaN;
        // PrepareFrameData が確定した描画フレーム番号。並列 get_frame_data / ConsumeFrameData が共有する。
        private float _pendingDrawFrame;
        // 直近の ScheduleMeshUpdate が再構築を省いたか。ApplyToMesh が再アップロード可否に使う。
        private bool _frameSkipped;

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
            _hasInstanceParts = false;
            _hasEffectParts = false;
            _hasMaskParts = false;
            _maskPartData = null;
            _effectPartCount = 0;
            _instancePartCount = 0;
            _lastBuiltDrawFrameNo = float.NaN; // 再ロード時は必ず再構築する
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
                _effectPartIndices = new int[partsLength];
                _instancePartIndices = new int[partsLength];
                _maskPartData = new MaskPartData[partsLength];

                for (int i = 0; i < partsLength; i++)
                {
                    var pObj = binary.Parts(i);
                    if (!pObj.HasValue) continue;
                    var p = pObj.Value;

                    // Cache the static mask flags for every part (any Normal/Mesh/Shape/Mask part can
                    // be a writer or a target). Derivation mirrors _build_mask_writers exactly.
                    {
                        bool isMaskPart = p.PartTypeType == ss.format.PartType.PartTypeMask;
                        bool drawAsMask = p.DrawAsMask;
                        bool maskWrite = p.MaskWrite;
                        bool pureMask = isMaskPart || drawAsMask;
                        bool writes = pureMask || maskWrite;
                        // Geometry kind for the mesh job. PartTypeMask -> Normal quad (no colour batch).
                        byte drawKind = SsDrawBatchKind.Normal;
                        if (p.PartTypeType == ss.format.PartType.PartTypeShape) drawKind = SsDrawBatchKind.Shape;
                        else if (p.PartTypeType == ss.format.PartType.PartTypeMesh) drawKind = SsDrawBatchKind.Mesh;
                        _maskPartData[i] = new MaskPartData
                        {
                            IsMaskPart = isMaskPart,
                            DrawAsMask = drawAsMask,
                            MaskWrite = maskWrite,
                            MaskInfluence = p.MaskInfluence,
                            VisibleInsideMask = p.VisibleInsideMask,
                            PureMask = pureMask,
                            Writes = writes,
                            IsClipping = maskWrite && !pureMask,
                            DrawKind = drawKind,
                        };
                        if (writes) _hasMaskParts = true;
                    }

                    if (p.PartTypeType == ss.format.PartType.PartTypeEffect)
                    {
                        var pt = p.PartTypeAsPartTypeEffect();
                        uint refHash = pt.RefEffectNameHash;
                        uint seed = (uint)i ^ refHash;
                        void* slot = NativeMethods.ss_effect_slot_create(_resourceHandle, refHash, seed);
                        _effectSlots[i].EffectSlot = (System.IntPtr)slot;
                        _effectPartIndices[_effectPartCount++] = i;
                        _hasEffectParts = true;
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
                        _instancePartIndices[_instancePartCount++] = i;
                        _hasInstanceParts = true;
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
        private SubmeshInfo[] _submeshes;
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
        public SubmeshInfo[] Submeshes => _submeshes;
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
            if (!_handle.IsValid) return;

            // 永続バッファを書き換える前に、前フレームのジョブ完了を保証する
            _updateJobHandle.Complete();

            // ss_runtime_update は delta を「ミリ秒」で受け取る (update_interval = 1000/fps)。
            if (advance)
            {
                unsafe { NativeMethods.ss_runtime_update(_handle.ptr, deltaTime * 1000.0f); }
            }

            drawFrame = SubFrameEnabled ? GetFrame() : UnityEngine.Mathf.Floor(GetFrame());

            // subFrame 無効・エフェクト/インスタンス無しで整数フレームが前回ビルドと同一なら、SoA 出力は
            // 完全に同一になる。FFI・集計・Job を省いて前フレームのジオメトリをそのまま再利用する。
            // エフェクトは粒子が毎フレーム進み、インスタンスは子アニメが独立に進むため除外する。
            _frameSkipped = false;
            if (!SubFrameEnabled && !_hasEffectParts && !_hasInstanceParts
                && _vertexCount > 0 && drawFrame == _lastBuiltDrawFrameNo)
            {
                _frameSkipped = true;
                return;
            }

            _pendingDrawFrame = drawFrame;
            unsafe { handle = (System.IntPtr)_handle.ptr; }
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
        /// Rebuilds <see cref="_maskWriters"/> for the current frame from the draw order, mirroring
        /// SSPlayerForGodot's _build_mask_writers: a writer is any part whose static MaskPartData
        /// marks it as writing (pure mask or clipping). Hidden masks are already excluded from the
        /// draw order by the runtime, so iterating draw_order is sufficient. The per-frame threshold
        /// comes from PartState.Mask: (255 - mask) / 255 (texel alpha &lt;= threshold is cut out).
        /// </summary>
        private void BuildMaskWriters(ss.runtime.FrameData frameData, int drawOrderLength, int partsLength)
        {
            _maskWriterCount = 0;
            if (!_hasMaskParts || _maskPartData == null) return;
            if (_maskWriters == null || _maskWriters.Length < drawOrderLength)
            {
                _maskWriters = new MaskWriter[drawOrderLength < 1 ? 1 : drawOrderLength];
            }
            for (int rank = 0; rank < drawOrderLength; rank++)
            {
                int p_idx = _drawOrder[rank];
                if (p_idx < 0 || p_idx >= partsLength || p_idx >= _maskPartData.Length) continue;
                var md = _maskPartData[p_idx];
                if (!md.Writes) continue;
                var ps = frameData.Parts(p_idx);
                if (!ps.HasValue) continue;
                _maskWriters[_maskWriterCount++] = new MaskWriter
                {
                    PartIndex = p_idx,
                    DrawRank = (ushort)rank,
                    OpInvert = md.MaskInfluence,
                    IsClipping = md.IsClipping,
                    Threshold = (255f - ps.Value.Mask) / 255f,
                };
            }
        }

        /// <summary>
        /// 並列 GetFrameDataJob が取得した frame-data ポインタ (job 出力) を消費し、
        /// SoA を読み出してメッシュ構築 Burst Job を発行する。ptr==Zero / len==0 はジオメトリ無しとして扱う。
        /// </summary>
        public void ConsumeFrameData(System.IntPtr frameDataPtrRaw, int len, float deltaTime)
        {
            if (!_handle.IsValid) return;

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

                // _childHandles への書き込みは InstanceSlot != Zero のブランチ内だけで、それは
                // _hasInstanceParts が真のときしか成立しない。永続 grow-only で毎フレームの Temp 確保を避ける。
                int childHandleCount = 0;
                if (_hasInstanceParts)
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

                for (int k = 0; k < _effectPartCount; k++)
                {
                    int i = _effectPartIndices[k];
                    var ev = NativeMethods.ss_runtime_get_active_effect_event(_handle.ptr, (uint)i);
                    var step = NativeMethods.ss_effect_slot_step((void*)_effectSlots[i].EffectSlot, ev, drawFrameNo, deltaTime, fps, parentLooped, false, 3);
                    if (step.visible && step.draw_plan_len > 0)
                        _effectPlans[i] = AppendEffectDrawPlan(step.draw_plan_buf, (int)step.draw_plan_len);
                    // 非 visible は MemClear 済の default のまま
                }

                for (int k = 0; k < _instancePartCount; k++)
                {
                    int i = _instancePartIndices[k];
                    var childPlayer = _instanceChildren[i].Player;
                    if (childPlayer == null) continue; // _instanceMeshes[i] は MemClear 済 default

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
                for (int k = 0; k < _effectPartCount; k++)
                {
                    int i = _effectPartIndices[k];
                    if (_effectPlans[i].CommandCount > 0)
                        maxBatches += _effectPlans[i].CommandCount;
                }
                for (int k = 0; k < _instancePartCount; k++)
                {
                    int i = _instancePartIndices[k];
                    if (_instanceMeshes[i].Vertices != null)
                    {
                        // Instance は子メッシュ全体を 1 draw batch として取り込むが、テクスチャ単位の SubMesh は
                        // 子の submesh 数ぶん発生する。submesh 数 + 1 (draw batch ぶん) を見込む。
                        maxBatches += _instanceChildren[i].Player.SubmeshCount + 1;
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

                // PartState を先に充填し、下のバッチ集計 (Normal の needsCenter 判定) が
                // UpdateFlag を FlatBuffer から再デコードせず _partStates から読めるようにする。
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
                            int p_idx = _drawOrder[rank];
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
                                        TextureHash = childSubmesh.TextureHash,
                                        BlendType = childSubmesh.BlendType // inherit the child's per-submesh blend
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
                            int p_idx = _drawOrder[rank];
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
                                        TextureHash = cmd.CellmapHash,
                                        // Effect keeps Mix; its Mix/Add is still handled by the in-shader
                                        // a=0 trick (see SpriteStudioDefault.shader TODO). Migrate to a
                                        // per-blend material later.
                                        BlendType = SpriteStudioMaterial.BlendMix
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
                            int p_idx = _drawOrder[rank];
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
                                TextureHash = b.TextureHash,
                                BlendType = (byte)b.BlendType // ssab BlendType for the per-blend material
                            };

                            totalVertices += vc;
                            totalIndices += ic;
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
        public void ApplyToMesh(UnityEngine.Mesh targetMesh, bool perBatchSubmesh = false)
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
                ApplyJobComputedBounds(targetMesh);
                return;
            }

            // --- フルアップロード (初回 or トポロジ変化) ---
            // バッファ再宣言時の領域外参照を避けるため一旦 submesh を空にする。
            targetMesh.SetSubMesh(0, new UnityEngine.Rendering.SubMeshDescriptor(0, 0), updateFlags);
            targetMesh.subMeshCount = 1;

            targetMesh.SetVertexBufferParams(_vertexCount, s_vertexLayout);
            targetMesh.SetIndexBufferParams(_indexCount, UnityEngine.Rendering.IndexFormat.UInt32);

            targetMesh.SetVertexBufferData(_meshVertices, 0, 0, _vertexCount, 0, updateFlags);
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

            // バッファは破棄せず再利用する (Dispose() でのみ解放)
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
            if (_childHandles.IsCreated) _childHandles.Dispose();
            if (_meshVertices.IsCreated) _meshVertices.Dispose();
            if (_meshIndices.IsCreated) _meshIndices.Dispose();
            if (_drawBatches.IsCreated) _drawBatches.Dispose();
            if (_drawOrder.IsCreated) _drawOrder.Dispose();
            if (_partStates.IsCreated) _partStates.Dispose();
            if (_partColors.IsCreated) _partColors.Dispose();
            if (_submeshDescriptors.IsCreated) _submeshDescriptors.Dispose();
            if (_meshBounds.IsCreated) _meshBounds.Dispose();
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
    }
}
