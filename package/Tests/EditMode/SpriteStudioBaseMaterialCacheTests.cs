using System.Collections.Generic;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// The base materials made at runtime for textures with no material asset: one per (texture, shader),
    /// shared by every asset that uses the texture, living exactly as long as an enabled asset holds it.
    /// </summary>
    public class SpriteStudioBaseMaterialCacheTests
    {
        private readonly List<Object> _made = new List<Object>();

        private T Make<T>(T obj) where T : Object
        {
            _made.Add(obj);
            return obj;
        }

        private Texture2D NewTexture(string name) => Make(new Texture2D(2, 2) { name = name });

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _made) if (o != null) Object.DestroyImmediate(o);
            _made.Clear();
        }

        [Test]
        public void TheMaterial_IsMadeOnFirstUse_SharedByEveryHolder_AndDestroyedWithTheLastReference()
        {
            var texture = NewTexture("atlas");
            int before = SpriteStudioBaseMaterialCache.EntryCount;

            var a = SpriteStudioBaseMaterialCache.Acquire(texture, null);
            var b = SpriteStudioBaseMaterialCache.Acquire(texture, null);
            Assert.AreEqual(2, SpriteStudioBaseMaterialCache.RefCountOf(a));

            var material = SpriteStudioBaseMaterialCache.Get(a);
            Assert.IsNotNull(material);
            Assert.AreSame(material, SpriteStudioBaseMaterialCache.Get(b), "one texture, one material");
            Assert.AreSame(texture, material.mainTexture);
            Assert.AreEqual(SpriteStudioBaseMaterialCache.DefaultShader, material.shader);
            Assert.AreEqual("atlas_Material", material.name, "named like the .mat the import writes");
            Assert.AreEqual(HideFlags.HideAndDontSave, material.hideFlags);

            SpriteStudioBaseMaterialCache.Release(a);
            Assert.IsTrue(material != null, "another holder still draws with it");

            SpriteStudioBaseMaterialCache.Release(b);
            Assert.IsTrue(material == null, "the last reference destroys it");
            Assert.AreEqual(before, SpriteStudioBaseMaterialCache.EntryCount);
            Assert.IsNull(SpriteStudioBaseMaterialCache.Get(a), "a handed-back reference finds nothing");
        }

        [Test]
        public void TwoTextures_OrTwoShaders_GetTheirOwnMaterials()
        {
            var first = NewTexture("same_name");
            var second = NewTexture("same_name");
            var other = Shader.Find("SpriteStudio/MaskWrite");
            Assert.IsNotNull(other);

            var r1 = SpriteStudioBaseMaterialCache.Acquire(first, null);
            var r2 = SpriteStudioBaseMaterialCache.Acquire(second, null);
            var r3 = SpriteStudioBaseMaterialCache.Acquire(first, other);
            try
            {
                var m1 = SpriteStudioBaseMaterialCache.Get(r1);
                Assert.AreNotSame(m1, SpriteStudioBaseMaterialCache.Get(r2), "keyed by the texture, not its name");
                Assert.AreNotSame(m1, SpriteStudioBaseMaterialCache.Get(r3));
                Assert.AreEqual(other, SpriteStudioBaseMaterialCache.Get(r3).shader);
            }
            finally
            {
                SpriteStudioBaseMaterialCache.Release(r1);
                SpriteStudioBaseMaterialCache.Release(r2);
                SpriteStudioBaseMaterialCache.Release(r3);
            }
        }

        [Test]
        public void ADestroyedTexture_DrawsNothing_AndItsReferenceCanStillBeHandedBack()
        {
            var texture = NewTexture("gone");
            int before = SpriteStudioBaseMaterialCache.EntryCount;
            var r = SpriteStudioBaseMaterialCache.Acquire(texture, null);
            var material = SpriteStudioBaseMaterialCache.Get(r);

            Object.DestroyImmediate(texture);
            Assert.IsNull(SpriteStudioBaseMaterialCache.Get(r));

            var unrelated = SpriteStudioBaseMaterialCache.Acquire(NewTexture("other"), null);
            Assert.IsTrue(material == null, "the next acquire drops the material of a destroyed texture");

            SpriteStudioBaseMaterialCache.Release(r);
            SpriteStudioBaseMaterialCache.Release(unrelated);
            Assert.AreEqual(before, SpriteStudioBaseMaterialCache.EntryCount);
        }

        [Test]
        public void AMissingTexture_TakesNoReference()
        {
            var r = SpriteStudioBaseMaterialCache.Acquire(null, null);
            Assert.IsFalse(r.IsValid);
            Assert.IsNull(SpriteStudioBaseMaterialCache.Get(r));
            SpriteStudioBaseMaterialCache.Release(r); // no-op
        }

        [Test]
        public void AnAsset_HoldsItsTexturesWhileEnabled()
        {
            var texture = NewTexture("atlas");
            var source = Make(ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>());
            source.Textures.Add(texture);
            source.Textures.Add(null);

            // A copy is enabled with its textures in place, as an asset loaded from disk is.
            var asset = Object.Instantiate(source);
            var other = Object.Instantiate(source);
            var material = asset.GetRuntimeBaseMaterial(0);
            Assert.IsNotNull(material);
            Assert.AreSame(material, other.GetRuntimeBaseMaterial(0), "assets sharing a texture share its material");
            Assert.IsNull(asset.GetRuntimeBaseMaterial(1), "a missing texture has none");
            Assert.IsNull(asset.GetRuntimeBaseMaterial(2), "out of range");

            Object.DestroyImmediate(asset);
            Assert.IsTrue(material != null, "the other asset still holds it");
            Object.DestroyImmediate(other);
            Assert.IsTrue(material == null, "disabling the last asset destroys it");
        }
    }
}
