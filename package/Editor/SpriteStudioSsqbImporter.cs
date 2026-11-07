using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor.AssetImporters;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Importer for .ssqb (pre-converted sequence binary).
    /// Interprets the FlatBuffers and generates a SpriteStudioSequenceAsset (ScriptableObject).
    /// Referenced anime packs are resolved to SpriteStudioAnimeAsset in the project via
    /// <see cref="SpriteStudioAnimeAssetBuilder.ResolveSsabPath"/> and hard-referenced.
    ///
    /// The runtime implementation of sequence playback is out of scope for this importer. It only goes as
    /// far as making the .ssqb recognizable and referenceable as data by Unity.
    /// </summary>
    [ScriptedImporter(1, "ssqb")]
    [UnityEngine.Icon("Packages/jp.co.cri-mw.spritestudio.ssplayer-unity/Editor/Icons/ssqb_icon.png")]
    public class SpriteStudioSsqbImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var asset = ScriptableObject.CreateInstance<SpriteStudioSequenceAsset>();
            asset.SsqbData = File.ReadAllBytes(ctx.assetPath);

            var bb = new Google.FlatBuffers.ByteBuffer(asset.SsqbData);
            // Input validation: reject anything that is not converter output (e.g. an unrelated file that
            // merely has the .ssqb extension) and surface an error that explains the cause.
            // Reading the identifier needs the leading offset (4) + magic (4), so at least 8 bytes are required.
            if (asset.SsqbData.Length < 8 || !ss.format.SsSequenceBinary.SsSequenceBinaryBufferHasIdentifier(bb))
            {
                ctx.LogImportError($"[SpriteStudioSsqbImporter] Not a valid .ssqb (FlatBuffers 'SSQB' identifier missing): {ctx.assetPath}");
                ctx.AddObjectToAsset("MainAsset", asset);
                ctx.SetMainObject(asset);
                return;
            }

            var ssqb = ss.format.SsSequenceBinary.GetRootAsSsSequenceBinary(bb);
            string baseDir = Path.GetDirectoryName(ctx.assetPath);

            // Records to avoid resolving the same referenced anime pack more than once.
            var seenPacks = new HashSet<string>();
            var seenAssets = new HashSet<SpriteStudioAnimeAsset>();

            int seqLen = ssqb.SequenceListLength;
            for (int s = 0; s < seqLen; s++)
            {
                var seqOpt = ssqb.SequenceList(s);
                if (!seqOpt.HasValue) continue;
                var seq = seqOpt.Value;

                var entry = new SpriteStudioSequenceAsset.SequenceEntry
                {
                    Name = seq.Name,
                    NameHash = seq.NameHash,
                    Index = seq.Index,
                    // Explicit mapping by name (not a value cast). Does not break even if the upstream .fbs reorders values.
                    Type = ToTermination(seq.Type),
                };

                int itemLen = seq.ListLength;
                for (int i = 0; i < itemLen; i++)
                {
                    var itOpt = seq.List(i);
                    if (!itOpt.HasValue) continue;
                    var it = itOpt.Value;

                    entry.Items.Add(new SpriteStudioSequenceAsset.SequenceItemEntry
                    {
                        RefAnimePack = it.RefAnimePack,
                        RefAnimePackHash = it.RefAnimePackHash,
                        RefAnimeName = it.RefAnimeName,
                        RefAnimeNameHash = it.RefAnimeNameHash,
                        RepeatCount = it.RepeatCount,
                    });

                    // Resolve the referenced anime pack (.ssab) to a project asset and hard-reference it.
                    // The resolution logic is shared with the .ssab importer (ResolveSsabPath), and so is the
                    // artifact dependency that makes the referenced pack import first (ArtifactDependency).
                    if (!string.IsNullOrEmpty(it.RefAnimePack) && seenPacks.Add(it.RefAnimePack))
                    {
                        SpriteStudioAnimeAssetBuilder.ResolveSsabPath(baseDir, it.RefAnimePack, out var animeAsset, SpriteStudioAnimeAssetBuilder.ArtifactDependency(ctx));
                        // A missing pack is not reported: one the conversion has not written or imported yet lands here too.
                        if (animeAsset != null && seenAssets.Add(animeAsset)) asset.ReferencedAnimeAssets.Add(animeAsset);
                    }
                }

                asset.Sequences.Add(entry);
            }

            if (SpriteStudioAnimeAssetBuilder.VerboseImportLog)
                Debug.Log($"[SS-IMPORT] {ctx.assetPath} -> sequences:{asset.Sequences.Count} animeRefs:{asset.ReferencedAnimeAssets.Count}");

            ctx.AddObjectToAsset("MainAsset", asset);
            ctx.SetMainObject(asset);
        }

        /// <summary>
        /// Maps ss.format.SequenceType to SequenceTermination by name.
        /// Unlike a value cast, it does not break even if the upstream .fbs value order changes (since it
        /// matches by enumerator name).
        /// Unknown values fall back to the safe-side Last.
        /// </summary>
        private static SpriteStudioSequenceAsset.SequenceTermination ToTermination(ss.format.SequenceType t)
        {
            switch (t)
            {
                case ss.format.SequenceType.Last: return SpriteStudioSequenceAsset.SequenceTermination.Last;
                case ss.format.SequenceType.Keep: return SpriteStudioSequenceAsset.SequenceTermination.Keep;
                case ss.format.SequenceType.Top:  return SpriteStudioSequenceAsset.SequenceTermination.Top;
                default:                          return SpriteStudioSequenceAsset.SequenceTermination.Last;
            }
        }
    }
}
