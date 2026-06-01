using System;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using SpriteStudio.Runtime.Bindings;

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

        public SpriteStudioInternalPlayer()
        {
            // TODO: FFI経由でネイティブ側のインスタンス（ポインタ）を生成して _handle に格納する
            // _handle = new SsPlayerHandle { ptr = SSRuntimeMethods.ss_runtime_player_create(...) };
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

            // TODO: ネイティブ側の Tick() 呼び出し
            // SSRuntimeMethods.ss_runtime_player_tick(_handle.ptr, deltaTime);
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
                    // TODO: FFI経由でポインタを解放する
                    // SSRuntimeMethods.ss_runtime_player_destroy(_handle.ptr);
                    _handle.ptr = null;
                }
            }

            // TODO: メッシュ生成用 NativeArray 群の解放
            // if (_vertices.IsCreated) _vertices.Dispose();
            // if (_indices.IsCreated) _indices.Dispose();
        }
    }
}
