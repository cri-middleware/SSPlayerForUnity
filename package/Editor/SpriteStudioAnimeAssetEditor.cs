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

            string sspjPath = SpriteStudioSourceMap.GetSspjForSsab(AssetDatabase.GetAssetPath(target));
            bool hasSspj = !string.IsNullOrEmpty(sspjPath) && File.Exists(sspjPath);

            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Source SSPJ", EditorStyles.boldLabel);
            
            if (hasSspj)
            {
                GUILayout.Label(sspjPath, EditorStyles.wordWrappedMiniLabel);
                
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Open SSPJ"))
                {
                    Application.OpenURL("file://" + sspjPath);
                }
                
                if (GUILayout.Button("Reconvert"))
                {
                    SpriteStudioImporterWindow.RunConvertDirect(sspjPath);
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
                    SpriteStudioReconvertMenu.RelinkSsab(AssetDatabase.GetAssetPath(target));
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

        private static void InstantiatePlayer(SpriteStudioAnimeAsset asset)
        {
            var go = new GameObject(asset.name);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;
            if (asset.AnimationNames != null && asset.AnimationNames.Length > 0)
            {
                player.AnimationName = asset.AnimationNames[0];
            }
            
            if (asset.Materials != null && asset.Materials.Count > 0)
            {
                var renderer = go.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = asset.Materials.ToArray();
            }

            Undo.RegisterCreatedObjectUndo(go, "Create SpriteStudioPlayer");
            Selection.activeGameObject = go;
        }
    }
}
