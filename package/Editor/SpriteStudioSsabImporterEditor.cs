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
                "Use a shared per-texture material (.mat). When OFF, no material is assigned."));

            using (new EditorGUI.DisabledScope(!_generateMaterials.boolValue))
            {
                EditorGUILayout.PropertyField(_overrideShader, new GUIContent(
                    "Override Shader",
                    "Shader a newly created .mat uses; SpriteStudio/Default when unset. A .mat that already exists keeps its own shader: change it on the .mat."));
            }

            EditorGUILayout.HelpBox(
                "Materials are treated as shared per-texture .mat assets (Spine-Unity style). " +
                "Multiple .ssab files referencing the same texture share the same .mat.",
                MessageType.None);

            serializedObject.ApplyModifiedProperties();

            // ScriptedImporterEditor must always draw Apply/Revert
            ApplyRevertGUI();
        }
    }
}
