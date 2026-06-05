using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// .ssqb インポーターのインスペクタ。
    /// .ssqb はインポート設定を持たないため、解析済みシーケンスの概要を読み取り専用で表示し、
    /// ランタイム再生が未実装である旨を明示する。
    /// </summary>
    [CustomEditor(typeof(SpriteStudioSsqbImporter))]
    public class SpriteStudioSsqbImporterEditor : ScriptedImporterEditor
    {
        public override void OnInspectorGUI()
        {
            var importer = target as SpriteStudioSsqbImporter;
            var asset = importer != null
                ? AssetDatabase.LoadAssetAtPath<SpriteStudioSequenceAsset>(importer.assetPath)
                : null;

            if (asset != null)
            {
                EditorGUILayout.LabelField($"Sequences ({asset.Sequences.Count})", EditorStyles.boldLabel);
                foreach (var seq in asset.Sequences)
                {
                    if (seq == null) continue;
                    EditorGUILayout.LabelField($"• {seq.Name}", $"{seq.Type}, {seq.Items.Count} items");
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField($"Referenced anime assets: {asset.ReferencedAnimeAssets.Count}");
            }

            EditorGUILayout.HelpBox(
                ".ssqb はアニメ再生リスト (シーケンス) です。現状は Unity がデータとして認識・参照する" +
                "ところまでをサポートします (ランタイム再生は未実装)。",
                MessageType.Info);

            ApplyRevertGUI();
        }
    }
}
