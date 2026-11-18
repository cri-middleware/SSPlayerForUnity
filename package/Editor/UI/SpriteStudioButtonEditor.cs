using UnityEditor;
using UnityEditor.UI;

namespace SpriteStudio.UI.Editor
{
    // ButtonEditor draws Selectable's and Button's own fields by name, so the button's would not show.
    [CustomEditor(typeof(SpriteStudioButton), true)]
    [CanEditMultipleObjects]
    public class SpriteStudioButtonEditor : ButtonEditor
    {
        private SerializedProperty _reducedMotionProp;
        private SerializedProperty _stateClipsProp;
        private SerializedProperty _accessibilityLabelProp;

        protected override void OnEnable()
        {
            base.OnEnable();
            _reducedMotionProp = serializedObject.FindProperty("_reducedMotion");
            _stateClipsProp = serializedObject.FindProperty("_stateClips");
            _accessibilityLabelProp = serializedObject.FindProperty("_accessibilityLabel");
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            EditorGUILayout.Space();
            serializedObject.Update();
            EditorGUILayout.PropertyField(_reducedMotionProp);
            EditorGUILayout.PropertyField(_stateClipsProp, true);
            EditorGUILayout.PropertyField(_accessibilityLabelProp);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
