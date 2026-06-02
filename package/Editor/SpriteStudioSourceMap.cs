using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System;

namespace SpriteStudio.Editor
{
    public static class SpriteStudioSourceMap
    {
        private const string MAP_FILE_PATH = "ProjectSettings/SpriteStudioSourceMap.json";

        [Serializable]
        private class SourceMapData
        {
            public List<string> SsabPaths = new List<string>();
            public List<string> SspjPaths = new List<string>();
        }

        private static Dictionary<string, string> _map;

        public static Dictionary<string, string> GetMap()
        {
            if (_map != null) return _map;

            _map = new Dictionary<string, string>();
            if (File.Exists(MAP_FILE_PATH))
            {
                try
                {
                    string json = File.ReadAllText(MAP_FILE_PATH);
                    var data = JsonUtility.FromJson<SourceMapData>(json);
                    if (data != null && data.SsabPaths.Count == data.SspjPaths.Count)
                    {
                        for (int i = 0; i < data.SsabPaths.Count; i++)
                        {
                            _map[data.SsabPaths[i]] = data.SspjPaths[i];
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[SSPlayer] Failed to load source map: {e.Message}");
                }
            }
            return _map;
        }

        public static void SaveMap()
        {
            if (_map == null) return;

            var data = new SourceMapData();
            foreach (var kvp in _map)
            {
                data.SsabPaths.Add(kvp.Key);
                data.SspjPaths.Add(kvp.Value);
            }
            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(MAP_FILE_PATH, json);
        }

        public static void Register(string ssabAbsolutePath, string sspjAbsolutePath)
        {
            var map = GetMap();
            string ssabRel = GetRelativePath(ssabAbsolutePath);
            string sspjRel = GetRelativePath(sspjAbsolutePath);

            map[ssabRel] = sspjRel;
            SaveMap();
        }

        public static string GetSspjForSsab(string ssabAbsolutePath)
        {
            var map = GetMap();
            string ssabRel = GetRelativePath(ssabAbsolutePath);
            if (map.TryGetValue(ssabRel, out string sspjRel))
            {
                return GetAbsolutePath(sspjRel);
            }
            return null;
        }

        public static string GetRelativePath(string absolutePath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            absolutePath = Path.GetFullPath(absolutePath);

            // Path.GetRelativePath は別ドライブ等で相対化できない場合は絶対パスをそのまま返す
            // (旧 Uri.MakeRelativeUri 実装のフォールバックと同等)。区切り文字は '/' に正規化する。
            return Path.GetRelativePath(projectRoot, absolutePath).Replace('\\', '/');
        }

        public static string GetAbsolutePath(string relativePath)
        {
            if (Path.IsPathRooted(relativePath))
            {
                return relativePath; // Already absolute
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, relativePath));
        }
    }
}
