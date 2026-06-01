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

        /// <summary>
        /// 毎フレームのアニメーション更新処理
        /// </summary>
        /// <param name="deltaTime">経過時間</param>
        public void Tick(float deltaTime)
        {
            // 前回フレームで投げた Job が終わるのを確実に待機する (純粋なゼロコピーパイプラインのため)
            _updateJobHandle.Complete();

            if (!_handle.IsValid) return;

            unsafe
            {
                NativeMethods.ss_runtime_update(_handle.ptr, deltaTime);
            }
        }

        /// <summary>
        /// Burst Job を発行してメッシュバッファを計算する
        /// </summary>
        public void ScheduleMeshUpdate()
        {
            if (!_handle.IsValid) return;

            // TODO: Job のパラメータ構築と Schedule
            // var job = new UpdateMeshJob { ... };
            // _updateJobHandle = job.Schedule();
        }

        /// <summary>
        /// 計算されたバッファを Unity の Mesh に適用する (ゼロアロケーション)
        /// Layer 3 のラッパーコンポーネント側から呼び出されます。
        /// </summary>
        public void ApplyToMesh(Mesh targetMesh)
        {
            // Meshに適用する前に Job の完了を待機
            _updateJobHandle.Complete();

            // targetMesh は初期化時に targetMesh.MarkDynamic() が呼ばれている前提
            
            // TODO: ゼロアロケーションでの Mesh 更新 (SetVertexBufferData / SetIndexBufferData)
            // targetMesh.SetVertexBufferData(_vertices, 0, 0, vertexCount);
            // targetMesh.SetIndexBufferData(_indices, 0, 0, indexCount);
            // targetMesh.subMeshCount = ...;
            // targetMesh.SetSubMesh(0, new UnityEngine.Rendering.SubMeshDescriptor(0, indexCount));
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

            // TODO: メッシュ生成用 NativeArray 群の解放
            // if (_vertices.IsCreated) _vertices.Dispose();
            // if (_indices.IsCreated) _indices.Dispose();
        }
    }
}
