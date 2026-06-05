using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// 一括インポートのエントリポイント。
    /// - ドロップ/指定されたパス(ファイル・フォルダ混在)を受け取り、
    ///   フォルダの再帰走査は libssconverter (FFI) に委譲して配下の .sspj を収集する
    ///   (<see cref="SpriteStudioDirectoryScan"/>)。
    /// - 出力先の衝突を検出してダイアログ確認する (interactive 時)。
    /// - 変換は <see cref="SpriteStudioConversionRunner"/> にキュー投入する
    ///   (進捗表示・同時実行上限・ノンブロッキングはランナー側)。
    /// </summary>
    public static class SpriteStudioImporter
    {
        private const string OutputFolderPrefKey = "SpriteStudio_OutputFolder";
        private const string DefaultOutputPath = "Assets/ssab_generated";

        /// <summary>
        /// ドロップ/選択されたパス群をインポートする。
        /// フォルダは libssconverter で再帰走査し、配下の .sspj をすべて一括変換する。
        /// </summary>
        public static void ImportPaths(IEnumerable<string> paths, bool interactive = true)
        {
            if (paths == null) return;

            var dirs = new List<string>();
            var directSspj = new List<string>();
            foreach (var raw in paths)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                if (Directory.Exists(raw))
                {
                    dirs.Add(raw);
                }
                else if (File.Exists(raw) && Path.GetExtension(raw).ToLowerInvariant() == ".sspj")
                {
                    directSspj.Add(raw);
                }
            }

            if (dirs.Count == 0)
            {
                BeginConvert(directSspj, interactive);
                return;
            }

            // ディレクトリ走査は FFI に委譲 (非同期)。完了後に直接指定分と結合して変換する。
            SpriteStudioDirectoryScan.Start(dirs, interactive, scanned =>
            {
                var combined = new List<string>(directSspj);
                combined.AddRange(scanned);
                BeginConvert(combined, interactive);
            });
        }

        private static void BeginConvert(List<string> sspjPaths, bool interactive)
        {
            var sspjList = Dedup(sspjPaths);
            if (sspjList.Count == 0)
            {
                if (interactive)
                {
                    Debug.LogWarning("[SSPlayer] No .sspj found in the dropped item(s).");
                }
                return;
            }

            string baseDir = ResolveOutputBaseDir();
            if (baseDir == null)
            {
                Debug.LogError("[SSPlayer] Invalid output folder. Open 'Window/SpriteStudio/Importer' and set it.");
                return;
            }

            var jobs = sspjList
                .Select(sspj => new Job { Sspj = sspj, OutDir = Path.Combine(baseDir, Path.GetFileNameWithoutExtension(sspj)) })
                .ToList();

            if (interactive && !ConfirmNoCollision(jobs))
            {
                return; // ユーザがキャンセル
            }

            foreach (var job in jobs)
            {
                SpriteStudioRecentFiles.Add(job.Sspj);
                SpriteStudioConversionRunner.Enqueue(job.Sspj, job.OutDir);
            }
        }

        private struct Job
        {
            public string Sspj;
            public string OutDir;
        }

        /// <summary>絶対パスで重複排除した .sspj 一覧を返す。</summary>
        private static List<string> Dedup(IEnumerable<string> sspjPaths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in sspjPaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                string full = Path.GetFullPath(p);
                if (seen.Add(full)) result.Add(full);
            }
            return result;
        }

        /// <summary>出力先ベースディレクトリ (絶対パス) を返す。無効なら null。</summary>
        private static string ResolveOutputBaseDir()
        {
            string assetPath = EditorPrefs.GetString(OutputFolderPrefKey, DefaultOutputPath);
            if (string.IsNullOrEmpty(assetPath) || !AssetDatabase.IsValidFolder(assetPath))
            {
                if (!AssetDatabase.IsValidFolder(DefaultOutputPath))
                {
                    AssetDatabase.CreateFolder("Assets", "ssab_generated");
                }
                assetPath = DefaultOutputPath;
                if (!AssetDatabase.IsValidFolder(assetPath)) return null;
            }
            return Path.GetFullPath(assetPath);
        }

        /// <summary>
        /// 出力衝突を検出し、あればダイアログで確認する。続行可なら true。
        /// 衝突1: バッチ内で同一 stem (出力先が重なる)。
        /// 衝突2: 既存出力先が別の .sspj 由来 (SourceMap 照合)。
        /// </summary>
        private static bool ConfirmNoCollision(List<Job> jobs)
        {
            var messages = new List<string>();

            // 衝突1: バッチ内 stem 重複
            var dupStems = jobs
                .GroupBy(j => Path.GetFileName(j.OutDir), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);
            foreach (var g in dupStems)
            {
                messages.Add($"・同名 '{g.Key}' が {g.Count()} 件 (出力先が重複):\n    " +
                             string.Join("\n    ", g.Select(j => j.Sspj)));
            }

            // 衝突2: 既存出力先が別 sspj 由来
            foreach (var job in jobs)
            {
                if (!Directory.Exists(job.OutDir)) continue;
                var existing = Directory.GetFiles(job.OutDir, "*.ssab", SearchOption.AllDirectories);
                foreach (var ssab in existing)
                {
                    string owner = SpriteStudioSourceMap.GetSspjForSsab(ssab);
                    if (!string.IsNullOrEmpty(owner) &&
                        Path.GetFullPath(owner) != Path.GetFullPath(job.Sspj))
                    {
                        messages.Add($"・出力先 '{Path.GetFileName(job.OutDir)}' は別プロジェクト由来:\n" +
                                     $"    既存: {owner}\n    今回: {job.Sspj}");
                        break; // この出力先は 1 回報告すれば十分
                    }
                }
            }

            if (messages.Count == 0) return true;

            string body = "以下の出力衝突が見つかりました。既存の生成物を上書きしますか?\n\n" +
                          string.Join("\n\n", messages);
            return EditorUtility.DisplayDialog("SpriteStudio: 出力の衝突", body, "上書きして続行", "キャンセル");
        }
    }
}
