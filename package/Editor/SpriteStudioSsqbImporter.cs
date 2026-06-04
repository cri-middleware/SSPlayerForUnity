using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor.AssetImporters;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// .ssqb (事前コンバート済みシーケンスバイナリ) のインポーター。
    /// FlatBuffers を解釈し、SpriteStudioSequenceAsset (ScriptableObject) を生成する。
    /// 参照アニメパックは <see cref="SpriteStudioAnimeAssetBuilder.ResolveSsabPath"/> で
    /// プロジェクト内の SpriteStudioAnimeAsset へ解決し、ハードリファレンスする。
    ///
    /// シーケンス再生のランタイム実装は本インポーターの対象外。Unity が .ssqb を
    /// データとして認識・参照できる状態を作るところまでを担う。
    /// </summary>
    [ScriptedImporter(1, "ssqb")]
    [UnityEngine.Icon("Packages/com.spritestudio.ssruntime/Editor/Icons/ssqb_icon.png")]
    public class SpriteStudioSsqbImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var asset = ScriptableObject.CreateInstance<SpriteStudioSequenceAsset>();
            asset.SsqbData = File.ReadAllBytes(ctx.assetPath);

            var bb = new Google.FlatBuffers.ByteBuffer(asset.SsqbData);
            // 入力検証: コンバータ出力以外 (拡張子だけ .ssqb の別物など) を弾き、原因が分かるエラーにする。
            // 識別子は先頭オフセット(4)+マジック(4) を読むので最低 8 バイト必要。
            if (asset.SsqbData.Length < 8 || !ss.format.SsSequenceBinary.SsSequenceBinaryBufferHasIdentifier(bb))
            {
                ctx.LogImportError($"[SpriteStudioSsqbImporter] Not a valid .ssqb (FlatBuffers 'SSQB' identifier missing): {ctx.assetPath}");
                ctx.AddObjectToAsset("MainAsset", asset);
                ctx.SetMainObject(asset);
                return;
            }

            var ssqb = ss.format.SsSequenceBinary.GetRootAsSsSequenceBinary(bb);
            string baseDir = Path.GetDirectoryName(ctx.assetPath);

            // 参照アニメパックの重複解決を避けるための記録。
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
                    // 名前で明示マッピング (値キャストではない)。上流 .fbs が値を並べ替えても破綻しない。
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

                    // 参照アニメパック (.ssab) をプロジェクト内アセットへ解決し、ハードリファレンスする。
                    // 解決ロジックは .ssab importer と共有 (ResolveSsabPath)。未解決でも (まだ未インポート
                    // 等で) 依存登録は済んでいるので、対象が入った時点で再インポートされ解決される。
                    if (!string.IsNullOrEmpty(it.RefAnimePack) && seenPacks.Add(it.RefAnimePack))
                    {
                        SpriteStudioAnimeAssetBuilder.ResolveSsabPath(baseDir, it.RefAnimePack, out var animeAsset, ctx.DependsOnSourceAsset);
                        if (animeAsset != null)
                        {
                            if (seenAssets.Add(animeAsset)) asset.ReferencedAnimeAssets.Add(animeAsset);
                        }
                        else
                        {
                            Debug.LogWarning($"[SpriteStudioSsqbImporter] Referenced anime pack not found: '{it.RefAnimePack}' (sequence '{seq.Name}')");
                        }
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
        /// ss.format.SequenceType を SequenceTermination へ名前ベースで写す。
        /// 値キャストと違い、上流 .fbs の値順が変わっても (列挙子名で対応するため) 破綻しない。
        /// 未知値は安全側の Last にフォールバックする。
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
