using UnityEngine;
using UnityEditor.AssetImporters;
using SpriteStudio.Runtime;
// using SpriteStudio.Editor.Bindings; // TODO: ssconverter FFI の使用

namespace SpriteStudio.Editor
{
    /// <summary>
    /// .sspj (SpriteStudio プロジェクトファイル) のインポーター
    /// libssconverter (C-API) を呼び出してメモリ上で .ssab バイナリへ変換し、
    /// その後 ScriptableObject を生成します。
    /// </summary>
    [ScriptedImporter(1, "sspj")]
    public class SpriteStudioSspjImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var asset = ScriptableObject.CreateInstance<SpriteStudioProjectAsset>();
            
            // 1. TODO: libssconverter の FFI を呼び出して .sspj をパース＆メモリ上コンバート
            // unsafe {
            //     byte* ssabPtr = SSConverterMethods.convert_sspj(ctx.assetPath, out int length);
            //     ...
            // }
            
            // （仮実装）エラー防止のためダミーデータ
            asset.SsabData = new byte[0];

            // 2. TODO: 生成された ssab バイナリを FlatBuffers でパースし、テクスチャのパス一覧を取得
            
            // 3. テクスチャ等の依存関係の明示 (ctx.DependsOnSourceAsset)
            // .sspj インポーターは、参照先の画像が更新されたら再インポートされる必要があります
            
            // メインアセットとして登録
            ctx.AddObjectToAsset("MainAsset", asset);
            ctx.SetMainObject(asset);
        }
    }
}
