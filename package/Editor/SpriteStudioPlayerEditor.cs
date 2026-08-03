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
        private SerializedProperty _flipXProp;
        private SerializedProperty _flipYProp;
        private SerializedProperty _offsetProp;
        private SerializedProperty _partTrackersProp;
        private SerializedProperty _playAudioProp;
        private SerializedProperty _audioVolumeProp;
        private SerializedProperty _audioBackendProp;

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
            _flipXProp = serializedObject.FindProperty("_flipX");
            _flipYProp = serializedObject.FindProperty("_flipY");
            _offsetProp = serializedObject.FindProperty("_offset");
            _partTrackersProp = serializedObject.FindProperty("_partTrackers");
            _playAudioProp = serializedObject.FindProperty("_playAudio");
            _audioVolumeProp = serializedObject.FindProperty("_audioVolume");
            _audioBackendProp = serializedObject.FindProperty("_audioBackend");

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

            EditorGUILayout.PropertyField(_animeAssetProp);

            DrawAnimationNameDropdown();

            EditorGUILayout.PropertyField(_autoPlayProp);
            EditorGUILayout.PropertyField(_loopProp);
            EditorGUILayout.Slider(_speedScaleProp, 0f, 4f);
            // Order: Frame Skipping (real-time tracking) → Sub-Frame Interpolation (inter-frame interpolation).
            EditorGUILayout.PropertyField(_skipFramesProp, _frameSkippingLabel);
            EditorGUILayout.PropertyField(_subFrameEnabledProp, _subFrameInterpolationLabel);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_sortingLayerNameProp);
            EditorGUILayout.PropertyField(_sortingOrderProp);
            EditorGUILayout.PropertyField(_flipXProp);
            EditorGUILayout.PropertyField(_flipYProp);
            EditorGUILayout.PropertyField(_offsetProp);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_partTrackersProp, true);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_playAudioProp);   // The [Header("Audio")] on the field renders the section label.
            // Volume / Backend only take effect while Play Audio is on (it is the master switch).
            using (new EditorGUI.DisabledScope(!_playAudioProp.boolValue))
            {
                EditorGUILayout.PropertyField(_audioVolumeProp);
                EditorGUILayout.PropertyField(_audioBackendProp);
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
            // Write only when the stored value differs from what the popup shows: an unconditional write
            // re-marks the scene modified on every repaint. An unset name still differs from the entry the
            // popup falls back to, so a player that reaches the inspector without one is still filled in.
            if (newIndex >= 0 && newIndex < asset.AnimationNames.Length
                && _animationNameProp.stringValue != asset.AnimationNames[newIndex])
            {
                _animationNameProp.stringValue = asset.AnimationNames[newIndex];
            }
        }
    }
}
