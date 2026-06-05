using UnityEngine;
using System.Collections.Generic;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// .ssqb (SpriteStudio Sequence Binary) のインポート成果物となる ScriptableObject。
    /// シーケンス (アニメ再生リスト) のメタデータと、参照アニメパック (SpriteStudioAnimeAsset) への
    /// ハードリファレンスを保持する。
    ///
    /// ランタイムでのシーケンス再生 (アニメの自動連結・繰り返し) はここでは実装しない。
    /// 本アセットは「Unity が .ssqb をデータとして認識・参照できる」ところまでを担う。
    /// </summary>
    [UnityEngine.Icon("Packages/com.spritestudio.ssruntime/Editor/Icons/ssqb_icon.png")]
    public class SpriteStudioSequenceAsset : ScriptableObject
    {
        [Tooltip("コンバート済みの .ssqb バイナリデータ")]
        [HideInInspector]
        public byte[] SsqbData;

        [Tooltip("ファイルに含まれるシーケンス定義のリスト")]
        public List<SequenceEntry> Sequences = new List<SequenceEntry>();

        [Tooltip("シーケンスが参照するアニメパック (SpriteStudioAnimeAsset) へのハードリファレンス。" +
                 "ビルドストリッピング防止 / GUID 安定参照のため、未配置でなければここに積む。")]
        public List<SpriteStudioAnimeAsset> ReferencedAnimeAssets = new List<SpriteStudioAnimeAsset>();

        /// <summary>
        /// シーケンス終端時の挙動。ss.format.SequenceType と同じ値 (Last=0 / Keep=1 / Top=2)。
        /// 生成コード (ss.format) への依存を ScriptableObject の公開 API から切り離すためにミラーする。
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
            [Tooltip("参照アニメパック名")]
            public string RefAnimePack;
            [Tooltip("参照アニメパック名のハッシュ (fnv1a)")]
            public uint RefAnimePackHash;
            [Tooltip("参照アニメ名")]
            public string RefAnimeName;
            [Tooltip("参照アニメ名のハッシュ (fnv1a)")]
            public uint RefAnimeNameHash;
            [Tooltip("このアイテムの繰り返し回数")]
            public int RepeatCount;
        }

        [System.Serializable]
        public class SequenceEntry
        {
            [Tooltip("シーケンス名")]
            public string Name;
            [Tooltip("シーケンス名のハッシュ (fnv1a)")]
            public uint NameHash;
            [Tooltip("シーケンスのインデックス")]
            public int Index;
            [Tooltip("シーケンス終端時の挙動")]
            public SequenceTermination Type;
            [Tooltip("再生順に並んだアイテム (各アイテムは 1 アニメ参照 + 繰り返し回数)")]
            public List<SequenceItemEntry> Items = new List<SequenceItemEntry>();
        }
    }
}
