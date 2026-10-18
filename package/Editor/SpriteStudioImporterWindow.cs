using UnityEngine;
using UnityEditor;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace SpriteStudio.Editor
{
    public class SpriteStudioImporterWindow : EditorWindow
    {
        // The field's value only ever comes from SpriteStudioOutputFolder, so what the window shows is
        // what the next import writes into. See CommitOutputFolder.
        private DefaultAsset outputFolder = null;

        private Vector2 scrollPosition;

        // libssconverter version string. The FFI query is expensive and OnGUI runs every frame, so fetch it once in OnEnable and cache it.
        private string converterVersion;
        private string playerVersion;

        private bool dragInWindow;

        [MenuItem("Window/SpriteStudio/Importer")]
        public static void ShowWindow()
        {
            var window = GetWindow<SpriteStudioImporterWindow>("SS Importer");
            window.minSize = new Vector2(400, 500);
        }

        private void OnEnable()
        {
            outputFolder = LoadOutputFolderAsset();

            converterVersion = LoadConverterVersion();
            playerVersion = LoadPlayerVersion();
        }

        private static DefaultAsset LoadOutputFolderAsset()
        {
            string assetPath = SpriteStudioOutputFolder.GetAssetPath();
            return assetPath == null ? null : AssetDatabase.LoadAssetAtPath<DefaultAsset>(assetPath);
        }

        private void OnGUI()
        {
            TrackDrag(Event.current);
            DrawOutputSection();
            EditorGUILayout.Space();
            DrawDropArea();
            EditorGUILayout.Space();
            DrawRecentFilesSection();
            DrawFooter();
            AcceptDrop(Event.current);
        }

        private void DrawOutputSection()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Output Folder", GUILayout.Width(100));

            EditorGUI.BeginChangeCheck();
            var picked = (DefaultAsset)EditorGUILayout.ObjectField(outputFolder, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                CommitOutputFolder(picked);
            }

            if (GUILayout.Button("Reset", GUILayout.Width(50)))
            {
                SpriteStudioOutputFolder.ResetToDefault();
                outputFolder = LoadOutputFolderAsset();
            }

            if (GUILayout.Button("Reveal", GUILayout.Width(60)))
            {
                if (outputFolder != null)
                {
                    string assetPath = AssetDatabase.GetAssetPath(outputFolder);
                    EditorUtility.RevealInFinder(Path.GetFullPath(assetPath));
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        /// <summary>
        /// Takes the field's new value only when it is a folder this package can write into, and rolls the
        /// field back to the folder the next import will actually use when it is not. An ObjectField of
        /// DefaultAsset accepts files as well as folders -- a .sspj has no importer, so it is one -- and
        /// clearing it used to leave the stored setting untouched, both of which left the window naming a
        /// destination the import never used.
        /// </summary>
        private void CommitOutputFolder(DefaultAsset picked)
        {
            string assetPath = picked == null ? null : AssetDatabase.GetAssetPath(picked);
            if (SpriteStudioOutputFolder.TrySet(assetPath, out string reason))
            {
                outputFolder = picked;
                return;
            }

            outputFolder = LoadOutputFolderAsset();
            EditorUtility.DisplayDialog("SpriteStudio: Output folder", reason, "OK");
        }

        // Runs before any control can Use() the event, so a drag over the Output Folder field still
        // lights the drop box.
        private void TrackDrag(Event evt)
        {
            switch (evt.type)
            {
                case EventType.DragUpdated:
                    if (!dragInWindow && DragAndDrop.paths != null && DragAndDrop.paths.Length > 0)
                    {
                        dragInWindow = true;
                        Repaint();
                    }
                    break;
                case EventType.DragPerform:
                case EventType.DragExited:
                    if (dragInWindow)
                    {
                        dragInWindow = false;
                        Repaint();
                    }
                    break;
            }
        }

        private void DrawDropArea()
        {
            Rect dropRect = GUILayoutUtility.GetRect(0.0f, 150.0f, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, GUIContent.none, EditorStyles.helpBox);
            if (dragInWindow)
                DrawDropHighlight(dropRect);

            var centeredStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            string text = dragInWindow
                ? "Release to convert"
                : "Drop .sspj or a folder here\n(folders are batch-converted recursively)";
            GUI.Label(dropRect, text, centeredStyle);
        }

        // Last in OnGUI, so a control that takes the drag first keeps it: the Output Folder field, given a
        // folder from the Project window.
        private static void AcceptDrop(Event evt)
        {
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)
                return;
            if (DragAndDrop.paths == null || DragAndDrop.paths.Length == 0)
                return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                // Handles folders (recursive scan), multiple files, duplicates, collisions, and Recent all together
                SpriteStudioImporter.ImportPaths(DragAndDrop.paths);
            }
            evt.Use();
        }

        private static void DrawDropHighlight(Rect rect)
        {
            var accent = new Color(0.24f, 0.52f, 0.95f);
            EditorGUI.DrawRect(rect, new Color(accent.r, accent.g, accent.b, 0.25f));

            const float border = 2.0f;
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, border), accent);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - border, rect.width, border), accent);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, border, rect.height), accent);
            EditorGUI.DrawRect(new Rect(rect.xMax - border, rect.y, border, rect.height), accent);
        }

        private void DrawRecentFilesSection()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Recent SSPJs", EditorStyles.boldLabel);
            if (GUILayout.Button("Clear", GUILayout.Width(60)))
            {
                SpriteStudioRecentFiles.Clear();
            }
            GUILayout.EndHorizontal();

            scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            var recent = SpriteStudioRecentFiles.Get();
            if (recent.Count == 0)
            {
                GUIStyle disabledStyle = new GUIStyle(EditorStyles.label);
                disabledStyle.normal.textColor = Color.gray;
                GUILayout.Label("No recent files. Drop a sspj or folder above to start.", disabledStyle);
            }
            else
            {
                // Base the row on miniButton: a flat clickable button at roughly the dropdown-field
                // brightness, but without the popup's baked-in dropdown arrow. Left-aligned.
                GUIStyle rowStyle = new GUIStyle(EditorStyles.miniButton) { alignment = TextAnchor.MiddleLeft };
                rowStyle.fixedHeight = 0;	// based on font-size
                rowStyle.padding.left += 8;
                rowStyle.padding.top += 3;
                rowStyle.padding.bottom += 3;

                foreach (string file in recent)
                {
                    if (!File.Exists(file)) continue;

                    string fileName = Path.GetFileName(file);
                    string parentDir = new DirectoryInfo(file).Parent.Name;
                    string label = $"{fileName}  ·  {parentDir}";

                    Rect rect = GUILayoutUtility.GetRect(new GUIContent(label), rowStyle, GUILayout.ExpandWidth(true));

                    // Right click -> Context Menu. GUI.Button consumes the MouseDown of *any* button,
                    // so handle (and consume) the context click ourselves BEFORE the button sees it.
                    Event e = Event.current;
                    if (rect.Contains(e.mousePosition) &&
                        (e.type == EventType.ContextClick || (e.type == EventType.MouseDown && e.button == 1)))
                    {
                        string captured = file;
                        GenericMenu menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Open SSPJ"), false, () => OpenSSPJ(captured));
                        menu.AddItem(new GUIContent("Reconvert"), false, () => RunConvert(captured));
                        menu.AddItem(new GUIContent("Reveal"), false, () => EditorUtility.RevealInFinder(captured));
                        menu.AddSeparator("");
                        menu.AddItem(new GUIContent("Remove from Recent"), false, () => SpriteStudioRecentFiles.Remove(captured));
                        menu.ShowAsContext();
                        e.Use();
                    }

                    // Left click -> Reconvert (the button reports the press directly).
                    // Dim the row background a little (0.9x) so it sits below the default brightness.
                    Color prevBg = GUI.backgroundColor;
                    GUI.backgroundColor = prevBg * 0.85f;
                    bool clicked = GUI.Button(rect, label, rowStyle);
                    GUI.backgroundColor = prevBg;
                    if (clicked)
                    {
                        RunConvert(file);
                    }
                }
            }

            GUILayout.EndScrollView();
        }

        // Show the libssconverter version at the bottom of the window.
        private void DrawFooter()
        {
            GUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label("player:", EditorStyles.miniLabel, GUILayout.Width(60));
            EditorGUILayout.SelectableLabel(playerVersion, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label("converter:", EditorStyles.miniLabel, GUILayout.Width(60));
            EditorGUILayout.SelectableLabel(converterVersion, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            GUILayout.EndHorizontal();
        }

        [System.Serializable]
        private class PackageJson
        {
            public string version;
        }

        private static string LoadPlayerVersion()
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SpriteStudioImporterWindow).Assembly);
            if (packageInfo != null)
            {
                // Unity's packageInfo.version may drop SemVer build metadata (everything after '+'),
                // so parse package.json directly to get the exact string
                string packageJsonPath = Path.Combine(packageInfo.resolvedPath, "package.json");
                if (File.Exists(packageJsonPath))
                {
                    try
                    {
                        string json = File.ReadAllText(packageJsonPath);
                        var parsed = JsonUtility.FromJson<PackageJson>(json);
                        if (!string.IsNullOrEmpty(parsed.version))
                        {
                            return parsed.version;
                        }
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[SSPlayer] Failed to parse package.json: {e.Message}");
                    }
                }
                
                return packageInfo.version;
            }
            return "unknown";
        }

        /// <summary>
        /// Fetches the version string from libssconverter via FFI.
        /// The native side returns a CString via into_raw(), so it must always be freed with ss_converter_version_free.
        /// </summary>
        private static unsafe string LoadConverterVersion()
        {
            try
            {
                byte* p = SpriteStudio.SsConverter.NativeMethods.ss_converter_version();
                if (p == null) return "unknown";
                string version = PtrToStringUtf8(p);
                SpriteStudio.SsConverter.NativeMethods.ss_converter_version_free(p);

                // Both schema versions: a conversion emits both formats and this package imports both, and
                // this is the line people copy into a bug report.
                uint ssabVersion = SpriteStudio.SsConverter.NativeMethods.ss_converter_ssab_schema_version();
                uint ssqbVersion = SpriteStudio.SsConverter.NativeMethods.ss_converter_ssqb_schema_version();
                string libVer = string.IsNullOrEmpty(version) ? "unknown" : version;

                return $"{libVer} (ssab v{ssabVersion}, ssqb v{ssqbVersion})";
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SSPlayer] Failed to query libssconverter version: {e.Message}");
                return "unavailable";
            }
        }

        private static unsafe string PtrToStringUtf8(byte* p)
        {
            if (p == null) return null;
            int len = 0;
            while (p[len] != 0) len++;
            return len == 0 ? string.Empty : Encoding.UTF8.GetString(p, len);
        }

        private void OpenSSPJ(string path)
        {
            if (File.Exists(path))
            {
                // This asks the OS to open the file with its default associated application (SpriteStudio).
                // A path, not a file:// URL: one built by concatenation is not escaped, and a space or a
                // Japanese folder name in it can make the OS refuse the URL without a word.
                EditorUtility.OpenWithDefaultApp(path);
            }
        }

        /// <summary>
        /// Existing API that converts a single .sspj (non-interactive) into the Output Folder.
        /// Called from AssetPostprocessor when a .sspj inside the project is imported, so the signature is kept.
        /// The actual work is delegated to the batch import infrastructure (<see cref="SpriteStudioImporter"/>).
        /// A reconvert anchored on an existing .ssab goes to <see cref="SpriteStudioImporter.ReconvertInto"/>
        /// instead, so that it writes back into the folder that .ssab lives in.
        /// </summary>
        public static void RunConvertDirect(string sspjPath)
        {
            SpriteStudioImporter.ImportPaths(new[] { sspjPath }, interactive: false);
        }

        private void RunConvert(string sspjPath)
        {
            SpriteStudioImporter.ImportPaths(new[] { sspjPath });
        }
    }
}
