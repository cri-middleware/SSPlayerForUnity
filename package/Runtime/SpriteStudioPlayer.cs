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
                    Debug.LogWarning($"[SpriteStudioPlayer] Failed to setup animation: {_animationName}");
                }
            }
        }

        private void Start()
        {
            if (_autoPlay && !string.IsNullOrEmpty(_animationName))
            {
                Play(_animationName);
            }
        }

        private SpriteStudioInternalPlayer _internalPlayer;
        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Mesh _sharedMesh;
        private MaterialPropertyBlock _mpb;

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

            if (_mpb == null) _mpb = new MaterialPropertyBlock();

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

        private void OnDisable()
        {
            if (_internalPlayer != null)
            {
                _internalPlayer.Dispose();
                _internalPlayer = null;
            }
        }

        private void OnDestroy()
        {
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

            // 2. メッシュ反映適用 (Job の待機と SetVertexBufferData)
            _internalPlayer.ApplyToMesh(_sharedMesh);
            
            // 3. マテリアルの設定
            if (_animeAsset != null && _meshRenderer != null)
            {
                var mats = _animeAsset.Materials;
                if (mats != null && mats.Count > 0)
                {
                    var currentMats = _meshRenderer.sharedMaterials;
                    if (currentMats == null || currentMats.Length != mats.Count || currentMats[0] != mats[0])
                    {
                        _meshRenderer.sharedMaterials = mats.ToArray();
                    }
                }
            }
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
