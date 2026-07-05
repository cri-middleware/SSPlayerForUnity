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

            try
            {
                // Write to a temp file, then swap atomically. If the editor crashes mid-overwrite the
                // JSON is corrupted and all links are lost on the next load (GetMap catches and resets to empty),
                // so temp -> File.Replace guarantees atomicity. Same directory, so same volume.
                string tmp = MAP_FILE_PATH + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(MAP_FILE_PATH))
                {
                    File.Replace(tmp, MAP_FILE_PATH, null);
                }
                else
                {
                    File.Move(tmp, MAP_FILE_PATH);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SSPlayer] Failed to save source map: {e.Message}");
            }
        }

        /// <summary>
        /// Registers the .ssab → .sspj link. When <paramref name="save"/>=false, only the dictionary is updated
        /// and nothing is saved (so callers can batch many registrations and call <see cref="SaveMap"/> once).
        /// </summary>
        public static void Register(string ssabAbsolutePath, string sspjAbsolutePath, bool save = true)
        {
            var map = GetMap();
            string ssabRel = GetRelativePath(ssabAbsolutePath);
            string sspjRel = GetRelativePath(sspjAbsolutePath);

            map[ssabRel] = sspjRel;
            if (save) SaveMap();
        }

        /// <summary>Removes the corresponding link when a .ssab is deleted (prevents stale entries).
        /// Returns true if a link was removed. When <paramref name="save"/>=false, the caller saves in a batch.</summary>
        public static bool RemoveSsab(string ssabPath, bool save = true)
        {
            var map = GetMap();
            string ssabRel = GetRelativePath(Path.GetFullPath(ssabPath));
            bool removed = map.Remove(ssabRel);
            if (removed && save) SaveMap();
            return removed;
        }

        /// <summary>Re-keys the entry when a .ssab is moved/renamed (the linked .sspj is preserved).
        /// Returns true if the key was moved. When <paramref name="save"/>=false, the caller saves in a batch.</summary>
        public static bool RenameSsab(string fromSsabPath, string toSsabPath, bool save = true)
        {
            var map = GetMap();
            string fromRel = GetRelativePath(Path.GetFullPath(fromSsabPath));
            if (!map.TryGetValue(fromRel, out string sspjRel)) return false;
            string toRel = GetRelativePath(Path.GetFullPath(toSsabPath));
            map.Remove(fromRel);
            map[toRel] = sspjRel;
            if (save) SaveMap();
            return true;
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

            // Path.GetRelativePath returns the absolute path unchanged when it can't be made relative (e.g. a different drive)
            // (equivalent to the old Uri.MakeRelativeUri fallback). Normalize separators to '/'.
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
