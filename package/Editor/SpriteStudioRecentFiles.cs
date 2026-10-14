using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Shared helper that persists the SS Importer window's "Recent SSPJs" list.
    /// Used by both the Window and the batch conversion (<see cref="SpriteStudioImporter"/>).
    /// </summary>
    /// <remarks>
    /// The list lives in <see cref="EditorUserSettings"/>, which writes to
    /// UserSettings/EditorUserSettings.asset -- one store per project, per user.
    /// <para>
    /// It used to live in EditorPrefs, which is per machine: every project opened with the same
    /// Unity install shared a single list under one unqualified key, so a project's Recent panel
    /// filled up with .sspj files belonging to whatever else had been imported on that machine.
    /// The window hides entries whose file no longer exists, which masked it -- another project's
    /// sources are still on disk, so they showed; only ones that had been moved or deleted
    /// disappeared, making the list look as though it cleaned itself up.
    /// </para>
    /// <para>
    /// A .sspj normally sits outside Assets/, so these are absolute paths and cannot be made
    /// project-relative. Scoping the store is the only place the project can be accounted for.
    /// </para>
    /// </remarks>
    internal static class SpriteStudioRecentFiles
    {
        private const string ConfigKey = "SpriteStudio_RecentFiles";
        private const int MaxEntries = 20;

        [System.Serializable]
        private class Store
        {
            public List<string> list = new List<string>();
        }

        /// <summary>Returns a fresh list (a copy). Safe for the caller to enumerate directly.</summary>
        public static List<string> Get()
        {
            // Null for a key this project has never written -- a first run, or a project that
            // predates the move off EditorPrefs.
            string json = EditorUserSettings.GetConfigValue(ConfigKey);
            if (string.IsNullOrEmpty(json)) return new List<string>();

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
            EditorUserSettings.SetConfigValue(ConfigKey, JsonUtility.ToJson(new Store { list = list }));
        }
    }
}
