using SpriteStudio.Editor;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.UI.Editor
{
    [CustomPropertyDrawer(typeof(SpriteStudioPartNameAttribute))]
    internal sealed class SpriteStudioPartNameDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.String)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }
            var asset = SpriteStudioAssetNames.GraphicAsset(property.serializedObject, out _);
            SpriteStudioNamePopup.Draw(position, label, property, SpriteStudioAssetNames.Parts(asset), null);
        }
    }
}
