using UnityEngine;
using UnityEditor.AssetImporters;
using System.IO;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// .ssab (事前コンバート済みバイナリ) のインポーター
    /// コンバート処理をスキップし、直接 ScriptableObject (SpriteStudioProjectAsset) を生成します。
    /// </summary>
    [ScriptedImporter(1, "ssab")]
    public class SpriteStudioSsabImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            
            // 1. .ssab バイナリの読み込みと保持 (メモリの供給元)
            asset.SsabData = File.ReadAllBytes(ctx.assetPath);
            
            // 2. FlatBuffersで SsabData をパースし、関連テクスチャのパスを取得する
            var bb = new Google.FlatBuffers.ByteBuffer(asset.SsabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
            
            string baseDir = Path.GetDirectoryName(ctx.assetPath);
            
            // 3. テクスチャ依存の登録とマテリアル生成 (ビルド落ち・Addressables対策)
            //    通常パーツのテクスチャ供給元は cellmap (image_path)。
            //    external_textures は TextureChange アトリビュート専用なので、
            //    両方を集約しつつ解決済みパスで重複排除する。
            // Shader.Find はインポート順によって null を返すことがあるため、パスから直接ロードする
            var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(
                "Packages/com.spritestudio.ssruntime/Runtime/Shaders/SpriteStudioDefault.shader"
            );
            if (shader == null)
            {
                shader = Shader.Find("SpriteStudio/Default");
            }

            var seenTexPaths = new System.Collections.Generic.HashSet<string>();

            // 3a. cellmaps: 通常パーツ等が参照する主テクスチャ
            int cellmapLen = ssab.CellmapsLength;
            for (int i = 0; i < cellmapLen; i++)
            {
                var cm = ssab.Cellmaps(i);
                if (cm.HasValue)
                {
                    AddTextureMaterial(ctx, asset, baseDir, cm.Value.ImagePath, shader, seenTexPaths);
                }
            }

            // 3b. external_textures: TextureChange アトリビュート用の差し替えテクスチャ
            int extLen = ssab.ExternalTexturesLength;
            for (int i = 0; i < extLen; i++)
            {
                var extTex = ssab.ExternalTextures(i);
                if (extTex.HasValue)
                {
                    AddTextureMaterial(ctx, asset, baseDir, extTex.Value.Name, shader, seenTexPaths);
                }
            }

            Debug.Log($"[SS-IMPORT] {ctx.assetPath} cellmaps:{cellmapLen} external:{extLen} -> textures:{asset.Textures.Count} materials:{asset.Materials.Count}");

            // 4. アニメーション名の抽出
            int animLen = ssab.AnimationsLength;
            asset.AnimationNames = new string[animLen];
            for (int i = 0; i < animLen; i++)
            {
                var anim = ssab.Animations(i);
                if (anim.HasValue)
                {
                    asset.AnimationNames[i] = anim.Value.Name;
                }
            }

            // メインアセットとして登録
            ctx.AddObjectToAsset("MainAsset", asset);
            ctx.SetMainObject(asset);
        }

        /// <summary>
        /// テクスチャ名 (cellmap.image_path もしくは external_texture.name) を解決し、
        /// テクスチャ参照とベースマテリアルをアセットに追加する。解決済みパスで重複排除する。
        /// </summary>
        private static void AddTextureMaterial(
            AssetImportContext ctx,
            SpriteStudioAnimeAsset asset,
            string baseDir,
            string texName,
            Shader shader,
            System.Collections.Generic.HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(texName)) return;

            string texPath = Path.Combine(baseDir, texName).Replace("\\", "/");
            ctx.DependsOnSourceAsset(texPath);
            var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);

            // image_path に相対ディレクトリが含まれる/ファイル名のみで配置されている場合のフォールバック
            if (tex == null)
            {
                string altPath = Path.Combine(baseDir, Path.GetFileName(texName)).Replace("\\", "/");
                if (altPath != texPath)
                {
                    ctx.DependsOnSourceAsset(altPath);
                    var altTex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(altPath);
                    if (altTex != null)
                    {
                        texPath = altPath;
                        tex = altTex;
                    }
                }
            }

            if (!seen.Add(texPath)) return; // 既に処理済みのパスはスキップ

            if (tex == null)
            {
                Debug.LogWarning($"[SpriteStudioSsabImporter] Texture not found for '{texName}' (tried '{texPath}')");
                return;
            }

            asset.Textures.Add(tex);

            if (shader != null)
            {
                var mat = new Material(shader);
                mat.name = tex.name + "_Material";
                mat.mainTexture = tex;
                ctx.AddObjectToAsset("Mat_" + asset.Materials.Count, mat);
                asset.Materials.Add(mat);
            }
        }
    }
}
