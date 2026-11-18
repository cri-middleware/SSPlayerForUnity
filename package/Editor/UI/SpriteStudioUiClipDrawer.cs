using SpriteStudio.Editor;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.UI.Editor
{
    [CustomPropertyDrawer(typeof(SpriteStudioUiClip))]
    internal sealed class SpriteStudioUiClipDrawer : PropertyDrawer
    {
        private static readonly GUIContent _animationLabel = new GUIContent("Animation");
        private static readonly GUIContent _labelLabel = new GUIContent("Label");

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return 4f * EditorGUIUtility.singleLineHeight + 3f * EditorGUIUtility.standardVerticalSpacing;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var stateProp = property.FindPropertyRelative(nameof(SpriteStudioUiClip.State));
            var selectedProp = property.FindPropertyRelative(nameof(SpriteStudioUiClip.Selected));
            var animationProp = property.FindPropertyRelative(nameof(SpriteStudioUiClip.Animation));
            var labelProp = property.FindPropertyRelative(nameof(SpriteStudioUiClip.Label));

            var asset = SpriteStudioAssetNames.GraphicAsset(property.serializedObject, out string graphicAnimation);
            string clipAnimation = animationProp.hasMultipleDifferentValues ? null
                                 : string.IsNullOrEmpty(animationProp.stringValue) ? graphicAnimation
                                 : animationProp.stringValue;

            float step = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            EditorGUI.PropertyField(row, stateProp);
            row.y += step;
            EditorGUI.PropertyField(row, selectedProp);
            row.y += step;
            SpriteStudioNamePopup.Draw(row, _animationLabel, animationProp, asset?.AnimationNames, "(the graphic's animation)");
            row.y += step;
            SpriteStudioNamePopup.Draw(row, _labelLabel, labelProp, SpriteStudioAssetNames.Labels(asset, clipAnimation), "(whole animation)");
        }
    }
}
