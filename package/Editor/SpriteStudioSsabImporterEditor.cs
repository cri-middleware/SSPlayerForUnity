using UnityEngine;
using UnityEditor;
using UnityEditor.AssetImporters;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Settings UI for the .ssab importer.
    /// Exposes only the material-generation settings (shared .mat approach).
    /// The preview and the imported-object inspector (SpriteStudioAnimeAssetEditor) are
    /// delegated to the ScriptedImporterEditor's default drawing.
    /// </summary>
    [CustomEditor(typeof(SpriteStudioSsabImporter))]
    [CanEditMultipleObjects]
    public class SpriteStudioSsabImporterEditor : ScriptedImporterEditor
    {
        private SerializedProperty _generateMaterials;
        private SerializedProperty _overrideShader;

        public override void OnEnable()
        {
            base.OnEnable();
            _generateMaterials = serializedObject.FindProperty("GenerateMaterials");
            _overrideShader = serializedObject.FindProperty("OverrideShader");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Material Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_generateMaterials, new GUIContent(
                "Generate Materials",
                "Write a shared per-texture material (.mat) next to each texture and use it. When OFF, no .mat is written or used: the materials are made at runtime, and an existing .mat is left in place but no longer referenced."));

            EditorGUILayout.PropertyField(_overrideShader, new GUIContent(
                "Override Shader",
                "Shader a newly created .mat and a material made at runtime use; SpriteStudio/Default when unset. A .mat that already exists keeps its own shader: change it on the .mat."));

            EditorGUILayout.HelpBox(
                "Each texture's material is shared by every .ssab that uses the texture, " +
                "whether it is a .mat or made at runtime.",
                MessageType.None);

            serializedObject.ApplyModifiedProperties();

            // ScriptedImporterEditor must always draw Apply/Revert
            ApplyRevertGUI();
        }
    }
}
