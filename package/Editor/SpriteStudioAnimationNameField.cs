using UnityEditor;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// The Anime Asset and Animation Name fields of the player and graphic Inspectors. The field writes only
    /// what the user picks, as any Inspector field does: showing a player never changes it.
    /// </summary>
    internal static class SpriteStudioAnimationNameField
    {
        private const string Label = "Animation Name";

        /// <summary>
        /// Draws the Anime Asset field. When the user assigns an asset that lacks the stored animation name
        /// (none set yet, or one from the previous asset), the name becomes the asset's first animation, so a
        /// player the user just set up shows and plays something.
        /// </summary>
        public static void DrawAsset(SerializedProperty assetProp, SerializedProperty nameProp)
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(assetProp);
            if (!EditorGUI.EndChangeCheck() || assetProp.hasMultipleDifferentValues) return;

            var names = (assetProp.objectReferenceValue as SpriteStudioAnimeAsset)?.AnimationNames;
            if (names == null || names.Length == 0) return;
            if (nameProp.hasMultipleDifferentValues || System.Array.IndexOf(names, nameProp.stringValue) < 0)
                nameProp.stringValue = names[0];
        }

        /// <summary>
        /// Draws the Animation Name as a popup of the asset's animations. A name the asset does not have --
        /// none set, or one a reconvert renamed away -- is shown as it is rather than replaced: the runtime
        /// warns about it, and Play mode honours an empty one. Several objects with different names show as
        /// mixed; several on different assets get the plain text field.
        /// </summary>
        public static void DrawName(SerializedProperty assetProp, SerializedProperty nameProp)
        {
            var names = (assetProp.objectReferenceValue as SpriteStudioAnimeAsset)?.AnimationNames;
            if (assetProp.hasMultipleDifferentValues || names == null || names.Length == 0)
            {
                EditorGUILayout.PropertyField(nameProp);
                return;
            }

            string current = nameProp.stringValue;
            int index = System.Array.IndexOf(names, current);
            string[] options = names;
            int offset = 0;
            if (index < 0 && !nameProp.hasMultipleDifferentValues)
            {
                // The stored name heads the list, so the popup shows it without writing anything.
                options = new string[names.Length + 1];
                options[0] = string.IsNullOrEmpty(current) ? "(none)" : $"(missing) {current}";
                names.CopyTo(options, 1);
                offset = 1;
                index = -1;
            }

            EditorGUI.showMixedValue = nameProp.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            int picked = EditorGUILayout.Popup(Label, index + offset, options);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck() && picked >= offset)
                nameProp.stringValue = names[picked - offset];
        }
    }
}
