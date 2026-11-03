using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor
{
    internal static class SpriteStudioNamePopup
    {
        public static void Draw(Rect rect, GUIContent label, SerializedProperty prop, string[] names, string emptyChoice)
        {
            if (names == null)
            {
                EditorGUI.PropertyField(rect, prop, label);
                return;
            }

            bool mixed = prop.hasMultipleDifferentValues;
            var values = Choices(names, mixed ? null : prop.stringValue, emptyChoice, out GUIContent[] options);
            int index = mixed ? -1 : values.IndexOf(prop.stringValue);

            EditorGUI.BeginProperty(rect, label, prop);
            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            int picked = EditorGUI.Popup(rect, label, index, options);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck() && picked >= 0) prop.stringValue = values[picked];
            EditorGUI.EndProperty();
        }

        internal static List<string> Choices(string[] names, string current, string emptyChoice, out GUIContent[] options)
        {
            var values = new List<string>(names.Length + 2);
            var shown = new List<GUIContent>(names.Length + 2);
            var seen = new HashSet<string>();

            bool known = current == null
                      || (current.Length == 0 ? emptyChoice != null : System.Array.IndexOf(names, current) >= 0);
            if (!known)
            {
                values.Add(current);
                shown.Add(new GUIContent(current.Length == 0 ? "(none)" : $"(missing) {current}"));
                seen.Add(current);
            }
            if (emptyChoice != null && seen.Add(""))
            {
                values.Add("");
                shown.Add(new GUIContent(emptyChoice));
            }
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                values.Add(name);
                shown.Add(new GUIContent(name));
            }

            options = shown.ToArray();
            return values;
        }
    }
}
