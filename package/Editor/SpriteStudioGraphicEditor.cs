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
        private SerializedProperty _skipFramesProp;

        private static readonly GUIContent _frameSkippingLabel = new GUIContent("Frame Skipping",
            "ON: 負荷でフレームが落ちても経過時間ぶん進める (実時間追従)。OFF: 毎 update で必ず 1 フレームずつ進める (コマ送り)。");
        private static readonly GUIContent _subFrameInterpolationLabel = new GUIContent("Sub-Frame Interpolation",
            "ON: フレーム間を補間して滑らかに描画 (高FPS でヌルヌル)。OFF: キーフレームにスナップ (コマ感)。");

        protected override void OnEnable()
        {
            base.OnEnable();
            _animeAssetProp = serializedObject.FindProperty("_animeAsset");
            _animationNameProp = serializedObject.FindProperty("_animationName");
            _autoPlayProp = serializedObject.FindProperty("_autoPlay");
            _loopProp = serializedObject.FindProperty("_loopCount");
            _speedScaleProp = serializedObject.FindProperty("_speedScale");
            _subFrameEnabledProp = serializedObject.FindProperty("_subFrameEnabled");
            _skipFramesProp = serializedObject.FindProperty("_skipFrames");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_animeAssetProp);

            DrawAnimationNameDropdown();

            EditorGUILayout.PropertyField(_autoPlayProp);
            EditorGUILayout.PropertyField(_loopProp);
            EditorGUILayout.Slider(_speedScaleProp, 0f, 4f);
            // 順番: Frame Skipping (実時間追従) → Sub-Frame Interpolation (フレーム間補間)。
            EditorGUILayout.PropertyField(_skipFramesProp, _frameSkippingLabel);
            EditorGUILayout.PropertyField(_subFrameEnabledProp, _subFrameInterpolationLabel);

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
