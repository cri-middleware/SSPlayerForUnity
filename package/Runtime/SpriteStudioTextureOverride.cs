using System.Collections.Generic;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// One authored/persisted cellmap texture replacement: a cellmap (or external-texture) name and the
    /// texture that replaces it at runtime. Serialized on the player components so a replacement set in the
    /// inspector survives into a build and is re-applied on load.
    /// </summary>
    [System.Serializable]
    public class SpriteStudioCellmapTextureOverride
    {
        [Tooltip("Cellmap name (e.g. \"common\") or external-texture name (e.g. \"common.png\"). " +
                 "Use SpriteStudioPlayer.GetReplaceableTextureNames() to discover the replaceable names.")]
        public string CellmapName;

        [Tooltip("Replacement texture. Should share the original's size / atlas layout (UV rects come from " +
                 "the original cellmap's pixel space).")]
        public Texture2D Texture;
    }

    /// <summary>
    /// Per-player runtime texture replacement state (a Player-side override, NOT the Override Layer API):
    /// a cellmap-name → replacement-texture table.
    ///
    /// Two-layer design:
    ///   * The name-keyed source of truth lives on the owning component as a serialized list
    ///     (<see cref="SpriteStudioCellmapTextureOverride"/>), because names are resource-independent and
    ///     survive an asset swap.
    ///   * This class holds the derived, draw-time map keyed by <c>DrawBatch.TextureHash</c>. A cellmap name
    ///     resolves to more than one candidate hash (the cellmap's NameHash and ImagePathHash), an external
    ///     name to one; every candidate points at the same replacement so whichever hash a batch carries
    ///     resolves. Rebuild it from the name list whenever the list or the asset changes.
    ///
    /// The replacement is applied by cloning the resolved (asset-shared) material and swapping its main
    /// texture — never by mutating the shared material or via a MaterialPropertyBlock — so it stays
    /// per-player and SRP-Batcher friendly.
    /// </summary>
    internal sealed class SpriteStudioTextureOverride
    {
        private readonly Dictionary<uint, Texture2D> _byHash = new Dictionary<uint, Texture2D>();

        /// <summary>
        /// Bumped on every rebuild. The world player folds it into its material-list signature so a
        /// replacement change forces the per-submesh material list to be rebuilt even when the draw layout
        /// is otherwise identical.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>Whether any replacement is currently active (fast opt-out on the draw path).</summary>
        public bool HasAny => _byHash.Count > 0;

        /// <summary>Look up the replacement texture for a draw batch's texture hash.</summary>
        public bool TryGet(uint textureHash, out Texture2D texture) => _byHash.TryGetValue(textureHash, out texture);

        /// <summary>
        /// Rebuild the draw-time hash map from the name-keyed list, resolving each name against the asset's
        /// cellmap / external-texture tables. Entries with no texture or an unresolvable name are skipped.
        /// <see cref="Version"/> is bumped ONLY when the resolved map actually changes: the world player
        /// folds Version into its material-list signature, so bumping it on every OnValidate/OnEnable would
        /// force a full submesh material rebuild (destroying and recreating the AddOn / override clones) even
        /// when no replacement changed. Resolves into a temp first, then swaps only on a real difference.
        /// Not on the per-frame path (OnEnable / OnValidate / asset swap / SetCellmapTexture only), so the
        /// transient allocation does not touch the zero-alloc tick loop.
        /// </summary>
        public void Rebuild(SpriteStudioAnimeAsset asset, List<SpriteStudioCellmapTextureOverride> entries)
        {
            var next = new Dictionary<uint, Texture2D>();
            if (asset != null && entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e == null || e.Texture == null || string.IsNullOrEmpty(e.CellmapName)) continue;
                    if (!asset.TryGetCellmapHashes(e.CellmapName, out var hashes) || hashes == null) continue;
                    for (int h = 0; h < hashes.Length; h++)
                    {
                        if (hashes[h] != 0) next[hashes[h]] = e.Texture;
                    }
                }
            }

            if (MapsEqual(_byHash, next)) return;
            _byHash.Clear();
            foreach (var kv in next) _byHash[kv.Key] = kv.Value;
            Version++;
        }

        private static bool MapsEqual(Dictionary<uint, Texture2D> a, Dictionary<uint, Texture2D> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var v) || !ReferenceEquals(v, kv.Value)) return false;
            }
            return true;
        }

        // --- Shared name-keyed list management (used by both SpriteStudioPlayer and SpriteStudioGraphic so
        //     the add / replace / remove and lookup semantics stay identical across the two components). ---

        /// <summary>Set (add / replace) or, when <paramref name="texture"/> is null, remove the entry for a name.</summary>
        public static void SetEntry(List<SpriteStudioCellmapTextureOverride> list, string cellmapName, Texture2D texture)
        {
            if (list == null || string.IsNullOrEmpty(cellmapName)) return;
            int found = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].CellmapName == cellmapName) { found = i; break; }
            }
            if (texture != null)
            {
                if (found >= 0) list[found].Texture = texture;
                else list.Add(new SpriteStudioCellmapTextureOverride { CellmapName = cellmapName, Texture = texture });
            }
            else if (found >= 0)
            {
                list.RemoveAt(found);
            }
        }

        /// <summary>The replacement texture stored for a name, or null when none is set (never the original).</summary>
        public static Texture2D GetEntry(List<SpriteStudioCellmapTextureOverride> list, string cellmapName)
        {
            if (list == null || string.IsNullOrEmpty(cellmapName)) return null;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].CellmapName == cellmapName) return list[i].Texture;
            }
            return null;
        }
    }
}
