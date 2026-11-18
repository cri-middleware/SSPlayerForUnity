using UnityEngine;
using UnityEditor;
using SpriteStudio.Runtime;

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

            SpriteStudioSourceSspjGUI.DrawBox(AssetDatabase.GetAssetPath(target));

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
