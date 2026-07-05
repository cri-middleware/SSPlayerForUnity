using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Inspector for the .ssqb importer.
    /// .ssqb has no import settings, so it shows a read-only summary of the parsed sequences
    /// and makes clear that runtime playback is not yet implemented.
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
                ".ssqb is an animation playlist (sequence). Currently support extends to Unity recognizing and " +
                "referencing it as data (runtime playback is not yet implemented).",
                MessageType.Info);

            ApplyRevertGUI();
        }
    }
}
