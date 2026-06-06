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
                // 一時ファイルへ書いてからアトミックに置換する。直接上書き中にエディタが落ちると
                // JSON が壊れ、次回ロードで全リンクが失われる (GetMap が catch して空にリセットする)
                // ため、temp → File.Replace で原子性を確保する。同一ディレクトリなので同一ボリューム。
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
        /// .ssab → .sspj のリンクを登録する。<paramref name="save"/>=false のときは辞書だけ更新し、
        /// 保存しない (大量登録時に呼び出し側でまとめて <see cref="SaveMap"/> を 1 回呼ぶため)。
        /// </summary>
        public static void Register(string ssabAbsolutePath, string sspjAbsolutePath, bool save = true)
        {
            var map = GetMap();
            string ssabRel = GetRelativePath(ssabAbsolutePath);
            string sspjRel = GetRelativePath(sspjAbsolutePath);

            map[ssabRel] = sspjRel;
            if (save) SaveMap();
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
