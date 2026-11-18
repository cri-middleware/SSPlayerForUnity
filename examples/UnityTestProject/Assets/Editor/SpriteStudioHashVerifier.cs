using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudioDev
{
    /// <summary>
    /// Editor utility that verifies SpriteStudioShaderCatalog.HashId (= the native ss_runtime_hash_string)
    /// reproduces the hashes the converter baked into the .ssab. Run it from the Project view on a
    /// selected SpriteStudioAnimeAsset: it hashes each animation name and compares to Animation.NameHash.
    ///
    /// A full match confirms ss_runtime_hash_string is the converter's canonical name hash, so the
    /// SpriteStudioShaderCatalog can resolve per-part shader ids by hashing the catalog's id names
    /// (every name_hash in the .ssab — including the shader id_hash — uses the same hash).
    /// Editor-only dev tool; it lives in the example project and is never part of the package.
    /// </summary>
    public static class SpriteStudioHashVerifier
    {
        private const string MenuPath = "Assets/SpriteStudio/Dev/Verify Hash Function (ss_runtime_hash_string)";

        [MenuItem(MenuPath, true)]
        private static bool ValidateVerify() => Selection.activeObject is SpriteStudioAnimeAsset;

        [MenuItem(MenuPath, false, 2000)]
        private static void Verify()
        {
            var asset = Selection.activeObject as SpriteStudioAnimeAsset;
            if (asset == null)
            {
                Debug.LogWarning("[SS-HashVerify] Select a SpriteStudioAnimeAsset in the Project view first.");
                return;
            }
            if (asset.SsabData == null || asset.SsabData.Length == 0)
            {
                Debug.LogWarning($"[SS-HashVerify] '{asset.name}' has no .ssab data.");
                return;
            }

            var bb = new Google.FlatBuffers.ByteBuffer(asset.SsabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);

            int total = 0, matched = 0;
            var sb = new StringBuilder();

            int animLen = ssab.AnimationsLength;
            for (int i = 0; i < animLen; i++)
            {
                var a = ssab.Animations(i);
                if (!a.HasValue) continue;
                string name = a.Value.Name;
                if (string.IsNullOrEmpty(name)) continue;

                uint expected = a.Value.NameHash;
                uint got = SpriteStudioShaderCatalog.HashId(name);
                total++;
                bool ok = got == expected;
                if (ok) matched++;
                sb.AppendLine($"  anim \"{name}\": ssab=0x{expected:X8} HashId=0x{got:X8} {(ok ? "OK" : "MISMATCH")}");
            }

            if (total == 0)
            {
                Debug.LogWarning($"[SS-HashVerify] '{asset.name}': no named animations to check.");
                return;
            }

            string header = $"[SS-HashVerify] '{asset.name}': {matched}/{total} animation name hashes match ss_runtime_hash_string.";
            if (matched == total)
            {
                Debug.Log(header +
                    "\nCanonical hash CONFIRMED -> SpriteStudioShaderCatalog (approach A: hash the id name) is safe.\n" + sb);
            }
            else
            {
                Debug.LogError(header +
                    "\nMISMATCH -> ss_runtime_hash_string is NOT the converter's name hash. " +
                    "Fall back to per-asset baking from PartAttributeShader.Id strings (approach B).\n" + sb);
            }
        }

        private const string DumpMenuPath = "Assets/SpriteStudio/Dev/Dump Per-Part Shader Ids";

        [MenuItem(DumpMenuPath, true)]
        private static bool ValidateDump() => Selection.activeObject is SpriteStudioAnimeAsset;

        // Enumerate every per-part shader id used by the selected asset (initial + animated attributes)
        // and log each unique (id string, id hash). Use this to learn exactly which id names a part uses
        // so the AddOn catalog / built-in registry can map them. An id printed here that resolves to a
        // registered, is-per-part AddOn is what the dispatch will draw with.
        [MenuItem(DumpMenuPath, false, 2001)]
        private static void DumpShaderIds()
        {
            var asset = Selection.activeObject as SpriteStudioAnimeAsset;
            if (asset == null) { Debug.LogWarning("[SS-ShaderDump] Select a SpriteStudioAnimeAsset first."); return; }
            if (asset.SsabData == null || asset.SsabData.Length == 0) { Debug.LogWarning($"[SS-ShaderDump] '{asset.name}' has no .ssab data."); return; }

            var bb = new Google.FlatBuffers.ByteBuffer(asset.SsabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);

            var seen = new Dictionary<uint, string>();
            int animLen = ssab.AnimationsLength;
            for (int a = 0; a < animLen; a++)
            {
                var animOpt = ssab.Animations(a);
                if (!animOpt.HasValue) continue;
                var anim = animOpt.Value;

                int ipaLen = anim.PartsInitialAttributesLength;
                for (int j = 0; j < ipaLen; j++)
                {
                    var ipa = anim.PartsInitialAttributes(j);
                    if (!ipa.HasValue) continue;
                    var sh = ipa.Value.Shader;
                    if (sh.HasValue && !string.IsNullOrEmpty(sh.Value.Id)) seen[sh.Value.IdHash] = sh.Value.Id;
                }
            }

            // Every keyframed shader value of the pack, each stored once.
            var keyframes = ssab.Keyframes;
            int shLen = keyframes.HasValue ? keyframes.Value.ShadersLength : 0;
            for (int k = 0; k < shLen; k++)
            {
                var sh = keyframes.Value.Shaders(k);
                if (sh.HasValue && !string.IsNullOrEmpty(sh.Value.Id)) seen[sh.Value.IdHash] = sh.Value.Id;
            }

            if (seen.Count == 0)
            {
                Debug.Log($"[SS-ShaderDump] '{asset.name}': no per-part shader attributes found (this asset has no AddOn/custom-shader parts).");
                return;
            }

            var sb = new StringBuilder($"[SS-ShaderDump] '{asset.name}': {seen.Count} per-part shader id(s) used:\n");
            foreach (var kv in seen)
                sb.AppendLine($"  id=\"{kv.Value}\"  hash=0x{kv.Key:X8}  (register this IdName in the catalog, isPerPart=true)");
            Debug.Log(sb.ToString());
        }
    }
}
