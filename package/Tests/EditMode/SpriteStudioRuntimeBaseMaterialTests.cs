using System.Collections.Generic;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// How an asset resolves a texture hash to a base material when some or all of its textures have no
    /// .mat: the .mat where there is one, a shared base material made at runtime where there is not.
    /// </summary>
    public class SpriteStudioRuntimeBaseMaterialTests
    {
        private const uint HashA = 0x1111u;
        private const uint HashB = 0x2222u;

        private readonly List<Object> _made = new List<Object>();

        private T Make<T>(T obj) where T : Object
        {
            _made.Add(obj);
            return obj;
        }

        [TearDown]
        public void TearDown()
        {
            // Copies first, so they hand back their references before the textures go.
            for (int i = _made.Count - 1; i >= 0; i--) if (_made[i] != null) Object.DestroyImmediate(_made[i]);
            _made.Clear();
        }

        // An asset as the import leaves it, enabled with its data in place (as one loaded from disk is).
        private SpriteStudioAnimeAsset Imported(Texture2D[] textures, Material[] materials, uint[] hashes, Shader overrideShader = null)
        {
            var source = Make(ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>());
            source.Textures.AddRange(textures);
            source.Materials.AddRange(materials);
            source.OverrideShader = overrideShader;
            source.TextureHashKeys = hashes;
            var indices = new int[hashes.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            source.TextureHashTextureIndices = indices;
            return Make(Object.Instantiate(source));
        }

        private Texture2D NewTexture(string name) => Make(new Texture2D(2, 2) { name = name });

        [Test]
        public void ATextureWithAMat_UsesIt_AndOneWithout_GetsARuntimeMaterial()
        {
            var a = NewTexture("a");
            var b = NewTexture("b");
            var mat = Make(new Material(SpriteStudioBaseMaterialCache.DefaultShader) { mainTexture = a });
            var asset = Imported(new[] { a, b }, new[] { mat, null }, new[] { HashA, HashB });

            Assert.AreSame(mat, asset.ResolveMaterialForHash(HashA));

            var made = asset.ResolveMaterialForHash(HashB);
            Assert.IsNotNull(made, "no .mat: drawn with a material made at runtime, not magenta");
            Assert.AreSame(b, made.mainTexture);
            Assert.AreEqual(SpriteStudioBaseMaterialCache.DefaultShader, made.shader);
        }

        [Test]
        public void TwoAssets_WithTheSameTexture_ShareTheRuntimeMaterial()
        {
            var t = NewTexture("shared");
            var first = Imported(new[] { t }, new Material[] { null }, new[] { HashA });
            var second = Imported(new[] { t }, new Material[] { null }, new[] { HashB });

            Assert.AreSame(first.ResolveMaterialForHash(HashA), second.ResolveMaterialForHash(HashB));
        }

        [Test]
        public void TheOverrideShader_IsWhatTheRuntimeMaterialUses()
        {
            var t = NewTexture("t");
            var shader = Shader.Find("SpriteStudio/MaskWrite");
            Assume.That(shader, Is.Not.Null);
            var asset = Imported(new[] { t }, new Material[] { null }, new[] { HashA }, shader);

            Assert.AreEqual(shader, asset.ResolveMaterialForHash(HashA).shader);
        }

        [Test]
        public void ACellLessPart_DrawsWithTheMatsShader_ThenTheOverrideShader()
        {
            var t = NewTexture("t");
            var overrideShader = Shader.Find("SpriteStudio/MaskWrite");
            Assume.That(overrideShader, Is.Not.Null);

            // A .mat that kept the default shader after the Override Shader changed: the textured parts draw with
            // the .mat, so the cell-less ones must too.
            var mat = Make(new Material(SpriteStudioBaseMaterialCache.DefaultShader) { mainTexture = t });
            var withMat = Imported(new[] { t }, new[] { mat }, new[] { HashA }, overrideShader);
            Assert.AreEqual(mat.shader, withMat.ResolveMaterial(0, SpriteStudioMaterial.BlendMix).shader);

            var withoutMat = Imported(new[] { t }, new Material[] { null }, new[] { HashA }, overrideShader);
            Assert.AreEqual(overrideShader, withoutMat.ResolveMaterial(0, SpriteStudioMaterial.BlendMix).shader);
        }

        [Test]
        public void ATextureOnlyAnInstanceTargetHas_ResolvesThroughIt()
        {
            var own = NewTexture("own");
            var theirs = NewTexture("theirs");
            var target = Imported(new[] { theirs }, new Material[] { null }, new[] { HashB });
            var asset = Imported(new[] { own }, new Material[] { null }, new[] { HashA });
            asset.ExternalReferences.Add(target);

            Assert.AreSame(theirs, asset.ResolveMaterialForHash(HashB).mainTexture);
        }

        [Test]
        public void TheVariants_AndTheCanvasMaterial_AreBuiltOnTheRuntimeMaterial()
        {
            var t = NewTexture("t");
            var asset = Imported(new[] { t }, new Material[] { null }, new[] { HashA });

            var additive = asset.ResolveMaterial(HashA, 1);
            Assert.IsNotNull(additive);
            Assert.AreSame(t, additive.mainTexture);

            var canvas = asset.ResolveCanvasMaterial(HashA, 0);
            Assert.IsNotNull(canvas, "a Canvas player draws a texture with no .mat too");
            Assert.AreSame(t, canvas.mainTexture);
            Assert.AreEqual("SpriteStudio/Canvas", canvas.shader.name);
        }

        [Test]
        public void TheFallback_IsTheFirstTexturesMaterial_WhetherAMatOrMadeAtRuntime()
        {
            var t = NewTexture("t");
            var asset = Imported(new[] { t }, new Material[] { null }, new uint[0]);
            Assert.AreSame(t, asset.GetFallbackMaterial().mainTexture);

            var empty = Imported(new Texture2D[0], new Material[0], new uint[0]);
            Assert.IsNull(empty.GetFallbackMaterial());
        }
    }
}
