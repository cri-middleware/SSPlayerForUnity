using UnityEngine;
using UnityEngine.Timeline;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Layer 3: ワールド座標用プレイヤーコンポーネント (3D空間/2Dシーン用)
    /// Unity の Transform および MeshRenderer を利用して描画を行います。
    /// 再生ロジックは共有コア (SpriteStudioPlaybackCore) に委譲し、毎フレームの駆動は
    /// SpriteStudioUpdateManager が「全 Schedule → 全 Apply」でまとめて行います (Job 並列化)。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    // NOTE: [ExecuteAlways] は無効。Play モードでのみ再生・描画する (編集モードは静止)。
    public class SpriteStudioPlayer : MonoBehaviour, ITimeControl, ISpriteStudioRenderable
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
            _core.Play(_animationName, _loop, _speedScale, "SpriteStudioPlayer");
        }

        private void Start()
        {
            if (_autoPlay && !string.IsNullOrEmpty(_animationName))
            {
                Play(_animationName);
            }
        }

        private readonly SpriteStudioPlaybackCore _core = new SpriteStudioPlaybackCore();
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

            _core.EnsureCreated(_animeAsset, _subFrameEnabled);
            SpriteStudioUpdateManager.Register(this);
        }

        private void OnDisable()
        {
            SpriteStudioUpdateManager.Unregister(this);
            _core.Dispose();
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

        // --- ISpriteStudioRenderable (SpriteStudioUpdateManager から駆動される) ---

        public void TickAndSchedule(float deltaTime)
        {
            if (_core.Internal == null) return;

            if (_timelineControlled)
            {
                // Timeline がフレームを制御するため自動進行はしない (SetTime で設定済みのフレームを描画する)
                _core.Internal.ScheduleMeshUpdate(0f);
            }
            else
            {
                _core.TickAndSchedule(deltaTime, SubFrameEnabled);
            }
        }

        public void ApplyAndRender()
        {
            if (_core.Internal == null) return;

            // バッチ単位に SubMesh を分割し、テクスチャごとに別マテリアルを適用する
            _core.Internal.ApplyToMesh(_sharedMesh, true);
            UpdateSubmeshMaterials();
        }

        private Material[] _submeshMaterials;
        private uint _lastMaterialSignature;

        /// <summary>
        /// SubMesh (= 描画バッチ) ごとに、テクスチャハッシュへ対応するマテリアルを割り当てる。
        /// バッチ構成が前フレームと同じなら再割り当てしない (毎フレームのアロケーションを避ける)。
        /// </summary>
        private void UpdateSubmeshMaterials()
        {
            if (_animeAsset == null || _meshRenderer == null || _core.Internal == null) return;

            var mats = _animeAsset.Materials;
            if (mats == null || mats.Count == 0) return;

            int subCount = _core.Internal.SubmeshCount;
            if (subCount <= 0) return;

            // 構成 (SubMesh 数 + 各テクスチャハッシュ) のシグネチャが不変なら再割り当てしない
            uint signature = (uint)subCount;
            for (int s = 0; s < subCount; s++)
            {
                signature = signature * 31u + _core.Internal.GetSubmeshTextureHash(s);
            }
            if (signature == _lastMaterialSignature && _submeshMaterials != null && _submeshMaterials.Length == subCount)
            {
                return;
            }
            _lastMaterialSignature = signature;

            if (_submeshMaterials == null || _submeshMaterials.Length != subCount)
            {
                _submeshMaterials = new Material[subCount];
            }
            for (int s = 0; s < subCount; s++)
            {
                uint hash = _core.Internal.GetSubmeshTextureHash(s);
                int mi = _animeAsset.GetMaterialIndexForHash(hash);
                if (mi < 0 || mi >= mats.Count) 
                {
                    Debug.LogError($"[SpriteStudioPlayer] TextureHash {hash} not found in AnimeAsset (Submesh {s}). Falling back to material 0.");
                    mi = 0; // 対応不明なら先頭にフォールバック (退行なし)
                }
                _submeshMaterials[s] = mats[mi];
            }
            _meshRenderer.sharedMaterials = _submeshMaterials;
        }

        #region ITimeControl (Timeline 連携)

        private bool _timelineControlled;

        public void OnControlTimeStart()
        {
            _timelineControlled = true;
            // クリップ制御の開始時にアニメーションをセットアップしておく
            if (_core.Internal != null && !string.IsNullOrEmpty(_animationName))
            {
                _core.Internal.SetupAnimation(_animationName);
            }
        }

        public void SetTime(double time)
        {
            // Timeline 経由のシーク: クリップ内ローカル時間 (秒) をフレーム番号へ変換して設定する
            if (_core.Internal == null) return;
            int fps = _core.Internal.GetFrameRate();
            if (fps <= 0) fps = 60;
            _core.Internal.SetFrame((float)(time * fps));
        }

        public void OnControlTimeStop()
        {
            _timelineControlled = false;
        }

        #endregion
    }
}
