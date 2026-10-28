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
        private SerializedProperty _cellmapTextureOverridesProp;
        private SerializedProperty _playAudioProp;
        private SerializedProperty _audioVolumeProp;
        private SerializedProperty _fitProp;
        private SerializedProperty _alignProp;

        private SerializedProperty _onAnimationStartedEventProp;
        private SerializedProperty _onAnimationFinishedEventProp;
        private SerializedProperty _onAnimationLoopedEventProp;
        private SerializedProperty _onUserDataEventProp;
        private SerializedProperty _onSignalEventProp;
        private SerializedProperty _onAudioEventProp;

        private static readonly GUIContent _frameSkippingLabel = new GUIContent("Frame Skipping",
            "ON: advance by elapsed time even when frames are dropped under load (real-time tracking). OFF: advance exactly one frame per update (frame-by-frame).");
        private static readonly GUIContent _subFrameInterpolationLabel = new GUIContent("Sub-Frame Interpolation",
            "ON: interpolate between frames for smooth rendering (fluid at high FPS). OFF: snap to keyframes (stepped look).");

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
            _cellmapTextureOverridesProp = serializedObject.FindProperty("_cellmapTextureOverrides");
            _playAudioProp = serializedObject.FindProperty("_playAudio");
            _audioVolumeProp = serializedObject.FindProperty("_audioVolume");
            _fitProp = serializedObject.FindProperty("_fit");
            _alignProp = serializedObject.FindProperty("_align");
            SetShowNativeSize(true, true);

            _onAnimationStartedEventProp = serializedObject.FindProperty("OnAnimationStartedEvent");
            _onAnimationFinishedEventProp = serializedObject.FindProperty("OnAnimationFinishedEvent");
            _onAnimationLoopedEventProp = serializedObject.FindProperty("OnAnimationLoopedEvent");
            _onUserDataEventProp = serializedObject.FindProperty("OnUserDataEvent");
            _onSignalEventProp = serializedObject.FindProperty("OnSignalEvent");
            _onAudioEventProp = serializedObject.FindProperty("OnAudioEvent");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SpriteStudioAnimationNameField.DrawAsset(_animeAssetProp, _animationNameProp);
            SpriteStudioAnimationNameField.DrawName(_animeAssetProp, _animationNameProp);

            EditorGUILayout.PropertyField(_autoPlayProp);
            EditorGUILayout.PropertyField(_loopProp);
            EditorGUILayout.Slider(_speedScaleProp, 0f, 4f);
            // Order: Frame Skipping (real-time tracking) → Sub-Frame Interpolation (inter-frame interpolation).
            EditorGUILayout.PropertyField(_skipFramesProp, _frameSkippingLabel);
            EditorGUILayout.PropertyField(_subFrameEnabledProp, _subFrameInterpolationLabel);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_fitProp);   // The [Header("Layout")] on the field renders the section label.
            EditorGUILayout.PropertyField(_alignProp);
            NativeSizeButtonGUI();

            EditorGUILayout.Space();
            AppearanceControlsGUI();
            RaycastControlsGUI();
            MaskableControlsGUI();

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_cellmapTextureOverridesProp, true);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_playAudioProp);   // The [Header("Audio")] on the field renders the section label.
            // Volume only takes effect while Play Audio is on (it is the master switch).
            using (new EditorGUI.DisabledScope(!_playAudioProp.boolValue))
            {
                EditorGUILayout.PropertyField(_audioVolumeProp);
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_onAnimationStartedEventProp);
            EditorGUILayout.PropertyField(_onAnimationFinishedEventProp);
            EditorGUILayout.PropertyField(_onAnimationLoopedEventProp);
            EditorGUILayout.PropertyField(_onUserDataEventProp);
            EditorGUILayout.PropertyField(_onSignalEventProp);
            EditorGUILayout.PropertyField(_onAudioEventProp);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
