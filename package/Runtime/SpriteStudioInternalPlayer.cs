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

        public SpriteStudioInternalPlayer()
        {
            unsafe
            {
                _handle = new SsPlayerHandle { ptr = NativeMethods.ss_runtime_create() };
            }
        }

        /// <summary>
        /// SsabData (バイト配列) を読み込んでリソースをバインドする
        /// </summary>
        public void LoadSsab(byte[] ssabData)
        {
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
                byte* ptr = (byte*)_ssabPin.AddrOfPinnedObject();
                NativeMethods.ss_runtime_load_ssab_borrow(_handle.ptr, ptr, (nuint)ssabData.Length);
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

        private byte[] _frameDataArray;
        private Google.FlatBuffers.ByteBuffer _frameDataByteBuffer;

        // ゼロアロケーションのため、Job 入出力バッファは永続確保し grow-only で再利用する。
        // NativeArray.Length は確保容量、論理件数は別途 *_Count で保持する。
        private NativeArray<SsVertex> _meshVertices;
        private NativeArray<ushort> _meshIndices;
        private NativeArray<DrawBatchData> _drawBatches;
        private NativeArray<ushort> _drawOrder;
        private NativeArray<PartStateData> _partStates;
        private NativeArray<PartColorData> _partColors;

        private int _vertexCount;
        private int _indexCount;
        private int _drawBatchCount;
        private int _partCount;
        private int _partColorCount;

        // SetVertexBufferParams/SetIndexBufferParams はバッファを再確保するため、
        // 件数が変化したときだけ呼ぶ (毎フレームの再確保を防ぐ)。-1 = 未適用。
        private int _lastAppliedVertexCount = -1;
        private int _lastAppliedIndexCount = -1;

        // バッチ単位の SubMesh 情報 (MeshRenderer 経路のマルチマテリアル用)。
        // Job では使わず main thread でのみ参照するため managed 配列で保持する。
        private struct SubmeshInfo { public int IndexStart; public int IndexCount; public uint TextureHash; }
        private SubmeshInfo[] _submeshes;
        private int _submeshCount;

        /// <summary>直近の ScheduleMeshUpdate で生成された描画バッチ (= SubMesh) の数。</summary>
        public int SubmeshCount => _submeshCount;

        /// <summary>指定 SubMesh のテクスチャハッシュ (DrawBatch.TextureHash)。</summary>
        public uint GetSubmeshTextureHash(int index) => _submeshes[index].TextureHash;

        /// <summary>必要件数を満たすまで grow-only で確保する。容量が足りていれば再利用する。</summary>
        private static void EnsureCapacity<T>(ref NativeArray<T> arr, int needed) where T : struct
        {
            if (arr.IsCreated && arr.Length >= needed) return;
            if (arr.IsCreated) arr.Dispose();
            arr = new NativeArray<T>(needed < 1 ? 1 : needed, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
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
                if (_frameDataArray == null || _frameDataArray.Length < len)
                {
                    _frameDataArray = new byte[UnityEngine.Mathf.Max(len, 4096)];
                    _frameDataByteBuffer = new Google.FlatBuffers.ByteBuffer(_frameDataArray);
                }

                // ゼロGC: 既存のbyte[]にポインタからメモリコピー
                System.Runtime.InteropServices.Marshal.Copy((System.IntPtr)frameDataPtr, _frameDataArray, 0, len);
                _frameDataByteBuffer.Position = 0; // Reset ByteBuffer offset

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

                // 永続バッファを grow-only で確保 (毎フレーム new しない)
                EnsureCapacity(ref _drawBatches, drawBatchesLength);
                EnsureCapacity(ref _drawOrder, drawOrderLength);
                EnsureCapacity(ref _partStates, partsLength);
                EnsureCapacity(ref _partColors, partsColorLength);

                _drawBatchCount = drawBatchesLength;
                _partCount = partsLength;
                _partColorCount = partsColorLength;

                int totalVertices = 0;
                int totalIndices = 0;
                _submeshCount = 0;
                if (_submeshes == null || _submeshes.Length < drawBatchesLength)
                {
                    _submeshes = new SubmeshInfo[drawBatchesLength < 1 ? 1 : drawBatchesLength];
                }
                for (int i = 0; i < drawBatchesLength; i++)
                {
                    var b = frameData.DrawBatches(i).Value;

                    // 描画対象は Normal(0)/Shape(1)/Mesh(4) のみ。ジョブが書き込まない種別は
                    // 0 件にして未書き込み領域 (= 不正な三角形) を生まないようにする。
                    int vc = 0;
                    int ic = 0;
                    if ((int)b.Kind == 0 || (int)b.Kind == 1 || (int)b.Kind == 4)
                    {
                        for (int k = 0; k < b.Count; k++)
                        {
                            int rank = b.StartRank + k;
                            int p_idx = frameData.DrawOrder(rank);
                            if (p_idx < 0 || p_idx >= partsLength) continue;

                            if ((int)b.Kind == 0) // Normal
                            {
                                var p = frameData.Parts(p_idx).Value;
                                bool needsCenter = ((ulong)p.UpdateFlag & (0x10UL | 0x200UL)) != 0; // AttributeVertex | AttributePartColor
                                vc += needsCenter ? 5 : 4;
                                ic += needsCenter ? 12 : 6;
                            }
                            else if ((int)b.Kind == 1) // Shape
                            {
                                int svc = frame.ShapeVertexCounts[p_idx];
                                if (svc >= 3)
                                {
                                    vc += svc;
                                    ic += (svc - 2) * 3;
                                }
                            }
                            else if ((int)b.Kind == 4) // Mesh
                            {
                                // ジョブ側 BuildMesh のガードと完全に一致させる (全条件成立時のみ加算)
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
                    }

                    _drawBatches[i] = new DrawBatchData
                    {
                        StartRank = (ushort)b.StartRank,
                        Count = (ushort)b.Count,
                        VertexCount = (ushort)vc,
                        IndexCount = (ushort)ic,
                        Kind = (byte)b.Kind
                    };

                    // 描画されるバッチごとに SubMesh 範囲を記録する (インデックスは batch 順に連続するため描画順を保持)
                    if (ic > 0)
                    {
                        _submeshes[_submeshCount++] = new SubmeshInfo
                        {
                            IndexStart = totalIndices,
                            IndexCount = ic,
                            TextureHash = b.TextureHash
                        };
                    }

                    totalVertices += vc;
                    totalIndices += ic;
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

                var job = new UpdateMeshJob
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

                    DrawBatches = _drawBatches,
                    DrawOrder = _drawOrder,
                    PartStates = _partStates,
                    PartColors = _partColors,

                    BatchCount = _drawBatchCount,
                    PartCount = _partCount,
                    PartColorCount = _partColorCount,

                    Vertices = _meshVertices,
                    Indices = _meshIndices
                };

                _updateJobHandle = job.Schedule();
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
                // Clear でバッファ仕様がリセットされるため、次回は再指定が必要
                _lastAppliedVertexCount = -1;
                _lastAppliedIndexCount = -1;
                return;
            }

            // ゼロアロケーションでの Mesh 更新 (バッファは過剰確保されうるので論理件数で渡す)。
            // SetVertexBufferParams は件数が変わったときのみ呼び、毎フレームの再確保を避ける。
            if (_vertexCount != _lastAppliedVertexCount)
            {
                targetMesh.SetVertexBufferParams(_vertexCount,
                    new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Position, UnityEngine.Rendering.VertexAttributeFormat.Float32, 3),
                    new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Color, UnityEngine.Rendering.VertexAttributeFormat.UNorm8, 4),
                    new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.TexCoord0, UnityEngine.Rendering.VertexAttributeFormat.Float32, 2),
                    new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.TexCoord1, UnityEngine.Rendering.VertexAttributeFormat.Float32, 4)
                );
                _lastAppliedVertexCount = _vertexCount;
            }
            targetMesh.SetVertexBufferData(_meshVertices, 0, 0, _vertexCount, 0, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);

            if (_indexCount != _lastAppliedIndexCount)
            {
                targetMesh.SetIndexBufferParams(_indexCount, UnityEngine.Rendering.IndexFormat.UInt16);
                _lastAppliedIndexCount = _indexCount;
            }
            targetMesh.SetIndexBufferData(_meshIndices, 0, 0, _indexCount, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);

            if (perBatchSubmesh && _submeshCount > 0)
            {
                // MeshRenderer 経路: バッチ単位に SubMesh を分割し、テクスチャごとに別マテリアルを適用できるようにする
                targetMesh.subMeshCount = _submeshCount;
                for (int s = 0; s < _submeshCount; s++)
                {
                    var info = _submeshes[s];
                    var desc = new UnityEngine.Rendering.SubMeshDescriptor(info.IndexStart, info.IndexCount, UnityEngine.MeshTopology.Triangles);
                    targetMesh.SetSubMesh(s, desc, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
                }
            }
            else
            {
                // uGUI 経路など: 単一 SubMesh にまとめる (CanvasRenderer は 1 マテリアルのみ)
                targetMesh.subMeshCount = 1;
                var subMeshDescriptor = new UnityEngine.Rendering.SubMeshDescriptor(0, _indexCount, UnityEngine.MeshTopology.Triangles);
                targetMesh.SetSubMesh(0, subMeshDescriptor, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
            }
            targetMesh.RecalculateBounds();

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

            if (_meshVertices.IsCreated) _meshVertices.Dispose();
            if (_meshIndices.IsCreated) _meshIndices.Dispose();
            if (_drawBatches.IsCreated) _drawBatches.Dispose();
            if (_drawOrder.IsCreated) _drawOrder.Dispose();
            if (_partStates.IsCreated) _partStates.Dispose();
            if (_partColors.IsCreated) _partColors.Dispose();
        }
    }
}
