using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Building an asset at runtime from the bytes of a .ssab that was not imported, with textures and sounds
    /// the application hands over. The imported fixtures supply both, and what the import made of them is
    /// what the runtime build should match.
    /// </summary>
    public class SpriteStudioAnimeAssetLoaderTests
    {
        private readonly List<Object> _made = new List<Object>();
        private GameObject _go;

        private T Made<T>(T obj) where T : Object
        {
            _made.Add(obj);
            return obj;
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            for (int i = _made.Count - 1; i >= 0; i--) if (_made[i] != null) Object.DestroyImmediate(_made[i]);
            _made.Clear();
        }

        private static SpriteStudioAnimeAsset Imported(string path)
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(path);
            if (asset == null) Assert.Ignore($"The {path} fixture is unavailable (converted from the SDK's tests).");
            return asset;
        }

        private static string[] Names(IEnumerable<SpriteStudioDependency> deps, SpriteStudioDependencyKind kind) =>
            deps.Where(d => d.Kind == kind).Select(d => d.Name).ToArray();

        [Test]
        public void GetDependencies_NamesTheTextures_AndTheOtherPacks()
        {
            var ringo = Imported("Ringo/Ringo");
            var ringoDeps = SpriteStudioAnimeAssetLoader.GetDependencies(ringo.SsabData);
            Assert.That(Names(ringoDeps, SpriteStudioDependencyKind.Texture), Is.Not.Empty);
            Assert.That(Names(ringoDeps, SpriteStudioDependencyKind.AnimePack), Is.Empty, "Ringo plays no other pack");

            // Ringo_tyu draws nothing of its own: it plays Ringo through an Instance part.
            var tyu = Imported("Ringo/Ringo_tyu");
            var deps = SpriteStudioAnimeAssetLoader.GetDependencies(tyu.SsabData);
            var packs = Names(deps, SpriteStudioDependencyKind.AnimePack);
            Assume.That(tyu.ExternalReferences, Is.Not.Empty, "Ringo_tyu plays Ringo through an Instance part");
            foreach (var ext in tyu.ExternalReferences)
            {
                Assert.That(packs, Has.Some.EqualTo(ext.name).IgnoreCase, "the packs the import found are listed");
            }
            Assert.AreEqual(deps.Count, deps.Distinct().Count(), "each name once");
        }

        [Test]
        public void GetDependencies_NamesTheSoundFiles()
        {
            var sound = Imported("overall/Sound");
            Assume.That(sound.Sounds, Is.Not.Empty);
            Assert.That(Names(SpriteStudioAnimeAssetLoader.GetDependencies(sound.SsabData), SpriteStudioDependencyKind.Audio), Is.Not.Empty);
        }

        [Test]
        public void NotAnSsab_IsRefused()
        {
            var bytes = new byte[64];
            Assert.Throws<ArgumentException>(() => SpriteStudioAnimeAssetLoader.GetDependencies(bytes));
            Assert.Throws<ArgumentException>(() => SpriteStudioAnimeAssetLoader.Create(bytes, _ => null));
        }

        [Test]
        public void Create_MapsEveryTextureHash_AsTheImportDoes_AndDrawsWithoutAMat()
        {
            var imported = Imported("Ringo/Ringo");
            var asset = Made(SpriteStudioAnimeAssetLoader.Create(imported.SsabData, imported.Textures));

            Assert.AreEqual(imported.PackHash, asset.PackHash);
            CollectionAssert.AreEqual(imported.AnimationNames, asset.AnimationNames);
            CollectionAssert.AreEquivalent(imported.Textures, asset.Textures);
            Assert.That(asset.Materials, Has.Count.EqualTo(asset.Textures.Count).And.All.Null);
            CollectionAssert.AreEquivalent(imported.TextureHashKeys, asset.TextureHashKeys);
            foreach (uint hash in imported.TextureHashKeys)
            {
                var expected = imported.Textures[imported.GetTextureIndexForHash(hash)];
                var mat = asset.ResolveMaterialForHash(hash);
                Assert.IsNotNull(mat, $"hash 0x{hash:X8} draws with a runtime material");
                Assert.AreSame(expected, mat.mainTexture);
            }
        }

        [Test]
        public void TheResolver_IsAskedOncePerName_AndAMissingTextureIsReported()
        {
            var imported = Imported("Ringo/Ringo");
            var asked = new List<string>();
            LogAssert.Expect(LogType.Warning, new Regex("no texture was given"));
            var asset = Made(SpriteStudioAnimeAssetLoader.Create(imported.SsabData, name => { asked.Add(name); return null; }));

            CollectionAssert.AreEqual(Names(SpriteStudioAnimeAssetLoader.GetDependencies(imported.SsabData), SpriteStudioDependencyKind.Texture), asked);
            Assert.That(asset.Textures, Is.Empty);
        }

        [Test]
        public void ACreatedAsset_PlaysInAGraphic()
        {
            var imported = Imported("Ringo/Ringo");
            var asset = Made(SpriteStudioAnimeAssetLoader.Create(imported.SsabData, imported.Textures));

            _go = new GameObject("LoaderGraphic");
            _go.AddComponent<Canvas>();
            var graphic = _go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = asset;
            Assert.IsTrue(graphic.SetAnimation("walk"));
            graphic.InternalPlayer.ScheduleMeshUpdate(0f);
            graphic.ApplyAndRender();
            Assert.Greater(graphic.CanvasMesh.vertexCount, 0);
        }

        [Test]
        public void DestroyingTheAsset_HandsBackItsRuntimeMaterials()
        {
            var imported = Imported("Ringo/Ringo");
            int before = SpriteStudioBaseMaterialCache.EntryCount;
            var fresh = new Dictionary<string, Texture2D>();
            var asset = Made(SpriteStudioAnimeAssetLoader.Create(imported.SsabData,
                name => fresh.TryGetValue(name, out var t) ? t : fresh[name] = Made(new Texture2D(2, 2) { name = name })));

            Assert.AreEqual(before + asset.Textures.Count, SpriteStudioBaseMaterialCache.EntryCount, "the textures are referenced once filled in");
            Object.DestroyImmediate(asset);
            Assert.AreEqual(before, SpriteStudioBaseMaterialCache.EntryCount);
        }

        [Test]
        public void ByFileName_TwoGivenTexturesWithOneName_IsRefused()
        {
            var imported = Imported("Ringo/Ringo");
            var a = Made(new Texture2D(2, 2) { name = "same" });
            var b = Made(new Texture2D(2, 2) { name = "SAME" });
            Assert.Throws<ArgumentException>(() => SpriteStudioAnimeAssetLoader.Create(imported.SsabData, new[] { a, b }));
        }

        [Test]
        public void TheSounds_AreMatchedAsTheImportMatchedThem()
        {
            var imported = Imported("overall/Sound");
            Assume.That(imported.Sounds, Is.Not.Empty);
            var clips = imported.Sounds.Select(s => s.Clip).Distinct();
            var asset = Made(SpriteStudioAnimeAssetLoader.Create(imported.SsabData, imported.Textures, clips));

            Assert.AreEqual(imported.Sounds.Count, asset.Sounds.Count);
            foreach (var s in imported.Sounds)
            {
                Assert.AreSame(s.Clip, asset.ResolveAudioClip(s.SoundListNameHash, s.SoundNameHash));
            }
        }

        [Test]
        public void Link_PointsAnInstanceAtThePackItPlays()
        {
            var tyuImported = Imported("Ringo/Ringo_tyu");
            var ringoImported = Imported("Ringo/Ringo");
            var tyu = Made(SpriteStudioAnimeAssetLoader.Create(tyuImported.SsabData, tyuImported.Textures));
            var ringo = Made(SpriteStudioAnimeAssetLoader.Create(ringoImported.SsabData, ringoImported.Textures));

            SpriteStudioAnimeAssetLoader.LinkExternalReferences(new[] { tyu, ringo });

            CollectionAssert.AreEquivalent(tyuImported.ExternalReferences.Select(e => e.PackHash), tyu.ExternalReferences.Select(e => e.PackHash));
            Assert.AreSame(ringo.SsabData, tyu.ResolveExternalSsab(ringo.PackHash, 0));
        }

        [Test]
        public void APlayer_DropsTheOtherPacksMaterial_WhenThatPackAloneIsReloaded()
        {
            var tyuImported = Imported("Ringo/Ringo_tyu");
            var ringoImported = Imported("Ringo/Ringo");
            var tyu = Made(SpriteStudioAnimeAssetLoader.Create(tyuImported.SsabData, tyuImported.Textures));
            // Textures no other asset holds, so Ringo's runtime material is Ringo's alone and reloading Ringo
            // replaces it. With the imported ones, the imported Ringo would keep the same material alive.
            var fresh = new Dictionary<string, Texture2D>();
            var ringo = Made(SpriteStudioAnimeAssetLoader.Create(ringoImported.SsabData,
                name => fresh.TryGetValue(name, out var t) ? t : fresh[name] = Made(new Texture2D(2, 2) { name = name })));
            SpriteStudioAnimeAssetLoader.LinkExternalReferences(new[] { tyu, ringo });
            Assume.That(tyu.Textures, Is.Empty, "Ringo_tyu draws nothing of its own");

            _go = new GameObject("LoaderInstancePlayer");
            var player = _go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = tyu;
            Assert.IsTrue(player.SetAnimation(tyu.AnimationNames[0]));
            player.Play();
            player.InternalPlayer.ScheduleMeshUpdate(0f);
            player.ApplyAndRender();
            var renderer = _go.GetComponent<MeshRenderer>();
            var before = ringo.GetFallbackMaterial();
            Assume.That(renderer.sharedMaterials, Has.Member(before), "precondition: the Instance draws with Ringo's material");

            ringo.ReloadRuntimeState();
            var after = ringo.GetFallbackMaterial();
            Assume.That(after, Is.Not.SameAs(before));

            player.InternalPlayer.ScheduleMeshUpdate(0f);
            player.ApplyAndRender();
            Assert.That(renderer.sharedMaterials, Has.Member(after));
            Assert.That(renderer.sharedMaterials, Has.No.Member(before), "the material Ringo let go of stays on the renderer");
        }

        [Test]
        public void Link_ReportsAPackThatIsMissing_AndRefusesOnePackTwice()
        {
            var tyuImported = Imported("Ringo/Ringo_tyu");
            var tyu = Made(SpriteStudioAnimeAssetLoader.Create(tyuImported.SsabData, tyuImported.Textures));
            Assume.That(tyuImported.ExternalReferences, Is.Not.Empty);

            LogAssert.Expect(LogType.Warning, new Regex("is not among the assets given"));
            SpriteStudioAnimeAssetLoader.LinkExternalReferences(new[] { tyu });
            Assert.That(tyu.ExternalReferences, Is.Empty);

            var twice = Made(SpriteStudioAnimeAssetLoader.Create(tyuImported.SsabData, tyuImported.Textures));
            Assert.Throws<ArgumentException>(() => SpriteStudioAnimeAssetLoader.LinkExternalReferences(new[] { tyu, twice }));
        }
    }
}
