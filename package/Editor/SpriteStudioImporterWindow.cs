using UnityEngine;
using UnityEditor;
using System.IO;

namespace SpriteStudio.Editor
{
    public class SpriteStudioImporterWindow : EditorWindow
    {
        private string sspjPath = "";
        private DefaultAsset outputFolder = null;

        private void OnEnable()
        {
            string defaultPath = "Assets/ssab_generated";
            if (!AssetDatabase.IsValidFolder(defaultPath))
            {
                AssetDatabase.CreateFolder("Assets", "ssab_generated");
            }
            outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(defaultPath);
        }

        [MenuItem("Window/SpriteStudio/Importer")]
        public static void ShowWindow()
        {
            GetWindow<SpriteStudioImporterWindow>("SSImporter");
        }

        private void OnGUI()
        {
            GUILayout.Label("SpriteStudio 外部ファイルコンバーター", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            GUILayout.BeginHorizontal();
            sspjPath = EditorGUILayout.TextField("SSPJ Path", sspjPath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string path = EditorUtility.OpenFilePanel("Select .sspj file", "", "sspj");
                if (!string.IsNullOrEmpty(path))
                {
                    sspjPath = path;
                }
            }
            GUILayout.EndHorizontal();

            HandleDragAndDrop(new Rect(0, 0, position.width, position.height));

            outputFolder = (DefaultAsset)EditorGUILayout.ObjectField("Output Folder", outputFolder, typeof(DefaultAsset), false);

            EditorGUILayout.Space();

            GUI.enabled = !string.IsNullOrEmpty(sspjPath) && outputFolder != null;
            if (GUILayout.Button("Convert & Import", GUILayout.Height(30)))
            {
                RunConvert();
            }
            GUI.enabled = true;
        }

        private void RunConvert()
        {
            if (!File.Exists(sspjPath))
            {
                Debug.LogError($"[SSPlayer] SSPJ file not found: {sspjPath}");
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(outputFolder);
            if (!AssetDatabase.IsValidFolder(assetPath))
            {
                Debug.LogError("[SSPlayer] Invalid output folder.");
                return;
            }

            string absoluteOutputDir = Path.GetFullPath(assetPath);
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
                }

                SpriteStudio.SsConverter.NativeMethods.ss_converter_destroy(context);
            }

            AssetDatabase.Refresh();
        }

        private void HandleDragAndDrop(Rect dropArea)
        {
            Event evt = Event.current;
            switch (evt.type)
            {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    if (!dropArea.Contains(evt.mousePosition))
                        break;
                    
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        
                        foreach (string draggedPath in DragAndDrop.paths)
                        {
                            if (Path.GetExtension(draggedPath).ToLower() == ".sspj")
                            {
                                sspjPath = draggedPath;
                                break;
                            }
                        }
                    }
                    Event.current.Use();
                    break;
            }
        }
    }
}
