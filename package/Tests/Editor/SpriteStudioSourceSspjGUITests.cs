using System.Collections.Generic;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor.Tests
{
    public class SpriteStudioSourceSspjGUITests
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

        private SpriteStudioPlayer MakePlayer(SpriteStudioAnimeAsset asset)
        {
            var go = new GameObject("SourceSspjGUITest");
            _created.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;
            return player;
        }

        private SpriteStudioAnimeAsset MakeUnimportedAsset()
        {
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            _created.Add(asset);
            return asset;
        }

        private static string SsabPathOf(params Object[] targets)
        {
            var so = new SerializedObject(targets);
            return SpriteStudioSourceSspjGUI.SsabPathOf(so.FindProperty("_animeAsset"));
        }

        private static SpriteStudioAnimeAsset Ringo()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>("Ringo/Ringo");
            Assert.IsNotNull(asset, "Ringo fixture must be importable (warm the Library and re-run if null).");
            return asset;
        }

        [Test]
        public void SsabPathOf_IsTheSsabThePlayerPlays()
        {
            StringAssert.EndsWith("/Ringo.ssab", SsabPathOf(MakePlayer(Ringo())));
        }

        [Test]
        public void SsabPathOf_IsTheSharedSsabForSeveralPlayersOnIt()
        {
            StringAssert.EndsWith("/Ringo.ssab", SsabPathOf(MakePlayer(Ringo()), MakePlayer(Ringo())));
        }

        [Test]
        public void SsabPathOf_IsNullForPlayersOnDifferentAssets()
        {
            Assert.IsNull(SsabPathOf(MakePlayer(Ringo()), MakePlayer(MakeUnimportedAsset())));
        }

        [Test]
        public void SsabPathOf_IsNullForAnAssetNotImportedFromAnSsab()
        {
            Assert.IsNull(SsabPathOf(MakePlayer(MakeUnimportedAsset())));
        }

        [Test]
        public void SsabPathOf_IsNullWithoutAnAsset()
        {
            Assert.IsNull(SsabPathOf(MakePlayer(null)));
        }
    }
}
