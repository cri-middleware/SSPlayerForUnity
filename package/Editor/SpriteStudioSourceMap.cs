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
            // Project-shared conversion output folder (asset path). Kept in the same file as the
            // map — the Godot player does the same with [general] output_directory in its
            // .ssplayer_sources.cfg — so the whole team converts into the same folder.
            // Missing in pre-existing files; JsonUtility then leaves the default "" (= unset).
            public string OutputFolder = "";
        }

        private static Dictionary<string, string> _map;
        private static string _outputFolder;

        public static Dictionary<string, string> GetMap()
        {
            EnsureLoaded();
            return _map;
        }

        /// <summary>
        /// The team-shared conversion output folder (asset path such as "Assets/ssab_generated").
        /// Returns <paramref name="defaultAssetPath"/> when unset. Callers validate the folder
        /// themselves (it may not exist in this checkout).
        /// </summary>
        public static string GetOutputFolder(string defaultAssetPath)
        {
            EnsureLoaded();
            return string.IsNullOrEmpty(_outputFolder) ? defaultAssetPath : _outputFolder;
        }

        public static void SetOutputFolder(string assetPath)
        {
            EnsureLoaded();
            _outputFolder = assetPath ?? "";
            SaveMap();
        }

        private static void EnsureLoaded()
        {
            if (_map != null) return;

            _map = new Dictionary<string, string>();
            _outputFolder = "";
            if (File.Exists(MAP_FILE_PATH))
            {
                try
                {
                    string json = File.ReadAllText(MAP_FILE_PATH);
                    var data = JsonUtility.FromJson<SourceMapData>(json);
                    if (data != null)
                    {
                        _outputFolder = data.OutputFolder ?? "";
                        if (data.SsabPaths.Count == data.SspjPaths.Count)
                        {
                            for (int i = 0; i < data.SsabPaths.Count; i++)
                            {
                                _map[data.SsabPaths[i]] = data.SspjPaths[i];
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[SSPlayer] Failed to load source map: {e.Message}");
                }
            }
        }

        public static void SaveMap()
        {
            if (_map == null) return;

            var data = new SourceMapData();
            data.OutputFolder = _outputFolder ?? "";
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

            // The keys are meant to be assets of THIS project (Open SSPJ / Reconvert targets).
            // Conversions whose output lies outside the project root — e.g. test fixtures generated
            // into Application.temporaryCachePath — can never be looked up as assets and would only
            // pollute the committed file with machine-local paths, so they are not recorded.
            // (GetRelativePath normalizes separators to '/', and returns the path unchanged —
            // still rooted — when it cannot be made relative at all.)
            if (Path.IsPathRooted(ssabRel) || ssabRel.StartsWith("../")) return;

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
