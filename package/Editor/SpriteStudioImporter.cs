using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Entry point for batch import.
    /// - Accepts dropped/specified paths (a mix of files and folders) and delegates recursive folder
    ///   traversal to libssconverter (FFI) to collect the .sspj files beneath them
    ///   (<see cref="SpriteStudioDirectoryScan"/>).
    /// - Detects output collisions and confirms via dialog (when interactive).
    /// - Queues the conversion into <see cref="SpriteStudioConversionRunner"/>
    ///   (progress display, concurrency limit, and non-blocking behavior are handled by the runner).
    /// </summary>
    public static class SpriteStudioImporter
    {
        private const string OutputFolderConfigKey = "SpriteStudio_OutputFolder";
        private const string DefaultOutputPath = "Assets/ssab_generated";

        /// <summary>
        /// Imports the dropped/selected paths.
        /// Folders are recursively scanned with libssconverter, and every .sspj beneath them is batch-converted.
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

            // Delegate directory traversal to the FFI (asynchronous). After it completes, combine with the directly specified files and convert.
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
                return; // user canceled
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

        /// <summary>Returns the .sspj list de-duplicated by absolute path.</summary>
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

        /// <summary>Returns the output base directory (absolute path). null if invalid.</summary>
        private static string ResolveOutputBaseDir()
        {
            // Null for a project that has never chosen one; the guard below falls back.
            string assetPath = EditorUserSettings.GetConfigValue(OutputFolderConfigKey);
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
        /// Detects output collisions and, if any, confirms via dialog. Returns true if it is OK to proceed.
        /// Collision 1: the same stem within the batch (outputs overlap).
        /// Collision 2: an existing output originates from a different .sspj (checked against the SourceMap).
        /// </summary>
        private static bool ConfirmNoCollision(List<Job> jobs)
        {
            var messages = new List<string>();

            // Collision 1: duplicate stems within the batch
            var dupStems = jobs
                .GroupBy(j => Path.GetFileName(j.OutDir), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);
            foreach (var g in dupStems)
            {
                messages.Add($"- '{g.Key}' appears {g.Count()} times (outputs overlap):\n    " +
                             string.Join("\n    ", g.Select(j => j.Sspj)));
            }

            // Collision 2: existing output originates from a different sspj
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
                        messages.Add($"- Output '{Path.GetFileName(job.OutDir)}' originates from a different project:\n" +
                                     $"    Existing: {owner}\n    This run: {job.Sspj}");
                        break; // reporting this output once is enough
                    }
                }
            }

            if (messages.Count == 0) return true;

            string body = "The following output collisions were found. Overwrite the existing generated output?\n\n" +
                          string.Join("\n\n", messages);
            return EditorUtility.DisplayDialog("SpriteStudio: Output collision", body, "Overwrite and continue", "Cancel");
        }
    }
}
