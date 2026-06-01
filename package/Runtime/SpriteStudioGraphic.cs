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
        [Header("SpriteStudio Asset")]
        [Tooltip("描画対象となるコンバート済みの ssab アセット")]
        [SerializeField] private SpriteStudioAnimeAsset _animeAsset;

        public SpriteStudioAnimeAsset AnimeAsset
        {
            get => _animeAsset;
            set
            {
                _animeAsset = value;
                if (_internalPlayer != null && _animeAsset != null && _animeAsset.SsabData != null)
                {
                    _internalPlayer.LoadSsab(_animeAsset.SsabData);
                }
            }
        }
            // TODO: アニメーション名からインデックスを引くための辞書情報など

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
                if (_internalPlayer != null)
                {
                    _internalPlayer.SetLoop(_loop ? -1 : 1);
                }
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
                if (_internalPlayer != null)
                {
                    _internalPlayer.SetSpeed(_speedScale);
                }
            }
        }

        [Tooltip("サブフレーム描画（フレーム間の補間）を有効にするか")]
        [SerializeField] private bool _subFrameEnabled = false;
        public bool SubFrameEnabled
        {
            get => _internalPlayer != null ? _internalPlayer.SubFrameEnabled : _subFrameEnabled;
            set
            {
                _subFrameEnabled = value;
                if (_internalPlayer != null) _internalPlayer.SubFrameEnabled = value;
            }
        }

        /// <summary>
        /// 指定したアニメーションの再生を開始します。
        /// </summary>
        public void Play(string animName)
        {
            _animationName = animName;
            if (_internalPlayer != null && !string.IsNullOrEmpty(_animationName))
            {
                if (_internalPlayer.SetupAnimation(_animationName))
                {
                    _internalPlayer.SetPlaybackStyle(1, 0); // Forward, Normal
                    _internalPlayer.SetLoop(_loop ? -1 : 1); // -1 = Infinite loop, 1 = Play once
                    _internalPlayer.SetSpeed(_speedScale);
                    _internalPlayer.Play();
                }
                else
                {
                    Debug.LogWarning($"[SpriteStudioGraphic] Failed to setup animation: {_animationName}");
                }
            }
        }

        protected override void Start()
        {
            base.Start();
            if (_autoPlay && !string.IsNullOrEmpty(_animationName))
            {
                Play(_animationName);
            }
        }

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
                _internalPlayer.SubFrameEnabled = _subFrameEnabled;
                if (_animeAsset != null && _animeAsset.SsabData != null)
                {
                    _internalPlayer.LoadSsab(_animeAsset.SsabData);
                }
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
            _internalPlayer.Tick(Time.deltaTime, SubFrameEnabled);
            _internalPlayer.ScheduleMeshUpdate(Time.deltaTime);

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
