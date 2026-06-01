using UnityEngine;
using UnityEngine.Timeline;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Layer 3: ワールド座標用プレイヤーコンポーネント (3D空間/2Dシーン用)
    /// Unity の Transform および MeshRenderer を利用して描画を行います。
    /// 内部で Layer 2 の InternalPlayer を保持し、処理を委譲します。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [ExecuteAlways]
    public class SpriteStudioPlayer : MonoBehaviour, ITimeControl
    {
        [Header("Sorting")]
        [SerializeField] private string _sortingLayerName = "Default";
        [SerializeField] private int _sortingOrder = 0;

        public string SortingLayerName
        {
            get => _meshRenderer != null ? _meshRenderer.sortingLayerName : _sortingLayerName;
            set
            {
                _sortingLayerName = value;
                if (_meshRenderer != null) _meshRenderer.sortingLayerName = value;
            }
        }

        public int SortingOrder
        {
            get => _meshRenderer != null ? _meshRenderer.sortingOrder : _sortingOrder;
            set
            {
                _sortingOrder = value;
                if (_meshRenderer != null) _meshRenderer.sortingOrder = value;
            }
        }

        private SpriteStudioInternalPlayer _internalPlayer;
        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Mesh _sharedMesh;

        private void OnEnable()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();

            // 描画順の初期適用
            _meshRenderer.sortingLayerName = _sortingLayerName;
            _meshRenderer.sortingOrder = _sortingOrder;

            // ゼロアロケーション描画用の単一メッシュを構築 (再生成させない)
            if (_sharedMesh == null)
            {
                _sharedMesh = new Mesh();
                _sharedMesh.name = "SpriteStudio_DynamicMesh";
                _sharedMesh.MarkDynamic(); // 毎フレーム更新用に最適化
            }
            _meshFilter.sharedMesh = _sharedMesh;

            if (_internalPlayer == null)
            {
                _internalPlayer = new SpriteStudioInternalPlayer();
            }
        }

        private void OnDisable()
        {
            if (_internalPlayer != null)
            {
                _internalPlayer.Dispose();
                _internalPlayer = null;
            }

            if (_sharedMesh != null)
            {
                if (Application.isPlaying)
                    Destroy(_sharedMesh);
                else
                    DestroyImmediate(_sharedMesh);
                _sharedMesh = null;
            }
        }

        private void LateUpdate()
        {
            if (_internalPlayer == null) return;

            // 1. ロジック更新 (Tick)
            _internalPlayer.Tick(Time.deltaTime);

            // 2. メッシュ生成 Job スケジュール
            _internalPlayer.ScheduleMeshUpdate();

            // 3. メッシュ適用 (Job の待機と SetVertexBufferData)
            _internalPlayer.ApplyToMesh(_sharedMesh);
            
            // TODO: マテリアルが切り替わった場合は _meshRenderer.sharedMaterials を更新する処理を追加
        }

        #region ITimeControl (Timeline 連携)

        public void SetTime(double time)
        {
            // Timeline 経由でのシーク再生時に呼び出される
            // TODO: _internalPlayer に対して特定時間のアニメーションポーズ計算を要求する
        }

        public void OnControlTimeStart() { }
        public void OnControlTimeStop() { }

        #endregion
    }
}
