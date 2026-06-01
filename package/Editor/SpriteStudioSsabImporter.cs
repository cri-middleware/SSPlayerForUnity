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
            
            // 3. テクスチャ等の依存関係を登録し、ロードして参照を保持 (ビルド落ち・Addressables対策)
            int len = ssab.ExternalTexturesLength;
            for (int i = 0; i < len; i++)
            {
                var extTex = ssab.ExternalTextures(i);
                if (extTex.HasValue)
                {
                    string texName = extTex.Value.Name;
                    if (!string.IsNullOrEmpty(texName))
                    {
                        string texPath = Path.Combine(baseDir, texName).Replace("\\", "/");
                        
                        // AssetDatabase への明示的な依存登録
                        ctx.DependsOnSourceAsset(texPath);
                        
                        var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                        if (tex != null)
                        {
                            asset.Textures.Add(tex);
                        }
                    }
                }
            }

            // メインアセットとして登録
            ctx.AddObjectToAsset("MainAsset", asset);
            ctx.SetMainObject(asset);
        }
    }
}
