using UnityEngine;
using UnityEditor;
using System.IO;

namespace SpriteStudio.Editor
{
    public class SpriteStudioAssetPostprocessor : AssetPostprocessor
    {
        private static System.Collections.Generic.HashSet<string> delayedSsabChecks = new System.Collections.Generic.HashSet<string>();
        private static bool isDelayCallRegistered = false;
        // Session-scoped dirty check so an unchanged .sspj (matching mtime) is not reconverted on every import.
        private static readonly System.Collections.Generic.Dictionary<string, System.DateTime> convertedSspjMtime
            = new System.Collections.Generic.Dictionary<string, System.DateTime>();

        void OnPreprocessTexture()
        {
            if (assetImporter.importSettingsMissing)
            {
                string dir = Path.GetDirectoryName(assetPath);
                if (Directory.Exists(dir))
                {
                    bool isSpriteStudioTexture = Directory.GetFiles(dir, "*.sspj").Length > 0 || Directory.GetFiles(dir, "*.ssab").Length > 0;
                    if (isSpriteStudioTexture)
                    {
                        var textureImporter = (TextureImporter)assetImporter;
                        // Avoid compression artifacts in 2D animation
                        textureImporter.textureCompression = TextureImporterCompression.Uncompressed;
                        // SpriteStudio mostly uses 1:1 pixel rendering, no mipmaps
                        textureImporter.mipmapEnabled = false;
                        // Prevent edge bleeding
                        textureImporter.wrapMode = TextureWrapMode.Clamp;
                        // Set explicitly instead of relying on the importer's default
                        textureImporter.filterMode = FilterMode.Bilinear;
                        // We use custom meshes, not standard Unity sprites
                        textureImporter.spriteImportMode = SpriteImportMode.None;
                        // For standard Straight Alpha, this prevents black fringes around edges
                        textureImporter.alphaIsTransparency = true;
                        // Prevent large textures from being downscaled (e.g. 4096x4096)
                        textureImporter.maxTextureSize = 8192;
                        // Prevent NPOT textures from being scaled to power of two
                        textureImporter.npotScale = TextureImporterNPOTScale.None;
                    }
                }
            }
        }

        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            var pluginsToConfigure = new System.Collections.Generic.HashSet<string>();

            foreach (string str in importedAssets)
            {
                if (str.EndsWith(".sspj"))
                {
                    // Start async conversion (SpriteStudioConversionRunner refreshes on completion)
                    ConvertSspj(str);
                }
                else if (str.EndsWith(".ssab"))
                {
                    delayedSsabChecks.Add(str.Replace("\\", "/"));
                }
                else if (str.EndsWith(".png") || str.EndsWith(".jpg") || str.EndsWith(".tga"))
                {
                    string dir = Path.GetDirectoryName(str);
                    if (Directory.Exists(dir))
                    {
                        string[] ssabFiles = Directory.GetFiles(dir, "*.ssab");
                        foreach (var ssab in ssabFiles)
                        {
                            delayedSsabChecks.Add(ssab.Replace("\\", "/"));
                        }
                    }
                }
                else if (str.Contains("Plugins") && (str.Contains("ssruntime") || str.Contains("ssconverter")))
                {
                    pluginsToConfigure.Add(GetPluginRoot(str));
                }
            }

            if (delayedSsabChecks.Count > 0 && !isDelayCallRegistered)
            {
                EditorApplication.delayCall += ProcessDelayedSsabs;
                isDelayCallRegistered = true;
            }

            // Follow .ssab moves/deletes to re-key/prune the source map (prevents stale entries).
            bool sourceMapDirty = false;
            for (int i = 0; i < movedAssets.Length; i++)
            {
                if (movedAssets[i].EndsWith(".ssab") &&
                    SpriteStudioSourceMap.RenameSsab(movedFromAssetPaths[i], movedAssets[i], save: false))
                {
                    sourceMapDirty = true;
                }
                else if (movedAssets[i].Contains("Plugins") && (movedAssets[i].Contains("ssruntime") || movedAssets[i].Contains("ssconverter")))
                {
                    pluginsToConfigure.Add(GetPluginRoot(movedAssets[i]));
                }
            }
            foreach (string del in deletedAssets)
            {
                if (del.EndsWith(".ssab") && SpriteStudioSourceMap.RemoveSsab(del, save: false))
                {
                    sourceMapDirty = true;
                }
            }
            if (sourceMapDirty) SpriteStudioSourceMap.SaveMap();

            foreach (var plugin in pluginsToConfigure)
            {
                ConfigurePluginPlatform(plugin);
            }
        }

        private static string GetPluginRoot(string path)
        {
            // If the path is inside a .xcframework or .bundle, find the root directory
            int xcIndex = path.IndexOf(".xcframework", System.StringComparison.OrdinalIgnoreCase);
            if (xcIndex != -1) return path.Substring(0, xcIndex + 12);
            
            int bundleIndex = path.IndexOf(".bundle", System.StringComparison.OrdinalIgnoreCase);
            if (bundleIndex != -1) return path.Substring(0, bundleIndex + 7);
            
            return path;
        }

        private static void ConfigurePluginPlatform(string assetPath)
        {
            var pluginImporter = AssetImporter.GetAtPath(assetPath) as PluginImporter;
            if (pluginImporter == null) return;

            string lowerPath = assetPath.ToLowerInvariant();
            if (!lowerPath.Contains("runtime/plugins/") && !lowerPath.Contains("editor/plugins/")) return;

            bool isEditorPlugin = lowerPath.Contains("editor/plugins/");
            bool changed = false;

            if (pluginImporter.GetCompatibleWithAnyPlatform())
            {
                pluginImporter.SetCompatibleWithAnyPlatform(false);
                changed = true;
            }

            if (lowerPath.Contains("/ios/"))
            {
                if (pluginImporter.GetCompatibleWithEditor()) { pluginImporter.SetCompatibleWithEditor(false); changed = true; }
                if (!pluginImporter.GetCompatibleWithPlatform(BuildTarget.iOS)) { pluginImporter.SetCompatibleWithPlatform(BuildTarget.iOS, true); changed = true; }
            }
            else if (lowerPath.Contains("/android/"))
            {
                if (pluginImporter.GetCompatibleWithEditor()) { pluginImporter.SetCompatibleWithEditor(false); changed = true; }
                if (!pluginImporter.GetCompatibleWithPlatform(BuildTarget.Android)) { pluginImporter.SetCompatibleWithPlatform(BuildTarget.Android, true); changed = true; }
                
                if (lowerPath.Contains("arm64-v8a") && pluginImporter.GetPlatformData(BuildTarget.Android, "CPU") != "ARM64")
                {
                    pluginImporter.SetPlatformData(BuildTarget.Android, "CPU", "ARM64"); changed = true;
                }
                else if (lowerPath.Contains("armeabi-v7a") && pluginImporter.GetPlatformData(BuildTarget.Android, "CPU") != "ARMv7")
                {
                    pluginImporter.SetPlatformData(BuildTarget.Android, "CPU", "ARMv7"); changed = true;
                }
                else if (lowerPath.Contains("x86_64") && pluginImporter.GetPlatformData(BuildTarget.Android, "CPU") != "X86_64")
                {
                    pluginImporter.SetPlatformData(BuildTarget.Android, "CPU", "X86_64"); changed = true;
                }
            }
            else if (lowerPath.Contains("/webgl/"))
            {
                if (pluginImporter.GetCompatibleWithEditor()) { pluginImporter.SetCompatibleWithEditor(false); changed = true; }
                if (!pluginImporter.GetCompatibleWithPlatform(BuildTarget.WebGL)) { pluginImporter.SetCompatibleWithPlatform(BuildTarget.WebGL, true); changed = true; }
            }
            else if (lowerPath.Contains("/macos/"))
            {
                if (!pluginImporter.GetCompatibleWithEditor()) { pluginImporter.SetCompatibleWithEditor(true); changed = true; }
                if (pluginImporter.GetEditorData("OS") != "OSX") { pluginImporter.SetEditorData("OS", "OSX"); changed = true; }
                if (pluginImporter.GetEditorData("CPU") != "AnyCPU") { pluginImporter.SetEditorData("CPU", "AnyCPU"); changed = true; }
                if (!isEditorPlugin && !pluginImporter.GetCompatibleWithPlatform(BuildTarget.StandaloneOSX))
                {
                    pluginImporter.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true); changed = true;
                }
            }
            else if (lowerPath.Contains("/windows/"))
            {
                if (!pluginImporter.GetCompatibleWithEditor()) { pluginImporter.SetCompatibleWithEditor(true); changed = true; }
                if (pluginImporter.GetEditorData("OS") != "Windows") { pluginImporter.SetEditorData("OS", "Windows"); changed = true; }
                if (pluginImporter.GetEditorData("CPU") != "AnyCPU") { pluginImporter.SetEditorData("CPU", "AnyCPU"); changed = true; }
                if (!isEditorPlugin && (!pluginImporter.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64) || !pluginImporter.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows)))
                {
                    pluginImporter.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
                    pluginImporter.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, true);
                    changed = true;
                }
            }
            else if (lowerPath.Contains("/linux/"))
            {
                if (!pluginImporter.GetCompatibleWithEditor()) { pluginImporter.SetCompatibleWithEditor(true); changed = true; }
                if (pluginImporter.GetEditorData("OS") != "Linux") { pluginImporter.SetEditorData("OS", "Linux"); changed = true; }
                if (pluginImporter.GetEditorData("CPU") != "AnyCPU") { pluginImporter.SetEditorData("CPU", "AnyCPU"); changed = true; }
                if (!isEditorPlugin && !pluginImporter.GetCompatibleWithPlatform(BuildTarget.StandaloneLinux64))
                {
                    pluginImporter.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, true); changed = true;
                }
            }

            if (changed)
            {
                pluginImporter.SaveAndReimport();
            }
        }

        private static void ProcessDelayedSsabs()
        {
            isDelayCallRegistered = false;
            var ssabsToReimport = new System.Collections.Generic.List<string>();

            foreach (var ssab in delayedSsabChecks)
            {
                if (EnsureExternalMaterials(ssab))
                {
                    ssabsToReimport.Add(ssab);
                }
            }
            delayedSsabChecks.Clear();

            foreach (var ssab in ssabsToReimport)
            {
                AssetDatabase.ImportAsset(ssab, ImportAssetOptions.ForceUpdate);
            }
        }

        /// <summary>
        /// Auto-generates a single shared material (.mat) next to each texture referenced by the .ssab.
        /// Returns true if anything was created (so the caller can reimport the .ssab to pick it up).
        /// Texture resolution is shared with <see cref="SpriteStudioAnimeAssetBuilder.EnumerateTextures"/>.
        /// </summary>
        private static bool EnsureExternalMaterials(string ssabPath)
        {
            // By the time the delayed callback (delayCall) runs, the .ssab may have been deleted/moved
            // (asset deletion, rename, test teardown, etc.). If it no longer exists, do nothing.
            if (string.IsNullOrEmpty(ssabPath) || !File.Exists(ssabPath)) return false;

            var importer = AssetImporter.GetAtPath(ssabPath) as SpriteStudioSsabImporter;
            if (importer != null && !importer.GenerateMaterials) return false;

            var ssabData = File.ReadAllBytes(ssabPath);
            string baseDir = Path.GetDirectoryName(ssabPath);
            var textures = SpriteStudioAnimeAssetBuilder.EnumerateTextures(ssabData, baseDir);

            Shader shader = null;
            bool createdAny = false;

            foreach (var t in textures)
            {
                if (t.Texture == null) continue;
                var existing = AssetDatabase.LoadAssetAtPath<Material>(t.MatPath);
                if (existing != null)
                {
                    if (RelinkBrokenTexture(existing, t.Texture))
                    {
                        EditorUtility.SetDirty(existing);
                        AssetDatabase.SaveAssetIfDirty(existing);
                    }
                    continue;
                }

                if (shader == null)
                {
                    shader = SpriteStudioAnimeAssetBuilder.ResolveShader(importer != null ? importer.OverrideShader : null);
                }
                if (shader == null) continue;

                var mat = new Material(shader) { name = t.Texture.name + "_Material", mainTexture = t.Texture };
                AssetDatabase.CreateAsset(mat, t.MatPath);
                createdAny = true;
            }

            return createdAny;
        }

        // A kept .mat whose texture reference broke (its PNG came back with a new GUID) would draw every part
        // white with no error. Only the missing reference is restored: the shader and properties stay the user's.
        internal static bool RelinkBrokenTexture(Material mat, Texture texture)
        {
            if (!mat.HasProperty("_MainTex") || mat.mainTexture != null) return false;
            mat.mainTexture = texture;
            return true;
        }

        [InitializeOnLoadMethod]
        private static void WatchConversionResults()
        {
            SpriteStudioConversionRunner.JobFinished += OnConversionFinished;
        }

        /// <summary>
        /// The mtime recorded before a conversion starts is what keeps an unchanged .sspj from being
        /// reconverted on every import. A conversion that failed converted nothing, so its record goes:
        /// otherwise the file is skipped for the rest of the session and reimporting it -- the obvious
        /// thing to try after fixing whatever the converter complained about -- does nothing at all.
        /// </summary>
        private static void OnConversionFinished(string sspjPath, bool succeeded)
        {
            if (succeeded || string.IsNullOrEmpty(sspjPath)) return;
            convertedSspjMtime.Remove(Path.GetFullPath(sspjPath));
        }

        private static bool ConvertSspj(string sspjPath)
        {
            string sspjAbsolutePath = Path.GetFullPath(sspjPath);

            // Dirty check: don't reconvert an unchanged .sspj (matching mtime) on every import.
            // Prevents wasteful reconversion (chaining) on no-op reimports or on the Refresh after a
            // conversion completes. If the mtime changed, convert. If it can't be read, convert to be safe.
            try
            {
                var mtime = File.GetLastWriteTimeUtc(sspjAbsolutePath);
                if (convertedSspjMtime.TryGetValue(sspjAbsolutePath, out var prev) && prev == mtime)
                {
                    return false; // Already converted the latest version this session
                }
                convertedSspjMtime[sspjAbsolutePath] = mtime;
            }
            catch { /* If the mtime can't be read, convert */ }

            SpriteStudioImporterWindow.RunConvertDirect(sspjAbsolutePath);
            return true;
        }
    }
}
