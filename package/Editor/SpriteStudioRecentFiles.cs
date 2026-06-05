using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Importer Window の "Recent SSPJs" を EditorPrefs に永続化する共有ヘルパ。
    /// Window と一括変換 (<see cref="SpriteStudioImporter"/>) の双方から利用する。
    /// 既存の保存フォーマット (キー / JSON 形状) と互換。
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

        /// <summary>新しいリスト(コピー)を返す。呼び出し側でそのまま列挙しても安全。</summary>
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
