using UnityEngine;
using UnityEngine.UI;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Layer 3: Canvas (uGUI) 用プレイヤーコンポーネント
    /// CanvasRenderer に対して直接メッシュを送り込みます。
    /// Unity UI の標準再構築フロー (Rebuild) を回避し、高効率な描画を実現します。
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    [ExecuteAlways]
    public class SpriteStudioGraphic : Graphic
    {
        private SpriteStudioInternalPlayer _internalPlayer;
        private Mesh _sharedMesh;

        protected override void OnEnable()
        {
            base.OnEnable();

            if (_sharedMesh == null)
            {
                _sharedMesh = new Mesh();
                _sharedMesh.name = "SpriteStudio_CanvasMesh";
                _sharedMesh.MarkDynamic();
            }

            if (_internalPlayer == null)
            {
                _internalPlayer = new SpriteStudioInternalPlayer();
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();

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

            // 1. 更新
            _internalPlayer.Tick(Time.deltaTime);
            _internalPlayer.ScheduleMeshUpdate();

            // 2. メッシュ反映
            _internalPlayer.ApplyToMesh(_sharedMesh);

            // 3. CanvasRenderer に直接メッシュを適用
            // OnPopulateMesh などの標準パスをバイパスする
            canvasRenderer.SetMesh(_sharedMesh);
        }

        // --- Canvas の Rebuild フローとの衝突回避 ---
        // uGUI の Layout や Vertex の自動再構築が発動して
        // 勝手にメッシュを破壊・再アロケーションするのを防ぐ

        public override void SetVerticesDirty()
        {
            // 無効化: 標準の頂点再構築を発動させない
        }

        public override void SetMaterialDirty()
        {
            // 無効化: 標準のマテリアル再構築を発動させない
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            // 無効化: 勝手に UI Vertex に変換させない
            vh.Clear();
        }
    }
}
