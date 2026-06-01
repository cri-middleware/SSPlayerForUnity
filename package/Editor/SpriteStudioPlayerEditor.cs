using UnityEditor;
using UnityEngine;
using SpriteStudio.Runtime;
using System.Linq;

namespace SpriteStudio.Editor
{
    [CustomEditor(typeof(SpriteStudioPlayer))]
    [CanEditMultipleObjects]
    public class SpriteStudioPlayerEditor : UnityEditor.Editor
    {
        private SerializedProperty _animeAssetProp;
        private SerializedProperty _animationNameProp;
        private SerializedProperty _autoPlayProp;
        private SerializedProperty _loopProp;
        private SerializedProperty _speedScaleProp;
        private SerializedProperty _subFrameEnabledProp;
        private SerializedProperty _sortingLayerNameProp;
        private SerializedProperty _sortingOrderProp;

        private void OnEnable()
        {
            _animeAssetProp = serializedObject.FindProperty("_animeAsset");
            _animationNameProp = serializedObject.FindProperty("_animationName");
            _autoPlayProp = serializedObject.FindProperty("_autoPlay");
            _loopProp = serializedObject.FindProperty("_loop");
            _speedScaleProp = serializedObject.FindProperty("_speedScale");
            _subFrameEnabledProp = serializedObject.FindProperty("_subFrameEnabled");
            _sortingLayerNameProp = serializedObject.FindProperty("_sortingLayerName");
            _sortingOrderProp = serializedObject.FindProperty("_sortingOrder");
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
            EditorGUILayout.PropertyField(_sortingLayerNameProp);
            EditorGUILayout.PropertyField(_sortingOrderProp);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawAnimationNameDropdown()
        {
            var asset = _animeAssetProp.objectReferenceValue as SpriteStudioAnimeAsset;
            if (asset == null || asset.AnimationNames == null || asset.AnimationNames.Length == 0)
            {
                // Fallback to normal text field
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
