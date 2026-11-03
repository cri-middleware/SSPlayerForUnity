using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor.Tests
{
    public class SpriteStudioAssetNamesTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _created)
            {
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _created.Clear();
        }

        private static SpriteStudioAnimeAsset Load(string path)
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(path);
            if (asset == null) Assert.Ignore($"{path} is unavailable (warm the Library and re-run).");
            return asset;
        }

        private SpriteStudioGraphic MakeGraphic(SpriteStudioAnimeAsset asset, string animation)
        {
            var go = new GameObject("AssetNamesTest", typeof(RectTransform));
            _created.Add(go);
            var graphic = go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = asset;
            var so = new SerializedObject(graphic);
            so.FindProperty("_animationName").stringValue = animation;
            so.ApplyModifiedPropertiesWithoutUndo();
            return graphic;
        }

        private static SpriteStudioAnimeAsset GraphicAsset(out string animation, params Object[] targets)
        {
            return SpriteStudioAssetNames.GraphicAsset(new SerializedObject(targets), out animation);
        }

        [Test]
        public void Labels_AreTheAnimationsLabelsInFrameOrder()
        {
            CollectionAssert.AreEqual(new[] { "Start", "End" }, SpriteStudioAssetNames.Labels(Load("overall/Signal"), "anime_1"));
        }

        [Test]
        public void Labels_AreNullForAnAnimationTheAssetLacks()
        {
            Assert.IsNull(SpriteStudioAssetNames.Labels(Load("overall/Signal"), "no_such_animation"));
        }

        [Test]
        public void Parts_AreThePartsThePlayerResolves()
        {
            var asset = Load("Ringo/Ringo");
            var go = new GameObject("AssetNamesTest");
            _created.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;
            var resolved = player.GetPartNames();
            Assert.IsNotEmpty(resolved, "The player must have set the asset up.");
            CollectionAssert.AreEqual(resolved, SpriteStudioAssetNames.Parts(asset).Select(n => n ?? ""));
        }

        [Test]
        public void GraphicAsset_IsTheAssetAndAnimationTheGraphicPlays()
        {
            var asset = Load("Ringo/Ringo");
            string second = asset.AnimationNames[asset.AnimationNames.Length - 1];
            Assert.AreSame(asset, GraphicAsset(out string animation, MakeGraphic(asset, second), MakeGraphic(asset, second)));
            Assert.AreEqual(second, animation);
        }

        [Test]
        public void GraphicAsset_ReadsAnEmptyAnimationNameAsTheFirstAnimation()
        {
            var asset = Load("Ringo/Ringo");
            GraphicAsset(out string animation, MakeGraphic(asset, ""));
            Assert.AreEqual(asset.AnimationNames[0], animation);
        }

        [Test]
        public void GraphicAsset_HasNoAnimationForGraphicsOnDifferentAnimations()
        {
            var asset = Load("Ringo/Ringo");
            Assume.That(asset.AnimationNames.Length, Is.GreaterThan(1));
            Assert.AreSame(asset, GraphicAsset(out string animation,
                MakeGraphic(asset, asset.AnimationNames[0]), MakeGraphic(asset, asset.AnimationNames[1])));
            Assert.IsNull(animation);
        }

        [Test]
        public void GraphicAsset_IsNullForGraphicsOnDifferentAssets()
        {
            Assert.IsNull(GraphicAsset(out _, MakeGraphic(Load("Ringo/Ringo"), ""), MakeGraphic(Load("overall/Signal"), "")));
        }
    }
}
