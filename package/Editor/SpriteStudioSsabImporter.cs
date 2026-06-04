using UnityEngine;
using UnityEditor.AssetImporters;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// .ssab (事前コンバート済みバイナリ) のインポーター。
    /// コンバート処理をスキップし、直接 ScriptableObject (SpriteStudioAnimeAsset) を生成します。
    /// 構築処理は <see cref="SpriteStudioAnimeAssetBuilder"/> に委譲します。
    /// </summary>
    [ScriptedImporter(4, "ssab")]
    [UnityEngine.Icon("Packages/com.spritestudio.ssruntime/Editor/Icons/ssab_icon.png")]
    public class SpriteStudioSsabImporter : ScriptedImporter
    {
        [Tooltip("テクスチャ単位の共有マテリアル(.mat)を使用します。OFF の場合はマテリアルを割り当てません。")]
        public bool GenerateMaterials = true;

        [Tooltip("マテリアル生成に使用するシェーダ。未指定の場合は SpriteStudio/Default を使用します。")]
        public Shader OverrideShader;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            SpriteStudioAnimeAssetBuilder.Build(this, ctx);
        }
    }
}
