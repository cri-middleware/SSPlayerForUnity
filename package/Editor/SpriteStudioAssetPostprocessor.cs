using UnityEngine;
using UnityEditor;
using System.IO;

namespace SpriteStudio.Editor
{
    public class SpriteStudioAssetPostprocessor : AssetPostprocessor
    {
        private static System.Collections.Generic.HashSet<string> delayedSsabChecks = new System.Collections.Generic.HashSet<string>();
        private static bool isDelayCallRegistered = false;

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
                        // We use custom meshes, not standard Unity sprites
                        textureImporter.spriteImportMode = SpriteImportMode.None;
                        // For standard Straight Alpha, this prevents black fringes around edges
                        textureImporter.alphaIsTransparency = true;
                    }
                }
            }
        }

        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            bool hasConversion = false;

            foreach (string str in importedAssets)
            {
                if (str.EndsWith(".sspj"))
                {
                    hasConversion |= ConvertSspj(str);
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
            }

            if (hasConversion)
            {
                // 新しく書き出された .ssab やテクスチャを Unity に認識させる
                AssetDatabase.Refresh();
            }

            if (delayedSsabChecks.Count > 0 && !isDelayCallRegistered)
            {
                EditorApplication.delayCall += ProcessDelayedSsabs;
                isDelayCallRegistered = true;
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
        /// .ssab が参照するテクスチャに対応する共有マテリアル(.mat)を、隣接位置に1つだけ自動生成する。
        /// 生成したものがあれば true を返す (= 呼び出し側が .ssab を再インポートして参照させる)。
        /// テクスチャ解決は <see cref="SpriteStudioAnimeAssetBuilder.EnumerateTextures"/> と共有する。
        /// </summary>
        private static bool EnsureExternalMaterials(string ssabPath)
        {
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
                if (AssetDatabase.LoadAssetAtPath<Material>(t.MatPath) != null) continue;

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

        private static bool ConvertSspj(string sspjPath)
        {
            string sspjAbsolutePath = Path.GetFullPath(sspjPath);
            SpriteStudioImporterWindow.RunConvertDirect(sspjAbsolutePath);
            return true;
        }
    }
}
