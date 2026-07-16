using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Shared helper that persists the Importer Window's "Recent SSPJs" list.
    /// Used by both the Window and the batch conversion (<see cref="SpriteStudioImporter"/>).
    /// Recency is personal workflow state, so the list is stored per-project AND per-user under
    /// UserSettings/ (git-ignored by Unity convention) rather than in EditorPrefs (machine-global:
    /// leaks across projects and survives `git clean`) or in the team-shared
    /// <see cref="SpriteStudioSourceMap"/>. Entries are stored project-relative (falling back to
    /// absolute when not relativizable), same rule as the SourceMap, so the list survives moving
    /// the project directory.
    /// </summary>
    internal static class SpriteStudioRecentFiles
    {
        // Relative to the project root (the Unity Editor's working directory),
        // same convention as SpriteStudioSourceMap.MAP_FILE_PATH.
        private const string FILE_PATH = "UserSettings/SpriteStudioRecentFiles.json";
        private const int MaxEntries = 20;

        [Serializable]
        private class Store
        {
            public List<string> list = new List<string>();
        }

        /// <summary>Returns a fresh list (a copy) of absolute paths. Safe for the caller to enumerate directly.</summary>
        public static List<string> Get()
        {
            return LoadRaw().Select(SpriteStudioSourceMap.GetAbsolutePath).ToList();
        }

        public static void Add(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string rel = SpriteStudioSourceMap.GetRelativePath(Path.GetFullPath(path));
            var list = LoadRaw();
            list.RemoveAll(p => p == rel);
            list.Insert(0, rel);
            if (list.Count > MaxEntries)
            {
                list = list.Take(MaxEntries).ToList();
            }
            Save(list);
        }

        public static void Remove(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string rel = SpriteStudioSourceMap.GetRelativePath(Path.GetFullPath(path));
            var list = LoadRaw();
            list.RemoveAll(p => p == rel);
            Save(list);
        }

        public static void Clear()
        {
            Save(new List<string>());
        }

        /// <summary>
        /// Loads the stored (project-relative) entries. Each entry is round-tripped through the
        /// SourceMap path helpers so that comparisons in Add/Remove always see one canonical form.
        /// </summary>
        private static List<string> LoadRaw()
        {
            if (!File.Exists(FILE_PATH)) return new List<string>();
            try
            {
                var store = JsonUtility.FromJson<Store>(File.ReadAllText(FILE_PATH));
                if (store?.list == null) return new List<string>();
                return store.list
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Select(p => SpriteStudioSourceMap.GetRelativePath(SpriteStudioSourceMap.GetAbsolutePath(p)))
                    .ToList();
            }
            catch
            {
                // Disposable per-user data: a corrupted file simply resets the list.
                return new List<string>();
            }
        }

        private static void Save(List<string> list)
        {
            try
            {
                // UserSettings/ exists in any project the Editor has opened, but create it defensively
                // (e.g. right after a `git clean -fdx`).
                Directory.CreateDirectory(Path.GetDirectoryName(FILE_PATH));
                File.WriteAllText(FILE_PATH, JsonUtility.ToJson(new Store { list = list }));
            }
            catch (Exception e)
            {
                Debug.LogError($"[SSPlayer] Failed to save recent files: {e.Message}");
            }
        }
    }
}
