using System;
using System.Collections.Generic;
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
        public NativeArray<SubmeshInfo> Submeshes => _geometryBuilder != null ? _geometryBuilder.Submeshes : default;
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

        // Part name -> part index (into the world-matrix array). Built in SetupSlots; used by part tracking.
        private Dictionary<string, int> _partNameToIndex;
        // Part index -> part name (index-aligned with ssab parts). Built once in SetupSlots so the event
        // dispatch path resolves names by array lookup instead of decoding a fresh string from the
        // FlatBuffer per event. Null until SetupSlots runs.
        private string[] _partNames;
        // True when this ssab has at least one mask writer (pure mask or clipping). When false the
        // whole mask path is skipped, like _build_mask_writers returning empty.
        internal bool _hasMaskParts;

        // Per-part AddOn shader catalog (set by the owning world player each frame before the build).
        // null = no per-part shader dispatch (the Canvas/Graphic path leaves this null). The geometry
        // builder consults it to carve out is-per-part shader parts; the player resolves their material.
        public SpriteStudioShaderCatalog ShaderCatalog;

        // When true (the Canvas/uGUI path), SpriteStudio internal masking is suppressed and the stencil
        // buffer is left to uGUI's own Mask/RectMask2D. The world (MeshRenderer) path leaves it false.
        internal bool _suppressInternalMask;

        // True when the asset has mask writers and this player is not suppressing them (Canvas suppresses).
        internal bool MaskActive => _hasMaskParts && !_suppressInternalMask;

        /// <summary>
        /// Suppress SpriteStudio internal masking for this player and (recursively) its instance
        /// children. Set true on the Canvas path; left false for the world path. Propagates so an
        /// instance child's own internal masks are also dropped on Canvas instead of writing stencil.
        /// </summary>
        public bool SuppressInternalMask
        {
            get => _suppressInternalMask;
            set
            {
                _suppressInternalMask = value;
                if (_instanceChildren != null)
                {
                    for (int i = 0; i < _instanceChildren.Length; i++)
                    {
                        var c = _instanceChildren[i].Player;
                        if (c != null) c.SuppressInternalMask = value;
                    }
                }
            }
        }

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

        public event Action OnAnimationFinished;
        public event Action OnAnimationLooped;
        public event Action<SpriteStudioUserData> OnUserData;
        public event Action<SpriteStudioSignal> OnSignal;
        public event Action<SpriteStudioAudio> OnAudio;

        private ss.format.SsAnimeBinary? _ssab;
        private ss.format.AnimationData? _currentAnimation;

        // OnAnimationFinished のエッジ検出用 (!is_playing && is_end_frame_reached の前回値)。
        private bool _prevFinished;

        // True once an ssab has been loaded into the native runtime. Lets the owner (PlaybackCore /
        // player) detect "Internal created but ssab not yet loaded" — which happens when OnEnable runs
        // before the .ssab finishes importing on an Editor restart — and self-heal by loading it later.
        public bool HasSsab => _hasSsab;
        private bool _hasSsab;

        /// <summary>
        /// SsabData (バイト配列) を読み込んでリソースをバインドする。
        /// Instance パーツがあれば子プレイヤーを再帰的に生成する。
        /// </summary>
        public void LoadSsab(byte[] ssabData, AssetResolver resolver = null)
        {
            _assetResolver = resolver;

            // 再ロード (実行時のアセット差し替え) では、旧 ssab バッファを borrow している
            // in-flight な Burst Job が残っている可能性がある。ネイティブリソースを破棄/再 borrow
            // する前に必ず Job を完了させ、解放済みメモリ参照によるセグフォールトを防ぐ。
            // 初回ロード時は Job 未投入なので default ハンドルへの Complete() で no-op。
            _geometryBuilder?.CurrentUpdateJobHandle.Complete();

            _nativeContext.LoadSsab(ssabData);
            
            var bb = new Google.FlatBuffers.ByteBuffer(ssabData);
            _ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
            SetupSlots(_ssab.Value);
            _hasSsab = true;
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
                        // 子の Dispose は再帰 managed で例外を投げ得るため隔離し、兄弟の解放を止めない。
                        try { _instanceChildren[i].Player?.Dispose(); }
                        catch (Exception e) { Debug.LogException(e); }
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

                // Part-name -> index map for part tracking (resolve user-specified part names to the
                // index used by the world-matrix array). Built once per load; static for the asset.
                if (_partNameToIndex == null) _partNameToIndex = new Dictionary<string, int>(partsLength);
                else _partNameToIndex.Clear();
                if (_partNames == null || _partNames.Length < partsLength) _partNames = new string[partsLength];
                else System.Array.Clear(_partNames, 0, partsLength);

                for (int i = 0; i < partsLength; i++)
                {
                    var pObj = binary.Parts(i);
                    if (!pObj.HasValue) continue;
                    var p = pObj.Value;

                    // Decode the FlatBuffer name once and cache it both by-index (event path) and
                    // by-name (part tracking). First occurrence wins on the (unexpected) chance of
                    // duplicate names.
                    string partName = p.Name;
                    _partNames[i] = partName;
                    if (!string.IsNullOrEmpty(partName) && !_partNameToIndex.ContainsKey(partName))
                        _partNameToIndex[partName] = i;

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
                        // Inherit the parent's suppression so children (re)created on load match the
                        // Canvas/world choice; the public setter also propagates to existing children.
                        childPlayer._suppressInternalMask = _suppressInternalMask;
                        childPlayer.LoadSsab(childSsab, _assetResolver);

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
                bool ok = NativeMethods.ss_runtime_setup_animation_by_hash(_handle.ptr, animationNameHash);
                if (ok)
                {
                    // setup_animation resets the native world matrices to identity; force a geometry rebuild.
                    _geometryBuilder?.InvalidateFrameCache();

                    if (_ssab.HasValue)
                    {
                        _currentAnimation = null;
                        var binary = _ssab.Value;
                        for (int i = 0; i < binary.AnimationsLength; i++)
                        {
                            var anim = binary.Animations(i);
                            if (anim.HasValue && anim.Value.NameHash == animationNameHash)
                            {
                                _currentAnimation = anim;
                                break;
                            }
                        }
                    }
                }
                return ok;
            }
        }

        #region Playback Control
        public bool IsPlaying() { unsafe { return _handle.IsValid && NativeMethods.ss_runtime_is_playing(_handle.ptr); } }
        // 現在の実効再生方向が順方向か (ピンポン等の折り返しも反映)。逆再生では音を鳴らさないための判定に使う。
        public bool IsPlayingForward() { unsafe { return _handle.IsValid && NativeMethods.ss_runtime_is_playing_forward(_handle.ptr); } }
        public void Play(float startFrame = -1.0f)
        {
            unsafe
            {
                if (!_handle.IsValid) return;
                // Play() always heads out (rewinds to a start frame); "resume" is Pause()'s toggle,
                // not Play(). So a fresh play must clear each slot's "last started key" memory, else a
                // re-played animation would debounce an already-fired independent effect/instance as
                // the same key and never re-fire it. Mirrors the Godot player's play().
                ResetSlotsPlaybackState();
                if (startFrame < 0.0f) NativeMethods.ss_runtime_play(_handle.ptr);
                else NativeMethods.ss_runtime_play_with_start_frame(_handle.ptr, startFrame);
            }
        }

        /// <summary>
        /// Reset every effect/instance slot's "last started key" playback memory (recursing into
        /// instance children) so an explicit head-out re-fires independent effects/instances from
        /// scratch instead of debouncing the first key as the same one the slot last started. Called
        /// from Play(). The reset itself lives in the runtime (ss_effect_slot_reset /
        /// ss_instance_slot_reset); the host only iterates the slots it owns. Mirrors the Godot
        /// player's _reset_slots_playback_state().
        /// </summary>
        internal void ResetSlotsPlaybackState()
        {
            unsafe
            {
                if (_effectSlots != null)
                {
                    for (int i = 0; i < _effectSlots.Length; i++)
                    {
                        if (_effectSlots[i].EffectSlot != System.IntPtr.Zero)
                            NativeMethods.ss_effect_slot_reset((void*)_effectSlots[i].EffectSlot);
                    }
                }
                if (_instanceChildren != null)
                {
                    for (int i = 0; i < _instanceChildren.Length; i++)
                    {
                        if (_instanceChildren[i].InstanceSlot != System.IntPtr.Zero)
                            NativeMethods.ss_instance_slot_reset((void*)_instanceChildren[i].InstanceSlot);
                        // Recurse so a re-play also restarts independent effects/instances nested
                        // inside instance children (their slots live on the child).
                        _instanceChildren[i].Player?.ResetSlotsPlaybackState();
                    }
                }
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

        /// <summary>Number of animations in the loaded ssab (0 if none loaded).</summary>
        public int GetAnimationCount() => _ssab.HasValue ? _ssab.Value.AnimationsLength : 0;

        /// <summary>Name of the animation at <paramref name="index"/> in the loaded ssab, or null if out of range.</summary>
        public string GetAnimationName(int index)
        {
            if (!_ssab.HasValue || index < 0 || index >= _ssab.Value.AnimationsLength) return null;
            var anim = _ssab.Value.Animations(index);
            return anim.HasValue ? anim.Value.Name : null;
        }

        // --- Part tracking support ---

        /// <summary>Resolve a part name to its index, or -1 if the part is not present in this animation.</summary>
        public int ResolvePartIndex(string partName)
        {
            if (string.IsNullOrEmpty(partName) || _partNameToIndex == null) return -1;
            return _partNameToIndex.TryGetValue(partName, out int idx) ? idx : -1;
        }

        /// <summary>Resolve a part index to its cached name, or null if out of range. Avoids decoding a
        /// fresh FlatBuffer string per event in the event dispatch path.</summary>
        private string PartNameAt(int index)
        {
            return (_partNames != null && (uint)index < (uint)_partNames.Length) ? _partNames[index] : null;
        }

        /// <summary>
        /// Player-local transform of the given part for the current frame, matching what the mesh draws:
        /// (1/PPU) * draw_m. Column-major; the draw matrix already folds in inherited Scale and the part's
        /// own localScale (callers that want to ignore localScale divide it out from the extracted scale).
        /// Returns false if the handle is invalid or the index is out of range.
        /// </summary>
        public bool TryGetPartLocalMatrix(int partIndex, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            if (partIndex < 0) return false;
            unsafe
            {
                if (!_handle.IsValid) return false;
                float* data = null;
                nuint len = 0;
                NativeMethods.ss_runtime_get_world_matrices(_handle.ptr, &data, &len);
                if (data == null) return false;
                int baseIndex = partIndex * 16;
                if ((long)baseIndex + 16 > (long)len) return false;

                // Native layout is column-major 4x4 (draw_m[0..3]=col0 ... [12..15]=col3), which matches
                // Unity Matrix4x4's linear indexer (index = row + column*4).
                var m = new Matrix4x4();
                for (int e = 0; e < 16; e++) m[e] = data[baseIndex + e];

                // Fold in the same uniform 1/PPU scale the mesh job applies to vertex positions, so the
                // tracked frame is pixel-aligned with the rendered part (premultiply by uniform scale).
                float s = VertexScale;
                matrix = Matrix4x4.Scale(new Vector3(s, s, s)) * m;
                return true;
            }
        }

        /// <summary>
        /// Per-part hide / localScale for the current frame (for part tracking). False if unavailable;
        /// outputs default to visible / unit scale.
        /// </summary>
        public bool TryGetPartState(int partIndex, out bool hidden, out float localScaleX, out float localScaleY)
        {
            hidden = false;
            localScaleX = 1f;
            localScaleY = 1f;
            return _geometryBuilder != null
                && _geometryBuilder.TryGetPartState(partIndex, out hidden, out localScaleX, out localScaleY);
        }

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
                    bool ok = NativeMethods.ss_runtime_setup_animation(_handle.ptr, p);
                    if (ok)
                    {
                        // setup_animation resets the native world matrices to identity; force a geometry
                        // rebuild so a same-numbered start frame is not skipped (otherwise trackers read
                        // identity = origin).
                        _geometryBuilder?.InvalidateFrameCache();

                        if (_ssab.HasValue)
                        {
                            _currentAnimation = null;
                            var binary = _ssab.Value;
                            for (int i = 0; i < binary.AnimationsLength; i++)
                            {
                                var anim = binary.Animations(i);
                                if (anim.HasValue && anim.Value.Name == animationName)
                                {
                                    _currentAnimation = anim;
                                    break;
                                }
                            }
                        }
                    }
                    return ok;
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
                PollEvents();
            }
        }

        /// <summary>
        /// Rust ランタイムに蓄積されたイベントを取り出し、C# 側のイベントとして発火する。
        /// ss_runtime_update の直後に呼ばれる必要がある。
        /// </summary>
        public void PollEvents()
        {
            if (!_handle.IsValid) return;

            unsafe
            {
                // is_end_frame_reached は終端到達フラグで、(1) 最終フレーム着地時 (再生継続中) と
                // (2) 終端超過で停止した時の最大 2 update で true になり、停止後も true が貼り付く。
                // OnAnimationFinished は「再生が終了して停止した瞬間」に 1 度だけ通知したいので、
                // !is_playing && is_end_frame_reached の立ち上がり (false→true) でのみ発火する。
                bool finished = !NativeMethods.ss_runtime_is_playing(_handle.ptr)
                                && NativeMethods.ss_runtime_is_end_frame_reached(_handle.ptr);
                if (finished && !_prevFinished)
                {
                    OnAnimationFinished?.Invoke();
                }
                _prevFinished = finished;

                // is_looped は update() ごとに false へリセットされ、ループ境界を跨いだ update でのみ
                // true になる (1 update 1 パルス) ため、そのまま発火してよい。
                if (NativeMethods.ss_runtime_is_looped(_handle.ptr))
                {
                    OnAnimationLooped?.Invoke();
                }

                int eventCount = NativeMethods.ss_runtime_get_passed_event_count(_handle.ptr);
                if (eventCount > 0 && _currentAnimation.HasValue)
                {
                    var anim = _currentAnimation.Value;
                    for (int i = 0; i < eventCount; i++)
                    {
                        int eventIdx = NativeMethods.ss_runtime_get_passed_event_index(_handle.ptr, i);
                        var eventsObj = anim.Events(eventIdx);
                        if (!eventsObj.HasValue) continue;
                        var events = eventsObj.Value;

                        if (OnUserData != null && events.UsersLength > 0)
                        {
                            for (int j = 0; j < events.UsersLength; j++)
                            {
                                var userOpt = events.Users(j);
                                if (!userOpt.HasValue || !userOpt.Value.Value.HasValue) continue;
                                var user = userOpt.Value;
                                var val = user.Value.Value;

                                var ev = new SpriteStudioUserData
                                {
                                    PartIndex = user.PartIndex,
                                    PartName = PartNameAt(user.PartIndex),
                                    HasInt = val.Integer.HasValue,
                                    IntValue = val.Integer.HasValue ? val.Integer.Value.Value : 0,
                                    HasString = val.Str != null,
                                    StringValue = val.Str,
                                    HasRect = val.Rect.HasValue,
                                    RectValue = val.Rect.HasValue ? new UnityEngine.Rect(val.Rect.Value.X1, val.Rect.Value.Y1, val.Rect.Value.X2 - val.Rect.Value.X1, val.Rect.Value.Y2 - val.Rect.Value.Y1) : default,
                                    HasPoint = val.Point.HasValue,
                                    PointValue = val.Point.HasValue ? new UnityEngine.Vector2(val.Point.Value.V1, val.Point.Value.V2) : default
                                };
                                OnUserData.Invoke(ev);
                            }
                        }

                        if (OnSignal != null && events.SignalsLength > 0)
                        {
                            for (int j = 0; j < events.SignalsLength; j++)
                            {
                                var sigOpt = events.Signals(j);
                                if (!sigOpt.HasValue || !sigOpt.Value.Value.HasValue) continue;
                                var sig = sigOpt.Value;
                                var val = sig.Value.Value;
                                // 非アクティブ signal は通知しない (Godot / 移植ドキュメントと同じ)。
                                if (!val.Active) continue;

                                // command パラメータ (union: Bool/Index/Integer/Floating/Text) を展開。
                                Dictionary<string, object> paramsMap = null;
                                int paramCount = val.ParamsLength;
                                for (int k = 0; k < paramCount; k++)
                                {
                                    var paramOpt = val.Params(k);
                                    if (!paramOpt.HasValue) continue;
                                    var param = paramOpt.Value;
                                    var paramId = param.ParamId;
                                    if (paramId == null) continue;

                                    object boxed;
                                    switch (param.ValueType)
                                    {
                                        case ss.format.SignalCommandParamValue.Bool:     boxed = param.ValueAsBool().Value; break;
                                        case ss.format.SignalCommandParamValue.Index:    boxed = param.ValueAsIndex().Value; break;
                                        case ss.format.SignalCommandParamValue.Integer:  boxed = param.ValueAsInteger().Value; break;
                                        case ss.format.SignalCommandParamValue.Floating: boxed = param.ValueAsFloating().Value; break;
                                        case ss.format.SignalCommandParamValue.Text:     boxed = param.ValueAsText().Value; break;
                                        default: continue;
                                    }
                                    (paramsMap ??= new Dictionary<string, object>())[paramId] = boxed;
                                }

                                var ev = new SpriteStudioSignal
                                {
                                    PartIndex = sig.PartIndex,
                                    PartName = PartNameAt(sig.PartIndex),
                                    Active = val.Active,
                                    CommandId = val.CommandId,
                                    Params = paramsMap
                                };
                                OnSignal.Invoke(ev);
                            }
                        }

                        if (OnAudio != null && events.AudiosLength > 0)
                        {
                            for (int j = 0; j < events.AudiosLength; j++)
                            {
                                var audioOpt = events.Audios(j);
                                if (!audioOpt.HasValue || !audioOpt.Value.Value.HasValue) continue;
                                var audio = audioOpt.Value;
                                var val = audio.Value.Value;
                                var ev = new SpriteStudioAudio
                                {
                                    PartIndex = audio.PartIndex,
                                    PartName = PartNameAt(audio.PartIndex),
                                    SoundListNameHash = val.SoundListNameHash,
                                    SoundNameHash = val.SoundNameHash,
                                    SoundName = val.SoundName,
                                    LoopNum = val.LoopNum
                                };
                                OnAudio.Invoke(ev);
                            }
                        }
                    }
                }
            }
        }


        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            System.GC.SuppressFinalize(this);

            // 【最重要】ネイティブポインタを解放したりNativeArrayを破棄する直前に、
            // 必ず実行中の Burst Job を同期（完了待機）してセグフォールトを防ぐ。
            try { _geometryBuilder?.CurrentUpdateJobHandle.Complete(); }
            catch (Exception e) { Debug.LogException(e); }

            // 以降の解放グループは互いに独立。1 つが例外を投げても残りを必ず解放できるよう各々隔離する
            // (ネイティブハンドルにはファイナライザ側の回収手段が無いため、ここで漏らすと永久リーク)。
            try { _nativeContext?.Dispose(); }
            catch (Exception e) { Debug.LogException(e); }
            
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
                        // 子の Dispose は再帰 managed で例外を投げ得るため隔離し、兄弟の解放を止めない。
                        try { _instanceChildren[i].Player?.Dispose(); }
                        catch (Exception e) { Debug.LogException(e); }
                    }
                    _instanceChildren = null;
                }
            }

            try { _geometryBuilder?.Dispose(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // エフェクト draw_plan (FlatBuffers, ネイティブ) を frame-data と同じ流儀で読み、

        public void PrepareFrameData(float deltaTime, bool advance, out System.IntPtr handle, out float drawFrame) => _geometryBuilder.PrepareFrameData(deltaTime, advance, out handle, out drawFrame);
        public void ScheduleMeshUpdate(float deltaTime) => _geometryBuilder.ScheduleMeshUpdate(deltaTime);
        public void ConsumeFrameData(System.IntPtr frameDataPtrRaw, int len, float deltaTime) => _geometryBuilder.ConsumeFrameData(frameDataPtrRaw, len, deltaTime);
        public void ApplyToMesh(UnityEngine.Mesh targetMesh, bool perBatchSubmesh = false, bool use16BitIndices = false) => _geometryBuilder.ApplyToMesh(targetMesh, perBatchSubmesh, use16BitIndices);
    }
}
