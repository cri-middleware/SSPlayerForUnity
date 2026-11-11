using System.Collections.Generic;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Base materials made at runtime, for a texture that has no material asset. One material per
    /// (texture, shader), shared by every asset that uses the texture, so a texture drawn by several
    /// assets still batches as one material.
    ///
    /// Keyed by the Texture2D, never by the texture hash: two .sspj can each hold an image with the same
    /// name and so the same hash, and a hash key would hand one of them the other's material.
    ///
    /// Lifetime is reference counted. An asset takes a reference to each of its textures when it is
    /// enabled and hands it back when it is disabled, and nothing else changes the count, so a miscount
    /// cannot destroy a material that is still drawn or keep one forever. The material is made on first
    /// use and destroyed when the last reference goes. While it lives it holds its texture, so unloading
    /// the texture (or its AssetBundle) before the assets that use it leaves the material drawing nothing
    /// and keeps the texture's memory.
    ///
    /// Nothing is cleared when play mode starts. With domain reload off, the assets keep their references
    /// across the transition, so the counts stay right; clearing would orphan them.
    /// </summary>
    internal static class SpriteStudioBaseMaterialCache
    {
        private const string DefaultShaderName = "SpriteStudio/Default";

        /// <summary>What <see cref="Acquire"/> hands out and <see cref="Release"/> takes back.</summary>
        internal readonly struct Ref
        {
            // Instance IDs stay the same after the object is destroyed, so a reference to a texture that is
            // gone still finds its entry.
            internal readonly int Texture;
            internal readonly int Shader;
            internal Ref(int texture, int shader) { Texture = texture; Shader = shader; }
            internal bool IsValid => Texture != 0;
        }

        private sealed class Entry
        {
            public Texture2D Texture;
            public Shader Shader;
            public Material Material;
            public int RefCount;
        }

        private static readonly Dictionary<(int texture, int shader), Entry> s_entries
            = new Dictionary<(int, int), Entry>();
        private static Shader s_defaultShader;

        /// <summary>The shader a base material uses when the asset names none: the package's default.</summary>
        internal static Shader DefaultShader
        {
            get
            {
                if (s_defaultShader == null) s_defaultShader = Shader.Find(DefaultShaderName);
                return s_defaultShader;
            }
        }

        private static Shader ShaderOrDefault(Shader shader) => shader != null ? shader : DefaultShader;

        /// <summary>
        /// Takes a reference to the base material of <paramref name="texture"/> drawn with
        /// <paramref name="shader"/> (null: the default). Only an asset's OnEnable calls this. Returns an
        /// invalid reference when the texture or the shader is missing.
        /// </summary>
        internal static Ref Acquire(Texture2D texture, Shader shader)
        {
            DropDestroyed();

            shader = ShaderOrDefault(shader);
            if (texture == null || shader == null) return default;

            var key = (texture.GetInstanceID(), shader.GetInstanceID());
            if (!s_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry { Texture = texture, Shader = shader };
                s_entries[key] = entry;
            }
            entry.RefCount++;
            return new Ref(key.Item1, key.Item2);
        }

        /// <summary>Hands back a reference from <see cref="Acquire"/>. Only an asset's OnDisable calls this.</summary>
        internal static void Release(Ref reference)
        {
            if (!reference.IsValid) return;

            var key = (reference.Texture, reference.Shader);
            if (!s_entries.TryGetValue(key, out var entry)) return;
            if (--entry.RefCount > 0) return;

            DestroyMaterial(entry);
            s_entries.Remove(key);
        }

        /// <summary>
        /// The base material for a reference, made on first use. Null when the reference is invalid, or its
        /// texture or shader has been destroyed.
        /// </summary>
        internal static Material Get(Ref reference)
        {
            if (!reference.IsValid) return null;
            if (!s_entries.TryGetValue((reference.Texture, reference.Shader), out var entry)) return null;
            if (entry.Texture == null || entry.Shader == null) return null;

            if (entry.Material == null)
            {
                entry.Material = new Material(entry.Shader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    name = entry.Texture.name + "_Material",
                    mainTexture = entry.Texture,
                };
            }
            return entry.Material;
        }

        // A material whose texture or shader was destroyed draws nothing useful, so it goes now. The entry
        // stays until its references are handed back.
        private static void DropDestroyed()
        {
            foreach (var entry in s_entries.Values)
            {
                if (entry.Material != null && (entry.Texture == null || entry.Shader == null)) DestroyMaterial(entry);
            }
        }

        /// <summary>The number of entries. For tests.</summary>
        internal static int EntryCount => s_entries.Count;

        /// <summary>The reference count of an entry, 0 if there is none. For tests.</summary>
        internal static int RefCountOf(Ref reference)
            => s_entries.TryGetValue((reference.Texture, reference.Shader), out var e) ? e.RefCount : 0;

        private static void DestroyMaterial(Entry entry)
        {
            var m = entry.Material;
            entry.Material = null;
            if (m == null) return;
            if (Application.isPlaying) Object.Destroy(m); else Object.DestroyImmediate(m);
        }
    }
}
