using UnityEngine;
using System.Collections.Generic;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// The final artifact of the import process, a ScriptableObject.
    /// Holds the .ssab byte array and manages hard references to textures and the like.
    /// </summary>
    [UnityEngine.Icon("Packages/jp.co.cri-mw.spritestudio.ssplayer-unity/Editor/Icons/ssab_icon.png")]
    public class SpriteStudioAnimeAsset : ScriptableObject
    {
        [Tooltip("Converted .ssab binary data")]
        [HideInInspector]
        public byte[] SsabData;

        [Tooltip("Hard reference to prevent texture build stripping")]
        public List<Texture2D> Textures = new List<Texture2D>();

        [Tooltip("External animation references (for Instance parts)")]
        public List<SpriteStudioAnimeAsset> ExternalReferences = new List<SpriteStudioAnimeAsset>();

        [Tooltip("Material dictionary: references to the base materials needed for rendering")]
        public List<Material> Materials = new List<Material>();

        [Tooltip("List of animation names contained in the file")]
        public string[] AnimationNames;

        [Tooltip("AudioClips the asset's audio events refer to, resolved at import. Empty for packs with no sound.")]
        public List<SoundClipEntry> Sounds = new List<SoundClipEntry>();

        [HideInInspector]
        public uint PackHash;

        /// <summary>
        /// Resolve and return the .ssab binary of the child animation requested by an Instance part.
        /// Resolution priority:
        ///   1. A reference to another pack (external .ssab) is looked up from the ExternalReferences fixed at import time, by pack_hash.
        ///   2. A same-pack (self) animation reference returns our own SsabData.
        /// SpriteStudio Instances are mostly same-project references, so 2 is the primary path.
        /// At runtime no file is read at all; it only follows hard references (GUID references, so it is
        /// safe with Resources/AssetBundle/Addressables too).
        /// Nothing else resolves: an unmatched pack_hash returns null and the caller skips the part.
        /// Guessing by animation name instead would be worse than no answer — SpriteStudio names every
        /// animation in a pack independently, so a project where each pack holds one "anime_1" makes every
        /// such guess land on whichever pack happens to be first, silently playing the wrong animation.
        /// A miss means the ExternalReferences are incomplete; reimporting the asset is what fixes it.
        /// </summary>
        /// <param name="packHash">Name hash of the referenced anime pack (PartTypeInstance.RefAnimePackHash).</param>
        /// <param name="nameHash">Name hash of the referenced animation. Used by SetupAnimationByHash after resolution.</param>
        internal byte[] ResolveExternalSsab(uint packHash, uint nameHash)
        {
            // 1. Reference to another pack: look it up from the external references, prioritizing a pack_hash match.
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

            // 2. Same-pack (self) animation reference. An unset pack_hash (0) also falls back to the current binary.
            if (packHash == 0 || packHash == PackHash)
            {
                return SsabData;
            }

            // The part named a pack that is neither ours nor in our external references, so there is no
            // answer to give. Report the pack that is missing and let the caller skip the part: a wrong
            // animation played silently costs far more to track down than a missing one.
            Debug.LogWarning($"[SpriteStudioAnimeAsset] '{name}': Instance reference (pack=0x{packHash:X8}, name=0x{nameHash:X8}) is not among this asset's {(ExternalReferences == null ? 0 : ExternalReferences.Count)} external references. The Instance part will be skipped — reimport this .ssab to repopulate them.");
            return null;
        }

        // DrawBatch.TextureHash -> Materials index mapping table (serialized as parallel arrays).
        // A single texture can correspond to multiple candidate hashes (the cellmap's NameHash/ImagePathHash,
        // the external NameHash), so all candidates are registered so any of them can look it up.
        [HideInInspector] public uint[] TextureHashKeys;
        [HideInInspector] public int[] TextureHashMaterialIndices;

        [System.NonSerialized] private Dictionary<uint, int> _hashToMaterial;

        /// <summary>
        /// Return the Materials index corresponding to a DrawBatch's texture hash.
        /// Returns -1 if no correspondence is found (the caller falls back to material[0], etc.).
        /// </summary>
        internal int GetMaterialIndexForHash(uint textureHash)
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
        /// Return the Material corresponding to a DrawBatch's texture hash.
        /// First looks up our own table; if not found, also searches the Instance reference target's
        /// (external .ssab) table. Because Instance children use the external .ssab's textures/materials,
        /// hashes resolvable only on the external-reference side bubble up to the parent as submeshes.
        /// Returns null if not found (the caller falls back).
        /// </summary>
        internal Material ResolveMaterialForHash(uint textureHash)
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

        // Cellmap / external-texture name -> the candidate DrawBatch.TextureHash values it can appear as.
        // Built lazily from SsabData (like the sound tables) and used by the runtime texture-replacement
        // feature (SpriteStudioPlayer.SetCellmapTexture) to translate a resource NAME into the hash key(s)
        // the draw path binds by. A cellmap contributes both its NameHash and its ImagePathHash (either can
        // reach a batch); an external texture contributes its NameHash. NOT the Override Layer API.
        [System.NonSerialized] private Dictionary<string, uint[]> _cellmapNameToHashes;
        [System.NonSerialized] private string[] _cellmapNames;

        private void EnsureCellmapTable()
        {
            if (_cellmapNameToHashes != null) return;
            _cellmapNameToHashes = new Dictionary<string, uint[]>();
            var names = new List<string>();
            // Accumulate per name so that a name appearing in both tables (a cellmap and a like-named
            // external texture) collects every candidate hash instead of the first table winning.
            var acc = new Dictionary<string, List<uint>>();

            void Add(string texName, params uint[] hashes)
            {
                if (string.IsNullOrEmpty(texName)) return;
                if (!acc.TryGetValue(texName, out var list))
                {
                    list = new List<uint>();
                    acc[texName] = list;
                    names.Add(texName); // preserve first-seen order for the discovery list
                }
                foreach (var h in hashes)
                {
                    if (h != 0 && !list.Contains(h)) list.Add(h);
                }
            }

            if (SpriteStudioSsabFormat.IsSsabBuffer(SsabData))
            {
                var bb = new Google.FlatBuffers.ByteBuffer(SsabData);
                var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);

                // cellmaps: the primary textures. Both the cellmap NameHash and the ImagePathHash are valid
                // batch keys (the importer registers both), so a replacement must cover both.
                int cmLen = ssab.CellmapsLength;
                for (int i = 0; i < cmLen; i++)
                {
                    var cm = ssab.Cellmaps(i);
                    if (!cm.HasValue) continue;
                    var c = cm.Value;
                    Add(c.Name, c.NameHash, c.ImagePathHash);
                }

                // external_textures: the names a TextureChange (TCHG) attribute binds. Keyed by their own
                // NameHash (a different hash space than the cellmap name), so they are separately replaceable.
                int exLen = ssab.ExternalTexturesLength;
                for (int i = 0; i < exLen; i++)
                {
                    var ex = ssab.ExternalTextures(i);
                    if (!ex.HasValue) continue;
                    var e = ex.Value;
                    Add(e.Name, e.NameHash);
                }
            }

            foreach (var kv in acc) _cellmapNameToHashes[kv.Key] = kv.Value.ToArray();
            _cellmapNames = names.ToArray();
        }

        // Drop the lazily-built cellmap table so it is rebuilt from SsabData on next use. Called from
        // OnDisable alongside the material caches; also the reset path if SsabData is reassigned on a reused
        // asset instance (import normally creates a fresh asset, so this rarely matters).
        private void InvalidateCellmapTable()
        {
            _cellmapNameToHashes = null;
            _cellmapNames = null;
        }

        /// <summary>
        /// The names that can be handed to the runtime texture-replacement API: every cellmap name plus
        /// every external-texture (TextureChange target) name. Discovery helper for building an inspector or
        /// a call to <c>SetCellmapTexture</c>. Returns a copy; empty when the asset carries no textures.
        /// (Distinct from the internal player's GetCellmapNames, which lists cellmaps only for the cell
        /// Override Layer; texture replacement also covers external-texture names.)
        /// </summary>
        public string[] GetReplaceableTextureNames()
        {
            EnsureCellmapTable();
            return (string[])_cellmapNames.Clone();
        }

        /// <summary>
        /// Resolve a cellmap / external-texture name to the DrawBatch.TextureHash key(s) it can bind by.
        /// A cellmap yields both its NameHash and ImagePathHash; an external texture yields its NameHash.
        /// False (and hashes = null) when the name is unknown to this asset.
        /// </summary>
        internal bool TryGetCellmapHashes(string cellmapName, out uint[] hashes)
        {
            EnsureCellmapTable();
            if (!string.IsNullOrEmpty(cellmapName) && _cellmapNameToHashes.TryGetValue(cellmapName, out hashes))
            {
                return true;
            }
            hashes = null;
            return false;
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
        // stencil scaffolding) instead of SpriteStudio/Default. It is single-material (MIX/ADD are
        // folded in the shader via the a=0 trick), so there are no per-blend variants - only a
        // per-texture base and a white base for cell-less parts. Cleared with the others in OnDisable.
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
        /// with the blend state applied (see SpriteStudioMaterial).
        /// TextureHash 0 (cell-less, e.g. Shape) resolves to a white-textured base. Returns null
        /// only when even that cannot be created.
        /// </summary>
        internal Material ResolveMaterial(uint textureHash, byte blendType)
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
        /// Resolve a per-part AddOn colour material for a (shaderHash, textureHash, blendType) triple.
        /// The AddOn Shader is resolved by the caller (the player, via SpriteStudioShaderCatalog) and
        /// passed in. Cached per key like the per-blend variants; falls back to the default material when
        /// no AddOn shader is supplied. NOTE: this returns a SHARED cached material, correct for AddOns
        /// without per-frame params (e.g. TestStub). Param-carrying AddOns need a per-instance pool
        /// (added in the next step) so concurrent parts do not stomp each other's params.
        /// </summary>
        internal Material ResolveAddOnMaterial(Shader addonShader, uint shaderHash, uint textureHash, byte blendType)
        {
            if (addonShader == null || shaderHash == 0) return ResolveMaterial(textureHash, blendType);

            var baseMat = (textureHash == 0) ? GetWhiteBaseMaterial() : ResolveMaterialForHash(textureHash);
            if (baseMat == null) return null;

            var key = new SpriteStudioMaterialKey(textureHash, blendType, 0, shaderHash);
            if (_materialVariants != null && _materialVariants.TryGetValue(key, out var cached) && cached != null)
            {
                return cached;
            }

            if (_materialVariants == null) _materialVariants = new Dictionary<SpriteStudioMaterialKey, Material>();
            var variant = SpriteStudioMaterial.CreateAddOnVariant(baseMat, addonShader, blendType);
            _materialVariants[key] = variant;
            return variant;
        }

        /// <summary>
        /// Create a FRESH (non-cached) per-part AddOn material instance for the (shader, texture, blend)
        /// combination. Unlike ResolveAddOnMaterial this never shares: the caller (player) owns the
        /// instance, sets its per-part params each frame, and destroys it. Used for the bounded per-part
        /// pool so concurrent parts with different params do not stomp each other. HideAndDontSave.
        /// </summary>
        internal Material CreatePerPartMaterialInstance(Shader addonShader, uint textureHash, byte blendType)
        {
            if (addonShader == null) return null;
            var baseMat = (textureHash == 0) ? GetWhiteBaseMaterial() : ResolveMaterialForHash(textureHash);
            if (baseMat == null) return null;
            return SpriteStudioMaterial.CreateAddOnVariant(baseMat, addonShader, blendType);
        }

        /// <summary>
        /// Resolve a mask-test colour material for a (textureHash, blendType) pair: the per-blend
        /// colour material plus a stencil test. <paramref name="stencilComp"/> is a
        /// UnityEngine.Rendering.CompareFunction (NotEqual = draw inside the mask, Equal = outside).
        /// Cached per (textureHash, blend, comp) via the shared variant dictionary. Returns null only
        /// when the base material cannot be resolved.
        /// </summary>
        internal Material ResolveTestMaterial(uint textureHash, byte blendType, byte stencilComp)
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
        internal Material ResolveMaskWriteMaterial(uint textureHash, byte stencilOp)
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

            // Clone the world base (which already has _MainTex and the Mix blend state bound at import)
            // and swap only the shader to SpriteStudio/Canvas. Cloning copies every property by name, so
            // _MainTex carries over verbatim (more robust than reading material.mainTexture, which can be
            // null depending on the main-texture property resolution); the shader swap keeps matching
            // property values and picks up the Canvas-only uGUI stencil props at their defaults.
            var canvasBase = new Material(worldBase) { hideFlags = HideFlags.HideAndDontSave, name = "SpriteStudio_CanvasBase" };
            canvasBase.shader = _canvasShader;
            if (canvasBase.mainTexture == null) canvasBase.mainTexture = worldBase.mainTexture; // belt-and-suspenders
            // Fix the single-material Canvas PMA policy regardless of the source material: straight
            // input, premultiplied output, discard transparent. MIX/ADD are split per-vertex in-shader.
            canvasBase.SetFloat("_InputPMA", 0f);
            canvasBase.SetFloat("_OutputPMA", 1f);
            canvasBase.SetFloat("_NotDiscard", 0f);

            if (_canvasBaseByHash == null) _canvasBaseByHash = new Dictionary<uint, Material>();
            _canvasBaseByHash[textureHash] = canvasBase;
            return canvasBase;
        }

        /// <summary>
        /// Resolve the single Canvas (uGUI) material for a texture atlas. The Canvas path is one
        /// material with one fixed PMA blend; MIX vs ADD is handled per-vertex in-shader (a=0 trick)
        /// and all other ssab blends collapse to MIX, so there are no per-blend variants. blendType is
        /// accepted for call-site symmetry but ignored. Returns null only if the base cannot be created.
        /// </summary>
        internal Material ResolveCanvasMaterial(uint textureHash, byte blendType)
        {
            return GetCanvasBaseMaterial(textureHash);
        }

        /// <summary>
        /// Changes each time this asset is loaded. A reimport reloads the asset in place -- players keep the
        /// same reference, now holding new data -- and OnDisable has destroyed the material variants they
        /// resolved, so a player compares this to know that what it resolved is gone.
        /// </summary>
        [System.NonSerialized] internal int Revision;
        private static int s_revisions;

        private void OnEnable()
        {
            Revision = ++s_revisions;
#if UNITY_EDITOR
            // An edit-mode player rebuilds its preview in LateUpdate, which the editor runs only when
            // something asks for it; a reimport should show without the user touching the scene.
            if (!Application.isPlaying) UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        // Destroy runtime-created materials (blend / test / mask-write variants and the white base;
        // clones only) and drop every table built from the serialized data, which a reimport replaces. The
        // serialized base materials must not be touched here.
        private void OnDisable()
        {
            InvalidateCellmapTable();
            _hashToMaterial = null;
            _soundToClip = null;
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
        }

        /// <summary>
        /// Represents one sound referenced by EventAudio, with the minimum needed for playback.
        /// Only the two addressing hashes + a hard reference to the AudioClip for playback.
        /// Metadata such as name, source path, and duration already lives in SsabData, so it is not duplicated here.
        /// </summary>
        [System.Serializable]
        public class SoundClipEntry
        {
            [Tooltip("Hash of the SoundList name (fnv1a). Corresponds to EventAudio.SoundListNameHash.")]
            public uint SoundListNameHash;
            [Tooltip("Hash of the sound alias name (fnv1a). Corresponds to EventAudio.SoundNameHash.")]
            public uint SoundNameHash;
            [Tooltip("AudioClip for playback. Hard reference to prevent build stripping / for runtime loading.")]
            public AudioClip Clip;
        }

        [System.NonSerialized] private Dictionary<ulong, AudioClip> _soundToClip;

        /// <summary>
        /// Returns the AudioClip corresponding to EventAudio's (sound_list_name_hash, sound_name_hash), or null if
        /// not found. The built-in playback resolves through it, and so can a host that plays the sounds itself
        /// from the audio events.
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
