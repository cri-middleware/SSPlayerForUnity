using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    internal static class SpriteStudioAssetNames
    {
        private static SpriteStudioAnimeAsset _partsAsset;
        private static byte[] _partsData;
        private static string[] _partNames;
        private static bool[] _partSkinned;

        private static SpriteStudioAnimeAsset _labelsAsset;
        private static byte[] _labelsData;
        private static readonly Dictionary<string, string[]> _labels = new Dictionary<string, string[]>();

        public static SpriteStudioAnimeAsset GraphicAsset(SerializedObject so, out string animation)
        {
            animation = null;
            SpriteStudioAnimeAsset asset = null;
            string name = null;
            for (int i = 0; i < so.targetObjects.Length; i++)
            {
                var graphic = (so.targetObjects[i] as Component)?.GetComponent<SpriteStudioGraphic>();
                if (graphic == null || graphic.AnimeAsset == null) return null;
                if (i == 0)
                {
                    asset = graphic.AnimeAsset;
                    name = graphic.CurrentAnimation;
                }
                else
                {
                    if (graphic.AnimeAsset != asset) return null;
                    if (name != graphic.CurrentAnimation) name = null;
                }
            }
            if (asset == null) return null;

            if (name != null)
            {
                var names = asset.AnimationNames;
                animation = name.Length > 0 ? name : names != null && names.Length > 0 ? names[0] : null;
            }
            return asset;
        }

        public static void PartInfo(SpriteStudioAnimeAsset asset, out string[] names, out bool[] skinned)
        {
            if (asset == null || asset.SsabData == null || asset.SsabData.Length == 0)
            {
                names = null;
                skinned = null;
                return;
            }
            if (!ReferenceEquals(asset, _partsAsset) || !ReferenceEquals(asset.SsabData, _partsData))
            {
                var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(new Google.FlatBuffers.ByteBuffer(asset.SsabData));
                int n = ssab.PartsLength;
                _partNames = new string[n];
                _partSkinned = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    var p = ssab.Parts(i);
                    if (!p.HasValue) continue;
                    _partNames[i] = p.Value.Name;
                    // Same test the runtime uses (SpriteStudioSsabFormat), so the inspector and playback agree.
                    _partSkinned[i] = SpriteStudioSsabFormat.IsSkinnedMeshPart(p.Value);
                }
                _partsAsset = asset;
                _partsData = asset.SsabData;
            }
            names = _partNames;
            skinned = _partSkinned;
        }

        public static string[] Parts(SpriteStudioAnimeAsset asset)
        {
            PartInfo(asset, out string[] names, out _);
            return names;
        }

        public static string[] Labels(SpriteStudioAnimeAsset asset, string animation)
        {
            if (asset == null || asset.SsabData == null || asset.SsabData.Length == 0 || string.IsNullOrEmpty(animation))
                return null;
            if (!ReferenceEquals(asset, _labelsAsset) || !ReferenceEquals(asset.SsabData, _labelsData))
            {
                _labels.Clear();
                _labelsAsset = asset;
                _labelsData = asset.SsabData;
            }
            if (_labels.TryGetValue(animation, out string[] cached)) return cached;

            string[] found = null;
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(new Google.FlatBuffers.ByteBuffer(asset.SsabData));
            for (int i = 0; i < ssab.AnimationsLength; i++)
            {
                var anim = ssab.Animations(i);
                if (!anim.HasValue || anim.Value.Name != animation) continue;
                var labels = SpriteStudioLabelTable.Of(anim.Value).Labels;
                found = new string[labels.Count];
                for (int j = 0; j < labels.Count; j++) found[j] = labels[j].Name;
                break;
            }
            _labels[animation] = found;
            return found;
        }
    }
}
