using UnityEngine;
using UnityEditor;
using System.IO;

namespace SpriteStudio.Editor
{
    public class SpriteStudioAssetPostprocessor : AssetPostprocessor
    {
        private static System.Collections.Generic.HashSet<string> delayedSsabChecks = new System.Collections.Generic.HashSet<string>();
        private static bool isDelayCallRegistered = false;
        // 同一内容 (mtime 一致) の .sspj を毎インポートで再変換しないためのセッション内ダーティチェック。
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
            foreach (string str in importedAssets)
            {
                if (str.EndsWith(".sspj"))
                {
                    // 非同期変換を開始する (完了時に SpriteStudioConversionRunner が Refresh する)
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
            }

            if (delayedSsabChecks.Count > 0 && !isDelayCallRegistered)
            {
                EditorApplication.delayCall += ProcessDelayedSsabs;
                isDelayCallRegistered = true;
            }

            // .ssab の移動/削除に追従して source map を再キー/prune する (stale エントリ防止)。
            // .sspj は Model B で外部任意パスのため Unity の move/delete イベントには現れない。
            bool sourceMapDirty = false;
            for (int i = 0; i < movedAssets.Length; i++)
            {
                if (movedAssets[i].EndsWith(".ssab") &&
                    SpriteStudioSourceMap.RenameSsab(movedFromAssetPaths[i], movedAssets[i], save: false))
                {
                    sourceMapDirty = true;
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

            // ダーティチェック: 同一内容 (mtime 一致) の .sspj をインポートのたびに再変換しない。
            // 無変更リインポートや変換完了後 Refresh での無駄な再変換 (連鎖) を防ぐ。mtime が
            // 変わっていれば変換する。取得失敗時は安全側で従来どおり変換する。
            try
            {
                var mtime = File.GetLastWriteTimeUtc(sspjAbsolutePath);
                if (convertedSspjMtime.TryGetValue(sspjAbsolutePath, out var prev) && prev == mtime)
                {
                    return false; // このセッションで既に最新を変換済み
                }
                convertedSspjMtime[sspjAbsolutePath] = mtime;
            }
            catch { /* mtime 取得不可なら従来どおり変換 */ }

            SpriteStudioImporterWindow.RunConvertDirect(sspjAbsolutePath);
            return true;
        }
    }
}
