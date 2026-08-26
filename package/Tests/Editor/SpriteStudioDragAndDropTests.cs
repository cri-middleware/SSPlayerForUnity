using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Editor.Tests
{
    /// <summary>
    /// Placement via drag and drop must leave the object with an animation selected. Without one, every
    /// draw path early-returns on the empty name and the placed object stays invisible in both edit and
    /// play mode until the inspector's dropdown happens to fill it in.
    /// </summary>
    public class SpriteStudioDragAndDropTests
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

        // A stand-in asset: the placement path only reads AnimationNames to pick a default, and the
        // AnimeAsset setter no-ops on null SsabData, so no converted binary is needed here.
        private SpriteStudioAnimeAsset MakeAsset(params string[] animationNames)
        {
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.name = "TestAsset";
            asset.AnimationNames = animationNames;
            _created.Add(asset);
            return asset;
        }

        private static object InvokePrivateStatic(string name, params object[] args)
        {
            var method = typeof(SpriteStudioDragAndDrop).GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"SpriteStudioDragAndDrop.{name} not found (was it renamed?).");
            return method.Invoke(null, args);
        }

        [Test]
        public void GetDefaultAnimationName_ReturnsTheFirstAnimation()
        {
            var asset = MakeAsset("walk", "run", "dance");
            Assert.AreEqual("walk", InvokePrivateStatic("GetDefaultAnimationName", asset));
        }

        [Test]
        public void GetDefaultAnimationName_WithNoAnimations_ReturnsNull()
        {
            Assert.IsNull(InvokePrivateStatic("GetDefaultAnimationName", MakeAsset()));
            Assert.IsNull(InvokePrivateStatic("GetDefaultAnimationName", MakeAsset(null)));
        }

        // The UI branch is intentionally not exercised: it creates a Canvas / EventSystem in the open
        // scene when none exists. Both branches select the default through the same helper, covered above.
        [Test]
        public void InstantiatePlayers_WorldPlayer_SelectsAnAnimation()
        {
            var asset = MakeAsset("walk", "run");
            var assets = new List<SpriteStudioAnimeAsset> { asset };

            InvokePrivateStatic("InstantiatePlayers", assets, null, (Vector3?)Vector3.zero, false);

            var player = Object.FindFirstObjectByType<SpriteStudioPlayer>();
            Assert.IsNotNull(player, "Placement should have created a SpriteStudioPlayer.");
            _created.Add(player.gameObject);

            Assert.AreEqual(asset, player.AnimeAsset);
            Assert.AreEqual("walk", player.CurrentAnimation,
                "A placed player must start on the asset's first animation, or it renders nothing.");
        }
    }
}
