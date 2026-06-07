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
        private SerializedProperty _skipFramesProp;
        private SerializedProperty _sortingLayerNameProp;
        private SerializedProperty _sortingOrderProp;

        private SerializedProperty _onAnimationFinishedEventProp;
        private SerializedProperty _onAnimationLoopedEventProp;
        private SerializedProperty _onUserDataEventProp;
        private SerializedProperty _onSignalEventProp;
        private SerializedProperty _onAudioEventProp;

        private static readonly GUIContent _frameSkippingLabel = new GUIContent("Frame Skipping",
            "ON: 負荷でフレームが落ちても経過時間ぶん進める (実時間追従)。OFF: 毎 update で必ず 1 フレームずつ進める (コマ送り)。");
        private static readonly GUIContent _subFrameInterpolationLabel = new GUIContent("Sub-Frame Interpolation",
            "ON: フレーム間を補間して滑らかに描画 (高FPS でヌルヌル)。OFF: キーフレームにスナップ (コマ感)。");

        private void OnEnable()
        {
            _animeAssetProp = serializedObject.FindProperty("_animeAsset");
            _animationNameProp = serializedObject.FindProperty("_animationName");
            _autoPlayProp = serializedObject.FindProperty("_autoPlay");
            _loopProp = serializedObject.FindProperty("_loopCount");
            _speedScaleProp = serializedObject.FindProperty("_speedScale");
            _subFrameEnabledProp = serializedObject.FindProperty("_subFrameEnabled");
            _skipFramesProp = serializedObject.FindProperty("_skipFrames");
            _sortingLayerNameProp = serializedObject.FindProperty("_sortingLayerName");
            _sortingOrderProp = serializedObject.FindProperty("_sortingOrder");

            _onAnimationFinishedEventProp = serializedObject.FindProperty("OnAnimationFinishedEvent");
            _onAnimationLoopedEventProp = serializedObject.FindProperty("OnAnimationLoopedEvent");
            _onUserDataEventProp = serializedObject.FindProperty("OnUserDataEvent");
            _onSignalEventProp = serializedObject.FindProperty("OnSignalEvent");
            _onAudioEventProp = serializedObject.FindProperty("OnAudioEvent");
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
            EditorGUILayout.PropertyField(_sortingLayerNameProp);
            EditorGUILayout.PropertyField(_sortingOrderProp);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_onAnimationFinishedEventProp);
            EditorGUILayout.PropertyField(_onAnimationLoopedEventProp);
            EditorGUILayout.PropertyField(_onUserDataEventProp);
            EditorGUILayout.PropertyField(_onSignalEventProp);
            EditorGUILayout.PropertyField(_onAudioEventProp);

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
