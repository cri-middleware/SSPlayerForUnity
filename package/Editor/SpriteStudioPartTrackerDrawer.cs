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
            "Name of the part to follow (ssab PartData.Name).");

        // Cache the parsed part-name list (and per-part "is a skinned mesh" flags) per asset to avoid
        // re-parsing the ssab every repaint.
        private static SpriteStudioAnimeAsset _cachedAsset;
        private static byte[] _cachedData;
        private static string[] _cachedNames;
        private static bool[] _cachedSkinned;

        // Suffix shown on a skinned-mesh part. Such a part cannot be followed to its drawn position (a tracker
        // reads the part's own node, which stays near the root), so it is greyed out (disabled) in the dropdown
        // to prevent an easy mistake. It is not a hard block: a skinned name set via script (RegisterPartTracker)
        // still works, and a value already stored stays put.
        private const string SkinnedSuffix = "  (skinned mesh)";

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return 6f * EditorGUIUtility.singleLineHeight + 5f * EditorGUIUtility.standardVerticalSpacing;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var partNameProp = property.FindPropertyRelative("partName");
            var targetProp = property.FindPropertyRelative("target");
            var drivePositionProp = property.FindPropertyRelative("drivePosition");
            var driveRotationProp = property.FindPropertyRelative("driveRotation");
            var driveScaleProp = property.FindPropertyRelative("driveScale");
            var onHideProp = property.FindPropertyRelative("onHide");

            // A tracker added through the inspector list's "+" is zero-initialised: Unity does not run the C#
            // field initialisers, so drivePosition / driveRotation come out false and the tracker follows
            // nothing. Restore the intended defaults (both on) on a brand-new, wholly-unconfigured entry only,
            // so a tracker the user has deliberately set is never overridden.
            if (string.IsNullOrEmpty(partNameProp.stringValue) && targetProp.objectReferenceValue == null
                && !drivePositionProp.boolValue && !driveRotationProp.boolValue && !driveScaleProp.boolValue)
            {
                drivePositionProp.boolValue = true;
                driveRotationProp.boolValue = true;
            }

            float line = EditorGUIUtility.singleLineHeight;
            float step = line + EditorGUIUtility.standardVerticalSpacing;
            var row = new Rect(position.x, position.y, position.width, line);

            GetPartInfo((property.serializedObject.targetObject as SpriteStudioPlayer)?.AnimeAsset,
                        out string[] names, out bool[] skinned);
            DrawPartNameField(row, partNameProp, names, skinned);
            row.y += step;

            EditorGUI.PropertyField(row, targetProp);
            row.y += step;
            EditorGUI.PropertyField(row, drivePositionProp);
            row.y += step;
            EditorGUI.PropertyField(row, driveRotationProp);
            row.y += step;
            EditorGUI.PropertyField(row, driveScaleProp);
            row.y += step;
            EditorGUI.PropertyField(row, onHideProp);

            EditorGUI.EndProperty();
        }

        private static void DrawPartNameField(Rect rect, SerializedProperty partNameProp, string[] names, bool[] skinned)
        {
            if (names == null || names.Length == 0)
            {
                EditorGUI.PropertyField(rect, partNameProp, _partNameLabel);
                return;
            }

            string current = partNameProp.stringValue;
            int curIdx = System.Array.IndexOf(names, current);
            bool curSkinned = curIdx >= 0 && skinned != null && curIdx < skinned.Length && skinned[curIdx];

            // Button shows the current value; a skinned or not-in-asset current value stays visible (it is not
            // silently cleared) but is annotated.
            string shown = string.IsNullOrEmpty(current) ? "(none)"
                         : curIdx < 0 ? current + "  (not in asset)"
                         : curSkinned ? current + SkinnedSuffix
                         : current;

            Rect fieldRect = EditorGUI.PrefixLabel(rect, _partNameLabel);
            if (!EditorGUI.DropdownButton(fieldRect, new GUIContent(shown), FocusType.Keyboard)) return;

            // Re-find the property inside the deferred menu callback rather than capturing the (short-lived)
            // SerializedProperty, so writing back stays valid after OnGUI has returned.
            var so = partNameProp.serializedObject;
            string path = partNameProp.propertyPath;

            var menu = new GenericMenu();
            for (int i = 0; i < names.Length; i++)
            {
                if (string.IsNullOrEmpty(names[i])) continue;
                bool isSkinned = skinned != null && i < skinned.Length && skinned[i];
                if (isSkinned)
                {
                    // Greyed out and not selectable from the dropdown: a skinned mesh's node is not where its
                    // art draws, so tracking it is almost always a mistake. (Still settable via script.)
                    menu.AddDisabledItem(new GUIContent(names[i] + SkinnedSuffix));
                }
                else
                {
                    string picked = names[i];
                    menu.AddItem(new GUIContent(picked), picked == current, () =>
                    {
                        so.Update();
                        var sp = so.FindProperty(path);
                        if (sp != null) { sp.stringValue = picked; so.ApplyModifiedProperties(); }
                    });
                }
            }
            menu.DropDown(fieldRect);
        }

        private static void GetPartInfo(SpriteStudioAnimeAsset asset, out string[] names, out bool[] skinned)
        {
            if (asset == null || asset.SsabData == null || asset.SsabData.Length == 0)
            {
                names = null;
                skinned = null;
                return;
            }
            if (ReferenceEquals(asset, _cachedAsset) && ReferenceEquals(asset.SsabData, _cachedData))
            {
                names = _cachedNames;
                skinned = _cachedSkinned;
                return;
            }

            var bb = new Google.FlatBuffers.ByteBuffer(asset.SsabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
            int n = ssab.PartsLength;
            names = new string[n];
            skinned = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var p = ssab.Parts(i);
                if (!p.HasValue) continue;
                names[i] = p.Value.Name;
                // Same test the runtime uses (SpriteStudioSsabFormat), so the inspector and playback agree.
                skinned[i] = SpriteStudioSsabFormat.IsSkinnedMeshPart(p.Value);
            }

            _cachedAsset = asset;
            _cachedData = asset.SsabData;
            _cachedNames = names;
            _cachedSkinned = skinned;
        }
    }
}
