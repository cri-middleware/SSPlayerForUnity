using System.Collections.Generic;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Built-in per-part AddOn registry: the SpriteStudio standard AddOn shader ids (ss-sepia,
    /// ss-outline, ...) hardcoded in C# as a built-in embedded shader set so they dispatch with zero
    /// project setup. The optional user-authored <see cref="SpriteStudioShaderCatalog"/>
    /// is consulted first (so a project can override or add custom shaders); the built-in table is the
    /// fallback.
    ///
    /// Only AddOns whose shader has actually been ported are listed here; ids not in the table (nor in
    /// the user catalog) fold to the shared default path. The shader is located by name with Shader.Find
    /// and cached. NOTE: in a player build, Shader.Find only finds shaders kept in the build — the AddOn
    /// shaders still need a build-inclusion anchor (Always Included Shaders or a referencing asset);
    /// that is a separate packaging step and does not affect the Editor.
    /// </summary>
    public static class SpriteStudioAddOnRegistry
    {
        private readonly struct Builtin
        {
            public readonly string IdName;
            public readonly string ShaderName;
            public readonly bool IsPerPart;
            public Builtin(string idName, string shaderName, bool isPerPart)
            {
                IdName = idName; ShaderName = shaderName; IsPerPart = isPerPart;
            }
        }

        // Add an entry here when an AddOn shader is ported (id string must match the SpriteStudio editor).
        private static readonly Builtin[] s_builtins =
        {
            new Builtin("ss-sepia", "SpriteStudio/AddOn/Sepia", true),
            new Builtin("ss-hsb", "SpriteStudio/AddOn/Hsb", true),
            new Builtin("ss-step", "SpriteStudio/AddOn/Step", true),
            new Builtin("ss-bmask", "SpriteStudio/AddOn/Bmask", true),
            new Builtin("ss-outline", "SpriteStudio/AddOn/Outline", true),
            new Builtin("ss-blur", "SpriteStudio/AddOn/Blur", true),
            new Builtin("ss-pix", "SpriteStudio/AddOn/Pix", true),
            new Builtin("ss-noise", "SpriteStudio/AddOn/Noise", true),
            new Builtin("ss-wave", "SpriteStudio/AddOn/Wave", true),
            new Builtin("ss-move", "SpriteStudio/AddOn/Move", true),
            new Builtin("ss-scatter", "SpriteStudio/AddOn/Scatter", true),
            new Builtin("ss-circle", "SpriteStudio/AddOn/Circle", true), // uses _SsCellRect
            new Builtin("ss-spot", "SpriteStudio/AddOn/Spot", true),     // uses _SsCellRect
            new Builtin("TestStub", "SpriteStudio/AddOn/TestStub", true), // dev dispatch check
            // All 13 SS6 standard AddOns are ported. Custom user shaders go through SpriteStudioShaderCatalog.
        };

        // Built-in table accessor for the editor that generates the default catalog .asset (Resources).
        // Keeping one source (s_builtins) -> the generated .asset can never drift from it.
        public static int BuiltinCount => s_builtins.Length;
        public static void GetBuiltin(int i, out string idName, out string shaderName, out bool isPerPart)
        {
            var b = s_builtins[i];
            idName = b.IdName; shaderName = b.ShaderName; isPerPart = b.IsPerPart;
        }

        private struct Entry
        {
            public string ShaderName;
            public bool IsPerPart;
            public Shader Shader;
            public bool ShaderResolved;
        }

        private static Dictionary<uint, Entry> s_byHash;

        private static void EnsureMap()
        {
            if (s_byHash != null) return;
            s_byHash = new Dictionary<uint, Entry>(s_builtins.Length);
            for (int i = 0; i < s_builtins.Length; i++)
            {
                var b = s_builtins[i];
                uint h = SpriteStudioShaderCatalog.HashId(b.IdName);
                s_byHash[h] = new Entry { ShaderName = b.ShaderName, IsPerPart = b.IsPerPart };
            }
        }

        // The package-shipped default catalog (Resources/SpriteStudioAddOnCatalog.asset). It both resolves
        // the built-in AddOns AND, by hard-referencing their shaders, anchors them against build stripping
        // (a Resources asset and everything it references is always included in the player). Loaded once;
        // null in the editor before the asset is generated -> resolution falls back to the C# table below.
        public const string DefaultCatalogResourceName = "SpriteStudioAddOnCatalog";
        private static SpriteStudioShaderCatalog s_defaultCatalog;
        private static bool s_defaultCatalogTried;
        private static SpriteStudioShaderCatalog DefaultCatalog
        {
            get
            {
                if (!s_defaultCatalogTried)
                {
                    s_defaultCatalog = Resources.Load<SpriteStudioShaderCatalog>(DefaultCatalogResourceName);
                    s_defaultCatalogTried = true;
                }
                return s_defaultCatalog;
            }
        }

        /// <summary>
        /// Lightweight resolve for the geometry-builder carve decision: does this shader id hash map to
        /// a registered per-part AddOn? Consults the user catalog first, then the built-in table. No
        /// Shader.Find (so it stays cheap on the per-frame build path).
        /// </summary>
        public static bool TryGetIsPerPart(uint idHash, SpriteStudioShaderCatalog userCatalog, out bool isPerPart)
        {
            if (userCatalog != null && userCatalog.TryResolve(idHash, out _, out isPerPart)) return true;
            var def = DefaultCatalog;
            if (def != null && def.TryResolve(idHash, out _, out isPerPart)) return true;
            EnsureMap();
            if (s_byHash.TryGetValue(idHash, out var e)) { isPerPart = e.IsPerPart; return true; }
            isPerPart = false;
            return false;
        }

        /// <summary>
        /// Full resolve for material assignment: the AddOn Shader (Shader.Find, cached) and its dispatch
        /// class. User catalog first, then the built-in table. Returns false when unresolved or the
        /// shader cannot be found (the caller then folds to the default material).
        /// </summary>
        public static bool TryResolveShader(uint idHash, SpriteStudioShaderCatalog userCatalog, out Shader shader, out bool isPerPart)
        {
            if (userCatalog != null && userCatalog.TryResolve(idHash, out shader, out isPerPart)) return true;
            var def = DefaultCatalog;
            if (def != null && def.TryResolve(idHash, out shader, out isPerPart)) return true;
            EnsureMap();
            if (s_byHash.TryGetValue(idHash, out var e))
            {
                if (!e.ShaderResolved)
                {
                    e.Shader = Shader.Find(e.ShaderName);
                    e.ShaderResolved = true;
                    s_byHash[idHash] = e; // cache the resolved shader
                }
                shader = e.Shader;
                isPerPart = e.IsPerPart;
                return shader != null;
            }
            shader = null;
            isPerPart = false;
            return false;
        }
    }
}
