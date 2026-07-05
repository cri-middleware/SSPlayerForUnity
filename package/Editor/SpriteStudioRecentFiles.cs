using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Shared helper that persists the Importer Window's "Recent SSPJs" list to EditorPrefs.
    /// Used by both the Window and the batch conversion (<see cref="SpriteStudioImporter"/>).
    /// Compatible with the existing storage format (key / JSON shape).
    /// </summary>
    internal static class SpriteStudioRecentFiles
    {
        private const string PrefKey = "SpriteStudio_RecentFiles";
        private const int MaxEntries = 20;

        [System.Serializable]
        private class Store
        {
            public List<string> list = new List<string>();
        }

        /// <summary>Returns a fresh list (a copy). Safe for the caller to enumerate directly.</summary>
        public static List<string> Get()
        {
            string json = EditorPrefs.GetString(PrefKey, "[]");
            try
            {
                var store = JsonUtility.FromJson<Store>(json);
                return store?.list ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        public static void Add(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var list = Get();
            list.RemoveAll(p => p == path);
            list.Insert(0, path);
            if (list.Count > MaxEntries)
            {
                list = list.Take(MaxEntries).ToList();
            }
            Save(list);
        }

        public static void Remove(string path)
        {
            var list = Get();
            list.RemoveAll(p => p == path);
            Save(list);
        }

        public static void Clear()
        {
            Save(new List<string>());
        }

        private static void Save(List<string> list)
        {
            EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(new Store { list = list }));
        }
    }
}
