using UnityEngine;
using UnityEditor;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace SpriteStudio.Editor
{
    public class SpriteStudioImporterWindow : EditorWindow
    {
        private DefaultAsset outputFolder = null;
        private const string OUTPUT_FOLDER_PREF_KEY = "SpriteStudio_OutputFolder";
        private const string DEFAULT_OUTPUT_PATH = "Assets/ssab_generated";

        private Vector2 scrollPosition;

        // libssconverter のバージョン文字列。FFI 取得は重く OnGUI は毎フレーム走るため OnEnable で一度だけ取得してキャッシュする。
        private string converterVersion;
        private string playerVersion;

        [MenuItem("Window/SpriteStudio/Importer")]
        public static void ShowWindow()
        {
            var window = GetWindow<SpriteStudioImporterWindow>("SSImporter");
            window.minSize = new Vector2(400, 500);
        }

        private void OnEnable()
        {
            string savedPath = EditorPrefs.GetString(OUTPUT_FOLDER_PREF_KEY, DEFAULT_OUTPUT_PATH);
            if (string.IsNullOrEmpty(savedPath) || !AssetDatabase.IsValidFolder(savedPath))
            {
                savedPath = DEFAULT_OUTPUT_PATH;
            }

            if (!AssetDatabase.IsValidFolder(savedPath) && savedPath == DEFAULT_OUTPUT_PATH)
            {
                AssetDatabase.CreateFolder("Assets", "ssab_generated");
            }
            outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(savedPath);

            converterVersion = LoadConverterVersion();
            playerVersion = LoadPlayerVersion();
        }

        private void OnGUI()
        {
            DrawOutputSection();
            EditorGUILayout.Space();
            DrawDropArea();
            EditorGUILayout.Space();
            DrawRecentFilesSection();
            DrawFooter();
        }

        private void DrawOutputSection()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Output Folder", GUILayout.Width(100));

            EditorGUI.BeginChangeCheck();
            outputFolder = (DefaultAsset)EditorGUILayout.ObjectField(outputFolder, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                if (outputFolder != null)
                {
                    EditorPrefs.SetString(OUTPUT_FOLDER_PREF_KEY, AssetDatabase.GetAssetPath(outputFolder));
                }
            }

            if (GUILayout.Button("Reset", GUILayout.Width(50)))
            {
                if (!AssetDatabase.IsValidFolder(DEFAULT_OUTPUT_PATH))
                {
                    AssetDatabase.CreateFolder("Assets", "ssab_generated");
                }
                outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(DEFAULT_OUTPUT_PATH);
                EditorPrefs.SetString(OUTPUT_FOLDER_PREF_KEY, DEFAULT_OUTPUT_PATH);
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

        private void DrawDropArea()
        {
            Rect dropRect = GUILayoutUtility.GetRect(0.0f, 150.0f, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, GUIContent.none, EditorStyles.helpBox);

            var centeredStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUI.Label(dropRect, "Drop .sspj or a folder here\n(folders are batch-converted recursively)", centeredStyle);

            Event evt = Event.current;
            switch (evt.type)
            {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    if (!dropRect.Contains(evt.mousePosition))
                        break;

                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        // フォルダ(再帰走査)・複数ファイル・重複・衝突・Recent をまとめて処理する
                        SpriteStudioImporter.ImportPaths(DragAndDrop.paths);
                    }
                    Event.current.Use();
                    break;
            }
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

        // ウィンドウ下部に libssconverter のバージョンを表示する (Godot の ss_import_dock フッターと同等)。
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
                // Unityの packageInfo.version は SemVer のビルドメタデータ (+以降) を落とす場合があるため、
                // package.json を直接パースして正確な文字列を取得する
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
        /// libssconverter から FFI でバージョン文字列を取得する。
        /// ネイティブは CString を into_raw() で返すため、必ず ss_converter_version_free で解放する。
        /// </summary>
        private static unsafe string LoadConverterVersion()
        {
            try
            {
                byte* p = SpriteStudio.SsConverter.NativeMethods.ss_converter_version();
                if (p == null) return "unknown";
                string version = PtrToStringUtf8(p);
                SpriteStudio.SsConverter.NativeMethods.ss_converter_version_free(p);

                uint ssabVersion = SpriteStudio.SsConverter.NativeMethods.ss_converter_ssab_schema_version();
                string libVer = string.IsNullOrEmpty(version) ? "unknown" : version;
                
                return $"{libVer} (ssab v{ssabVersion})";
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
                // This asks the OS to open the file with its default associated application (SpriteStudio)
                Application.OpenURL("file://" + path);
            }
        }

        /// <summary>
        /// 単一 .sspj の変換 (非対話) を行う既存 API。
        /// AssetPostprocessor / AnimeAssetEditor の Reconvert から呼ばれるためシグネチャを維持する。
        /// 実体は一括インポート基盤 (<see cref="SpriteStudioImporter"/>) に委譲する。
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
