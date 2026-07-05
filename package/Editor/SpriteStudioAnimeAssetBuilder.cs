using UnityEngine;
using UnityEditor;
using UnityEditor.AssetImporters;
using System;
using System.IO;
using System.Collections.Generic;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Shared logic for interpreting the .ssab FlatBuffers and resolving textures, plus the
    /// SpriteStudioAnimeAsset build routine for SpriteStudioSsabImporter.
    /// Centralizes the parse / path resolution / de-duplication that was duplicated between the
    /// importer and SpriteStudioAssetPostprocessor.
    ///
    /// Material policy is unchanged: use a per-texture "shared .mat" (Spine-Unity style).
    /// The importer references an adjacent shared .mat if one exists, otherwise falls back to a
    /// provisional embedded material. Auto-generating the shared .mat itself is handled by
    /// SpriteStudioAssetPostprocessor.
    /// </summary>
    internal static class SpriteStudioAnimeAssetBuilder
    {
        // Verbose import logging (`[SS-IMPORT]`). Quiet by default. Referenced by both the ssab and ssqb importers.
        // Toggled via Window/SpriteStudio/Verbose Import Log (persisted in EditorPrefs).
        private const string VerboseLogPrefKey = "SpriteStudio.VerboseImportLog";
        internal static bool VerboseImportLog
        {
            get => EditorPrefs.GetBool(VerboseLogPrefKey, false);
            set => EditorPrefs.SetBool(VerboseLogPrefKey, value);
        }

        private const string VerboseLogMenu = "Window/SpriteStudio/Verbose Import Log";

        [MenuItem(VerboseLogMenu)]
        private static void ToggleVerboseImportLog() => VerboseImportLog = !VerboseImportLog;

        [MenuItem(VerboseLogMenu, true)]
        private static bool ToggleVerboseImportLogValidate()
        {
            Menu.SetChecked(VerboseLogMenu, VerboseImportLog);
            return true;
        }

        /// <summary>A resolved texture reference (cellmap / external_texture).</summary>
        internal struct TextureRef
        {
            public string TexName;    // Original name inside the ssab
            public string TexPath;    // Resolved project path (best candidate if not present)
            public Texture2D Texture; // null if not present
            public string MatPath;    // Shared material path (ChangeExtension(TexPath, ".mat"))
        }

        internal static Shader ResolveShader(Shader overrideShader)
        {
            if (overrideShader != null) return overrideShader;

            // Shader.Find can return null depending on import order, so load it directly from the path.
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Packages/com.spritestudio.ssruntime/Runtime/Resources/Shaders/SpriteStudioDefault.shader");
            if (shader == null) shader = Shader.Find("SpriteStudio/Default");
            return shader;
        }

        /// <summary>
        /// Shared logic for resolving an asset name (relative path or file name) to an asset of the
        /// given type in the project. Falls back in the order "with relative directory" -> "file name
        /// only" and returns the resolved instance via out.
        /// If onDependency is supplied, the searched paths are reported as dependencies (for the importer).
        /// The return value is the best-candidate path (returned even if not present).
        /// </summary>
        internal static string ResolveAssetPath<T>(string baseDir, string name, out T asset, Action<string> onDependency)
            where T : UnityEngine.Object
        {
            asset = null;
            if (string.IsNullOrEmpty(name)) return null;

            string path = Path.Combine(baseDir, name).Replace("\\", "/");
            onDependency?.Invoke(path);
            asset = AssetDatabase.LoadAssetAtPath<T>(path);

            if (asset == null)
            {
                string altPath = Path.Combine(baseDir, Path.GetFileName(name)).Replace("\\", "/");
                if (altPath != path)
                {
                    onDependency?.Invoke(altPath);
                    var alt = AssetDatabase.LoadAssetAtPath<T>(altPath);
                    if (alt != null)
                    {
                        path = altPath;
                        asset = alt;
                    }
                }
            }
            return path;
        }

        /// <summary>Resolves a texture name (image_path / external name) to a project path (<see cref="ResolveAssetPath{T}"/>).</summary>
        internal static string ResolveTexturePath(string baseDir, string texName, out Texture2D tex, Action<string> onDependency)
            => ResolveAssetPath(baseDir, texName, out tex, onDependency);

        /// <summary>Resolves an audio file name (SoundFile.file_path) to an AudioClip in the project (<see cref="ResolveAssetPath{T}"/>).</summary>
        internal static string ResolveAudioClipPath(string baseDir, string fileName, out AudioClip clip, Action<string> onDependency)
            => ResolveAssetPath(baseDir, fileName, out clip, onDependency);

        internal static string ResolveSsabPath(string baseDir, string animePackName, out SpriteStudioAnimeAsset asset, Action<string> onDependency)
        {
            asset = null;
            if (string.IsNullOrEmpty(animePackName)) return null;

            string filename = animePackName;
            if (!filename.EndsWith(".ssab", StringComparison.OrdinalIgnoreCase))
            {
                if (filename.EndsWith(".ssae", StringComparison.OrdinalIgnoreCase))
                {
                    filename = Path.ChangeExtension(filename, ".ssab");
                }
                else
                {
                    filename += ".ssab";
                }
            }

            string path = Path.Combine(baseDir, filename).Replace("\\", "/");
            onDependency?.Invoke(path);
            asset = AssetDatabase.LoadAssetAtPath<SpriteStudioAnimeAsset>(path);

            if (asset == null)
            {
                // Fallback when it is not directly adjacent: search the whole project for a .ssab of the same name.
                // FindAssets' name search is "partial match + no ordering guarantee", so taking guids[0] as-is
                // would let same/similar-named assets change the result across machines or import order
                // (non-deterministic import).
                // (a) narrow to entries whose file name matches exactly, and (b) sort by path to pick deterministically.
                // (c) multiple matches is an ambiguous case that depends on global state, so warn.
                string stem = Path.GetFileNameWithoutExtension(filename);
                string[] guids = AssetDatabase.FindAssets(stem + " t:SpriteStudioAnimeAsset");
                var matches = new List<string>();
                foreach (var g in guids)
                {
                    string p = AssetDatabase.GUIDToAssetPath(g);
                    if (string.Equals(Path.GetFileName(p), filename, StringComparison.OrdinalIgnoreCase))
                        matches.Add(p);
                }
                matches.Sort(StringComparer.Ordinal);
                if (matches.Count > 0)
                {
                    if (matches.Count > 1)
                    {
                        Debug.LogWarning($"[SSPlayer] Anime pack '{animePackName}' matched multiple candidates ({matches.Count}: {string.Join(", ", matches)}). Using '{matches[0]}' deterministically. Placing the .ssab next to the referencing asset avoids the ambiguity.");
                    }
                    path = matches[0];
                    onDependency?.Invoke(path);
                    asset = AssetDatabase.LoadAssetAtPath<SpriteStudioAnimeAsset>(path);
                }
            }

            return path;
        }

        /// <summary>
        /// Enumerates every texture referenced by the .ssab (cellmaps + external_textures),
        /// de-duplicated by resolved path.
        /// If onDependency is supplied, the scanned texture paths are reported as dependencies (for the importer).
        /// </summary>
        internal static List<TextureRef> EnumerateTextures(byte[] ssabData, string baseDir, Action<string> onDependency = null)
        {
            var bb = new Google.FlatBuffers.ByteBuffer(ssabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
            return CollectTextures(ssab, baseDir, onDependency);
        }

        private static List<TextureRef> CollectTextures(ss.format.SsAnimeBinary ssab, string baseDir, Action<string> onDependency)
        {
            var result = new List<TextureRef>();
            var seen = new HashSet<string>();

            // cellmaps: the primary textures referenced by normal parts, etc.
            int cellmapLen = ssab.CellmapsLength;
            for (int i = 0; i < cellmapLen; i++)
            {
                var cm = ssab.Cellmaps(i);
                if (cm.HasValue) TryAddTexture(result, seen, baseDir, cm.Value.ImagePath, onDependency);
            }

            // external_textures: replacement textures for the TextureChange attribute
            int extLen = ssab.ExternalTexturesLength;
            for (int i = 0; i < extLen; i++)
            {
                var extTex = ssab.ExternalTextures(i);
                if (extTex.HasValue) TryAddTexture(result, seen, baseDir, extTex.Value.Name, onDependency);
            }
            return result;
        }

        private static void TryAddTexture(List<TextureRef> result, HashSet<string> seen, string baseDir, string texName, Action<string> onDependency)
        {
            if (string.IsNullOrEmpty(texName)) return;

            string texPath = ResolveTexturePath(baseDir, texName, out var tex, onDependency);
            if (!seen.Add(texPath)) return; // de-duplicate by resolved path

            result.Add(new TextureRef
            {
                TexName = texName,
                TexPath = texPath,
                Texture = tex,
                MatPath = Path.ChangeExtension(texPath, ".mat"),
            });
        }

        /// <summary>Called from SpriteStudioSsabImporter to build the SpriteStudioAnimeAsset.</summary>
        internal static void Build(SpriteStudioSsabImporter importer, AssetImportContext ctx)
        {
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();

            // 1. Retain the .ssab binary (the runtime's memory source)
            asset.SsabData = File.ReadAllBytes(ctx.assetPath);

            // 2. Parse
            var bb = new Google.FlatBuffers.ByteBuffer(asset.SsabData);
            // Input validation: reject anything that is not converter output (e.g. an unrelated file that
            // merely has the .ssab extension) and surface an error that explains the cause.
            // Reading the identifier needs the leading offset (4) + magic (4), so at least 8 bytes are required.
            if (asset.SsabData.Length < 8 || !ss.format.SsAnimeBinary.SsAnimeBinaryBufferHasIdentifier(bb))
            {
                ctx.LogImportError($"[SpriteStudioSsabImporter] Not a valid .ssab (FlatBuffers 'SSAB' identifier missing): {ctx.assetPath}");
                ctx.AddObjectToAsset("MainAsset", asset);
                ctx.SetMainObject(asset);
                return;
            }
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
            string baseDir = Path.GetDirectoryName(ctx.assetPath);
            
            asset.PackHash = ssab.NameHash;

            // 3. Register texture dependencies and assign materials (guards against build stripping / Addressables)
            var shader = ResolveShader(importer.OverrideShader);
            var textures = CollectTextures(ssab, baseDir, ctx.DependsOnSourceAsset);

            // Resolved texture path -> index into Materials
            var pathToMaterialIndex = new Dictionary<string, int>();

            foreach (var t in textures)
            {
                if (t.Texture == null)
                {
                    Debug.LogWarning($"[SpriteStudioSsabImporter] Texture not found for '{t.TexName}' (tried '{t.TexPath}')");
                    continue;
                }

                asset.Textures.Add(t.Texture);

                if (!importer.GenerateMaterials) continue;

                // Use the shared .mat if it exists (the postprocessor is expected to auto-generate it).
                // Register a dependency so this is re-imported when the .mat is generated.
                ctx.DependsOnSourceAsset(t.MatPath);
                var externalMat = AssetDatabase.LoadAssetAtPath<Material>(t.MatPath);
                if (externalMat != null)
                {
                    asset.Materials.Add(externalMat);
                    pathToMaterialIndex[t.TexPath] = asset.Materials.Count - 1;
                }
                else if (shader != null)
                {
                    // Provisional embedded material used until the shared .mat is generated
                    var mat = new Material(shader) { name = t.Texture.name + "_Material", mainTexture = t.Texture };
                    ctx.AddObjectToAsset("Mat_" + asset.Materials.Count, mat);
                    asset.Materials.Add(mat);
                    pathToMaterialIndex[t.TexPath] = asset.Materials.Count - 1;
                }
            }

            // 4. Build the DrawBatch.TextureHash -> Material index lookup table.
            //    So any hash that arrives at runtime can be looked up, map the cellmap's
            //    NameHash/ImagePathHash and the external NameHash all (= the material the same texture resolves to).
            if (importer.GenerateMaterials && pathToMaterialIndex.Count > 0)
            {
                var hashKeys = new List<uint>();
                var hashMaterialIndices = new List<int>();
                var hashSeen = new HashSet<uint>();

                int cellmapLen = ssab.CellmapsLength;
                for (int i = 0; i < cellmapLen; i++)
                {
                    var cm = ssab.Cellmaps(i);
                    if (!cm.HasValue) continue;
                    string path = ResolveTexturePath(baseDir, cm.Value.ImagePath, out _, null);
                    if (path != null && pathToMaterialIndex.TryGetValue(path, out int mi))
                    {
                        RecordHash(hashKeys, hashMaterialIndices, hashSeen, cm.Value.NameHash, mi);
                        RecordHash(hashKeys, hashMaterialIndices, hashSeen, cm.Value.ImagePathHash, mi);
                    }
                }

                int extLen = ssab.ExternalTexturesLength;
                for (int i = 0; i < extLen; i++)
                {
                    var extTex = ssab.ExternalTextures(i);
                    if (!extTex.HasValue) continue;
                    string path = ResolveTexturePath(baseDir, extTex.Value.Name, out _, null);
                    if (path != null && pathToMaterialIndex.TryGetValue(path, out int mi))
                    {
                        RecordHash(hashKeys, hashMaterialIndices, hashSeen, extTex.Value.NameHash, mi);
                    }
                }

                asset.TextureHashKeys = hashKeys.ToArray();
                asset.TextureHashMaterialIndices = hashMaterialIndices.ToArray();
            }

            // 5. Extract animation names
            int animLen = ssab.AnimationsLength;
            asset.AnimationNames = new string[animLen];
            for (int i = 0; i < animLen; i++)
            {
                var anim = ssab.Animations(i);
                if (anim.HasValue) asset.AnimationNames[i] = anim.Value.Name;
            }

            // 5b. Resolve sounds (SoundList/SoundFile) to AudioClips and store them in the "slot".
            //     Runtime playback is not implemented yet, but the ScriptableObject only holds what playback
            //     needs: the two address hashes + the AudioClip instance (name/path/duration live in SsabData,
            //     so they are not duplicated here).
            //     Like textures, AudioClips are hard-referenced + dependency-registered to prevent build
            //     stripping. Sounds that cannot be resolved are warned about and skipped, like textures
            //     (= Sounds only contains the playable entries).
            int soundListLen = ssab.SoundListsLength;
            for (int li = 0; li < soundListLen; li++)
            {
                var sl = ssab.SoundLists(li);
                if (!sl.HasValue) continue;
                var soundList = sl.Value;

                int fileLen = soundList.TableDataLength;
                for (int fi = 0; fi < fileLen; fi++)
                {
                    var sf = soundList.TableData(fi);
                    if (!sf.HasValue) continue;
                    var soundFile = sf.Value;
                    if (string.IsNullOrEmpty(soundFile.FilePath)) continue;

                    ResolveAudioClipPath(baseDir, soundFile.FilePath, out var clip, ctx.DependsOnSourceAsset);
                    if (clip == null)
                    {
                        Debug.LogWarning($"[SpriteStudioSsabImporter] AudioClip not found for '{soundFile.FilePath}' (sound '{soundFile.Name}')");
                        continue;
                    }

                    asset.Sounds.Add(new SpriteStudioAnimeAsset.SoundClipEntry
                    {
                        SoundListNameHash = soundList.NameHash,
                        SoundNameHash = soundFile.NameHash,
                        Clip = clip,
                    });
                }
            }

            // 6. Resolve dependencies on external animations (Instance).
            //    The external_instances vector can be omitted by the converter (it was actually empty for
            //    Ringo_tyu), so scan the parts' PartTypeInstance directly to collect cross-pack references
            //    (the authoritative source).
            //    Also follow other packs that the external assets reference in turn (transitive).
            {
                var seenAssets = new HashSet<SpriteStudioAnimeAsset>();
                var seenPacks = new HashSet<string>();
                CollectExternalReferences(asset.SsabData, baseDir, asset.PackHash, ctx, asset.ExternalReferences, seenAssets, seenPacks);
            }

            if (VerboseImportLog)
                Debug.Log($"[SS-IMPORT] {ctx.assetPath} -> textures:{asset.Textures.Count} materials:{asset.Materials.Count} extRefs:{asset.ExternalReferences.Count} sounds:{asset.Sounds.Count}");

            ctx.AddObjectToAsset("MainAsset", asset);
            ctx.SetMainObject(asset);
        }

        /// <summary>Records a hash -> material mapping. Ignores 0 (unset) and already-seen hashes (first wins).</summary>
        private static void RecordHash(List<uint> keys, List<int> indices, HashSet<uint> seen, uint hash, int materialIndex)
        {
            if (hash == 0) return;
            if (!seen.Add(hash)) return;
            keys.Add(hash);
            indices.Add(materialIndex);
        }

        /// <summary>
        /// Recursively collects other packs (Instance) referenced by the ssab and adds them to outRefs.
        /// Gathers referenced pack names from both the external_instances vector (if present) and the parts'
        /// PartTypeInstance, then resolves them to project assets via ResolveSsabPath. The parts scan is the
        /// primary path because the converter can omit external_instances. Also follows other packs that the
        /// external assets reference in turn (transitive).
        /// seenAssets / seenPacks eliminate duplicates and cycles.
        /// </summary>
        private static void CollectExternalReferences(byte[] ssabData, string baseDir, uint ownPackHash,
            AssetImportContext ctx, List<SpriteStudioAnimeAsset> outRefs,
            HashSet<SpriteStudioAnimeAsset> seenAssets, HashSet<string> seenPacks)
        {
            if (ssabData == null || ssabData.Length == 0) return;

            var bb = new Google.FlatBuffers.ByteBuffer(ssabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);

            var packNames = new List<string>();

            int extLen = ssab.ExternalInstancesLength;
            for (int i = 0; i < extLen; i++)
            {
                var e = ssab.ExternalInstances(i);
                if (e.HasValue && !string.IsNullOrEmpty(e.Value.AnimePackName))
                    packNames.Add(e.Value.AnimePackName);
            }

            int partsLen = ssab.PartsLength;
            for (int i = 0; i < partsLen; i++)
            {
                var p = ssab.Parts(i);
                if (!p.HasValue || p.Value.PartTypeType != ss.format.PartType.PartTypeInstance) continue;
                var pt = p.Value.PartTypeAsPartTypeInstance();
                // A same-pack reference (self) is resolved to self at runtime, so don't add it as an external reference.
                if (pt.RefAnimePackHash == ownPackHash) continue;
                if (!string.IsNullOrEmpty(pt.RefAnimePack)) packNames.Add(pt.RefAnimePack);
            }

            foreach (var packName in packNames)
            {
                if (string.IsNullOrEmpty(packName) || !seenPacks.Add(packName)) continue;
                ResolveSsabPath(baseDir, packName, out var extAsset, ctx.DependsOnSourceAsset);
                if (extAsset == null || !seenAssets.Add(extAsset)) continue;
                outRefs.Add(extAsset);

                // transitive: also follow other packs that the external asset references (recurse with the external's baseDir).
                string extDir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(extAsset));
                CollectExternalReferences(extAsset.SsabData, extDir, extAsset.PackHash, ctx, outRefs, seenAssets, seenPacks);
            }
        }
    }
}
