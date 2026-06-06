using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// 構築済みジオメトリ (頂点 / インデックス / SubMesh / バウンズ) を Unity の <see cref="Mesh"/> へ
    /// アップロードする責務を <see cref="SpriteStudioGeometryBuilder"/> から分離したコラボレータ。
    ///
    /// トポロジ (index/submesh 構造) が前回アップロードと一致する間は頂点だけ更新し、index/submesh の
    /// 再アップロードを省く「index キャッシュ」を内部に持つ。キャッシュ状態は本クラスが完全所有する。
    /// </summary>
    internal sealed class SpriteStudioMeshUploader : IDisposable
    {
        /// <summary>アップロード対象の構築済みジオメトリ一式 (ビルダーが所有するバッファへの参照)。</summary>
        internal readonly struct UploadData
        {
            public readonly bool FrameSkipped;
            public readonly int VertexCount;
            public readonly int IndexCount;
            public readonly NativeArray<SsVertex> Vertices;
            public readonly NativeArray<uint> Indices;
            public readonly NativeArray<SubmeshInfo> Submeshes;
            public readonly int SubmeshCount;
            public readonly ulong TopologySignature;
            public readonly NativeArray<Vector3> Bounds;

            public UploadData(bool frameSkipped, int vertexCount, int indexCount,
                NativeArray<SsVertex> vertices, NativeArray<uint> indices,
                NativeArray<SubmeshInfo> submeshes, int submeshCount,
                ulong topologySignature, NativeArray<Vector3> bounds)
            {
                FrameSkipped = frameSkipped;
                VertexCount = vertexCount;
                IndexCount = indexCount;
                Vertices = vertices;
                Indices = indices;
                Submeshes = submeshes;
                SubmeshCount = submeshCount;
                TopologySignature = topologySignature;
                Bounds = bounds;
            }
        }

        // 頂点レイアウト (Position / Color / TexCoord0 / TexCoord1)。SetVertexBufferParams へ使い回す。
        private static readonly VertexAttributeDescriptor[] s_vertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
        };

        // SetSubMeshes 用の永続バッファ (grow-only で使い回す)。
        private NativeArray<SubMeshDescriptor> _submeshDescriptors;

        // index アップロードのキャッシュ用。トポロジ (バッチ構造) が不変なら index/submesh の再送を省く。
        private bool _meshUploaded;
        private int _lastMeshId;
        private ulong _uploadedTopologySignature;
        private int _uploadedVertexCount;
        private int _uploadedIndexCount;
        private bool _uploadedPerBatchSubmesh;

        /// <summary>
        /// 構築済みジオメトリを <paramref name="targetMesh"/> へ反映する。Job の完了待ちは呼び出し側
        /// (ビルダー) が済ませている前提。frame-skip 時は前回の Mesh が正しいので何もしない。
        /// </summary>
        public void Apply(Mesh targetMesh, bool perBatchSubmesh, in UploadData data)
        {
            // ScheduleMeshUpdate が再構築を省いたフレームは Mesh が前回のまま正しいので、再アップロードも省く。
            if (data.FrameSkipped) return;

            if (data.VertexCount == 0 || data.IndexCount == 0 || !data.Vertices.IsCreated || !data.Indices.IsCreated)
            {
                targetMesh.Clear();
                _meshUploaded = false; // 次回はフルアップロード (キャッシュ無効化)
                return;
            }

            // Unity の厳格なバリデーション (Overlap や領域外参照) を回避するためのフラグ。
            var updateFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

            // トポロジ (index/submesh 構造) が前回アップロードと完全一致するなら、毎フレーム動く頂点だけ
            // 更新し、index/submesh の再アップロードを省く。Mesh インスタンスが変わった場合 (共有/プレビュー
            // 等) は安全側でフルアップロードに倒す。
            int meshId = targetMesh.GetInstanceID();
            bool topoUnchanged = _meshUploaded
                && meshId == _lastMeshId
                && data.TopologySignature == _uploadedTopologySignature
                && data.VertexCount == _uploadedVertexCount
                && data.IndexCount == _uploadedIndexCount
                && perBatchSubmesh == _uploadedPerBatchSubmesh;

            if (topoUnchanged)
            {
                targetMesh.SetVertexBufferData(data.Vertices, 0, 0, data.VertexCount, 0, updateFlags);
                ApplyJobComputedBounds(targetMesh, data.Bounds);
                return;
            }

            // --- フルアップロード (初回 or トポロジ変化) ---
            // バッファ再宣言時の領域外参照を避けるため一旦 submesh を空にする。
            targetMesh.SetSubMesh(0, new SubMeshDescriptor(0, 0), updateFlags);
            targetMesh.subMeshCount = 1;

            targetMesh.SetVertexBufferParams(data.VertexCount, s_vertexLayout);
            targetMesh.SetIndexBufferParams(data.IndexCount, IndexFormat.UInt32);

            targetMesh.SetVertexBufferData(data.Vertices, 0, 0, data.VertexCount, 0, updateFlags);
            targetMesh.SetIndexBufferData(data.Indices, 0, 0, data.IndexCount, updateFlags);

            if (perBatchSubmesh && data.SubmeshCount > 0)
            {
                // SetSubMeshes で一括設定する (逐次設定時の Overlap 警告を回避)。
                EnsureDescriptors(data.SubmeshCount);
                for (int s = 0; s < data.SubmeshCount; s++)
                {
                    var info = data.Submeshes[s];
                    _submeshDescriptors[s] = new SubMeshDescriptor(info.IndexStart, info.IndexCount, MeshTopology.Triangles);
                }
                targetMesh.SetSubMeshes(_submeshDescriptors, 0, data.SubmeshCount, updateFlags);
            }
            else
            {
                targetMesh.subMeshCount = 1;
                targetMesh.SetSubMesh(0, new SubMeshDescriptor(0, data.IndexCount, MeshTopology.Triangles), updateFlags);
            }
            ApplyJobComputedBounds(targetMesh, data.Bounds);

            // アップロード済みトポロジを記録 (次フレーム以降の index 再送スキップ判定に使う)。
            _meshUploaded = true;
            _lastMeshId = meshId;
            _uploadedTopologySignature = data.TopologySignature;
            _uploadedVertexCount = data.VertexCount;
            _uploadedIndexCount = data.IndexCount;
            _uploadedPerBatchSubmesh = perBatchSubmesh;
        }

        /// <summary>
        /// UpdateMeshJob が算出した min/max を Mesh.bounds へ適用する。未算出時は RecalculateBounds。
        /// </summary>
        private static void ApplyJobComputedBounds(Mesh targetMesh, NativeArray<Vector3> bounds)
        {
            if (!bounds.IsCreated || bounds.Length < 2)
            {
                targetMesh.RecalculateBounds();
                return;
            }
            Vector3 mn = bounds[0];
            Vector3 mx = bounds[1];
            targetMesh.bounds = new Bounds((mn + mx) * 0.5f, mx - mn);
        }

        private void EnsureDescriptors(int needed)
        {
            if (_submeshDescriptors.IsCreated && _submeshDescriptors.Length >= needed) return;
            if (_submeshDescriptors.IsCreated) _submeshDescriptors.Dispose();
            _submeshDescriptors = new NativeArray<SubMeshDescriptor>(needed < 1 ? 1 : needed,
                Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        public void Dispose()
        {
            if (_submeshDescriptors.IsCreated) _submeshDescriptors.Dispose();
            _meshUploaded = false;
        }
    }
}
