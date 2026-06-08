using System.IO;
using UnityEditor;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Generates (or refreshes) the package-shipped default AddOn catalog at
    /// Runtime/Resources/SpriteStudioAddOnCatalog.asset from the built-in table in
    /// SpriteStudioAddOnRegistry. The asset both resolves the built-in AddOns at runtime and, by
    /// hard-referencing their shaders, anchors them against build stripping (a Resources asset and
    /// everything it references is always included in the player build).
    ///
    /// Run this after adding a new built-in AddOn (one entry in SpriteStudioAddOnRegistry) so the asset
    /// stays in sync, then commit the regenerated asset. TestStub is excluded (dev-only).
    /// </summary>
    public static class SpriteStudioAddOnCatalogBuilder
    {
        [MenuItem("Window/SpriteStudio/Create or Update Default AddOn Catalog")]
        public static void Build()
        {
            // Locate the package Runtime folder via the catalog script's asset path (robust across the
            // package name / install location).
            var tmp = ScriptableObject.CreateInstance<SpriteStudioShaderCatalog>();
            var ms = MonoScript.FromScriptableObject(tmp);
            string scriptPath = AssetDatabase.GetAssetPath(ms);
            Object.DestroyImmediate(tmp);
            if (string.IsNullOrEmpty(scriptPath))
            {
                Debug.LogError("[SS-AddOnCatalog] Could not locate SpriteStudioShaderCatalog's script path.");
                return;
            }

            string runtimeDir = Path.GetDirectoryName(scriptPath).Replace('\\', '/'); // .../Runtime
            string resourcesDir = runtimeDir + "/Resources";
            if (!AssetDatabase.IsValidFolder(resourcesDir))
                AssetDatabase.CreateFolder(runtimeDir, "Resources");

            string assetPath = resourcesDir + "/" + SpriteStudioAddOnRegistry.DefaultCatalogResourceName + ".asset";
            var catalog = AssetDatabase.LoadAssetAtPath<SpriteStudioShaderCatalog>(assetPath);
            bool created = catalog == null;
            if (created) catalog = ScriptableObject.CreateInstance<SpriteStudioShaderCatalog>();

            catalog.Entries.Clear();
            int added = 0, missing = 0;
            for (int i = 0; i < SpriteStudioAddOnRegistry.BuiltinCount; i++)
            {
                SpriteStudioAddOnRegistry.GetBuiltin(i, out string idName, out string shaderName, out bool isPerPart);
                if (idName == "TestStub") continue; // dev-only; not shipped in the strip anchor
                var shader = Shader.Find(shaderName);
                if (shader == null) { Debug.LogWarning($"[SS-AddOnCatalog] Shader not found: '{shaderName}' (id '{idName}')."); missing++; }
                catalog.Entries.Add(new SpriteStudioShaderCatalog.Entry { IdName = idName, Shader = shader, IsPerPart = isPerPart });
                added++;
            }
            catalog.Invalidate();

            if (created) AssetDatabase.CreateAsset(catalog, assetPath);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[SS-AddOnCatalog] {(created ? "Created" : "Updated")} '{assetPath}': {added} entries, {missing} missing shader(s). Commit this asset (it anchors the AddOn shaders into builds).");
        }
    }
}
