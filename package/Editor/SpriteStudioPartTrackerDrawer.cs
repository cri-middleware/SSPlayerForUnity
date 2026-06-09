using UnityEditor;
using UnityEngine;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Inspector drawer for <see cref="SpriteStudioPartTracker"/>. Renders the part name as a dropdown
    /// populated from the owning player's asset (ssab PartData names), falling back to a plain text field
    /// when the names are unavailable (no asset / not a world player / asset still importing).
    /// </summary>
    [CustomPropertyDrawer(typeof(SpriteStudioPartTracker))]
    public class SpriteStudioPartTrackerDrawer : PropertyDrawer
    {
        private static readonly GUIContent _partNameLabel = new GUIContent("Part Name",
            "追従対象のパーツ名 (ssab の PartData.Name)。");

        // Cache the parsed part-name list per asset to avoid re-parsing the ssab every repaint.
        private static SpriteStudioAnimeAsset _cachedAsset;
        private static byte[] _cachedData;
        private static string[] _cachedNames;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return 4f * EditorGUIUtility.singleLineHeight + 3f * EditorGUIUtility.standardVerticalSpacing;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var partNameProp = property.FindPropertyRelative("partName");
            var targetProp = property.FindPropertyRelative("target");
            var driveScaleProp = property.FindPropertyRelative("driveScale");
            var onHideProp = property.FindPropertyRelative("onHide");

            float line = EditorGUIUtility.singleLineHeight;
            float step = line + EditorGUIUtility.standardVerticalSpacing;
            var row = new Rect(position.x, position.y, position.width, line);

            string[] names = GetPartNames((property.serializedObject.targetObject as SpriteStudioPlayer)?.AnimeAsset);
            DrawPartNameField(row, partNameProp, names);
            row.y += step;

            EditorGUI.PropertyField(row, targetProp);
            row.y += step;
            EditorGUI.PropertyField(row, driveScaleProp);
            row.y += step;
            EditorGUI.PropertyField(row, onHideProp);

            EditorGUI.EndProperty();
        }

        private static void DrawPartNameField(Rect rect, SerializedProperty partNameProp, string[] names)
        {
            if (names == null || names.Length == 0)
            {
                EditorGUI.PropertyField(rect, partNameProp, _partNameLabel);
                return;
            }

            string current = partNameProp.stringValue;
            int idx = System.Array.IndexOf(names, current);
            if (idx >= 0)
            {
                int sel = EditorGUI.Popup(rect, _partNameLabel.text, idx, names);
                if (sel >= 0 && sel < names.Length) partNameProp.stringValue = names[sel];
                return;
            }

            // Current value is not a part of this asset: keep it visible (as the first option) so a stale
            // or hand-typed name is not silently lost, but let the user pick a valid part.
            var options = new string[names.Length + 1];
            options[0] = string.IsNullOrEmpty(current) ? "(none)" : current + "  (not in asset)";
            System.Array.Copy(names, 0, options, 1, names.Length);
            int picked = EditorGUI.Popup(rect, _partNameLabel.text, 0, options);
            if (picked > 0) partNameProp.stringValue = names[picked - 1];
        }

        private static string[] GetPartNames(SpriteStudioAnimeAsset asset)
        {
            if (asset == null || asset.SsabData == null || asset.SsabData.Length == 0) return null;
            if (ReferenceEquals(asset, _cachedAsset) && ReferenceEquals(asset.SsabData, _cachedData))
                return _cachedNames;

            var bb = new Google.FlatBuffers.ByteBuffer(asset.SsabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
            int n = ssab.PartsLength;
            var names = new string[n];
            for (int i = 0; i < n; i++)
            {
                var p = ssab.Parts(i);
                names[i] = p.HasValue ? p.Value.Name : null;
            }

            _cachedAsset = asset;
            _cachedData = asset.SsabData;
            _cachedNames = names;
            return names;
        }
    }
}
