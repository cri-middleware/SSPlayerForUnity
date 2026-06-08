using System.Text;
using UnityEditor;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Editor utility that verifies SpriteStudioShaderCatalog.HashId (= the native ss_runtime_hash_string)
    /// reproduces the hashes the converter baked into the .ssab. Run it from the Project view on a
    /// selected SpriteStudioAnimeAsset: it hashes each animation name and compares to Animation.NameHash.
    ///
    /// A full match confirms ss_runtime_hash_string is the converter's canonical name hash, so the
    /// SpriteStudioShaderCatalog can resolve per-part shader ids by hashing the catalog's id names
    /// (every name_hash in the .ssab — including the shader id_hash — uses the same hash). A mismatch
    /// means we must fall back to per-asset baking from the ssab's PartAttributeShader.Id strings.
    /// </summary>
    public static class SpriteStudioHashVerifier
    {
        private const string MenuPath = "Assets/SpriteStudio/Verify Hash Function (ss_runtime_hash_string)";

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
    }
}
