using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor.Tests
{
    /// <summary>
    /// What the .ssab import records about materials: Materials runs parallel to Textures, holding the .mat
    /// where one is used and null where the texture is drawn with a material made at runtime, and the hash
    /// table points at the texture either way. The import embeds no material of its own.
    /// </summary>
    public class SpriteStudioMaterialImportTests
    {
        private const string FixtureDir = "Packages/jp.co.cri-mw.spritestudio.ssplayer-unity/Tests/Runtime/Resources/Ringo";
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            string ssab = Path.GetFullPath(FixtureDir + "/Ringo.ssab");
            string png = Path.GetFullPath(FixtureDir + "/Ringo.png");
            if (!File.Exists(ssab) || !File.Exists(png)) Assert.Ignore("The Ringo fixture is unavailable (converted from the SDK's tests/Ringo).");

            _dir = "Assets/__SSMaterialImportTest_" + Path.GetRandomFileName().Replace(".", "");
            Directory.CreateDirectory(_dir);
            File.Copy(png, _dir + "/Ringo.png");
            File.Copy(ssab, _dir + "/Ringo.ssab");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_dir)) AssetDatabase.DeleteAsset(_dir);
        }

        private SpriteStudioAnimeAsset Reimport(bool generateMaterials)
        {
            var importer = (SpriteStudioSsabImporter)AssetImporter.GetAtPath(_dir + "/Ringo.ssab");
            importer.GenerateMaterials = generateMaterials;
            // A plain field on a ScriptedImporter does not mark it dirty, and SaveAndReimport skips a clean one.
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<SpriteStudioAnimeAsset>(_dir + "/Ringo.ssab");
        }

        private void AssertParallelAndMapped(SpriteStudioAnimeAsset asset)
        {
            Assert.That(asset.Textures, Is.Not.Empty);
            Assert.AreEqual(asset.Textures.Count, asset.Materials.Count, "Materials runs parallel to Textures");
            Assert.That(asset.TextureHashKeys, Is.Not.Empty, "the hash table is built whether or not there is a .mat");
            foreach (int i in asset.TextureHashTextureIndices) Assert.That(i, Is.InRange(0, asset.Textures.Count - 1));
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(_dir + "/Ringo.ssab"))
            {
                Assert.IsNotInstanceOf<Material>(sub, "no material is embedded in the asset");
            }
        }

        [Test]
        public void BeforeTheMatIsWritten_TheTextureHasNoMaterial_AndIsStillMapped()
        {
            // The postprocessor writes the .mat on a later editor update, so straight after the import there is none.
            var asset = Reimport(true);
            Assume.That(File.Exists(_dir + "/Ringo.mat"), Is.False);

            AssertParallelAndMapped(asset);
            Assert.That(asset.Materials, Has.All.Null);
        }

        [Test]
        public void GenerateMaterials_ChoosesWhetherTheMatIsUsed_AndOffLeavesItInPlace()
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(_dir + "/Ringo.png");
            var mat = new Material(Shader.Find("SpriteStudio/Default")) { mainTexture = texture };
            AssetDatabase.CreateAsset(mat, _dir + "/Ringo.mat");

            var on = Reimport(true);
            AssertParallelAndMapped(on);
            Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Material>(_dir + "/Ringo.mat"), on.Materials[on.Textures.IndexOf(texture)]);

            var off = Reimport(false);
            AssertParallelAndMapped(off);
            Assert.That(off.Materials, Has.All.Null, "off: drawn with materials made at runtime");
            Assert.IsTrue(File.Exists(_dir + "/Ringo.mat"), "the user's .mat is not deleted");
        }
    }
}
