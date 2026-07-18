using System;
using System.Collections.Generic;
using UnityEngine;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Per-part custom shader / AddOn catalog: maps a SpriteStudio shader id (the string authored in
    /// the SpriteStudio editor, e.g. "ss-sepia") to a Unity Shader and its dispatch class.
    ///
    /// Resolution works as follows: the runtime returns a 32-bit id hash
    /// (FrameData PartAttributeShader.IdHash); this catalog resolves it by hashing each registered id
    /// name with the SDK's canonical string hash (ss_runtime_hash_string), so the hash always matches
    /// the value the converter baked into the .ssab — no per-asset baking and no hash reimplementation.
    ///
    /// The catalog is also the build-time anchor that keeps the AddOn shaders referenced (so they are
    /// not stripped). User custom shaders are first-class: add an entry whose IdName matches the id set
    /// on the part in the SpriteStudio editor and a Shader that follows the AddOn contract
    /// (see SpriteStudioAddOn.hlsl).
    /// </summary>
    [CreateAssetMenu(fileName = "SpriteStudioShaderCatalog", menuName = "SpriteStudio/Shader Catalog")]
    public class SpriteStudioShaderCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("SpriteStudio shader id (the string set on the part in the editor, e.g. \"ss-sepia\").")]
            public string IdName;

            [Tooltip("World (MeshRenderer) shader for this id. Must follow the AddOn contract (SpriteStudioAddOn.hlsl).")]
            public Shader Shader;

            [Tooltip("When true the part is drawn on the per-part path (its own material/draw). AddOns " +
                     "(blur/outline/sepia/...) are per-part; a plain custom shader that is not per-part " +
                     "folds into the shared default path.")]
            public bool IsPerPart = true;

            [Tooltip("Reserved for the Canvas (uGUI) AddOn path. Currently unused — leave empty (world-only).")]
            public Shader CanvasShader;
        }

        [Tooltip("Registered per-part shaders. The built-in AddOns ship pre-registered; add rows for custom shaders.")]
        public List<Entry> Entries = new List<Entry>();

        [NonSerialized] private Dictionary<uint, Entry> _byHash;

        /// <summary>
        /// Resolve a runtime shader id hash (FrameData PartAttributeShader.IdHash) to its world Shader
        /// and dispatch class. Returns false (caller folds to the default path) when the id is not
        /// registered or its Shader is unassigned.
        /// </summary>
        public bool TryResolve(uint idHash, out Shader shader, out bool isPerPart)
        {
            EnsureMap();
            if (_byHash.TryGetValue(idHash, out var e) && e.Shader != null)
            {
                shader = e.Shader;
                isPerPart = e.IsPerPart;
                return true;
            }
            shader = null;
            isPerPart = false;
            return false;
        }

        /// <summary>Look up an entry by its (string) id name. Editor/authoring convenience.</summary>
        public Entry FindByName(string idName)
        {
            if (Entries == null) return null;
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                if (e != null && e.IdName == idName) return e;
            }
            return null;
        }

        /// <summary>Drop the cached hash map; call after editing Entries so the next resolve rebuilds it.</summary>
        public void Invalidate() => _byHash = null;

        private void EnsureMap()
        {
            if (_byHash != null) return;
            _byHash = new Dictionary<uint, Entry>();
            if (Entries == null) return;
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                if (e == null || string.IsNullOrEmpty(e.IdName)) continue;
                _byHash[HashId(e.IdName)] = e; // last write wins on duplicate ids
            }
        }

        /// <summary>
        /// Hash a SpriteStudio id string with the SDK's canonical hash (matches the .ssab id_hash).
        /// Uses the native ss_runtime_hash_string so the algorithm stays in sync with the converter.
        /// Requires the native runtime to be loaded (true whenever a player is rendering).
        /// </summary>
        public static unsafe uint HashId(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0u;
            int n = System.Text.Encoding.UTF8.GetByteCount(s);
            byte[] buf = new byte[n + 1]; // trailing 0 acts as the null terminator
            System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, buf, 0);
            fixed (byte* p = buf)
            {
                return NativeMethods.ss_runtime_hash_string(p);
            }
        }
    }
}
