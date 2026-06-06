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
        // ネイティブコンテキスト（ポインタ管理を委譲）
        internal SpriteStudioNativeContext _nativeContext;
        internal SsPlayerHandle _handle => _nativeContext.Handle;
        private unsafe void* _resourceHandle => _nativeContext.ResourceHandle;

        public delegate byte[] AssetResolver(uint packHash, uint nameHash);
        private AssetResolver _assetResolver;

        internal SpriteStudioGeometryBuilder _geometryBuilder;

        public JobHandle CurrentUpdateJobHandle => _geometryBuilder != null ? _geometryBuilder.CurrentUpdateJobHandle : default;
        public int VertexCount => _geometryBuilder != null ? _geometryBuilder.VertexCount : 0;
        public int IndexCount => _geometryBuilder != null ? _geometryBuilder.IndexCount : 0;
        public int SubmeshCount => _geometryBuilder != null ? _geometryBuilder.SubmeshCount : 0;
        public NativeArray<SpriteStudioGeometryBuilder.SubmeshInfo> Submeshes => _geometryBuilder != null ? _geometryBuilder.Submeshes : default;
        public uint GetSubmeshTextureHash(int index) => _geometryBuilder.GetSubmeshTextureHash(index);
        public byte GetSubmeshBlendType(int index) => _geometryBuilder.GetSubmeshBlendType(index);
        public NativeArray<SsVertex> MeshVertices => _geometryBuilder.MeshVertices;
        public NativeArray<uint> MeshIndices => _geometryBuilder.MeshIndices;

        /// <summary>
        /// 頂点位置に掛ける一様スケール (= 1 / PixelsPerUnit)。既定 1 (= ピクセル等倍)。
        /// トップレベルプレイヤーのみ設定し、Instance 子は 1 のまま据え置く
        /// (子は親に px のまま取り込まれ、親側で一括スケールされるため。二重スケール防止)。
        /// </summary>
        public float VertexScale { get; set; } = 1f;

        private bool _isDisposed = false;

        ~SpriteStudioInternalPlayer()
        {
            if (!_isDisposed)
            {
                Debug.LogError("[SpriteStudioInternalPlayer] Memory leak detected! Dispose() was not called before the object was garbage collected.");
            }
        }

        
        // --- Moved from Partial Classes ---

        internal struct EffectSlotState { public System.IntPtr EffectSlot; }
        internal EffectSlotState[] _effectSlots;
        internal int[] _effectPartIndices;
        internal int _effectPartCount;
        internal bool _hasEffectParts;

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
        internal MaskPartData[] _maskPartData;
        // True when this ssab has at least one mask writer (pure mask or clipping). When false the
        // whole mask path is skipped, like _build_mask_writers returning empty.
        internal bool _hasMaskParts;

        internal struct InstanceChildState { public System.IntPtr InstanceSlot; public SpriteStudioInternalPlayer Player; public float PreviousFrame; }
        internal InstanceChildState[] _instanceChildren;
        internal int[] _instancePartIndices;
        internal int _instancePartCount;
        internal bool _hasInstanceParts;

        // ----------------------------------
        public SpriteStudioInternalPlayer()
        {
            _nativeContext = new SpriteStudioNativeContext();
            _geometryBuilder = new SpriteStudioGeometryBuilder(this);
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
            _nativeContext.LoadSsab(ssabData);
            
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
            _geometryBuilder._lastBuiltDrawFrameNo = float.NaN; // 再ロード時は必ず再構築する
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
                        if (slot != null)
                        {
                            _effectPartIndices[_effectPartCount++] = i;
                            _hasEffectParts = true;
                        }
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

        // skip_frames (実時間追従) は Rust 側 (PlaybackController.is_skip_frames) が状態を保持する。
        // ここは薄いパススルー。Rust の setup() は他の再生パラメータ (loop/style/speed) と同様に
        // skip_frames を既定 (true) へ戻すため、望ましい値の再適用は PlaybackCore.Play 側でまとめて行う。
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
            _geometryBuilder?.CurrentUpdateJobHandle.Complete();

            if (!_handle.IsValid) return;

            unsafe
            {
                // Tick is executed strictly sequentially on the main thread (state mutability boundary)
                // ss_runtime_update は delta を「ミリ秒」で受け取る (update_interval = 1000/fps)。
                // Unity の Time.deltaTime は秒なので 1000 倍する (Godot 実装も delta_seconds * 1000)。
                NativeMethods.ss_runtime_update(_handle.ptr, deltaTime * 1000.0f);
            }
        }


        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            System.GC.SuppressFinalize(this);

            // 【最重要】ネイティブポインタを解放したりNativeArrayを破棄する直前に、
            // 必ず実行中の Burst Job を同期（完了待機）してセグフォールトを防ぐ。
            _geometryBuilder?.CurrentUpdateJobHandle.Complete();

            if (_nativeContext != null)
            {
                _nativeContext.Dispose();
            }
            
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
            }
            
            _geometryBuilder?.Dispose();
        }

        // エフェクト draw_plan (FlatBuffers, ネイティブ) を frame-data と同じ流儀で読み、

        public void PrepareFrameData(float deltaTime, bool advance, out System.IntPtr handle, out float drawFrame) => _geometryBuilder.PrepareFrameData(deltaTime, advance, out handle, out drawFrame);
        public void ScheduleMeshUpdate(float deltaTime) => _geometryBuilder.ScheduleMeshUpdate(deltaTime);
        public void ConsumeFrameData(System.IntPtr frameDataPtrRaw, int len, float deltaTime) => _geometryBuilder.ConsumeFrameData(frameDataPtrRaw, len, deltaTime);
        public void ApplyToMesh(UnityEngine.Mesh targetMesh, bool perBatchSubmesh = false) => _geometryBuilder.ApplyToMesh(targetMesh, perBatchSubmesh);
    }
}
