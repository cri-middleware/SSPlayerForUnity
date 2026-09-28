using UnityEngine;
using UnityEditor;
using SpriteStudio.Runtime;
using System.IO;

namespace SpriteStudio.Editor
{
    [CustomEditor(typeof(SpriteStudioAnimeAsset))]
    public class SpriteStudioAnimeAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            // Unity disables GUI.enabled for imported assets. We need to re-enable it for our custom buttons.
            bool wasEnabled = GUI.enabled;
            GUI.enabled = true;

            string ssabPath = AssetDatabase.GetAssetPath(target);
            string sspjPath = SpriteStudioSourceMap.GetSspjForSsab(ssabPath);
            bool hasSspj = !string.IsNullOrEmpty(sspjPath) && File.Exists(sspjPath);

            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Source SSPJ", EditorStyles.boldLabel);
            
            if (hasSspj)
            {
                GUILayout.Label(sspjPath, EditorStyles.wordWrappedMiniLabel);
                
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Open SSPJ"))
                {
                    EditorUtility.OpenWithDefaultApp(sspjPath);
                }
                
                if (GUILayout.Button("Reconvert"))
                {
                    // Back into the folder this asset lives in, not the Output Folder setting: the folder
                    // may have been moved or renamed since it was generated.
                    SpriteStudioImporter.ReconvertInto(sspjPath, Path.GetDirectoryName(ssabPath));
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUIStyle warningStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
                warningStyle.normal.textColor = Color.yellow;
                GUILayout.Label("Source SSPJ not found in map or doesn't exist.", warningStyle);

                // Broken-link repair: re-pick the .sspj and batch re-link + reconvert the .ssab files in the same output folder
                if (GUILayout.Button("Relink SSPJ..."))
                {
                    SpriteStudioReconvertMenu.RelinkSsab(ssabPath);
                }
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space();

            if (GUILayout.Button("Instantiate in Scene", GUILayout.Height(30)))
            {
                InstantiatePlayer((SpriteStudioAnimeAsset)target);
            }

            GUI.enabled = wasEnabled;

            EditorGUILayout.Space();
            base.OnInspectorGUI();
        }

        [MenuItem("Assets/SpriteStudio/Instantiate Player", false, 10)]
        public static void CreatePlayerFromContextMenu()
        {
            if (Selection.activeObject is SpriteStudioAnimeAsset asset)
            {
                InstantiatePlayer(asset);
            }
        }

        [MenuItem("Assets/SpriteStudio/Instantiate Player", true)]
        public static bool ValidateCreatePlayerFromContextMenu()
        {
            return Selection.activeObject is SpriteStudioAnimeAsset;
        }

        // The same placement a drop onto the Scene view's empty space makes: at the root of the stage being
        // edited, selected. The player assigns its own materials when it first draws.
        private static void InstantiatePlayer(SpriteStudioAnimeAsset asset)
        {
            SpriteStudioDragAndDrop.InstantiatePlayers(new System.Collections.Generic.List<SpriteStudioAnimeAsset> { asset }, null, null, false);
        }
    }
}
