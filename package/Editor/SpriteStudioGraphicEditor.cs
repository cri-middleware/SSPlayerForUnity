using UnityEditor;
using UnityEngine;
using SpriteStudio.Runtime;
using UnityEditor.UI;

namespace SpriteStudio.Editor
{
    [CustomEditor(typeof(SpriteStudioGraphic))]
    [CanEditMultipleObjects]
    public class SpriteStudioGraphicEditor : GraphicEditor
    {
        private SerializedProperty _animeAssetProp;
        private SerializedProperty _animationNameProp;
        private SerializedProperty _autoPlayProp;
        private SerializedProperty _loopProp;
        private SerializedProperty _speedScaleProp;
        private SerializedProperty _subFrameEnabledProp;

        protected override void OnEnable()
        {
            base.OnEnable();
            _animeAssetProp = serializedObject.FindProperty("_animeAsset");
            _animationNameProp = serializedObject.FindProperty("_animationName");
            _autoPlayProp = serializedObject.FindProperty("_autoPlay");
            _loopProp = serializedObject.FindProperty("_loop");
            _speedScaleProp = serializedObject.FindProperty("_speedScale");
            _subFrameEnabledProp = serializedObject.FindProperty("_subFrameEnabled");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_animeAssetProp);

            DrawAnimationNameDropdown();

            EditorGUILayout.PropertyField(_autoPlayProp);
            EditorGUILayout.PropertyField(_loopProp);
            EditorGUILayout.PropertyField(_speedScaleProp);
            EditorGUILayout.PropertyField(_subFrameEnabledProp);

            EditorGUILayout.Space();
            AppearanceControlsGUI();
            RaycastControlsGUI();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawAnimationNameDropdown()
        {
            var asset = _animeAssetProp.objectReferenceValue as SpriteStudioAnimeAsset;
            if (asset == null || asset.AnimationNames == null || asset.AnimationNames.Length == 0)
            {
                EditorGUILayout.PropertyField(_animationNameProp);
                return;
            }

            string currentName = _animationNameProp.stringValue;
            int currentIndex = Mathf.Max(0, System.Array.IndexOf(asset.AnimationNames, currentName));

            int newIndex = EditorGUILayout.Popup("Animation Name", currentIndex, asset.AnimationNames);
            if (newIndex >= 0 && newIndex < asset.AnimationNames.Length)
            {
                _animationNameProp.stringValue = asset.AnimationNames[newIndex];
            }
        }
    }
}
