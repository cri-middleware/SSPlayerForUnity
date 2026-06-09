using System.Collections.Generic;
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
    // [ExecuteAlways]: edit モードでも OnEnable/LateUpdate を回し、シーン設置時に「開始フレームの
    // 静止プレビュー」を1回だけ構築する (再生はしない)。Play モードの毎フレーム駆動は従来どおり
    // SpriteStudioUpdateManager が集中管理する (edit モードでは manager は動かない)。
    [ExecuteAlways]
    public class SpriteStudioPlayer : MonoBehaviour, ITimeControl, ISpriteStudioRenderable
    {
        [Header("SpriteStudio Asset")]
        [Tooltip("描画対象となるコンバート済みの ssab アセット")]
        [SerializeField] private SpriteStudioAnimeAsset _animeAsset;

        [Tooltip("Per-part AddOn / custom shader catalog. Optional: leave empty to disable per-part " +
                 "shader dispatch. The built-in AddOns ship pre-registered in the package catalog.")]
        [SerializeField] private SpriteStudioShaderCatalog _shaderCatalog;

        [Header("Part Tracking")]
        [Tooltip("指定パーツに追従させるユーザー所有 Transform の登録リスト。プレーヤーは GameObject を生成せず、" +
                 "登録された Transform を毎フレーム駆動するだけ (constraint 方式)。")]
        [SerializeField] private List<SpriteStudioPartTracker> _partTrackers = new List<SpriteStudioPartTracker>();

        public SpriteStudioAnimeAsset AnimeAsset
        {
            get => _animeAsset;
            set
            {
                _animeAsset = value;
                _core.ReloadAsset(_animeAsset);
                // The part set (and thus name->index mapping) may have changed: re-resolve trackers.
                MarkPartTrackersDirty();
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

        [Tooltip("1 Unity ワールド単位あたりのピクセル数。SS のピクセル座標をワールド単位へ換算する。既定 1=ピクセル等倍 (従来通り)。Unity スプライト慣習に合わせるなら 100 等を設定する。")]
        [SerializeField] private float _pixelsPerUnit = 1f;
        public float PixelsPerUnit
        {
            get => _pixelsPerUnit;
            set
            {
                _pixelsPerUnit = value;
                ApplyPixelsPerUnit();
            }
        }

        // PPU を内部プレイヤーの頂点スケール (= 1/PPU) へ反映する。トップレベルのみ設定し、
        // Instance 子は VertexScale=1 のまま据え置く (親側で一括スケールされるため二重スケールを避ける)。
        private void ApplyPixelsPerUnit()
        {
            if (_core.Internal != null)
                _core.Internal.VertexScale = 1f / Mathf.Max(_pixelsPerUnit, 0.0001f);
        }

        /// <summary>
        /// 指定したアニメーションの再生を開始します。
        /// </summary>
        public void Play(string animName)
        {
            _animationName = animName;
            _core.Play(_animationName, _loopCount, _speedScale, _skipFrames, "SpriteStudioPlayer");
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
            ApplyPixelsPerUnit();
            SpriteStudioUpdateManager.Register(this);
#if UNITY_EDITOR
            // edit モードでは設置直後に開始フレームの静止プレビューを1回構築する (LateUpdate で消化)。
            if (!Application.isPlaying) _editPreviewDirty = true;
#endif
        }

        private void OnDisable()
        {
            SpriteStudioUpdateManager.Unregister(this);
            // Release the per-part AddOn material instances and force a material-list rebuild on
            // re-enable (the cleared list count no longer matches the submesh count).
            DestroyAddOnInstances();
            _submeshMaterials.Clear();
            _core.Dispose();
            // Internal を破棄したので Timeline 制御状態もリセットする。これをしないと再有効化時に
            // 新しい (アニメ未 setup の) ネイティブハンドルへ「同名なので setup 不要」と誤判定し描画が破綻する。
            ReleaseTimelineControl();
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

#if UNITY_EDITOR
        // インスペクタはシリアライズ済みフィールドを直接書き換え、プロパティの setter を呼ばない。
        // そのため再生中のインスペクタ変更をランタイム (Rust) へ反映するにはここで同期する。
        // OnValidate は Editor 専用コールバック (ビルドでは呼ばれない) なので #if UNITY_EDITOR で囲む。
        // (Internal 未生成時 = デシリアライズ中等は何もしない。アニメ名の切替は破壊的なので対象外。)
        private void OnValidate()
        {
            if (_core.Internal != null)
            {
                _core.Internal.SetSpeed(_speedScale);
                _core.Internal.SetLoop(_loopCount);
                _core.Internal.SetSkipFrames(_skipFrames);
                _core.Internal.SubFrameEnabled = _subFrameEnabled;
            }
            if (_meshRenderer != null)
            {
                _meshRenderer.sortingLayerName = _sortingLayerName;
                _meshRenderer.sortingOrder = _sortingOrder;
            }
            // インスペクタ変更 (アニメ名・PPU 等) で edit プレビューを組み直す。
            if (!Application.isPlaying) _editPreviewDirty = true;
        }

        // edit モードの静止プレビュー。[ExecuteAlways] により LateUpdate は毎エディタフレーム呼ばれるが、
        // _editPreviewDirty が立っているときだけ「開始フレームを1回構築」して静止させる (再生はしない)。
        // Play モードは SpriteStudioUpdateManager が駆動するためここでは何もしない。
        private bool _editPreviewDirty;

        private void LateUpdate()
        {
            if (Application.isPlaying) return;
            if (!_editPreviewDirty) return;
            // Self-heal if the .ssab was still importing when OnEnable ran (Editor restart race): keep
            // retrying while the runtime has no ssab yet, and only clear the dirty flag (build once) once
            // it is ready. A setup failure after that is a config error, not a race, so we do not loop.
            _core.EnsureCreated(_animeAsset, _subFrameEnabled);
            if (_core.Internal == null || !_core.Internal.HasSsab) return;
            _editPreviewDirty = false;
            BuildEditPreview();
        }

        private void BuildEditPreview()
        {
            if (_core.Internal == null || _animeAsset == null || string.IsNullOrEmpty(_animationName)) return;
            if (!_core.Internal.SetupAnimation(_animationName)) return;
            // 開始フレームに固定し、進行させず 1 フレームだけ同期構築する (ScheduleMeshUpdate は advance=false)。
            _core.Internal.SetFrame(_core.Internal.GetAnimationSectionStart());
            _core.Internal.ScheduleMeshUpdate(0f);
            ApplyAndRender();
        }
#endif

        // --- ISpriteStudioRenderable (SpriteStudioUpdateManager から駆動される) ---

        public void PrepareFrame(float deltaTime, out System.IntPtr handle, out float drawFrame)
        {
            if (_core.Internal == null) { handle = System.IntPtr.Zero; drawFrame = 0f; return; }
            // Timeline 制御中は SetTime が設定したフレームを描画するため自動進行しない (advance=false)
            _core.Internal.PrepareFrameData(deltaTime, !_timelineControlled, out handle, out drawFrame);
        }

        public void ConsumeFrame(System.IntPtr frameDataPtr, int len, float deltaTime)
        {
            if (_core.Internal == null) return;
            // Timeline 制御中はエフェクト/インスタンスを wall-clock dt で進めない (決定論シーク維持のため dt=0)
            _core.Internal.ConsumeFrameData(frameDataPtr, len, _timelineControlled ? 0f : deltaTime);
        }

        public void ApplyAndRender()
        {
            if (_core.Internal == null) return;

            // Bind the per-part shader catalog before the build so the geometry builder can carve out
            // is-per-part AddOn parts (cheap reference assignment; null = no per-part dispatch).
            _core.Internal.ShaderCatalog = _shaderCatalog;

            // バッチ単位に SubMesh を分割し、テクスチャごとに別マテリアルを適用する
            _core.Internal.ApplyToMesh(_sharedMesh, true);
            UpdateSubmeshMaterials();

            // After the frame's world matrices are final, drive any registered part trackers. Done here
            // (LateUpdate, before the render phase) so attached renderers update in the same frame.
            MirrorTrackers();
        }

        /// <summary>
        /// Drive each registered tracker's target Transform from its part's frame for this frame.
        /// Writes local TRS in the target's parent space, so a target parented under the player matches the
        /// part exactly (scale included) while a target placed elsewhere gets the exact world pose
        /// (scale approximate only when its parent is rotated + non-uniformly scaled). Position/rotation
        /// are always exact. Scale is written only when driveScale is on (localScale divided out).
        /// </summary>
        private void MirrorTrackers()
        {
            if (_partTrackers == null || _partTrackers.Count == 0) return;
            var internalPlayer = _core.Internal;
            if (internalPlayer == null) return;

            Matrix4x4 rootToWorld = transform.localToWorldMatrix;

            for (int i = 0; i < _partTrackers.Count; i++)
            {
                var tracker = _partTrackers[i];
                if (tracker == null || tracker.target == null) continue;

                // Resolve part index (cached; rebuilt on asset reload via MarkPartTrackersDirty).
                if (tracker.ResolvedIndex == SpriteStudioPartTracker.ResolveDirty)
                    tracker.ResolvedIndex = internalPlayer.ResolvePartIndex(tracker.partName);
                int partIndex = tracker.ResolvedIndex;

                if (partIndex < 0)
                {
                    // Part absent from this animation: nothing to follow, so deactivate the target.
                    if (tracker.target.gameObject.activeSelf) tracker.target.gameObject.SetActive(false);
                    continue;
                }

                // A hidden part still has a valid matrix, so FollowAlways keeps tracking it; Deactivate hides.
                bool hasState = internalPlayer.TryGetPartState(partIndex, out bool hidden, out float lsx, out float lsy);
                if (hidden && tracker.onHide == SpriteStudioPartTrackerHide.Deactivate)
                {
                    if (tracker.target.gameObject.activeSelf) tracker.target.gameObject.SetActive(false);
                    continue;
                }

                if (!internalPlayer.TryGetPartLocalMatrix(partIndex, out Matrix4x4 partLocal)) continue;

                // Re-activate if it was auto-deactivated (absent / hidden) on a previous frame.
                if (!tracker.target.gameObject.activeSelf) tracker.target.gameObject.SetActive(true);

                // Remove the part's own localScale (it scales the basis columns) so only the inherited
                // scale drives the target. Exact; a degenerate (~0) localScale axis is left untouched.
                if (tracker.driveScale && hasState)
                {
                    if (Mathf.Abs(lsx) > 1e-6f) partLocal.SetColumn(0, partLocal.GetColumn(0) / lsx);
                    if (Mathf.Abs(lsy) > 1e-6f) partLocal.SetColumn(1, partLocal.GetColumn(1) / lsy);
                }

                // Express the desired world pose in the target's parent-local space, then write local TRS.
                Matrix4x4 desiredWorld = rootToWorld * partLocal;
                Transform parent = tracker.target.parent;
                Matrix4x4 local = parent != null ? parent.worldToLocalMatrix * desiredWorld : desiredWorld;

                tracker.target.localPosition = local.GetColumn(3);
                tracker.target.localRotation = local.rotation;
                if (tracker.driveScale)
                {
                    // 2D player: drive X/Y, preserve the target's own Z scale.
                    Vector3 s = local.lossyScale;
                    Vector3 cur = tracker.target.localScale;
                    tracker.target.localScale = new Vector3(s.x, s.y, cur.z);
                }
            }
        }

        // --- Part tracking public API ---

        /// <summary>Register a part tracker at runtime. Returns the created entry.</summary>
        public SpriteStudioPartTracker RegisterPartTracker(string partName, Transform target,
            bool driveScale = false, SpriteStudioPartTrackerHide onHide = SpriteStudioPartTrackerHide.FollowAlways)
        {
            if (_partTrackers == null) _partTrackers = new List<SpriteStudioPartTracker>();
            var tracker = new SpriteStudioPartTracker
            {
                partName = partName,
                target = target,
                driveScale = driveScale,
                onHide = onHide,
            };
            _partTrackers.Add(tracker);
            return tracker;
        }

        /// <summary>Remove all trackers whose target is the given Transform. Returns the number removed.</summary>
        public int UnregisterPartTracker(Transform target)
        {
            if (_partTrackers == null) return 0;
            return _partTrackers.RemoveAll(t => t != null && t.target == target);
        }

        /// <summary>Is the named part hidden on the current frame? False if the part is unknown / absent.</summary>
        public bool TryGetPartHidden(string partName, out bool hidden)
        {
            hidden = false;
            if (_core.Internal == null) return false;
            int idx = _core.Internal.ResolvePartIndex(partName);
            return idx >= 0 && _core.Internal.TryGetPartState(idx, out hidden, out _, out _);
        }

        /// <summary>Player-local matrix (1/PPU folded) of the named part for the current frame. False if unknown.</summary>
        public bool TryGetPartLocalMatrix(string partName, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            if (_core.Internal == null) return false;
            int idx = _core.Internal.ResolvePartIndex(partName);
            return idx >= 0 && _core.Internal.TryGetPartLocalMatrix(idx, out matrix);
        }

        /// <summary>Force part trackers to re-resolve their part index (call after the asset/part set changes).</summary>
        private void MarkPartTrackersDirty()
        {
            if (_partTrackers == null) return;
            for (int i = 0; i < _partTrackers.Count; i++)
                if (_partTrackers[i] != null) _partTrackers[i].ResolvedIndex = SpriteStudioPartTracker.ResolveDirty;
        }

        private readonly System.Collections.Generic.List<Material> _submeshMaterials = new System.Collections.Generic.List<Material>();
        private uint _lastMaterialSignature;
        private MaterialPropertyBlock _maskMpb;

        /// <summary>
        /// SubMesh (= 描画バッチ) ごとに、テクスチャハッシュ + ステンシルロールへ対応するマテリアルを割り当てる。
        /// 構成 (SubMesh 数 / texture / blend / role / op / comp) が前フレームと同じなら再割り当てしない。
        /// マスク書込 SubMesh の閾値 (_MaskThreshold) は毎フレーム変わり得るので MaterialPropertyBlock で別途設定する。
        /// </summary>
        private void UpdateSubmeshMaterials()
        {
            if (_animeAsset == null || _meshRenderer == null || _core.Internal == null) return;

            int subCount = _core.Internal.SubmeshCount;
            if (subCount <= 0) return;

            var submeshes = _core.Internal.Submeshes;

            // 構成シグネチャ。threshold は per-frame 可変なのでここには含めず MPB で渡す。
            uint signature = (uint)subCount;
            bool hasWrite = false;
            bool hasAddOn = false;
            for (int s = 0; s < subCount; s++)
            {
                var info = submeshes[s];
                signature = signature * 31u + info.TextureHash;
                signature = signature * 31u + info.ShaderHash;
                signature = signature * 31u + info.BlendType;
                signature = signature * 31u + (uint)info.Role;
                signature = signature * 31u + info.StencilOp;
                signature = signature * 31u + info.StencilComp;
                if (info.Role == StencilRole.Write) hasWrite = true;
                if (info.ShaderHash != 0) hasAddOn = true;
            }

            if (signature != _lastMaterialSignature || _submeshMaterials.Count != subCount)
            {
                _lastMaterialSignature = signature;
                _submeshMaterials.Clear();
                // Per-part AddOn material instances are owned by this player; recreate them when the
                // submesh layout changes (rare). Destroy the previous set first so they do not leak.
                DestroyAddOnInstances();
                if (_submeshMaterials.Capacity < subCount)
                {
                    _submeshMaterials.Capacity = subCount;
                }

                // 対応不明時のフォールバック先。自身の Materials が空でも外部参照で解決できる場合があるため、
                // ここでは early-return せず submesh ごとに解決する。
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
                            if (info.ShaderHash != 0
                                && SpriteStudioAddOnRegistry.TryResolveShader(info.ShaderHash, _shaderCatalog, out var addonShader, out _))
                            {
                                // Per-part AddOn: its own material instance (so its per-frame params do
                                // not stomp other parts'), tracked for destruction. Params are pushed in
                                // the per-frame pass below.
                                mat = _animeAsset.CreatePerPartMaterialInstance(addonShader, info.TextureHash, info.BlendType);
                                if (mat != null) _addOnInstances.Add(mat);
                                else mat = _animeAsset.ResolveMaterial(info.TextureHash, info.BlendType);
                            }
                            else
                            {
                                // Resolve self -> Instance external reference, then apply the per-blend material variant.
                                mat = _animeAsset.ResolveMaterial(info.TextureHash, info.BlendType);
                            }
                            break;
                    }
                    if (mat == null)
                    {
                        // 回復可能なフォールバック (アセットにマテリアル/テクスチャが未割当 or 未解決)。
                        // ここは signature 変化時のみ通るのでスパムにはならない。視覚的には壊れるが
                        // 実行は継続できるため、severity は Warning (設定不備の通知) が適切。
                        Debug.LogWarning($"[SpriteStudioPlayer] TextureHash {info.TextureHash} not resolved (Submesh {s}, role {info.Role}). Falling back to material 0.");
                        mat = fallback;
                    }
                    _submeshMaterials.Add(mat);
                }
                _meshRenderer.SetSharedMaterials(_submeshMaterials);
            }

            // マスク書込 SubMesh の cutout 閾値を per-submesh MaterialPropertyBlock で毎フレーム設定する
            // (閾値はアニメーションし得るので material-list キャッシュとは独立に更新する)。
            if (hasWrite)
            {
                for (int s = 0; s < subCount; s++)
                {
                    var info = submeshes[s];
                    if (info.Role != StencilRole.Write) continue;
                    if (_maskMpb == null) _maskMpb = new MaterialPropertyBlock();
                    _meshRenderer.GetPropertyBlock(_maskMpb, s);
                    _maskMpb.SetFloat(SpriteStudioMaterial.IdMaskThreshold, info.MaskThreshold);
                    _meshRenderer.SetPropertyBlock(_maskMpb, s);
                }
            }

            // Per-part AddOn params: pushed onto each part's own material instance every frame (the
            // params animate), independent of the signature-cached material list. Set on the Material
            // (not a MaterialPropertyBlock) so the per-part draws stay SRP Batcher compatible.
            if (hasAddOn)
            {
                for (int s = 0; s < subCount; s++)
                {
                    var info = submeshes[s];
                    if (info.ShaderHash == 0) continue;
                    if (s < _submeshMaterials.Count)
                    {
                        var m = _submeshMaterials[s];
                        if (m != null) SpriteStudioMaterial.ApplyAddOnParams(m, info);
                    }
                }
            }
        }

        private readonly System.Collections.Generic.List<Material> _addOnInstances = new System.Collections.Generic.List<Material>();

        // Destroy the per-part AddOn material instances this player owns (created per submesh-layout
        // change). The clones are HideAndDontSave, so nothing else releases them.
        private void DestroyAddOnInstances()
        {
            for (int i = 0; i < _addOnInstances.Count; i++)
            {
                var m = _addOnInstances[i];
                if (m == null) continue;
                if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
            }
            _addOnInstances.Clear();
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

        #region SpriteStudioTrack (専用 Timeline トラック) 連携

        // 専用トラックが現在セットアップしているアニメ名。変化時のみ SetupAnimation し直す。
        private string _timelineCurrentAnim;

        /// <summary>
        /// SpriteStudioTrack の mixer がクリップ評価時に呼ぶ。指定アニメへ (変化時のみ) 切替え、
        /// クリップローカル時間 (秒) のフレームへシークする。状態は Rust が時刻基準で一括計算するため、
        /// シークバーを前後にどれだけ動かしても決定論的で破綻しない。
        /// NOTE: play モード専用 (SpriteStudioPlayer は非 ExecuteAlways のため edit モードでは Internal/
        /// Mesh が未生成で描画されない)。edit モードのシークプレビューは今後の課題。
        /// </summary>
        public void EvaluateTimelineClip(string animationName, double clipLocalTime)
        {
            if (_core.Internal == null) return;
            _timelineControlled = true;

            if (!string.IsNullOrEmpty(animationName) && animationName != _timelineCurrentAnim)
            {
                if (_core.Internal.SetupAnimation(animationName))
                    _timelineCurrentAnim = animationName;
            }

            int fps = _core.Internal.GetFrameRate();
            if (fps <= 0) fps = 60;
            _core.Internal.SetFrame((float)(clipLocalTime * fps));
            // 描画は UpdateManager が PrepareFrame(advance=false)→ApplyAndRender で行う (SetTime と同じ経路)。
        }

        /// <summary>Timeline 制御を解除する (mixer のグラフ破棄/トラック非アクティブ時)。</summary>
        public void ReleaseTimelineControl()
        {
            _timelineControlled = false;
            _timelineCurrentAnim = null;
        }

        #endregion
    }
}
