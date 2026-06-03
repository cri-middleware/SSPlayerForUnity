using UnityEngine;
using System.Collections.Generic;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// インポート処理の最終成果物となる ScriptableObject。
    /// .ssab バイト配列の保持や、テクスチャ等のハードリファレンス管理を行います。
    /// </summary>
    public class SpriteStudioAnimeAsset : ScriptableObject
    {
        [Tooltip("コンバート済みの .ssab バイナリデータ")]
        [HideInInspector]
        public byte[] SsabData;

        [Tooltip("テクスチャのビルドストリッピング防止用ハードリファレンス")]
        public List<Texture2D> Textures = new List<Texture2D>();

        [Tooltip("外部アニメーション参照（Instance パーツ用）")]
        public List<SpriteStudioAnimeAsset> ExternalReferences = new List<SpriteStudioAnimeAsset>();

        [Tooltip("マテリアル辞書: 描画に必要なベースマテリアル群への参照")]
        public List<Material> Materials = new List<Material>();
        
        [Tooltip("ファイル内に含まれるアニメーション名のリスト")]
        public string[] AnimationNames;
        
        [HideInInspector]
        public uint PackHash;

        /// <summary>
        /// Instance パーツが要求する子アニメーションの .ssab バイナリを解決して返す。
        /// Godot の SsInternalPlayer::_resolve_ssab_by_hash と同じ優先順位:
        ///   1. 別パック (外部 .ssab) への参照は、インポート時に確定した ExternalReferences を pack_hash で引く。
        ///   2. 同一パック内 (自分自身) のアニメ参照は、自身の SsabData を返す。
        /// SpriteStudio の Instance は同一プロジェクト内参照が大半のため、2 が主経路になる。
        /// 実行時はファイルを一切読まず、ハードリファレンスを辿るだけ (GUID 参照のため
        /// Resources/AssetBundle/Addressables でも安全)。
        /// </summary>
        /// <param name="packHash">参照先アニメパックの名前ハッシュ (PartTypeInstance.RefAnimePackHash)。</param>
        /// <param name="nameHash">参照先アニメの名前ハッシュ。解決後に SetupAnimationByHash で使用。</param>
        public byte[] ResolveExternalSsab(uint packHash, uint nameHash)
        {
            // 1. 別パックへの参照: pack_hash 一致を最優先で外部参照から引く。
            if (packHash != 0 && ExternalReferences != null)
            {
                for (int i = 0; i < ExternalReferences.Count; i++)
                {
                    var ext = ExternalReferences[i];
                    if (ext != null && ext.PackHash == packHash)
                    {
                        return ext.SsabData;
                    }
                }
            }

            // 2. 同一パック内 (自分自身) のアニメ参照。pack_hash 未設定 (0) も現バイナリにフォールバックする。
            if (packHash == 0 || packHash == PackHash)
            {
                return SsabData;
            }

            // 3. フォールバック (pack 名ハッシュの運用差で 1 が一致しない場合の保険)。
            //    Godot の find_animation_by_hash と同様、要求アニメ名ハッシュを実際に含む ssab を
            //    自身 → 外部参照 の順で探す。pack_hash に依存せず解決できる。
            if (nameHash != 0)
            {
                if (ContainsAnimationHash(nameHash)) return SsabData;
                if (ExternalReferences != null)
                {
                    for (int i = 0; i < ExternalReferences.Count; i++)
                    {
                        var ext = ExternalReferences[i];
                        if (ext != null && ext.ContainsAnimationHash(nameHash)) return ext.SsabData;
                    }
                }
            }

            return null;
        }

        [System.NonSerialized] private HashSet<uint> _animationHashes;

        /// <summary>この ssab が指定アニメ名ハッシュのアニメーションを含むか (Animation.NameHash で判定)。</summary>
        public bool ContainsAnimationHash(uint nameHash)
        {
            if (_animationHashes == null)
            {
                _animationHashes = new HashSet<uint>();
                if (SsabData != null && SsabData.Length > 0)
                {
                    var bb = new Google.FlatBuffers.ByteBuffer(SsabData);
                    var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
                    int n = ssab.AnimationsLength;
                    for (int i = 0; i < n; i++)
                    {
                        var a = ssab.Animations(i);
                        if (a.HasValue) _animationHashes.Add(a.Value.NameHash);
                    }
                }
            }
            return _animationHashes.Contains(nameHash);
        }

        // DrawBatch.TextureHash → Materials のインデックス対応表 (並列配列で直列化)。
        // 1つのテクスチャに複数の候補ハッシュ (cellmap の NameHash/ImagePathHash, external の NameHash) が
        // 対応しうるため、どれが来ても引けるよう全候補を登録している。
        [HideInInspector] public uint[] TextureHashKeys;
        [HideInInspector] public int[] TextureHashMaterialIndices;

        [System.NonSerialized] private Dictionary<uint, int> _hashToMaterial;

        /// <summary>
        /// DrawBatch のテクスチャハッシュに対応する Materials のインデックスを返す。
        /// 対応が見つからない場合は -1 (呼び出し側で material[0] 等にフォールバックする)。
        /// </summary>
        public int GetMaterialIndexForHash(uint textureHash)
        {
            if (_hashToMaterial == null)
            {
                _hashToMaterial = new Dictionary<uint, int>();
                if (TextureHashKeys != null && TextureHashMaterialIndices != null)
                {
                    int n = Mathf.Min(TextureHashKeys.Length, TextureHashMaterialIndices.Length);
                    for (int i = 0; i < n; i++)
                    {
                        _hashToMaterial[TextureHashKeys[i]] = TextureHashMaterialIndices[i];
                    }
                }
            }
            return _hashToMaterial.TryGetValue(textureHash, out int index) ? index : -1;
        }

        /// <summary>
        /// DrawBatch のテクスチャハッシュに対応する Material を返す。
        /// まず自身のテーブルを引き、無ければ Instance 参照先 (外部 .ssab) のテーブルも探索する。
        /// Instance の子は外部 .ssab のテクスチャ/マテリアルを使うため、外部参照側でしか解決できない
        /// ハッシュがサブメッシュとして親へ上がってくる。見つからなければ null (呼び出し側でフォールバック)。
        /// </summary>
        public Material ResolveMaterialForHash(uint textureHash)
        {
            int mi = GetMaterialIndexForHash(textureHash);
            if (mi >= 0 && Materials != null && mi < Materials.Count)
            {
                return Materials[mi];
            }

            if (ExternalReferences != null)
            {
                for (int i = 0; i < ExternalReferences.Count; i++)
                {
                    var ext = ExternalReferences[i];
                    if (ext == null) continue;
                    int emi = ext.GetMaterialIndexForHash(textureHash);
                    if (emi >= 0 && ext.Materials != null && emi < ext.Materials.Count)
                    {
                        return ext.Materials[emi];
                    }
                }
            }
            return null;
        }
    }
}
