using UnityEngine;
using System.Collections.Generic;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// The import artifact of a .ssqb (SpriteStudio Sequence Binary), a ScriptableObject.
    /// Holds the sequence (animation playback list) metadata and hard references to the referenced anime
    /// packs (SpriteStudioAnimeAsset).
    ///
    /// Runtime sequence playback (automatic chaining / repetition of animations) is not implemented here.
    /// This asset only takes it as far as "Unity can recognize and reference the .ssqb as data".
    /// </summary>
    [UnityEngine.Icon("Packages/jp.co.cri-mw.optpix.ssplayer-unity/Editor/Icons/ssqb_icon.png")]
    public class SpriteStudioSequenceAsset : ScriptableObject
    {
        [Tooltip("Converted .ssqb binary data")]
        [HideInInspector]
        public byte[] SsqbData;

        [Tooltip("List of sequence definitions contained in the file")]
        public List<SequenceEntry> Sequences = new List<SequenceEntry>();

        [Tooltip("Hard references to the anime packs (SpriteStudioAnimeAsset) the sequence refers to. " +
                 "Kept here (unless unassigned) to prevent build stripping / for stable GUID references.")]
        public List<SpriteStudioAnimeAsset> ReferencedAnimeAssets = new List<SpriteStudioAnimeAsset>();

        /// <summary>
        /// Behavior at the end of a sequence. Same values as ss.format.SequenceType (Last=0 / Keep=1 / Top=2).
        /// Mirrored to decouple the ScriptableObject's public API from a dependency on the generated code (ss.format).
        /// </summary>
        public enum SequenceTermination : byte
        {
            Last = 0,
            Keep = 1,
            Top = 2,
        }

        [System.Serializable]
        public class SequenceItemEntry
        {
            [Tooltip("Referenced anime pack name")]
            public string RefAnimePack;
            [Tooltip("Hash of the referenced anime pack name (fnv1a)")]
            public uint RefAnimePackHash;
            [Tooltip("Referenced animation name")]
            public string RefAnimeName;
            [Tooltip("Hash of the referenced animation name (fnv1a)")]
            public uint RefAnimeNameHash;
            [Tooltip("Repeat count for this item")]
            public int RepeatCount;
        }

        [System.Serializable]
        public class SequenceEntry
        {
            [Tooltip("Sequence name")]
            public string Name;
            [Tooltip("Hash of the sequence name (fnv1a)")]
            public uint NameHash;
            [Tooltip("Sequence index")]
            public int Index;
            [Tooltip("Behavior at the end of the sequence")]
            public SequenceTermination Type;
            [Tooltip("Items in playback order (each item is 1 animation reference + a repeat count)")]
            public List<SequenceItemEntry> Items = new List<SequenceItemEntry>();
        }
    }
}
