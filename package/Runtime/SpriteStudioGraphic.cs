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
    [RequireComponent(typeof(CanvasRenderer))]
    [ExecuteAlways]
    public class SpriteStudioGraphic : MaskableGraphic, ISpriteStudioRenderable
    {
        [Header("SpriteStudio Asset")]
        [Tooltip("The converted ssab asset to render")]
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

        [Header("Audio")]
        [Tooltip("Play sounds from audio events (ADIO). When ON, plays them built-in with no extra component required.")]
        [SerializeField] private bool _playAudio = true;
        [Tooltip("Volume of built-in playback (AudioClip one-shot). Ignored when an Audio Backend is used.")]
        [Range(0f, 1f)]
        [SerializeField] private float _audioVolume = 1f;
        [Tooltip("Playback override hook (optional). Assign this to route to a custom audio backend such as audio middleware. " +
                 "When empty, resolution falls back to the global default, then built-in AudioClip playback.")]
        [SerializeField] private SpriteStudioAudioBackend _audioBackend;

        private SpriteStudioAudioController _audioController;

        [Header("Playback Settings")]
        [Tooltip("Animation name to play (e.g. pack name / animation name)")]
        [SerializeField] private string _animationName = "";
        public string AnimationName
        {
            get => _animationName;
            set => _animationName = value;
        }

        [Tooltip("Whether to start playback automatically on startup")]
        [SerializeField] private bool _autoPlay = true;
        public bool AutoPlay
        {
            get => _autoPlay;
            set => _autoPlay = value;
        }

        [Tooltip("Loop count. -1 = infinite loop / 1 = play once / n = play n times")]
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

        [Tooltip("Playback speed scale of the animation")]
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

        [Tooltip("Whether to enable sub-frame rendering (interpolation between frames)")]
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

        [Tooltip("Real-time tracking (timed). ON: advance by elapsed time even when frames drop under load. OFF (fixed): always advance exactly one frame per update (frame-by-frame).")]
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
        /// 再生するアニメーションを指定し、データをセットアップします (先頭フレームで posed 表示)。
        /// 再生は開始しません。開始するには <see cref="Play()"/> を呼びます。ループ/速度/スキップ等の
        /// 再生設定はここでは適用されません (setup で既定値へ戻るため)。実行時に切り替えたあとで設定を
        /// 反映するには <see cref="LoopCount"/> / <see cref="SpeedScale"/> / <see cref="SkipFrames"/> などの
        /// プロパティを setup 後に設定してください (Godot プレイヤーと同じ流れ)。setup に成功すれば true。
        /// </summary>
        public bool SetAnimation(string animName)
        {
            _animationName = animName;
            _audioController?.StopLoops();   // On animation switch, stop the previous animation's loop sounds (let one-shots end on their own)
            return _core.SetAnimation(_animationName, "SpriteStudioGraphic");
        }

        // Push the serialized playback parameters (loop / speed / skip) to the runtime. SetAnimation only
        // sets the animation up; the Rust setup resets these to defaults, so they are applied separately
        // afterwards — mirroring the Godot player, where loop/speed/skip are configured after the animation
        // is set. Direction/style (Reverse/PingPong) have no serialized default and stay at the setup
        // default (Forward/Normal) until set via their properties.
        private void ApplyPlaybackSettings()
        {
            var p = _core.Internal;
            if (p == null) return;
            p.SetLoop(_loopCount);
            p.SetSpeed(_speedScale);
            p.SetSkipFrames(_skipFrames);
        }

        /// <summary>現在セットアップ済みのアニメーションを先頭から再生開始します。</summary>
        public void Play() { _core.Play(); _audioController?.ResumeAll(); }   // Explicit play lifts the suspend (SDK doc §4)

        /// <summary>現在セットアップ済みのアニメーションを指定フレームから再生開始します。</summary>
        public void Play(float startFrame) { _core.Play(startFrame); _audioController?.ResumeAll(); }

        #region Playback Control (再生制御)
        // 内部プレイヤー (Rust ランタイム) が既に備える再生制御をコンポーネントの公開 API として薄く露出する。
        // 状態取得・シーク・一時停止・逆再生などはすべて native 側の実装で、ここは委譲のみ (新規ロジックなし)。

        /// <summary>再生セッションが有効か。一時停止中でも true (停止/未再生で false)。</summary>
        public bool IsPlaying => _core.Internal?.IsPlaying() ?? false;

        /// <summary>一時停止中か (再生セッション中で、かつ pause されているときだけ true)。</summary>
        public bool IsPaused
        {
            get
            {
                // ネイティブの is_pausing は初期値 true (未再生でも立っている) なので、
                // 再生中であることと併せて判定する。
                var p = _core.Internal;
                return p != null && p.IsPlaying() && p.IsPausing();
            }
        }

        /// <summary>
        /// 再生の一時停止 / 再開をトグルします (現在フレームを保持)。ネイティブの is_pausing を切り替えます
        /// (Godot プレイヤーの pause() と同じ。play() は先頭へ巻き戻すため再開には使えません)。
        /// </summary>
        public void Pause()
        {
            _core.Internal?.Pause();
            // Sync audio with the animation pause/resume (SDK doc §4). Pause is a toggle, so branch on the resulting state.
            if (_audioController != null && _core.Internal != null)
            {
                if (_core.Internal.IsPausing()) _audioController.PauseAll();
                else _audioController.ResumeAll();
            }
        }

        /// <summary>再生を停止する。</summary>
        public void Stop() => _core.Internal?.Stop();

        /// <summary>現在の再生フレーム (小数可)。set で任意フレームへシークする。</summary>
        public float CurrentFrame
        {
            get => _core.Internal?.GetFrame() ?? 0f;
            set => _core.Internal?.SetFrame(value);
        }

        /// <summary>現在のアニメーションの総フレーム数 (終端フレーム + 1)。未ロード時は 0。</summary>
        public int TotalFrames => _core.Internal?.GetTotalFrames() ?? 0;

        /// <summary>再生フレームレート (fps)。set で実行時に変更する。未ロード時の get は 0。</summary>
        public int FrameRate
        {
            get => _core.Internal?.GetFrameRate() ?? 0;
            set => _core.Internal?.SetFrameRate(value);
        }

        /// <summary>
        /// 逆再生 (Backward) か。<see cref="SetAnimation(string)"/> は毎回 Forward へ戻すため、
        /// 逆再生で開始したい場合は SetAnimation 後 (Play 前後どちらでも可) に true を設定する。
        /// </summary>
        public bool Reverse
        {
            get => (_core.Internal?.GetPlaybackDirection() ?? 0) != 0;
            set
            {
                var p = _core.Internal;
                if (p != null) p.SetPlaybackStyle(value ? 1 : 0, p.GetPlaybackStyle());
            }
        }

        /// <summary>
        /// 往復 (PingPong) 再生か。<see cref="SetAnimation(string)"/> は毎回 Normal へ戻すため、
        /// PingPong で開始したい場合は SetAnimation 後 (Play 前後どちらでも可) に true を設定する。
        /// </summary>
        public bool PingPong
        {
            get => (_core.Internal?.GetPlaybackStyle() ?? 0) != 0;
            set
            {
                var p = _core.Internal;
                if (p != null) p.SetPlaybackStyle(p.GetPlaybackDirection(), value ? 1 : 0);
            }
        }

        /// <summary>
        /// 再生範囲をフレームで限定する (SS6 の再生範囲指定のフレーム版。ラベル指定は未対応)。
        /// </summary>
        public void SetAnimationSection(int startFrame, int endFrame) => _core.Internal?.SetAnimationSection(startFrame, endFrame);

        #endregion

        protected override void Start()
        {
            base.Start();
            if (string.IsNullOrEmpty(_animationName)) return;
            // AutoPlay でなくても先頭フレームを posed 表示するため SetAnimation は常に行い、
            // 実際の再生開始は AutoPlay のときだけにする。SetAnimation は再生設定を適用しないため
            // setup 後に明示適用する (Godot のプロパティ適用順と同じ: animation -> loop/speed/skip -> play)。
            if (SetAnimation(_animationName))
            {
                ApplyPlaybackSettings();
                if (_autoPlay) Play();
            }
        }

        private readonly SpriteStudioPlaybackCore _core = new SpriteStudioPlaybackCore();

        /// <summary>再生開始時に発火します (<see cref="Play()"/> 呼び出し時)。</summary>
        public event System.Action OnAnimationStarted
        {
            add => _core.OnAnimationStarted += value;
            remove => _core.OnAnimationStarted -= value;
        }
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
        public UnityEvent OnAnimationStartedEvent = new UnityEvent();
        public UnityEvent OnAnimationFinishedEvent = new UnityEvent();
        public UnityEvent OnAnimationLoopedEvent = new UnityEvent();
        public UserDataUnityEvent OnUserDataEvent = new UserDataUnityEvent();
        public SignalUnityEvent OnSignalEvent = new SignalUnityEvent();
        public AudioUnityEvent OnAudioEvent = new AudioUnityEvent();

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

            _core.OnAnimationStarted += InvokeAnimationStarted;
            _core.OnAnimationFinished += InvokeAnimationFinished;
            _core.OnAnimationLooped += InvokeAnimationLooped;
            _core.OnUserData += InvokeUserData;
            _core.OnSignal += InvokeSignal;
            _core.OnAudio += InvokeAudio;

            // edit モードでは設置直後に開始フレームの静止プレビューを構築する (LateUpdate で setup を消化)。
            _editPreviewNeedsSetup = true;
        }

        private void InvokeAnimationStarted() => OnAnimationStartedEvent?.Invoke();
        private void InvokeAnimationFinished()
        {
            _audioController?.StopLoops();   // On animation end, stop loop sounds (let one-shots end on their own)
            OnAnimationFinishedEvent?.Invoke();
        }
        private void InvokeAnimationLooped() => OnAnimationLoopedEvent?.Invoke();
        private void InvokeUserData(SpriteStudioUserData data) => OnUserDataEvent?.Invoke(data);
        private void InvokeSignal(SpriteStudioSignal data) => OnSignalEvent?.Invoke(data);
        private void InvokeAudio(SpriteStudioAudio data)
        {
            OnAudioEvent?.Invoke(data);   // Notification (observation channel) always fires regardless of playback direction.
            // Built-in/Backend playback (zero-config). Only while running and playing forward (SDK doc §4: no sound on reverse playback).
            if (_playAudio && Application.isPlaying && (_core.Internal?.IsPlayingForward() ?? false))
            {
                if (_audioController == null) _audioController = new SpriteStudioAudioController(gameObject);
                _audioController.Play(in data, _animeAsset, _audioBackend, _audioVolume);
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();

            SpriteStudioUpdateManager.Unregister(this);

            _core.OnAnimationStarted -= InvokeAnimationStarted;
            _core.OnAnimationFinished -= InvokeAnimationFinished;
            _core.OnAnimationLooped -= InvokeAnimationLooped;
            _core.OnUserData -= InvokeUserData;
            _core.OnSignal -= InvokeSignal;
            _core.OnAudio -= InvokeAudio;
            _audioController?.StopAll();   // On disable, stop all built-in sounds

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
