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
            var asset = ScriptableObject.CreateInstance<SpriteStudioProjectAsset>();
            
            // 1. .ssab バイナリの読み込みと保持 (メモリの供給元)
            asset.SsabData = File.ReadAllBytes(ctx.assetPath);
            
            // 2. TODO: FlatBuffersで SsabData をパースし、関連テクスチャのパスを取得する
            // var texturePaths = ...;
            
            // 3. テクスチャ等の依存関係を登録し、ロードして参照を保持 (ビルド落ち・Addressables対策)
            /*
            foreach (var texPath in texturePaths) {
                // AssetDatabase への明示的な依存登録
                ctx.DependsOnSourceAsset(texPath); 
                
                var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                if (tex != null) asset.Textures.Add(tex);
            }
            */

            // メインアセットとして登録
            ctx.AddObjectToAsset("MainAsset", asset);
            ctx.SetMainObject(asset);
        }
    }
}
