using UnityEngine;
using System.Collections.Generic;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// インポート処理の最終成果物となる ScriptableObject。
    /// .ssab バイト配列の保持や、テクスチャ等のハードリファレンス管理を行います。
    /// </summary>
    [UnityEngine.Icon("Packages/com.spritestudio.ssruntime/Editor/Icons/ssab_icon.png")]
    public class SpriteStudioAnimeAsset : ScriptableObject
    {
        [Tooltip("コンバート済みの .ssab バイナリデータ")]
        [HideInInspector]
        public byte[] SsabData;

        [Tooltip("テクスチャのビルドストリッピング防止用ハードリファレンス")]
        public List<Texture2D> Textures = new List<Texture2D>();

        [Tooltip("外部アニメーション参照（Instance パーツ用）")]
        public List<SpriteStudioAnimeAsset> ExternalReferences = new List<SpriteStudioAnimeAsset>();

        [Tooltip("マテリアル辞書: 描画に必要なベースマテリアル群への参照")]
        public List<Material> Materials = new List<Material>();
        
        [Tooltip("ファイル内に含まれるアニメーション名のリスト")]
        public string[] AnimationNames;

        [Tooltip("ssab の音声 (EventAudio が参照) を再生するための『口』。アドレス用ハッシュ2つ + 解決済み AudioClip のみ。" +
                 "名前/元パス/再生長などのメタは SsabData にあるので重複保持しない。音声なし pack では空。")]
        public List<SoundClipEntry> Sounds = new List<SoundClipEntry>();

        [HideInInspector]
        public uint PackHash;

        /// <summary>
        /// Instance パーツが要求する子アニメーションの .ssab バイナリを解決して返す。
        /// Godot の SsInternalPlayer::_resolve_ssab_by_hash と同じ優先順位:
        ///   1. 別パック (外部 .ssab) への参照は、インポート時に確定した ExternalReferences を pack_hash で引く。
        ///   2. 同一パック内 (自分自身) のアニメ参照は、自身の SsabData を返す。
        /// SpriteStudio の Instance は同一プロジェクト内参照が大半のため、2 が主経路になる。
        /// 実行時はファイルを一切読まず、ハードリファレンスを辿るだけ (GUID 参照のため
        /// Resources/AssetBundle/Addressables でも安全)。
        /// </summary>
        /// <param name="packHash">参照先アニメパックの名前ハッシュ (PartTypeInstance.RefAnimePackHash)。</param>
        /// <param name="nameHash">参照先アニメの名前ハッシュ。解決後に SetupAnimationByHash で使用。</param>
        public byte[] ResolveExternalSsab(uint packHash, uint nameHash)
        {
            // 1. 別パックへの参照: pack_hash 一致を最優先で外部参照から引く。
            if (packHash != 0 && ExternalReferences != null)
            {
                for (int i = 0; i < ExternalReferences.Count; i++)
                {
                    var ext = ExternalReferences[i];
                    if (ext != null && ext.PackHash == packHash)
                    {
                        return ext.SsabData;
                    }
                }
            }

            // 2. 同一パック内 (自分自身) のアニメ参照。pack_hash 未設定 (0) も現バイナリにフォールバックする。
            if (packHash == 0 || packHash == PackHash)
            {
                return SsabData;
            }

            // 3. フォールバック (pack 名ハッシュの運用差で 1 が一致しない場合の保険)。
            //    Godot の find_animation_by_hash と同様、要求アニメ名ハッシュを実際に含む ssab を
            //    自身 → 外部参照 の順で探す。pack_hash に依存せず解決できる。
            if (nameHash != 0)
            {
                if (ContainsAnimationHash(nameHash)) return SsabData;
                if (ExternalReferences != null)
                {
                    for (int i = 0; i < ExternalReferences.Count; i++)
                    {
                        var ext = ExternalReferences[i];
                        if (ext != null && ext.ContainsAnimationHash(nameHash)) return ext.SsabData;
                    }
                }
            }

            return null;
        }

        [System.NonSerialized] private HashSet<uint> _animationHashes;

        /// <summary>この ssab が指定アニメ名ハッシュのアニメーションを含むか (Animation.NameHash で判定)。</summary>
        public bool ContainsAnimationHash(uint nameHash)
        {
            if (_animationHashes == null)
            {
                _animationHashes = new HashSet<uint>();
                if (SsabData != null && SsabData.Length > 0)
                {
                    var bb = new Google.FlatBuffers.ByteBuffer(SsabData);
                    var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
                    int n = ssab.AnimationsLength;
                    for (int i = 0; i < n; i++)
                    {
                        var a = ssab.Animations(i);
                        if (a.HasValue) _animationHashes.Add(a.Value.NameHash);
                    }
                }
            }
            return _animationHashes.Contains(nameHash);
        }

        // DrawBatch.TextureHash → Materials のインデックス対応表 (並列配列で直列化)。
        // 1つのテクスチャに複数の候補ハッシュ (cellmap の NameHash/ImagePathHash, external の NameHash) が
        // 対応しうるため、どれが来ても引けるよう全候補を登録している。
        [HideInInspector] public uint[] TextureHashKeys;
        [HideInInspector] public int[] TextureHashMaterialIndices;

        [System.NonSerialized] private Dictionary<uint, int> _hashToMaterial;

        /// <summary>
        /// DrawBatch のテクスチャハッシュに対応する Materials のインデックスを返す。
        /// 対応が見つからない場合は -1 (呼び出し側で material[0] 等にフォールバックする)。
        /// </summary>
        public int GetMaterialIndexForHash(uint textureHash)
        {
            if (_hashToMaterial == null)
            {
                _hashToMaterial = new Dictionary<uint, int>();
                if (TextureHashKeys != null && TextureHashMaterialIndices != null)
                {
                    int n = Mathf.Min(TextureHashKeys.Length, TextureHashMaterialIndices.Length);
                    for (int i = 0; i < n; i++)
                    {
                        _hashToMaterial[TextureHashKeys[i]] = TextureHashMaterialIndices[i];
                    }
                }
            }
            return _hashToMaterial.TryGetValue(textureHash, out int index) ? index : -1;
        }

        /// <summary>
        /// DrawBatch のテクスチャハッシュに対応する Material を返す。
        /// まず自身のテーブルを引き、無ければ Instance 参照先 (外部 .ssab) のテーブルも探索する。
        /// Instance の子は外部 .ssab のテクスチャ/マテリアルを使うため、外部参照側でしか解決できない
        /// ハッシュがサブメッシュとして親へ上がってくる。見つからなければ null (呼び出し側でフォールバック)。
        /// </summary>
        public Material ResolveMaterialForHash(uint textureHash)
        {
            int mi = GetMaterialIndexForHash(textureHash);
            if (mi >= 0 && Materials != null && mi < Materials.Count)
            {
                return Materials[mi];
            }

            if (ExternalReferences != null)
            {
                for (int i = 0; i < ExternalReferences.Count; i++)
                {
                    var ext = ExternalReferences[i];
                    if (ext == null) continue;
                    int emi = ext.GetMaterialIndexForHash(textureHash);
                    if (emi >= 0 && ext.Materials != null && emi < ext.Materials.Count)
                    {
                        return ext.Materials[emi];
                    }
                }
            }
            return null;
        }

        // Runtime cache of per-(texture, blend) material variants. Asset-level so that multiple
        // players sharing this asset reuse the same variants. Variants are clones flagged
        // HideAndDontSave and destroyed in OnDisable; never serialized.
        [System.NonSerialized] private Dictionary<SpriteStudioMaterialKey, Material> _materialVariants;
        [System.NonSerialized] private Material _whiteBaseMaterial;
        // Mask stencil-write materials (SpriteStudio/MaskWrite), cached per (textureHash, stencil op).
        // Separate from _materialVariants because they use a different shader, not a colour clone.
        [System.NonSerialized] private Dictionary<(uint textureHash, byte op), Material> _maskWriteVariants;
        [System.NonSerialized] private Shader _maskWriteShader;

        // Canvas (uGUI) material cache. The Canvas path uses the SpriteStudio/Canvas shader (uGUI
        // stencil scaffolding) instead of SpriteStudio/Default, but the same per-blend variants and a
        // white base for cell-less parts. Internal masking is suppressed on Canvas, so there are no
        // mask-test / mask-write Canvas variants. Cleared with the others in OnDisable.
        [System.NonSerialized] private Dictionary<SpriteStudioMaterialKey, Material> _canvasVariants;
        [System.NonSerialized] private Dictionary<uint, Material> _canvasBaseByHash;
        [System.NonSerialized] private Material _canvasWhiteBase;
        [System.NonSerialized] private Shader _canvasShader;

        // Base material for cell-less submeshes (TextureHash 0, e.g. Shape parts). It has no texture,
        // so the shader's _MainTex "white" default makes the sampler return white and the part shows
        // its vertex / part color. Cloned per blend like any other base material.
        private Material GetWhiteBaseMaterial()
        {
            if (_whiteBaseMaterial != null) return _whiteBaseMaterial;
            Shader shader = null;
            if (Materials != null)
            {
                for (int i = 0; i < Materials.Count; i++)
                {
                    if (Materials[i] != null) { shader = Materials[i].shader; break; }
                }
            }
            if (shader == null) shader = Shader.Find("SpriteStudio/Default");
            if (shader == null) return null;
            _whiteBaseMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = "SpriteStudio_WhiteBase" };
            return _whiteBaseMaterial;
        }

        /// <summary>
        /// Resolve the material for a (textureHash, blendType) pair. Mix (0) returns the base
        /// material directly (it is authored as Mix + PMA); other blend types return a cached clone
        /// with the blend state applied (see SpriteStudioMaterial / project_blend_mapping_truth).
        /// TextureHash 0 (cell-less, e.g. Shape) resolves to a white-textured base. Returns null
        /// only when even that cannot be created.
        /// </summary>
        public Material ResolveMaterial(uint textureHash, byte blendType)
        {
            var baseMat = (textureHash == 0) ? GetWhiteBaseMaterial() : ResolveMaterialForHash(textureHash);
            if (baseMat == null) return null;
            if (blendType == SpriteStudioMaterial.BlendMix) return baseMat;

            var key = new SpriteStudioMaterialKey(textureHash, blendType);
            if (_materialVariants != null && _materialVariants.TryGetValue(key, out var cached) && cached != null)
            {
                return cached;
            }

            if (_materialVariants == null) _materialVariants = new Dictionary<SpriteStudioMaterialKey, Material>();
            var variant = SpriteStudioMaterial.CreateVariant(baseMat, blendType);
            _materialVariants[key] = variant;
            return variant;
        }

        /// <summary>
        /// Resolve a mask-test colour material for a (textureHash, blendType) pair: the per-blend
        /// colour material plus a stencil test. <paramref name="stencilComp"/> is a
        /// UnityEngine.Rendering.CompareFunction (NotEqual = draw inside the mask, Equal = outside).
        /// Cached per (textureHash, blend, comp) via the shared variant dictionary. Returns null only
        /// when the base material cannot be resolved.
        /// </summary>
        public Material ResolveTestMaterial(uint textureHash, byte blendType, byte stencilComp)
        {
            var baseMat = (textureHash == 0) ? GetWhiteBaseMaterial() : ResolveMaterialForHash(textureHash);
            if (baseMat == null) return null;

            var key = new SpriteStudioMaterialKey(textureHash, blendType, stencilComp);
            if (_materialVariants != null && _materialVariants.TryGetValue(key, out var cached) && cached != null)
            {
                return cached;
            }

            if (_materialVariants == null) _materialVariants = new Dictionary<SpriteStudioMaterialKey, Material>();
            var variant = SpriteStudioMaterial.CreateTestVariant(baseMat, blendType, stencilComp);
            _materialVariants[key] = variant;
            return variant;
        }

        /// <summary>
        /// Resolve a stencil-write material (SpriteStudio/MaskWrite) for a mask writer.
        /// <paramref name="stencilOp"/> is a UnityEngine.Rendering.StencilOp (IncrementWrap /
        /// DecrementWrap / Invert). The writer's texture is taken from the colour material for the
        /// same hash; the per-frame _MaskThreshold is supplied by the player via a
        /// MaterialPropertyBlock. Cached per (textureHash, op). Null if the shader is missing.
        /// </summary>
        public Material ResolveMaskWriteMaterial(uint textureHash, byte stencilOp)
        {
            if (_maskWriteShader == null)
            {
                _maskWriteShader = Shader.Find("SpriteStudio/MaskWrite");
                if (_maskWriteShader == null) return null;
            }

            var key = (textureHash, stencilOp);
            if (_maskWriteVariants != null && _maskWriteVariants.TryGetValue(key, out var cached) && cached != null)
            {
                return cached;
            }

            // Cell-less writers (TextureHash 0, e.g. Shape masks) have no texture; the geometry is the
            // mask and the shader's "white" default keeps every covered texel.
            Texture tex = null;
            if (textureHash != 0)
            {
                var colorMat = ResolveMaterialForHash(textureHash);
                if (colorMat != null) tex = colorMat.mainTexture;
            }

            if (_maskWriteVariants == null) _maskWriteVariants = new Dictionary<(uint, byte), Material>();
            var m = SpriteStudioMaterial.CreateMaskWriteMaterial(_maskWriteShader, tex, stencilOp);
            if (m == null) return null;
            _maskWriteVariants[key] = m;
            return m;
        }

        // Canvas base material for a texture hash: a SpriteStudio/Canvas material carrying the same
        // texture as the world base (TextureHash 0 -> a textureless white base, like GetWhiteBaseMaterial).
        // Cached and HideAndDontSave; destroyed in OnDisable. Returns null if the Canvas shader or the
        // source texture base cannot be resolved.
        private Material GetCanvasBaseMaterial(uint textureHash)
        {
            if (_canvasShader == null)
            {
                _canvasShader = Shader.Find("SpriteStudio/Canvas");
                if (_canvasShader == null) return null;
            }

            if (textureHash == 0)
            {
                if (_canvasWhiteBase != null) return _canvasWhiteBase;
                _canvasWhiteBase = new Material(_canvasShader) { hideFlags = HideFlags.HideAndDontSave, name = "SpriteStudio_CanvasWhiteBase" };
                return _canvasWhiteBase;
            }

            if (_canvasBaseByHash != null && _canvasBaseByHash.TryGetValue(textureHash, out var cachedBase) && cachedBase != null)
            {
                return cachedBase;
            }

            var worldBase = ResolveMaterialForHash(textureHash);
            if (worldBase == null) return null;

            var canvasBase = new Material(_canvasShader) { hideFlags = HideFlags.HideAndDontSave, name = "SpriteStudio_CanvasBase" };
            canvasBase.mainTexture = worldBase.mainTexture; // copy the resolved atlas texture (_MainTex)
            if (_canvasBaseByHash == null) _canvasBaseByHash = new Dictionary<uint, Material>();
            _canvasBaseByHash[textureHash] = canvasBase;
            return canvasBase;
        }

        /// <summary>
        /// Resolve the Canvas (uGUI) material for a (textureHash, blendType) pair. Same per-blend
        /// scheme as <see cref="ResolveMaterial"/> but on the SpriteStudio/Canvas shader (uGUI stencil
        /// scaffolding). Mix (0) returns the Canvas base directly; other blends return a cached clone
        /// with the blend state applied. Internal masking is suppressed on Canvas, so there is no
        /// stencil-role variant here. Returns null only when the Canvas base cannot be created.
        /// </summary>
        public Material ResolveCanvasMaterial(uint textureHash, byte blendType)
        {
            var baseMat = GetCanvasBaseMaterial(textureHash);
            if (baseMat == null) return null;
            if (blendType == SpriteStudioMaterial.BlendMix) return baseMat;

            var key = new SpriteStudioMaterialKey(textureHash, blendType);
            if (_canvasVariants != null && _canvasVariants.TryGetValue(key, out var cached) && cached != null)
            {
                return cached;
            }

            if (_canvasVariants == null) _canvasVariants = new Dictionary<SpriteStudioMaterialKey, Material>();
            var variant = SpriteStudioMaterial.CreateVariant(baseMat, blendType);
            _canvasVariants[key] = variant;
            return variant;
        }

        // Destroy runtime-created materials (blend / test / mask-write variants and the white base;
        // clones only). The serialized base materials must not be touched here.
        private void OnDisable()
        {
            if (_whiteBaseMaterial != null)
            {
                if (Application.isPlaying) Destroy(_whiteBaseMaterial); else DestroyImmediate(_whiteBaseMaterial);
                _whiteBaseMaterial = null;
            }
            if (_materialVariants != null)
            {
                foreach (var kv in _materialVariants)
                {
                    var m = kv.Value;
                    if (m == null) continue;
                    if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
                }
                _materialVariants.Clear();
            }
            if (_maskWriteVariants != null)
            {
                foreach (var kv in _maskWriteVariants)
                {
                    var m = kv.Value;
                    if (m == null) continue;
                    if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
                }
                _maskWriteVariants.Clear();
            }
            if (_canvasWhiteBase != null)
            {
                if (Application.isPlaying) Destroy(_canvasWhiteBase); else DestroyImmediate(_canvasWhiteBase);
                _canvasWhiteBase = null;
            }
            if (_canvasBaseByHash != null)
            {
                foreach (var kv in _canvasBaseByHash)
                {
                    var m = kv.Value;
                    if (m == null) continue;
                    if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
                }
                _canvasBaseByHash.Clear();
            }
            if (_canvasVariants != null)
            {
                foreach (var kv in _canvasVariants)
                {
                    var m = kv.Value;
                    if (m == null) continue;
                    if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
                }
                _canvasVariants.Clear();
            }
        }

        /// <summary>
        /// EventAudio が参照する音声 1 件を、再生に必要な最小限で表す。
        /// アドレス用の 2 ハッシュ + 再生用 AudioClip のハードリファレンスのみ。
        /// 名前・元パス・再生長などのメタは SsabData 側に既にあるので重複保持しない。
        /// </summary>
        [System.Serializable]
        public class SoundClipEntry
        {
            [Tooltip("SoundList 名のハッシュ (fnv1a)。EventAudio.SoundListNameHash と対応。")]
            public uint SoundListNameHash;
            [Tooltip("音声エイリアス名のハッシュ (fnv1a)。EventAudio.SoundNameHash と対応。")]
            public uint SoundNameHash;
            [Tooltip("再生用 AudioClip。ビルドストリッピング防止 / 実行時ロードのためのハードリファレンス。")]
            public AudioClip Clip;
        }

        [System.NonSerialized] private Dictionary<ulong, AudioClip> _soundToClip;

        /// <summary>
        /// EventAudio の (sound_list_name_hash, sound_name_hash) に対応する AudioClip を返す。
        /// 見つからない場合は null。シーケンス/音声のランタイム再生を実装する際の解決口 (現状の呼び出し元はなし)。
        /// </summary>
        public AudioClip ResolveAudioClip(uint soundListNameHash, uint soundNameHash)
        {
            if (Sounds == null || Sounds.Count == 0) return null;
            if (_soundToClip == null)
            {
                _soundToClip = new Dictionary<ulong, AudioClip>();
                foreach (var s in Sounds)
                {
                    if (s == null) continue;
                    ulong key = ((ulong)s.SoundListNameHash << 32) | s.SoundNameHash;
                    _soundToClip[key] = s.Clip;
                }
            }
            ulong k = ((ulong)soundListNameHash << 32) | soundNameHash;
            return _soundToClip.TryGetValue(k, out var clip) ? clip : null;
        }
    }
}
