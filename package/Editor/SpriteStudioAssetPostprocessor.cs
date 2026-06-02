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

        private static bool EnsureExternalMaterials(string ssabPath)
        {
            bool createdAny = false;
            var ssabData = File.ReadAllBytes(ssabPath);
            var bb = new Google.FlatBuffers.ByteBuffer(ssabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
            string baseDir = Path.GetDirectoryName(ssabPath);

            var seenTexPaths = new System.Collections.Generic.HashSet<string>();

            int cellmapLen = ssab.CellmapsLength;
            for (int i = 0; i < cellmapLen; i++)
            {
                var cm = ssab.Cellmaps(i);
                if (cm.HasValue)
                {
                    createdAny |= CreateMaterialIfMissing(baseDir, cm.Value.ImagePath, seenTexPaths);
                }
            }

            int extLen = ssab.ExternalTexturesLength;
            for (int i = 0; i < extLen; i++)
            {
                var extTex = ssab.ExternalTextures(i);
                if (extTex.HasValue)
                {
                    createdAny |= CreateMaterialIfMissing(baseDir, extTex.Value.Name, seenTexPaths);
                }
            }
            return createdAny;
        }

        private static bool CreateMaterialIfMissing(string baseDir, string texName, System.Collections.Generic.HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(texName)) return false;

            string texPath = Path.Combine(baseDir, texName).Replace("\\", "/");

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex == null)
            {
                string altPath = Path.Combine(baseDir, Path.GetFileName(texName)).Replace("\\", "/");
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>(altPath);
                if (tex != null) texPath = altPath;
            }

            if (tex == null) return false;
            if (!seen.Add(texPath)) return false;

            string matPath = Path.ChangeExtension(texPath, ".mat");
            var existingMat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (existingMat == null)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.spritestudio.ssruntime/Runtime/Shaders/SpriteStudioDefault.shader");
                if (shader == null)
                {
                    shader = Shader.Find("SpriteStudio/Default");
                }
                var mat = new Material(shader);
                mat.name = tex.name + "_Material";
                mat.mainTexture = tex;
                AssetDatabase.CreateAsset(mat, matPath);
                return true;
            }
            return false;
        }

        private static bool ConvertSspj(string sspjPath)
        {
            string sspjAbsolutePath = Path.GetFullPath(sspjPath);
            string sspjName = Path.GetFileNameWithoutExtension(sspjPath);
            // .sspj と同じ階層を出力先ベースとする
            string baseOutputDir = Path.GetDirectoryName(sspjAbsolutePath);
            string finalOutputDir = Path.Combine(baseOutputDir, sspjName);

            if (!Directory.Exists(finalOutputDir))
            {
                Directory.CreateDirectory(finalOutputDir);
            }

            Debug.Log($"[SSPlayer] Converting: {sspjPath} to {finalOutputDir}");

            unsafe
            {
                var context = SpriteStudio.SsConverter.NativeMethods.ss_converter_create();
                
                var sspjBytes = System.Text.Encoding.UTF8.GetBytes(sspjAbsolutePath + "\0");
                var outDirBytes = System.Text.Encoding.UTF8.GetBytes(finalOutputDir + "\0");

                fixed (byte* sspjPtr = sspjBytes)
                fixed (byte* outDirPtr = outDirBytes)
                {
                    SpriteStudio.SsConverter.NativeMethods.ss_converter_convert(context, sspjPtr, outDirPtr, null);
                }

                while (!SpriteStudio.SsConverter.NativeMethods.ss_converter_is_finished(context))
                {
                    System.Threading.Thread.Sleep(10);
                }

                var result = SpriteStudio.SsConverter.NativeMethods.ss_converter_get_result(context);
                if (result != SpriteStudio.SsConverter.CConverterError.Success)
                {
                    byte* errData;
                    nuint errLen;
                    SpriteStudio.SsConverter.NativeMethods.ss_converter_get_error(context, &errData, &errLen);
                    if (errData != null && errLen > 0)
                    {
                        var errStr = System.Text.Encoding.UTF8.GetString(errData, (int)errLen);
                        Debug.LogError($"[SSPlayer] Converter Error: {errStr}");
                    }
                    else
                    {
                        Debug.LogError($"[SSPlayer] Converter Error: {result}");
                    }
                }
                else
                {
                    Debug.Log($"[SSPlayer] Convert Success: {sspjAbsolutePath}");
                }

                SpriteStudio.SsConverter.NativeMethods.ss_converter_destroy(context);
            }

            return true;
        }
    }
}
