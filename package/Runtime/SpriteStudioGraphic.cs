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
    [RequireComponent(typeof(CanvasRenderer))]
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
            // A Graphic drives a CanvasRenderer. RequireComponent adds one for newly created objects;
            // also self-heal a GameObject that was created before this requirement existed so that
            // canvasRenderer.materialCount does not throw a MissingComponentException.
            if (GetComponent<CanvasRenderer>() == null) gameObject.AddComponent<CanvasRenderer>();

            base.OnEnable();

            if (_sharedMesh == null)
            {
                _sharedMesh = new Mesh();
                _sharedMesh.name = "SpriteStudio_CanvasMesh";
                _sharedMesh.MarkDynamic();
            }

            _core.EnsureCreated(_animeAsset, _subFrameEnabled);
            // Canvas path: drop SpriteStudio internal masking; a parent uGUI Mask/RectMask2D clips this
            // graphic read-only via the per-submesh stencil wrap below.
            if (_core.Internal != null) _core.Internal.SuppressInternalMask = true;
            _canvasMaterialDirty = true;
            SpriteStudioUpdateManager.Register(this);
            // edit モードでは設置直後に開始フレームの静止プレビューを構築する (LateUpdate で setup を消化)。
            _editPreviewNeedsSetup = true;
        }

        protected override void OnDisable()
        {
            base.OnDisable();

            SpriteStudioUpdateManager.Unregister(this);
            _core.Dispose();

            // Release the uGUI stencil wrapper (refcounted by StencilMaterial) and invalidate the
            // material signature so OnEnable re-resolves from scratch.
            ReleaseCanvasStencil();
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

        // edit モードプレビューで一度だけ SetupAnimation すべきかのフラグ (OnEnable / OnValidate で立てる)。
        private bool _editPreviewNeedsSetup;

        private void LateUpdate()
        {
            // play モードは SpriteStudioUpdateManager が集中管理する。
            // edit モード (ExecuteAlways) の静止プレビューのみここで自前更新する。Start() は edit モードでは
            // 走らず SetupAnimation がされないため、設置直後は空フレームを組んで「一瞬出て消える」。明示的に
            // setup → 開始フレーム固定し、進行させず毎フレーム再構築/再アサートする (CanvasRenderer のメッシュが
            // uGUI にクリアされても出続けるよう dirty-once ではなく毎フレーム ApplyAndRender する)。
            if (Application.isPlaying) return;
            // Self-heal if the .ssab was still importing when OnEnable ran (Editor restart race): pick the
            // ssab up once it is available (EnsureCreated is a no-op once loaded). Without this an empty
            // Internal created at OnEnable would never get its ssab, so SetupAnimation fails forever.
            _core.EnsureCreated(_animeAsset, _subFrameEnabled);
            if (_core.Internal == null || !_core.Internal.HasSsab) return;
            if (_editPreviewNeedsSetup)
            {
                if (string.IsNullOrEmpty(_animationName) || !_core.Internal.SetupAnimation(_animationName)) return;
                _core.Internal.SetFrame(_core.Internal.GetAnimationSectionStart());
                _editPreviewNeedsSetup = false;
            }
            _core.Internal.ScheduleMeshUpdate(0f); // 静止: 開始フレームを advance なしで構築
            ApplyAndRender();
        }

#if UNITY_EDITOR
        // インスペクタはシリアライズ済みフィールドを直接書き換え、プロパティの setter を呼ばない。
        // 再生中のインスペクタ変更をランタイム (Rust) へ反映するにはここで同期する。Graphic.OnValidate は
        // UNITY_EDITOR 内のみで定義されるため override も #if UNITY_EDITOR で囲む。(アニメ名切替は対象外)
        protected override void OnValidate()
        {
            base.OnValidate();
            // インスペクタ変更 (アニメ名等) で edit プレビューを setup から組み直す。
            _editPreviewNeedsSetup = true;
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

            // Single-material Canvas: build the whole graphic as ONE submesh (perBatchSubmesh: false) and
            // drive it with one material; MIX/ADD are split per-vertex in-shader. CanvasRenderer only
            // accepts 16-bit index meshes, so upload 16-bit.
            _core.Internal.ApplyToMesh(_sharedMesh, false, use16BitIndices: true);
            EnsureCanvasShaderChannels();
            UpdateCanvasMaterials();
            // CanvasRenderer multiplies the mesh vertex colours by its own colour. The normal uGUI
            // rebuild (which we suppress) is what would push the Graphic colour into it; without that
            // the renderer colour stays at its uninitialised default (black) and zeroes the sprite RGB.
            // Push the Graphic colour every frame (default white = identity, also honours a user tint).
            canvasRenderer.SetColor(color);
            canvasRenderer.SetMesh(_sharedMesh);
        }

        // --- Single-material Canvas resolution (one atlas + one fixed PMA blend) ---
        // The whole graphic renders as ONE submesh with ONE material on the SpriteStudio/Canvas shader.
        // MIX vs ADD is handled per-vertex in-shader (a=0 trick); other blends collapse to MIX. SpriteStudio
        // internal masking is suppressed; the only stencil is the READ-ONLY test injected when this graphic
        // is nested under a uGUI Mask, via StencilMaterial.Add (we compute the mask depth ourselves and wrap
        // the single material, since MaskableGraphic.GetModifiedMaterial tracks only one material anyway).
        // RectMask2D needs nothing here — MaskableGraphic clips it through CanvasRenderer.EnableRectClipping.
        private Material _canvasStencilWrapped; // StencilMaterial.Add result to release (null if not wrapped)
        private uint _lastMaterialSignature;    // resolved atlas texture hash + 1 (0 = nothing resolved)
        private int _lastStencilDepth = -1;
        private bool _canvasMaterialDirty;
        private bool _warnedMultiTexture;

        private void UpdateCanvasMaterials()
        {
            if (_animeAsset == null || _core.Internal == null) return;

            int subCount = _core.Internal.SubmeshCount;
            if (subCount <= 0)
            {
                if (_lastMaterialSignature != 0)
                {
                    ReleaseCanvasStencil();
                    canvasRenderer.materialCount = 0;
                    _lastMaterialSignature = 0;
                }
                return;
            }

            var submeshes = _core.Internal.Submeshes;

            // Multi-atlas is not supported on the Canvas path: one CanvasRenderer = one mesh + one
            // material. Interim guard: draw with the first atlas only and warn once (other atlases
            // sample the wrong texture but never crash).
            if (!_warnedMultiTexture && _animeAsset.Textures != null && _animeAsset.Textures.Count > 1)
            {
                Debug.LogWarning($"[SpriteStudioGraphic] '{_animeAsset.name}' uses {_animeAsset.Textures.Count} atlases. " +
                                 "The Canvas (uGUI) path is single-atlas only: only the first atlas is drawn, so parts on other " +
                                 "atlases will sample the wrong texture. Merge to one atlas, or use the world (MeshRenderer) player.", this);
                _warnedMultiTexture = true;
            }

            uint texHash = submeshes[0].TextureHash;

            // Current uGUI mask depth (0 when not nested under a Mask). Checked every frame so a reparent
            // under/out of a Mask re-wraps the material even if the SetMaterialDirty signal is missed.
            int stencilDepth = maskable
                ? UnityEngine.UI.MaskUtilities.GetStencilDepth(transform, UnityEngine.UI.MaskUtilities.FindRootSortOverrideCanvas(transform))
                : 0;

            uint signature = texHash + 1u; // +1 so a valid hash never collides with the empty sentinel 0
            if (signature != _lastMaterialSignature || stencilDepth != _lastStencilDepth || _canvasMaterialDirty)
            {
                RebuildCanvasMaterial(texHash, stencilDepth);
                _lastMaterialSignature = signature;
                _lastStencilDepth = stencilDepth;
                _canvasMaterialDirty = false;
            }
        }

        private void RebuildCanvasMaterial(uint texHash, int stencilDepth)
        {
            ReleaseCanvasStencil();

            Material baseMat = _animeAsset.ResolveCanvasMaterial(texHash, 0);
            if (baseMat == null)
            {
                Debug.LogError($"[SpriteStudioGraphic] Canvas material for texture {texHash} could not be resolved.", this);
                canvasRenderer.materialCount = 0;
                return;
            }

            Material finalMat = baseMat;
            if (stencilDepth > 0)
            {
                // Read-only stencil test for a parent uGUI Mask (Comp Equal, Op Keep, no write).
                int stencilId = (1 << stencilDepth) - 1;
                _canvasStencilWrapped = UnityEngine.UI.StencilMaterial.Add(
                    baseMat, stencilId,
                    UnityEngine.Rendering.StencilOp.Keep,
                    UnityEngine.Rendering.CompareFunction.Equal,
                    UnityEngine.Rendering.ColorWriteMask.All,
                    stencilId, 0);
                finalMat = _canvasStencilWrapped;
            }

            canvasRenderer.materialCount = 1;
            canvasRenderer.SetMaterial(finalMat, 0);
        }

        // The mesh packs per-vertex [rate, blend_idx, isNormalized, reserved] into TEXCOORD1. A Canvas
        // only emits the shader channels listed in additionalShaderChannels; TexCoord1 is NOT included
        // by default, so without this the uv1 data is dropped/garbage and the part-color blend breaks
        // (renders black). Enable it on the canvas we render to.
        private void EnsureCanvasShaderChannels()
        {
            var c = canvas;
            if (c == null) return;
            const AdditionalCanvasShaderChannels needed = AdditionalCanvasShaderChannels.TexCoord1;
            if ((c.additionalShaderChannels & needed) != needed)
            {
                c.additionalShaderChannels |= needed;
            }
        }

        private void ReleaseCanvasStencil()
        {
            if (_canvasStencilWrapped != null)
            {
                UnityEngine.UI.StencilMaterial.Remove(_canvasStencilWrapped);
                _canvasStencilWrapped = null;
            }
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
