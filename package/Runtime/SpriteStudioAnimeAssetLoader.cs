using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>What kind of file a <see cref="SpriteStudioDependency"/> names.</summary>
    public enum SpriteStudioDependencyKind
    {
        /// <summary>An image: a cell map atlas, or a texture a texture change swaps in.</summary>
        Texture,
        /// <summary>A sound file an audio key plays.</summary>
        Audio,
        /// <summary>Another anime pack (.ssab) an Instance part plays.</summary>
        AnimePack,
    }

    /// <summary>One file a .ssab needs, named as the .ssab names it.</summary>
    public readonly struct SpriteStudioDependency : IEquatable<SpriteStudioDependency>
    {
        public SpriteStudioDependencyKind Kind { get; }

        /// <summary>
        /// The name as written in the .ssab: a path relative to the .sspj for a texture or a sound, the anime
        /// pack's name for a pack. These are the names the resolvers given to
        /// <see cref="SpriteStudioAnimeAssetLoader.Create(byte[], Func{string, Texture2D}, Func{string, AudioClip}, Shader)"/>
        /// receive.
        /// </summary>
        public string Name { get; }

        public SpriteStudioDependency(SpriteStudioDependencyKind kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public bool Equals(SpriteStudioDependency other) =>
            Kind == other.Kind && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) => obj is SpriteStudioDependency d && Equals(d);

        public override int GetHashCode() =>
            ((int)Kind * 397) ^ (Name == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Name));

        public override string ToString() => $"{Kind}: {Name}";
    }

    /// <summary>
    /// Builds a <see cref="SpriteStudioAnimeAsset"/> at runtime from the bytes of a .ssab that was not imported,
    /// with textures and sounds the application loads itself (from an AssetBundle, Addressables, and so on).
    ///
    /// The steps:
    /// <list type="number">
    /// <item><see cref="GetDependencies"/> lists the textures, sounds and other anime packs the bytes need.</item>
    /// <item>The application loads them, asynchronously if it likes; nothing here waits.</item>
    /// <item><see cref="Create(byte[], Func{string, Texture2D}, Func{string, AudioClip}, Shader)"/> builds the
    /// asset. Each texture is drawn with the shared base material made at runtime.</item>
    /// <item>When Instance parts play other packs, build those as well and pass them all to
    /// <see cref="LinkExternalReferences"/>.</item>
    /// </list>
    ///
    /// The application owns what it creates: <c>Object.Destroy</c> each asset when done, and before unloading
    /// the textures it was given. Destroying it hands back its share of the runtime materials.
    /// </summary>
    public static class SpriteStudioAnimeAssetLoader
    {
        /// <summary>
        /// The textures, sounds and other anime packs the .ssab needs, each named once (names that differ only
        /// in case count as one). A pack's reference to itself is not listed.
        /// </summary>
        /// <exception cref="ArgumentException">The bytes are not a .ssab.</exception>
        public static IReadOnlyList<SpriteStudioDependency> GetDependencies(byte[] ssabData)
        {
            var ssab = Parse(ssabData);
            var result = new List<SpriteStudioDependency>();
            var seen = new HashSet<SpriteStudioDependency>();

            void Add(SpriteStudioDependencyKind kind, string name)
            {
                if (string.IsNullOrEmpty(name)) return;
                var d = new SpriteStudioDependency(kind, name);
                if (seen.Add(d)) result.Add(d);
            }

            foreach (var name in TextureNames(ssab)) Add(SpriteStudioDependencyKind.Texture, name);
            foreach (var name in SoundFileNames(ssab)) Add(SpriteStudioDependencyKind.Audio, name);
            foreach (var pack in ReferencedPacks(ssab)) Add(SpriteStudioDependencyKind.AnimePack, pack.Name);
            return result;
        }

        /// <summary>
        /// Build an asset from the bytes of a .ssab, asking for each texture and sound by the name the .ssab
        /// gives it (see <see cref="GetDependencies"/>). Each resolver is called once per name.
        /// A texture the resolver returns null for is reported, and its parts do not draw correctly (as when an
        /// import cannot find a texture); a sound it returns null for is reported and left silent.
        /// </summary>
        /// <param name="ssabData">The .ssab bytes. The asset keeps this array; do not change it afterwards.</param>
        /// <param name="textureResolver">Returns the texture for a name. Hand back the same Texture2D for the same
        /// image every time: the runtime materials are shared per Texture2D object.</param>
        /// <param name="audioResolver">Returns the AudioClip for a sound's file name. Null: the asset has no sound.</param>
        /// <param name="overrideShader">The shader the runtime materials use. Null: SpriteStudio/Default.</param>
        /// <exception cref="ArgumentException">The bytes are not a .ssab.</exception>
        public static SpriteStudioAnimeAsset Create(byte[] ssabData, Func<string, Texture2D> textureResolver,
            Func<string, AudioClip> audioResolver = null, Shader overrideShader = null)
        {
            if (textureResolver == null) throw new ArgumentNullException(nameof(textureResolver));
            var ssab = Parse(ssabData);

            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.name = string.IsNullOrEmpty(ssab.Name) ? "SpriteStudioAnimeAsset" : ssab.Name;
            asset.SsabData = ssabData;
            asset.PackHash = ssab.NameHash;
            asset.OverrideShader = overrideShader;

            // Textures: one entry per distinct Texture2D, as the importer has one per distinct file.
            var textureByName = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            var textureIndex = new Dictionary<Texture2D, int>();
            foreach (var name in TextureNames(ssab))
            {
                if (textureByName.ContainsKey(name)) continue;
                var tex = textureResolver(name);
                textureByName[name] = tex;
                if (tex == null)
                {
                    Debug.LogWarning($"[SpriteStudioAnimeAssetLoader] '{asset.name}': no texture was given for '{name}'. The parts using it will not draw correctly.");
                    continue;
                }
                if (textureIndex.ContainsKey(tex)) continue;
                textureIndex[tex] = asset.Textures.Count;
                asset.Textures.Add(tex);
                asset.Materials.Add(null);
            }

            // DrawBatch.TextureHash -> Textures index, with every hash a texture can arrive as (as the importer does).
            var hashKeys = new List<uint>();
            var hashIndices = new List<int>();
            var hashSeen = new HashSet<uint>();
            void Record(uint hash, string name)
            {
                if (hash == 0 || !textureByName.TryGetValue(name ?? "", out var tex) || tex == null) return;
                if (!hashSeen.Add(hash)) return;
                hashKeys.Add(hash);
                hashIndices.Add(textureIndex[tex]);
            }
            for (int i = 0; i < ssab.CellmapsLength; i++)
            {
                var cm = ssab.Cellmaps(i);
                if (!cm.HasValue) continue;
                Record(cm.Value.NameHash, cm.Value.ImagePath);
                Record(cm.Value.ImagePathHash, cm.Value.ImagePath);
            }
            for (int i = 0; i < ssab.ExternalTexturesLength; i++)
            {
                var ex = ssab.ExternalTextures(i);
                if (ex.HasValue) Record(ex.Value.NameHash, ex.Value.Name);
            }
            asset.TextureHashKeys = hashKeys.ToArray();
            asset.TextureHashTextureIndices = hashIndices.ToArray();

            asset.AnimationNames = new string[ssab.AnimationsLength];
            for (int i = 0; i < asset.AnimationNames.Length; i++)
            {
                var anim = ssab.Animations(i);
                if (anim.HasValue) asset.AnimationNames[i] = anim.Value.Name;
            }

            if (audioResolver != null) AddSounds(asset, ssab, audioResolver);

            // CreateInstance enabled the asset while it was still empty, so it took no material references.
            asset.ReloadRuntimeState();
            return asset;
        }

        /// <summary>
        /// <see cref="Create(byte[], Func{string, Texture2D}, Func{string, AudioClip}, Shader)"/> with the
        /// textures and sounds matched to the .ssab's names by file name, without the extension and ignoring
        /// case: <c>"images/Ringo.png"</c> matches a Texture2D named <c>"ringo"</c>.
        /// </summary>
        /// <exception cref="ArgumentException">The bytes are not a .ssab; or file names cannot tell two of the
        /// .ssab's names apart (<c>a/body.png</c> and <c>b/body.png</c>, <c>body.png</c> and <c>body.tga</c>);
        /// or two different objects given share a name. Use the resolver overload then.</exception>
        public static SpriteStudioAnimeAsset Create(byte[] ssabData, IEnumerable<Texture2D> textures,
            IEnumerable<AudioClip> audioClips = null, Shader overrideShader = null)
        {
            if (textures == null) throw new ArgumentNullException(nameof(textures));
            var ssab = Parse(ssabData);

            var textureByStem = ByName(textures, "texture");
            CheckDistinctStems(TextureNames(ssab), "texture");
            Func<string, AudioClip> audio = null;
            if (audioClips != null)
            {
                var clipByStem = ByName(audioClips, "AudioClip");
                CheckDistinctStems(SoundFileNames(ssab), "sound");
                audio = name => clipByStem.TryGetValue(Stem(name), out var c) ? c : null;
            }
            return Create(ssabData, name => textureByStem.TryGetValue(Stem(name), out var t) ? t : null, audio, overrideShader);
        }

        /// <summary>
        /// Point each asset's Instance parts at the other packs they play, found among <paramref name="assets"/>
        /// by pack. The packs those packs play are added too, since an Instance plays its own Instances through
        /// the asset at the top. Replaces each asset's <see cref="SpriteStudioAnimeAsset.ExternalReferences"/>;
        /// a pack not found among them is reported, and its Instance parts are skipped when played.
        /// Call it before the assets are played.
        /// </summary>
        /// <exception cref="ArgumentException">Two different assets are the same pack.</exception>
        public static void LinkExternalReferences(IEnumerable<SpriteStudioAnimeAsset> assets)
        {
            if (assets == null) throw new ArgumentNullException(nameof(assets));
            var list = new List<SpriteStudioAnimeAsset>();
            var byPack = new Dictionary<uint, SpriteStudioAnimeAsset>();
            foreach (var a in assets)
            {
                if (a == null || list.Contains(a)) continue;
                list.Add(a);
                if (byPack.TryGetValue(a.PackHash, out var other))
                {
                    throw new ArgumentException($"'{other.name}' and '{a.name}' are the same anime pack (0x{a.PackHash:X8}).", nameof(assets));
                }
                byPack[a.PackHash] = a;
            }

            foreach (var root in list)
            {
                var refs = new List<SpriteStudioAnimeAsset>();
                var visited = new HashSet<uint> { root.PackHash };
                var pending = new Queue<SpriteStudioAnimeAsset>();
                pending.Enqueue(root);
                while (pending.Count > 0)
                {
                    var current = pending.Dequeue();
                    if (!SpriteStudioSsabFormat.IsSsabBuffer(current.SsabData)) continue;
                    var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(new Google.FlatBuffers.ByteBuffer(current.SsabData));
                    foreach (var pack in ReferencedPacks(ssab))
                    {
                        if (!visited.Add(pack.Hash)) continue;
                        if (!byPack.TryGetValue(pack.Hash, out var target))
                        {
                            Debug.LogWarning($"[SpriteStudioAnimeAssetLoader] '{current.name}' plays anime pack '{pack.Name}', which is not among the assets given. Its Instance parts will be skipped.");
                            continue;
                        }
                        refs.Add(target);
                        pending.Enqueue(target);
                    }
                }
                root.ExternalReferences = refs;
                root.MarkChanged();
            }
        }

        private static ss.format.SsAnimeBinary Parse(byte[] ssabData)
        {
            if (!SpriteStudioSsabFormat.IsSsabBuffer(ssabData))
            {
                throw new ArgumentException("Not a .ssab (the FlatBuffers 'SSAB' identifier is missing).", nameof(ssabData));
            }
            return ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(new Google.FlatBuffers.ByteBuffer(ssabData));
        }

        // The cell map images, then the textures a texture change swaps in (the importer's order).
        private static IEnumerable<string> TextureNames(ss.format.SsAnimeBinary ssab)
        {
            for (int i = 0; i < ssab.CellmapsLength; i++)
            {
                var cm = ssab.Cellmaps(i);
                if (cm.HasValue && !string.IsNullOrEmpty(cm.Value.ImagePath)) yield return cm.Value.ImagePath;
            }
            for (int i = 0; i < ssab.ExternalTexturesLength; i++)
            {
                var ex = ssab.ExternalTextures(i);
                if (ex.HasValue && !string.IsNullOrEmpty(ex.Value.Name)) yield return ex.Value.Name;
            }
        }

        private static IEnumerable<string> SoundFileNames(ss.format.SsAnimeBinary ssab)
        {
            for (int li = 0; li < ssab.SoundListsLength; li++)
            {
                var sl = ssab.SoundLists(li);
                if (!sl.HasValue) continue;
                for (int fi = 0; fi < sl.Value.TableDataLength; fi++)
                {
                    var sf = sl.Value.TableData(fi);
                    if (sf.HasValue && !string.IsNullOrEmpty(sf.Value.FilePath)) yield return sf.Value.FilePath;
                }
            }
        }

        // Other packs the Instance parts play, from the parts themselves (the converter can leave
        // external_instances out) and from external_instances. A reference to the pack itself is skipped.
        private static IEnumerable<(string Name, uint Hash)> ReferencedPacks(ss.format.SsAnimeBinary ssab)
        {
            uint own = ssab.NameHash;
            for (int i = 0; i < ssab.PartsLength; i++)
            {
                var p = ssab.Parts(i);
                if (!p.HasValue || p.Value.PartTypeType != ss.format.PartType.PartTypeInstance) continue;
                var pt = p.Value.PartTypeAsPartTypeInstance();
                if (pt.RefAnimePackHash == 0 || pt.RefAnimePackHash == own || string.IsNullOrEmpty(pt.RefAnimePack)) continue;
                yield return (pt.RefAnimePack, pt.RefAnimePackHash);
            }
            for (int i = 0; i < ssab.ExternalInstancesLength; i++)
            {
                var e = ssab.ExternalInstances(i);
                if (!e.HasValue) continue;
                var x = e.Value;
                if (x.AnimePackNameHash == 0 || x.AnimePackNameHash == own || string.IsNullOrEmpty(x.AnimePackName)) continue;
                yield return (x.AnimePackName, x.AnimePackNameHash);
            }
        }

        private static void AddSounds(SpriteStudioAnimeAsset asset, ss.format.SsAnimeBinary ssab, Func<string, AudioClip> audioResolver)
        {
            var clipByName = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
            for (int li = 0; li < ssab.SoundListsLength; li++)
            {
                var sl = ssab.SoundLists(li);
                if (!sl.HasValue) continue;
                var list = sl.Value;
                for (int fi = 0; fi < list.TableDataLength; fi++)
                {
                    var sf = list.TableData(fi);
                    if (!sf.HasValue || string.IsNullOrEmpty(sf.Value.FilePath)) continue;
                    var file = sf.Value;
                    if (!clipByName.TryGetValue(file.FilePath, out var clip))
                    {
                        clip = audioResolver(file.FilePath);
                        clipByName[file.FilePath] = clip;
                        if (clip == null)
                        {
                            Debug.LogWarning($"[SpriteStudioAnimeAssetLoader] '{asset.name}': no AudioClip was given for '{file.FilePath}'. Its sounds stay silent.");
                        }
                    }
                    if (clip == null) continue;
                    asset.Sounds.Add(new SpriteStudioAnimeAsset.SoundClipEntry
                    {
                        SoundListNameHash = list.NameHash,
                        SoundNameHash = file.NameHash,
                        Clip = clip,
                    });
                }
            }
        }

        private static string Stem(string name) =>
            Path.GetFileNameWithoutExtension((name ?? "").Replace('\\', '/'));

        private static Dictionary<string, T> ByName<T>(IEnumerable<T> objects, string what) where T : UnityEngine.Object
        {
            var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in objects)
            {
                if (o == null) continue;
                if (map.TryGetValue(o.name, out var existing))
                {
                    if (existing == o) continue;
                    throw new ArgumentException($"Two {what}s given are both named '{o.name}', so the .ssab's names cannot be matched to them. Pass a resolver instead.");
                }
                map[o.name] = o;
            }
            return map;
        }

        private static void CheckDistinctStems(IEnumerable<string> names, string what)
        {
            var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                string stem = Stem(name);
                if (byStem.TryGetValue(stem, out var first))
                {
                    if (string.Equals(first, name, StringComparison.OrdinalIgnoreCase)) continue;
                    throw new ArgumentException($"The .ssab names {what}s '{first}' and '{name}', which a file name alone cannot tell apart. Pass a resolver instead.");
                }
                byStem[stem] = name;
            }
        }
    }
}
