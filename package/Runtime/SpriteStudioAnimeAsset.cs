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

        [Tooltip("マテリアル辞書: 描画に必要なベースマテリアル群への参照")]
        public List<Material> Materials = new List<Material>();
        
        [Tooltip("ファイル内に含まれるアニメーション名のリスト")]
        public string[] AnimationNames;

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
    }
}
