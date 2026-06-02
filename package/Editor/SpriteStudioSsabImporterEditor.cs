using UnityEngine;
using UnityEditor;
using UnityEditor.AssetImporters;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// .ssab インポーターの設定 UI。
    /// マテリアル生成設定 (共有 .mat 方式) のみを公開する。
    /// プレビューおよび imported-object のインスペクタ (SpriteStudioAnimeAssetEditor) は
    /// ScriptedImporterEditor の標準描画に委譲する。
    /// </summary>
    [CustomEditor(typeof(SpriteStudioSsabImporter))]
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
                "テクスチャ単位の共有マテリアル(.mat)を使用します。OFF の場合はマテリアルを割り当てません。"));

            using (new EditorGUI.DisabledScope(!_generateMaterials.boolValue))
            {
                EditorGUILayout.PropertyField(_overrideShader, new GUIContent(
                    "Override Shader",
                    "マテリアル生成に使うシェーダ。未指定なら SpriteStudio/Default。"));
            }

            EditorGUILayout.HelpBox(
                "マテリアルはテクスチャ単位の共有 .mat として扱われます (Spine-Unity 方式)。" +
                "同じテクスチャを参照する複数の .ssab が同一の .mat を共有します。",
                MessageType.None);

            serializedObject.ApplyModifiedProperties();

            // ScriptedImporterEditor では Apply/Revert を必ず描画する
            ApplyRevertGUI();
        }
    }
}
