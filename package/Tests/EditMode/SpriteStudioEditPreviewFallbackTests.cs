using System.Reflection;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// The edit-mode preview must still show something when no animation has been chosen yet (a player
    /// assigned an asset from script, or one whose asset was swapped in the inspector). It previews the
    /// asset's first animation without writing that choice back, so nothing is serialized and Play mode
    /// keeps honouring the empty name.
    /// </summary>
    public class SpriteStudioEditPreviewFallbackTests
    {
        private const string RingoAssetPath = "Ringo/Ringo";

        private GameObject _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            _go = null;
        }

        private static SpriteStudioInternalPlayer GetInternal(SpriteStudioPlayer player)
        {
            var coreField = typeof(SpriteStudioPlayer).GetField("_core",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var core = coreField?.GetValue(player) as SpriteStudioPlaybackCore;
            return core?.Internal;
        }

        private static void BuildEditPreview(SpriteStudioPlayer player)
        {
            var method = typeof(SpriteStudioPlayer).GetMethod("BuildEditPreview",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "SpriteStudioPlayer.BuildEditPreview not found (was it renamed?).");
            method.Invoke(player, null);
        }

        private SpriteStudioPlayer PlacePlayerWithoutAnimationName()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            Assert.IsNotNull(asset, "Ringo fixture must be importable (warm the Library and re-run if null).");
            Assert.IsNotNull(asset.SsabData);
            Assert.Greater(asset.SsabData.Length, 0);

            _go = new GameObject("PreviewFallbackTest");
            var player = _go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;   // no AnimationName assignment
            Assert.IsEmpty(player.AnimationName, "Precondition: the player starts with no animation chosen.");
            return player;
        }

        [Test]
        public void BuildEditPreview_WithNoAnimationName_BuildsGeometry()
        {
            var player = PlacePlayerWithoutAnimationName();

            var internalPlayer = GetInternal(player);
            Assert.IsNotNull(internalPlayer);
            Assert.IsTrue(internalPlayer.HasSsab, "The ssab should be loaded by the AnimeAsset setter.");
            Assert.AreEqual(0, internalPlayer.VertexCount, "Precondition: nothing has been built yet.");

            BuildEditPreview(player);

            Assert.Greater(internalPlayer.VertexCount, 0,
                "The preview must fall back to the asset's first animation instead of drawing nothing.");
        }

        [Test]
        public void BuildEditPreview_WithNoAnimationName_LeavesTheNameUnset()
        {
            var player = PlacePlayerWithoutAnimationName();

            BuildEditPreview(player);

            Assert.IsEmpty(player.AnimationName,
                "The preview fallback is a display convenience and must not write the scene.");
        }
    }
}
