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
    public class SpriteStudioGraphic : MaskableGraphic, ISpriteStudioRenderable
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
            // Canvas path: drop SpriteStudio internal masking and leave the stencil buffer to uGUI's
            // own Mask/RectMask2D (project_unity_canvas_plan). Pure-mask parts then draw nothing and
            // the rest draw unmasked; a parent uGUI Mask clips this graphic read-only via the per-
            // submesh stencil wrap below.
            if (_core.Internal != null) _core.Internal.SuppressInternalMask = true;
            _canvasMaterialDirty = true;
            SpriteStudioUpdateManager.Register(this);
        }

        protected override void OnDisable()
        {
            base.OnDisable();

            SpriteStudioUpdateManager.Unregister(this);
            _core.Dispose();

            // Release the uGUI stencil wrappers (refcounted by StencilMaterial) and invalidate the
            // material signature so OnEnable re-resolves from scratch.
            ReleaseStencilWrappers();
            _canvasMaterials.Clear();
            _lastMaterialSignature = 0;
            _lastStencilDepth = -1;
            _canvasMaterialDirty = true;

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

        // --- Per-submesh material resolution (multi-texture / blend, + uGUI read-only mask) ---
        // SpriteStudio internal masking is suppressed on the Canvas path, so every submesh resolves to
        // a plain colour material (Role None) on the SpriteStudio/Canvas shader. The only stencil here
        // is the READ-ONLY test injected when this graphic is nested under a uGUI Mask: we compute the
        // mask depth and wrap each base material with StencilMaterial.Add (the same thing
        // MaskableGraphic.GetModifiedMaterial does, but done per-submesh since GetModifiedMaterial only
        // tracks one material). RectMask2D needs nothing here — MaskableGraphic clips it through
        // CanvasRenderer.EnableRectClipping.
        private readonly System.Collections.Generic.List<Material> _canvasMaterials = new System.Collections.Generic.List<Material>();
        // StencilMaterial.Add results to release (one slot per submesh; null when not stencil-wrapped).
        private readonly System.Collections.Generic.List<Material> _stencilWrapped = new System.Collections.Generic.List<Material>();
        private uint _lastMaterialSignature;
        private int _lastStencilDepth = -1;
        private bool _canvasMaterialDirty;
        private bool _warnedMultiTexture;

        private void UpdateCanvasMaterials()
        {
            if (_animeAsset == null || _core.Internal == null) return;

            int subCount = _core.Internal.SubmeshCount;
            if (subCount <= 0)
            {
                if (_canvasMaterials.Count > 0)
                {
                    ReleaseStencilWrappers();
                    _canvasMaterials.Clear();
                    canvasRenderer.materialCount = 0;
                    _lastMaterialSignature = 0;
                }
                return;
            }

            var submeshes = _core.Internal.Submeshes;

            // Composition signature over (texture, blend). Roles never vary here (masking suppressed).
            uint signature = (uint)subCount;
            for (int s = 0; s < subCount; s++)
            {
                var info = submeshes[s];
                signature = signature * 31u + info.TextureHash;
                signature = signature * 31u + info.BlendType;
            }

            // Current uGUI mask depth (0 when not nested under a Mask). Checked every frame so a reparent
            // under/out of a Mask re-wraps the materials even if the SetMaterialDirty signal is missed;
            // the tree walk is shallow for UI hierarchies.
            int stencilDepth = maskable
                ? UnityEngine.UI.MaskUtilities.GetStencilDepth(transform, UnityEngine.UI.MaskUtilities.FindRootSortOverrideCanvas(transform))
                : 0;

            // Re-resolve when the geometry composition changes, the mask depth changes, a uGUI mask
            // state change marked us dirty (SetMaterialDirty), or on first build.
            if (signature != _lastMaterialSignature || stencilDepth != _lastStencilDepth
                || _canvasMaterialDirty || _canvasMaterials.Count != subCount)
            {
                RebuildCanvasMaterials(subCount, submeshes, stencilDepth);

                _lastMaterialSignature = signature;
                _lastStencilDepth = stencilDepth;
                _canvasMaterialDirty = false;
            }
        }

        private void RebuildCanvasMaterials(int subCount, Unity.Collections.NativeArray<SpriteStudioGeometryBuilder.SubmeshInfo> submeshes, int stencilDepth)
        {
            ReleaseStencilWrappers();
            _canvasMaterials.Clear();

            // The Canvas path assumes a single atlas (one texture). Multiple cellmaps cannot be drawn
            // distinctly through one CanvasRenderer; warn once and fall back to the first texture's
            // material (use the world player or merge to a single atlas for multi-texture assets).
            if (!_warnedMultiTexture && _animeAsset.Textures != null && _animeAsset.Textures.Count > 1)
            {
                Debug.LogWarning($"[SpriteStudioGraphic] '{_animeAsset.name}' has {_animeAsset.Textures.Count} textures. " +
                                 "The Canvas (uGUI) path is single-atlas; extra textures may not draw correctly. " +
                                 "Use the world (MeshRenderer) player or merge into one atlas.", this);
                _warnedMultiTexture = true;
            }

            int stencilId = stencilDepth > 0 ? (1 << stencilDepth) - 1 : 0;

            for (int s = 0; s < subCount; s++)
            {
                var info = submeshes[s];
                Material baseMat = _animeAsset.ResolveCanvasMaterial(info.TextureHash, info.BlendType);
                if (baseMat == null)
                {
                    Debug.LogError($"[SpriteStudioGraphic] TextureHash {info.TextureHash} not resolved (Submesh {s}).", this);
                }

                Material finalMat = baseMat;
                Material wrapped = null;
                if (stencilDepth > 0 && baseMat != null)
                {
                    // Read-only stencil test for a parent uGUI Mask (Comp Equal, Op Keep, no write).
                    wrapped = UnityEngine.UI.StencilMaterial.Add(
                        baseMat, stencilId,
                        UnityEngine.Rendering.StencilOp.Keep,
                        UnityEngine.Rendering.CompareFunction.Equal,
                        UnityEngine.Rendering.ColorWriteMask.All,
                        stencilId, 0);
                    finalMat = wrapped;
                }
                _canvasMaterials.Add(finalMat);
                _stencilWrapped.Add(wrapped);
            }

            canvasRenderer.materialCount = subCount;
            for (int s = 0; s < subCount; s++)
            {
                canvasRenderer.SetMaterial(_canvasMaterials[s], s);
            }
        }

        private void ReleaseStencilWrappers()
        {
            for (int i = 0; i < _stencilWrapped.Count; i++)
            {
                var m = _stencilWrapped[i];
                if (m != null) UnityEngine.UI.StencilMaterial.Remove(m);
            }
            _stencilWrapped.Clear();
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
            // Do NOT call base: the standard uGUI material rebuild (CanvasUpdateRegistry) would try to
            // drive our mesh/material through the normal Graphic path and fight our direct SetMesh/
            // SetMaterial. Instead just flag a re-resolve so a mask-hierarchy change (MaskableGraphic
            // raises SetMaterialDirty on stencil-state changes) re-wraps the per-submesh materials.
            _canvasMaterialDirty = true;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            // 無効化: 勝手に UI Vertex に変換させない
            vh.Clear();
        }
    }
}
