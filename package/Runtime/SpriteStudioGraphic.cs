using UnityEngine;
using UnityEngine.UI;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Layer 3: Canvas (uGUI) 用プレイヤーコンポーネント
    /// CanvasRenderer に対して直接メッシュを送り込みます。
    /// Unity UI の標準再構築フロー (Rebuild) を回避し、高効率な描画を実現します。
    /// 再生ロジックは共有コア (SpriteStudioPlaybackCore) に委譲します。
    /// play モードでは SpriteStudioUpdateManager がまとめて駆動し、edit モード (ExecuteAlways) では
    /// 自前の LateUpdate でプレビュー更新します。
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    [ExecuteAlways]
    public class SpriteStudioGraphic : Graphic, ISpriteStudioRenderable
    {
        [Header("SpriteStudio Asset")]
        [Tooltip("描画対象となるコンバート済みの ssab アセット")]
        [SerializeField] private SpriteStudioAnimeAsset _animeAsset;

        public SpriteStudioAnimeAsset AnimeAsset
        {
            get => _animeAsset;
            set
            {
                _animeAsset = value;
                _core.ReloadAsset(_animeAsset);
            }
        }

        [Header("Playback Settings")]
        [Tooltip("再生するアニメーション名 (パック名/アニメ名など)")]
        [SerializeField] private string _animationName = "";
        public string AnimationName
        {
            get => _animationName;
            set => _animationName = value;
        }

        [Tooltip("起動時に自動で再生を開始するか")]
        [SerializeField] private bool _autoPlay = true;
        public bool AutoPlay
        {
            get => _autoPlay;
            set => _autoPlay = value;
        }

        [Tooltip("ループ再生を有効にするか")]
        [SerializeField] private bool _loop = true;
        public bool Loop
        {
            get => _loop;
            set
            {
                _loop = value;
                _core.Internal?.SetLoop(_loop ? -1 : 1);
            }
        }

        [Tooltip("アニメーションの再生速度スケール")]
        [SerializeField] private float _speedScale = 1.0f;
        public float SpeedScale
        {
            get => _speedScale;
            set
            {
                _speedScale = value;
                _core.Internal?.SetSpeed(_speedScale);
            }
        }

        [Tooltip("サブフレーム描画（フレーム間の補間）を有効にするか")]
        [SerializeField] private bool _subFrameEnabled = false;
        public bool SubFrameEnabled
        {
            get => _core.Internal != null ? _core.Internal.SubFrameEnabled : _subFrameEnabled;
            set
            {
                _subFrameEnabled = value;
                if (_core.Internal != null) _core.Internal.SubFrameEnabled = value;
            }
        }

        /// <summary>
        /// 指定したアニメーションの再生を開始します。
        /// </summary>
        public void Play(string animName)
        {
            _animationName = animName;
            _core.Play(_animationName, _loop, _speedScale, "SpriteStudioGraphic");
        }

        protected override void Start()
        {
            base.Start();
            if (_autoPlay && !string.IsNullOrEmpty(_animationName))
            {
                Play(_animationName);
            }
        }

        private readonly SpriteStudioPlaybackCore _core = new SpriteStudioPlaybackCore();
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

            _core.EnsureCreated(_animeAsset, _subFrameEnabled);
            SpriteStudioUpdateManager.Register(this);
        }

        protected override void OnDisable()
        {
            base.OnDisable();

            SpriteStudioUpdateManager.Unregister(this);
            _core.Dispose();

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
            // play モードは SpriteStudioUpdateManager が集中管理する。
            // edit モード (ExecuteAlways) のプレビューのみここで自前更新する。
            if (Application.isPlaying) return;
            TickAndSchedule(Time.deltaTime);
            ApplyAndRender();
        }

        // --- ISpriteStudioRenderable (SpriteStudioUpdateManager から駆動される) ---

        public void TickAndSchedule(float deltaTime)
        {
            _core.TickAndSchedule(deltaTime, SubFrameEnabled);
        }

        public void ApplyAndRender()
        {
            if (_core.Internal == null || _sharedMesh == null) return;

            // uGUI は単一 SubMesh (CanvasRenderer は 1 マテリアルのみ)
            _core.Internal.ApplyToMesh(_sharedMesh, false);
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
