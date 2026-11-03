using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    internal static class SpriteStudioSourceSspjGUI
    {
        private static readonly GUIContent _relink = new GUIContent("Relink SSPJ...",
            "Source SSPJ not found in map or doesn't exist. Pick it to relink every .ssab in this folder and reconvert.");

        public static void DrawBox(string ssabPath)
        {
            string sspjPath = LinkedSspj(ssabPath);

            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Source SSPJ", EditorStyles.boldLabel);

            if (sspjPath != null)
            {
                GUILayout.Label(sspjPath, EditorStyles.wordWrappedMiniLabel);
                DrawButtons(ssabPath, sspjPath);
            }
            else
            {
                GUIStyle warningStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
                warningStyle.normal.textColor = Color.yellow;
                GUILayout.Label("Source SSPJ not found in map or doesn't exist.", warningStyle);
                DrawRelink(ssabPath);
            }
            GUILayout.EndVertical();
        }

        public static void DrawRow(SerializedProperty assetProp)
        {
            string ssabPath = SsabPathOf(assetProp);
            if (ssabPath == null) return;

            string sspjPath = LinkedSspj(ssabPath);
            if (sspjPath != null) DrawButtons(ssabPath, sspjPath);
            else DrawRelink(ssabPath);
        }

        internal static string SsabPathOf(SerializedProperty assetProp)
        {
            if (assetProp.hasMultipleDifferentValues) return null;
            if (!(assetProp.objectReferenceValue is SpriteStudioAnimeAsset asset)) return null;
            string path = AssetDatabase.GetAssetPath(asset);
            return path.EndsWith(".ssab", StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        private static string LinkedSspj(string ssabPath)
        {
            string sspjPath = SpriteStudioSourceMap.GetSspjForSsab(ssabPath);
            return !string.IsNullOrEmpty(sspjPath) && File.Exists(sspjPath) ? sspjPath : null;
        }

        private static void DrawButtons(string ssabPath, string sspjPath)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Open SSPJ", sspjPath)))
                {
                    EditorUtility.OpenWithDefaultApp(sspjPath);
                }

                if (GUILayout.Button(new GUIContent("Reconvert", "Reconvert from " + sspjPath)))
                {
                    // Back into the folder this asset lives in, not the Output Folder setting: the folder
                    // may have been moved or renamed since it was generated.
                    SpriteStudioImporter.ReconvertInto(sspjPath, Path.GetDirectoryName(ssabPath));
                }
            }
        }

        private static void DrawRelink(string ssabPath)
        {
            // Broken-link repair: re-pick the .sspj and batch re-link + reconvert the .ssab files in the same output folder
            if (GUILayout.Button(_relink))
            {
                SpriteStudioReconvertMenu.RelinkSsab(ssabPath);
                GUIUtility.ExitGUI();
            }
        }
    }
}
