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

        // メッシュバッファ (Job からの書き込み用)
        // ※実際には Vertex 構造体などを別途定義して利用します
        // private NativeArray<Vertex> _vertices;
        // private NativeArray<ushort> _indices;

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

            public float* CellMeta;
            public nuint CellMetaLen;

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

        /// <summary>
        /// Update実行後、FFIからSoA配列のポインタを取得してメッシュ構築ジョブを発行する
        /// </summary>
        public void ScheduleMeshUpdate(float deltaTime)
        {
            if (!_handle.IsValid) return;

            float drawFrameNo = SubFrameEnabled ? GetFrame() : UnityEngine.Mathf.Floor(GetFrame());

            unsafe
            {
                byte* frameDataPtr = null;
                nuint frameDataLen = 0;
                NativeMethods.ss_runtime_get_frame_data(_handle.ptr, drawFrameNo, &frameDataPtr, &frameDataLen);

                if (frameDataPtr == null || frameDataLen == 0) return;

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
                NativeMethods.ss_runtime_get_cell_meta(_handle.ptr, &frame.CellMeta, &frame.CellMetaLen);
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

                // Job System に渡すバッファの確保とジョブのスケジュール
                int drawBatchesLength = frameData.DrawBatchesLength;
                int drawOrderLength = frameData.DrawOrderLength;
                int partsLength = frameData.PartsLength;
                int partsColorLength = frameData.PartsColorLength;

                var drawBatches = new Unity.Collections.NativeArray<DrawBatchData>(drawBatchesLength, Unity.Collections.Allocator.TempJob);
                var drawOrder = new Unity.Collections.NativeArray<ushort>(drawOrderLength, Unity.Collections.Allocator.TempJob);
                var partStates = new Unity.Collections.NativeArray<PartStateData>(partsLength, Unity.Collections.Allocator.TempJob);
                var partColors = new Unity.Collections.NativeArray<PartColorData>(partsColorLength, Unity.Collections.Allocator.TempJob);

                int totalVertices = 0;
                int totalIndices = 0;
                for (int i = 0; i < drawBatchesLength; i++)
                {
                    var b = frameData.DrawBatches(i).Value;
                    int vc = b.VertexCount;
                    int ic = b.IndexCount;

                    // DrawBatchKind: Normal=0, Shape=1, Instance=2, Effect=3, Mesh=4, Text=5, Nines=6, Mask=7
                    if ((int)b.Kind == 1 || (int)b.Kind == 4)
                    {
                        vc = 0;
                        ic = 0;
                        for (int k = 0; k < b.Count; k++)
                        {
                            int rank = b.StartRank + k;
                            int p_idx = frameData.DrawOrder(rank);
                            if (p_idx < 0 || p_idx >= partsLength) continue;

                            if ((int)b.Kind == 1) // Shape
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
                                uint v0 = frame.MeshVertexOffsets[p_idx];
                                uint v1 = frame.MeshVertexOffsets[p_idx + 1];
                                if (v1 > v0) vc += (int)(v1 - v0);

                                uint i0 = frame.MeshIndexOffsets[p_idx];
                                uint i1 = frame.MeshIndexOffsets[p_idx + 1];
                                if (i1 > i0) ic += (int)(i1 - i0);
                            }
                        }
                    }

                    drawBatches[i] = new DrawBatchData
                    {
                        StartRank = (ushort)b.StartRank,
                        Count = (ushort)b.Count,
                        VertexCount = (ushort)vc,
                        IndexCount = (ushort)ic,
                        Kind = (byte)b.Kind
                    };
                    totalVertices += vc;
                    totalIndices += ic;
                }

                // デバッグ用: もし再生されていればVerticesが0より大きいはず
                byte firstKind = drawBatchesLength > 0 ? drawBatches[0].Kind : (byte)255;
                int firstBlend = drawBatchesLength > 0 ? (int)frameData.DrawBatches(0).Value.BlendType : -1;
                Debug.Log($"[SS-DIAG] FrameNo:{frame.FrameNo} V:{totalVertices} I:{totalIndices} Batches:{drawBatchesLength} Kind0:{firstKind} Blend0:{firstBlend} | bufLen WM:{frame.WorldMatricesLen} LV:{frame.LocalVerticesLen} LUV:{frame.LocalUvsLen} parts:{partsLength}");

                for (int i = 0; i < drawOrderLength; i++)
                {
                    drawOrder[i] = frameData.DrawOrder(i);
                }

                for (int i = 0; i < partsLength; i++)
                {
                    var p = frameData.Parts(i).Value;
                    partStates[i] = new PartStateData
                    {
                        UpdateFlag = (ulong)p.UpdateFlag,
                        Alpha = p.Alpha,
                        PartColorIndex = p.PartColor
                    };
                }

                for (int i = 0; i < partsColorLength; i++)
                {
                    var pc = frameData.PartsColor(i).Value;
                    partColors[i] = new PartColorData
                    {
                        BlendType = (byte)pc.BlendType,
                        Lt = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Lt.Rgba.R, pc.Lt.Rgba.G, pc.Lt.Rgba.B, pc.Lt.Rgba.A), Rate = pc.Lt.Rate },
                        Rt = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Rt.Rgba.R, pc.Rt.Rgba.G, pc.Rt.Rgba.B, pc.Rt.Rgba.A), Rate = pc.Rt.Rate },
                        Lb = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Lb.Rgba.R, pc.Lb.Rgba.G, pc.Lb.Rgba.B, pc.Lb.Rgba.A), Rate = pc.Lb.Rate },
                        Rb = new CornerColorData { Rgba = new UnityEngine.Color32(pc.Rb.Rgba.R, pc.Rb.Rgba.G, pc.Rb.Rgba.B, pc.Rb.Rgba.A), Rate = pc.Rb.Rate }
                    };
                }

                // 以前のバッファが未解放なら待機して解放
                _updateJobHandle.Complete();
                if (_meshVertices.IsCreated) _meshVertices.Dispose();
                if (_meshIndices.IsCreated) _meshIndices.Dispose();
                if (_drawBatches.IsCreated) _drawBatches.Dispose();
                if (_drawOrder.IsCreated) _drawOrder.Dispose();
                if (_partStates.IsCreated) _partStates.Dispose();
                if (_partColors.IsCreated) _partColors.Dispose();

                if (totalVertices == 0 || totalIndices == 0)
                {
                    _meshVertices = default;
                    _meshIndices = default;
                    _drawBatches = default;
                    _drawOrder = default;
                    _partStates = default;
                    _partColors = default;

                    // 未使用の TempJob バッファは即座に解放
                    drawBatches.Dispose();
                    drawOrder.Dispose();
                    partStates.Dispose();
                    partColors.Dispose();
                    return;
                }

                _meshVertices = new Unity.Collections.NativeArray<SsVertex>(totalVertices, Unity.Collections.Allocator.TempJob);
                _meshIndices = new Unity.Collections.NativeArray<ushort>(totalIndices, Unity.Collections.Allocator.TempJob);
                _drawBatches = drawBatches;
                _drawOrder = drawOrder;
                _partStates = partStates;
                _partColors = partColors;

                var job = new UpdateMeshJob
                {
                    WorldMatrices = frame.WorldMatrices,
                    LocalUvs = frame.LocalUvs,
                    LocalVertices = frame.LocalVertices,
                    CellMeta = frame.CellMeta,

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

                    Vertices = _meshVertices,
                    Indices = _meshIndices
                };

                _updateJobHandle = job.Schedule();
            }
        }

        private Unity.Collections.NativeArray<SsVertex> _meshVertices;
        private Unity.Collections.NativeArray<ushort> _meshIndices;
        private Unity.Collections.NativeArray<DrawBatchData> _drawBatches;
        private Unity.Collections.NativeArray<ushort> _drawOrder;
        private Unity.Collections.NativeArray<PartStateData> _partStates;
        private Unity.Collections.NativeArray<PartColorData> _partColors;

        /// <summary>
        /// 発行済みのメッシュ構築ジョブの完了を待ち、UnityのMeshへ反映する
        /// </summary>
        public void ApplyToMesh(UnityEngine.Mesh targetMesh)
        {
            // Meshに適用する前に Job の完了を待機
            _updateJobHandle.Complete();

            if (!_meshVertices.IsCreated || !_meshIndices.IsCreated)
            {
                targetMesh.Clear();
                return;
            }

            // ゼロアロケーションでの Mesh 更新
            targetMesh.SetVertexBufferParams(_meshVertices.Length,
                new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Position, UnityEngine.Rendering.VertexAttributeFormat.Float32, 3),
                new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Color, UnityEngine.Rendering.VertexAttributeFormat.UNorm8, 4),
                new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.TexCoord0, UnityEngine.Rendering.VertexAttributeFormat.Float32, 2),
                new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.TexCoord1, UnityEngine.Rendering.VertexAttributeFormat.Float32, 4)
            );
            targetMesh.SetVertexBufferData(_meshVertices, 0, 0, _meshVertices.Length, 0, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);

            targetMesh.SetIndexBufferParams(_meshIndices.Length, UnityEngine.Rendering.IndexFormat.UInt16);
            targetMesh.SetIndexBufferData(_meshIndices, 0, 0, _meshIndices.Length, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);

            // サブメッシュは 1 つの前提（あるいはDrawBatchごとにSubMeshを切る場合は変更が必要）
            // Memo: uGUI では単一 SubMesh しか描画できないため 1 つにまとめる
            var subMeshDescriptor = new UnityEngine.Rendering.SubMeshDescriptor(0, _meshIndices.Length, UnityEngine.MeshTopology.Triangles);
            targetMesh.SetSubMesh(0, subMeshDescriptor, UnityEngine.Rendering.MeshUpdateFlags.Default);
            targetMesh.RecalculateBounds();
            
            // バッファを解放する前にデバッグ: 全頂点をスキャンして「ゼロか実データか」を判定
            if (_meshVertices.Length > 0)
            {
                int nonZeroPos = 0, nonZeroAlpha = 0, nonZeroUv = 0, rateNonZero = 0;
                int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
                float minRate = float.MaxValue, maxRate = float.MinValue;
                for (int vi = 0; vi < _meshVertices.Length; vi++)
                {
                    var v = _meshVertices[vi];
                    if (v.Position.x != 0f || v.Position.y != 0f) nonZeroPos++;
                    if (v.Color.a != 0) nonZeroAlpha++;
                    if (v.TexCoord0.x != 0f || v.TexCoord0.y != 0f) nonZeroUv++;
                    if (v.Color.r < minR) minR = v.Color.r; if (v.Color.r > maxR) maxR = v.Color.r;
                    if (v.Color.g < minG) minG = v.Color.g; if (v.Color.g > maxG) maxG = v.Color.g;
                    if (v.Color.b < minB) minB = v.Color.b; if (v.Color.b > maxB) maxB = v.Color.b;
                    float rate = v.TexCoord1.x;
                    if (rate != 0f) rateNonZero++;
                    if (rate < minRate) minRate = rate; if (rate > maxRate) maxRate = rate;
                }
                var mid = _meshVertices[_meshVertices.Length / 2];
                var b = targetMesh.bounds;
                Debug.Log($"[SS-DIAG] applied verts:{_meshVertices.Length} nonZeroPos:{nonZeroPos} nonZeroAlpha:{nonZeroAlpha} nonZeroUv:{nonZeroUv} | colR[{minR}-{maxR}] G[{minG}-{maxG}] B[{minB}-{maxB}] rate[{minRate:F2}-{maxRate:F2}] rateNonZero:{rateNonZero} | bounds sz:{b.size} | midV col:{mid.Color} uv:{mid.TexCoord0} tc1:{mid.TexCoord1}");
            }

            // バッファを解放
            _meshVertices.Dispose();
            _meshIndices.Dispose();
            _drawBatches.Dispose();
            _drawOrder.Dispose();
            _partStates.Dispose();
            _partColors.Dispose();
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
