using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace SpriteStudio.Editor
{
    public class SpriteStudioImporterWindow : EditorWindow
    {
        private DefaultAsset outputFolder = null;
        private List<string> recentFiles = new List<string>();
        private const string RECENT_FILES_PREF_KEY = "SpriteStudio_RecentFiles";
        private const string OUTPUT_FOLDER_PREF_KEY = "SpriteStudio_OutputFolder";
        private const int MAX_RECENT_FILES = 20;
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

            LoadRecentFiles();
        }

        private void LoadRecentFiles()
        {
            string json = EditorPrefs.GetString(RECENT_FILES_PREF_KEY, "[]");
            try
            {
                recentFiles = JsonUtility.FromJson<SerializationHelper>(json).list ?? new List<string>();
            }
            catch
            {
                recentFiles = new List<string>();
            }
        }

        private void SaveRecentFiles()
        {
            string json = JsonUtility.ToJson(new SerializationHelper { list = recentFiles });
            EditorPrefs.SetString(RECENT_FILES_PREF_KEY, json);
        }

        private void AddToRecentFiles(string path)
        {
            recentFiles.RemoveAll(p => p == path);
            recentFiles.Insert(0, path);
            if (recentFiles.Count > MAX_RECENT_FILES)
            {
                recentFiles = recentFiles.Take(MAX_RECENT_FILES).ToList();
            }
            SaveRecentFiles();
        }

        private void RemoveFromRecentFiles(string path)
        {
            recentFiles.RemoveAll(p => p == path);
            SaveRecentFiles();
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
            GUI.Box(dropRect, "Drop SSPJ here\n(drag from your file manager)", EditorStyles.helpBox);

            var centeredStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(dropRect, "Drop SSPJ here\n(drag from your file manager)", centeredStyle);

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
                        
                        foreach (string draggedPath in DragAndDrop.paths)
                        {
                            if (Path.GetExtension(draggedPath).ToLower() == ".sspj")
                            {
                                RunConvert(draggedPath);
                            }
                        }
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
                recentFiles.Clear();
                SaveRecentFiles();
            }
            GUILayout.EndHorizontal();

            scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            if (recentFiles.Count == 0)
            {
                GUIStyle disabledStyle = new GUIStyle(EditorStyles.label);
                disabledStyle.normal.textColor = Color.gray;
                GUILayout.Label("No recent files. Drop a sspj above to start.", disabledStyle);
            }
            else
            {
                foreach (string file in new List<string>(recentFiles))
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
                            GenericMenu menu = new GenericMenu();
                            menu.AddItem(new GUIContent("Open SSPJ"), false, () => OpenSSPJ(file));
                            menu.AddItem(new GUIContent("Reconvert"), false, () => RunConvert(file));
                            menu.AddItem(new GUIContent("Reveal"), false, () => EditorUtility.RevealInFinder(file));
                            menu.AddSeparator("");
                            menu.AddItem(new GUIContent("Remove from Recent"), false, () => RemoveFromRecentFiles(file));
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

        public static void RunConvertDirect(string sspjPath)
        {
            if (!File.Exists(sspjPath))
            {
                Debug.LogError($"[SSPlayer] SSPJ file not found: {sspjPath}");
                return;
            }

            string savedPath = EditorPrefs.GetString(OUTPUT_FOLDER_PREF_KEY, DEFAULT_OUTPUT_PATH);
            if (!AssetDatabase.IsValidFolder(savedPath))
            {
                Debug.LogError($"[SSPlayer] Invalid output folder: {savedPath}. Please check Importer Window settings.");
                return;
            }

            string absoluteOutputDir = Path.GetFullPath(savedPath);
            string sspjName = Path.GetFileNameWithoutExtension(sspjPath);
            string finalOutputDir = Path.Combine(absoluteOutputDir, sspjName);

            if (!Directory.Exists(finalOutputDir))
            {
                Directory.CreateDirectory(finalOutputDir);
            }

            Debug.Log($"[SSPlayer] Converting {sspjPath} to {finalOutputDir}");

            unsafe
            {
                var context = SpriteStudio.SsConverter.NativeMethods.ss_converter_create();
                
                var sspjBytes = System.Text.Encoding.UTF8.GetBytes(sspjPath + "\0");
                var outDirBytes = System.Text.Encoding.UTF8.GetBytes(finalOutputDir + "\0");

                fixed (byte* sspjPtr = sspjBytes)
                fixed (byte* outDirPtr = outDirBytes)
                {
                    SpriteStudio.SsConverter.NativeMethods.ss_converter_convert(context, sspjPtr, outDirPtr, null);
                }

                while (!SpriteStudio.SsConverter.NativeMethods.ss_converter_is_finished(context))
                {
                    System.Threading.Thread.Sleep(10);
                }

                var result = SpriteStudio.SsConverter.NativeMethods.ss_converter_get_result(context);
                if (result != SpriteStudio.SsConverter.CConverterError.Success)
                {
                    byte* errData;
                    nuint errLen;
                    SpriteStudio.SsConverter.NativeMethods.ss_converter_get_error(context, &errData, &errLen);
                    if (errData != null && errLen > 0)
                    {
                        var errStr = System.Text.Encoding.UTF8.GetString(errData, (int)errLen);
                        Debug.LogError($"[SSPlayer] Converter Error: {errStr}");
                    }
                    else
                    {
                        Debug.LogError($"[SSPlayer] Converter Error: {result}");
                    }
                }
                else
                {
                    Debug.Log($"[SSPlayer] Convert Success!");
                    
                    var generatedSsabs = Directory.GetFiles(finalOutputDir, "*.ssab", SearchOption.AllDirectories);
                    foreach (var ssab in generatedSsabs)
                    {
                        SpriteStudioSourceMap.Register(ssab, sspjPath);
                    }
                }

                SpriteStudio.SsConverter.NativeMethods.ss_converter_destroy(context);
            }

            AssetDatabase.Refresh();
        }

        private void RunConvert(string sspjPath)
        {
            if (!File.Exists(sspjPath))
            {
                Debug.LogError($"[SSPlayer] SSPJ file not found: {sspjPath}");
                return;
            }

            if (outputFolder == null)
            {
                Debug.LogError("[SSPlayer] Please specify an Output Folder.");
                return;
            }

            AddToRecentFiles(sspjPath);
            RunConvertDirect(sspjPath);
        }

        [Serializable]
        private class SerializationHelper
        {
            public List<string> list;
        }
    }
}
