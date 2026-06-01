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
        
        // TODO: アニメーション名からインデックスを引くための辞書情報など
    }
}
