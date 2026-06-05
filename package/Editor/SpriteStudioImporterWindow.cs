using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

namespace SpriteStudio.Editor
{
    public class SpriteStudioImporterWindow : EditorWindow
    {
        private DefaultAsset outputFolder = null;
        private const string OUTPUT_FOLDER_PREF_KEY = "SpriteStudio_OutputFolder";
        private const string DEFAULT_OUTPUT_PATH = "Assets/ssab_generated";

        private Vector2 scrollPosition;

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
        }

        private void OnGUI()
        {
            DrawOutputSection();
            EditorGUILayout.Space();
            DrawDropArea();
            EditorGUILayout.Space();
            DrawRecentFilesSection();
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
                foreach (string file in recent)
                {
                    if (!File.Exists(file)) continue;

                    string fileName = Path.GetFileName(file);
                    string parentDir = new DirectoryInfo(file).Parent.Name;
                    string label = $"{fileName}  ·  {parentDir}";

                    GUILayout.BeginHorizontal();

                    // Invisible button covering the whole row for right click / left click
                    Rect rect = GUILayoutUtility.GetRect(new GUIContent(label), EditorStyles.label, GUILayout.ExpandWidth(true));
                    GUI.Label(rect, label);

                    Event e = Event.current;
                    if (rect.Contains(e.mousePosition))
                    {
                        if (e.type == EventType.MouseDown && e.button == 0)
                        {
                            // Left click -> Reconvert
                            RunConvert(file);
                            e.Use();
                        }
                        else if (e.type == EventType.ContextClick || (e.type == EventType.MouseDown && e.button == 1))
                        {
                            // Right click -> Context Menu
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
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.EndScrollView();
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
