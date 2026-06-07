using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

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

        [Tooltip("ループ回数。-1 = 無限ループ / 1 = 1回再生 / n = n回再生")]
        [SerializeField] private int _loopCount = -1;
        public int LoopCount
        {
            get => _loopCount;
            set
            {
                _loopCount = value;
                _core.Internal?.SetLoop(value);
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

        [Tooltip("実時間追従 (timed)。ON: 負荷でフレームが落ちても経過時間ぶん進める。OFF (fixed): 毎 update で必ず 1 フレームずつ進める (コマ送り)。")]
        [SerializeField] private bool _skipFrames = true;
        public bool SkipFrames
        {
            get => _core.Internal != null ? _core.Internal.IsSkipFrames() : _skipFrames;
            set
            {
                _skipFrames = value;
                _core.Internal?.SetSkipFrames(value);
            }
        }

        /// <summary>
        /// 指定したアニメーションの再生を開始します。
        /// </summary>
        public void Play(string animName)
        {
            _animationName = animName;
            _core.Play(_animationName, _loopCount, _speedScale, _skipFrames, "SpriteStudioGraphic");
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

        public event System.Action OnAnimationFinished
        {
            add => _core.OnAnimationFinished += value;
            remove => _core.OnAnimationFinished -= value;
        }
        public event System.Action OnAnimationLooped
        {
            add => _core.OnAnimationLooped += value;
            remove => _core.OnAnimationLooped -= value;
        }
        public event System.Action<SpriteStudioUserData> OnUserData
        {
            add => _core.OnUserData += value;
            remove => _core.OnUserData -= value;
        }
        public event System.Action<SpriteStudioSignal> OnSignal
        {
            add => _core.OnSignal += value;
            remove => _core.OnSignal -= value;
        }
        public event System.Action<SpriteStudioAudio> OnAudio
        {
            add => _core.OnAudio += value;
            remove => _core.OnAudio -= value;
        }

        [Header("Events")]
        public UnityEvent OnAnimationFinishedEvent = new UnityEvent();
        public UnityEvent OnAnimationLoopedEvent = new UnityEvent();
        public UserDataUnityEvent OnUserDataEvent = new UserDataUnityEvent();
        public SignalUnityEvent OnSignalEvent = new SignalUnityEvent();
        public AudioUnityEvent OnAudioEvent = new AudioUnityEvent();

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

            _core.OnAnimationFinished += InvokeAnimationFinished;
            _core.OnAnimationLooped += InvokeAnimationLooped;
            _core.OnUserData += InvokeUserData;
            _core.OnSignal += InvokeSignal;
            _core.OnAudio += InvokeAudio;
        }

        private void InvokeAnimationFinished() => OnAnimationFinishedEvent?.Invoke();
        private void InvokeAnimationLooped() => OnAnimationLoopedEvent?.Invoke();
        private void InvokeUserData(SpriteStudioUserData data) => OnUserDataEvent?.Invoke(data);
        private void InvokeSignal(SpriteStudioSignal data) => OnSignalEvent?.Invoke(data);
        private void InvokeAudio(SpriteStudioAudio data) => OnAudioEvent?.Invoke(data);

        protected override void OnDisable()
        {
            base.OnDisable();

            SpriteStudioUpdateManager.Unregister(this);

            _core.OnAnimationFinished -= InvokeAnimationFinished;
            _core.OnAnimationLooped -= InvokeAnimationLooped;
            _core.OnUserData -= InvokeUserData;
            _core.OnSignal -= InvokeSignal;
            _core.OnAudio -= InvokeAudio;

            _core.Dispose();

            // クローン所有マテリアルを破棄し、次回有効化時に再解決させるためシグネチャを無効化する。
            DestroyOwnedMaterials();
            _canvasMaterials.Clear();
            _lastMaterialSignature = 0;
            _hasWriteSubmesh = false;

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
            // edit モード (ExecuteAlways) のプレビューのみここで自前更新する (直列・並列経路とは分離)。
            if (Application.isPlaying) return;
            _core.TickAndSchedule(Time.deltaTime, SubFrameEnabled);
            ApplyAndRender();
        }

#if UNITY_EDITOR
        // インスペクタはシリアライズ済みフィールドを直接書き換え、プロパティの setter を呼ばない。
        // 再生中のインスペクタ変更をランタイム (Rust) へ反映するにはここで同期する。Graphic.OnValidate は
        // UNITY_EDITOR 内のみで定義されるため override も #if UNITY_EDITOR で囲む。(アニメ名切替は対象外)
        protected override void OnValidate()
        {
            base.OnValidate();
            if (_core.Internal == null) return;
            _core.Internal.SetSpeed(_speedScale);
            _core.Internal.SetLoop(_loopCount);
            _core.Internal.SetSkipFrames(_skipFrames);
            _core.Internal.SubFrameEnabled = _subFrameEnabled;
        }
#endif

        // --- ISpriteStudioRenderable (SpriteStudioUpdateManager から駆動される) ---

        public void PrepareFrame(float deltaTime, out System.IntPtr handle, out float drawFrame)
        {
            if (_core.Internal == null) { handle = System.IntPtr.Zero; drawFrame = 0f; return; }
            _core.Internal.PrepareFrameData(deltaTime, true, out handle, out drawFrame);
        }

        public void ConsumeFrame(System.IntPtr frameDataPtr, int len, float deltaTime)
        {
            if (_core.Internal == null) return;
            _core.Internal.ConsumeFrameData(frameDataPtr, len, deltaTime);
        }

        public void ApplyAndRender()
        {
            if (_core.Internal == null || _sharedMesh == null) return;

            // バッチ単位に SubMesh を分割し、テクスチャ/ブレンド/マスクごとに別マテリアルを当てる
            // (Mesh 版 SpriteStudioPlayer と同等)。CanvasRenderer は materialCount + SetMaterial で
            // SubMesh と 1:1 にマテリアルを対応づけられる。
            _core.Internal.ApplyToMesh(_sharedMesh, true);
            UpdateCanvasMaterials();
            canvasRenderer.SetMesh(_sharedMesh);
        }

        // --- SubMesh ごとのマテリアル解決 (マルチテクスチャ / ブレンド / マスク) ---
        // 解決規則は SpriteStudioPlayer.UpdateSubmeshMaterials と同一。相違点は 2 つ:
        //   1) MeshRenderer.SetSharedMaterials の代わりに CanvasRenderer.materialCount + SetMaterial を使う。
        //   2) CanvasRenderer は MaterialPropertyBlock 非対応なので、マスク書込 SubMesh の per-frame 閾値
        //      (_MaskThreshold) はクローンした所有マテリアルへ直接書き込む。
        private readonly System.Collections.Generic.List<Material> _canvasMaterials = new System.Collections.Generic.List<Material>();
        // Write SubMesh 用にクローンして所有するマテリアル (閾値書込のため)。OnDisable / 再構築時に破棄する。
        private readonly System.Collections.Generic.List<Material> _ownedMaterialInstances = new System.Collections.Generic.List<Material>();
        private uint _lastMaterialSignature;
        private bool _hasWriteSubmesh;

        private void UpdateCanvasMaterials()
        {
            if (_animeAsset == null || _core.Internal == null) return;

            int subCount = _core.Internal.SubmeshCount;
            if (subCount <= 0) return;

            var submeshes = _core.Internal.Submeshes;

            // 構成シグネチャ。threshold は per-frame 可変なのでここには含めず、書込マテリアルへ別途設定する。
            uint signature = (uint)subCount;
            bool hasWrite = false;
            for (int s = 0; s < subCount; s++)
            {
                var info = submeshes[s];
                signature = signature * 31u + info.TextureHash;
                signature = signature * 31u + info.BlendType;
                signature = signature * 31u + (uint)info.Role;
                signature = signature * 31u + info.StencilOp;
                signature = signature * 31u + info.StencilComp;
                if (info.Role == StencilRole.Write) hasWrite = true;
            }

            if (signature != _lastMaterialSignature || _canvasMaterials.Count != subCount)
            {
                _lastMaterialSignature = signature;
                DestroyOwnedMaterials();
                _canvasMaterials.Clear();

                var mats = _animeAsset.Materials;
                Material fallback = (mats != null && mats.Count > 0) ? mats[0] : null;

                for (int s = 0; s < subCount; s++)
                {
                    var info = submeshes[s];
                    Material mat;
                    switch (info.Role)
                    {
                        case StencilRole.Write:
                            mat = _animeAsset.ResolveMaskWriteMaterial(info.TextureHash, info.StencilOp);
                            break;
                        case StencilRole.Test:
                            mat = _animeAsset.ResolveTestMaterial(info.TextureHash, info.BlendType, info.StencilComp);
                            break;
                        default:
                            mat = _animeAsset.ResolveMaterial(info.TextureHash, info.BlendType);
                            break;
                    }
                    if (mat == null)
                    {
                        // 回復可能なフォールバック (マテリアル/テクスチャ未割当 or 未解決)。signature 変化時のみ
                        // 通るためスパムにはならない。実行継続可能なので severity は Warning が適切。
                        Debug.LogWarning($"[SpriteStudioGraphic] TextureHash {info.TextureHash} not resolved (Submesh {s}, role {info.Role}). Falling back to material 0.");
                        mat = fallback;
                    }
                    // Write SubMesh は閾値を毎フレーム書き換えるため、共有マテリアル (解決値/フォールバック共に)
                    // を汚さないようインスタンス化して所有する。CanvasRenderer は MPB 非対応のための回避策。
                    if (info.Role == StencilRole.Write && mat != null)
                    {
                        mat = new Material(mat);
                        _ownedMaterialInstances.Add(mat);
                    }
                    _canvasMaterials.Add(mat);
                }

                // CanvasRenderer へ SubMesh と 1:1 でマテリアルを反映する。
                canvasRenderer.materialCount = subCount;
                for (int s = 0; s < subCount; s++)
                {
                    canvasRenderer.SetMaterial(_canvasMaterials[s], s);
                }
                _hasWriteSubmesh = hasWrite;
            }

            // マスク書込 SubMesh の cutout 閾値は per-frame でアニメーションし得るので、所有インスタンスへ毎フレーム設定する。
            if (_hasWriteSubmesh)
            {
                for (int s = 0; s < subCount; s++)
                {
                    var info = submeshes[s];
                    if (info.Role != StencilRole.Write) continue;
                    var mat = _canvasMaterials[s];
                    if (mat != null) mat.SetFloat(SpriteStudioMaterial.IdMaskThreshold, info.MaskThreshold);
                }
            }
        }

        private void DestroyOwnedMaterials()
        {
            for (int i = 0; i < _ownedMaterialInstances.Count; i++)
            {
                var m = _ownedMaterialInstances[i];
                if (m == null) continue;
                if (Application.isPlaying) Destroy(m);
                else DestroyImmediate(m);
            }
            _ownedMaterialInstances.Clear();
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
