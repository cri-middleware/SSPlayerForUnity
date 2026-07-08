using UnityEngine;
using UnityEditor.AssetImporters;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Importer for .ssab (pre-converted binary).
    /// Skips the conversion step and directly creates a ScriptableObject (SpriteStudioAnimeAsset).
    /// The build work is delegated to <see cref="SpriteStudioAnimeAssetBuilder"/>.
    /// </summary>
    [ScriptedImporter(4, "ssab")]
    [UnityEngine.Icon("Packages/jp.co.cri-mw.optpix.ssplayer-unity/Editor/Icons/ssab_icon.png")]
    public class SpriteStudioSsabImporter : ScriptedImporter
    {
        [Tooltip("Use a shared per-texture material (.mat). When OFF, no material is assigned.")]
        public bool GenerateMaterials = true;

        [Tooltip("Shader used for material generation. When unset, SpriteStudio/Default is used.")]
        public Shader OverrideShader;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            SpriteStudioAnimeAssetBuilder.Build(this, ctx);
        }
    }
}
