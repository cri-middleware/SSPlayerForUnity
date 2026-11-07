using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Entry point for batch import.
    /// - Accepts dropped/specified paths (a mix of files and folders) and delegates recursive folder
    ///   traversal to libssconverter (FFI) to collect the .sspj files beneath them
    ///   (<see cref="SpriteStudioDirectoryScan"/>).
    /// - Gives every .sspj its own output folder, and reports the ones a collision leaves out.
    /// - Detects an output that belongs to a different .sspj and confirms via dialog (when interactive).
    /// - Queues the conversion into <see cref="SpriteStudioConversionRunner"/>
    ///   (progress display, concurrency limit, and non-blocking behavior are handled by the runner).
    /// </summary>
    public static class SpriteStudioImporter
    {
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

        /// <summary>
        /// Reconverts one .sspj back into an output folder that already exists, instead of into the folder
        /// the <c>&lt;output folder&gt;/&lt;stem&gt;</c> rule would name.
        /// The generated folder may be moved or renamed after it is created (the source map follows the
        /// move), so a reconvert started from an existing .ssab has to write where
        /// that .ssab is -- writing by the rule instead would recreate the original folder and leave the
        /// asset the user asked about untouched.
        /// </summary>
        public static void ReconvertInto(string sspjPath, string outputDir)
        {
            if (string.IsNullOrEmpty(sspjPath) || string.IsNullOrEmpty(outputDir)) return;

            RunPlan(new List<Job>
            {
                new Job { Sspj = Path.GetFullPath(sspjPath), OutDir = Path.GetFullPath(outputDir) },
            }, interactive: false);
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

            var jobs = new List<Job>();
            foreach (var sspj in sspjList)
            {
                string outDir = SpriteStudioOutputFolder.ResolveConversionDir(sspj);
                if (outDir == null)
                {
                    Debug.LogError("[SSPlayer] Invalid output folder. Open 'Window/SpriteStudio/Importer' and set it.");
                    return;
                }
                jobs.Add(new Job { Sspj = sspj, OutDir = outDir });
            }

            RunPlan(jobs, interactive);
        }

        private static void RunPlan(List<Job> jobs, bool interactive)
        {
            jobs = DedupePlan(jobs);
            if (jobs.Count == 0) return;

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

        /// <summary>
        /// One output folder, one job. Two .sspj whose file names match resolve to the same folder, and
        /// nothing downstream would tell them apart.
        /// The same project reaching the plan twice is the same work, so the duplicate goes quietly; two
        /// different projects cannot both own the folder, so the first keeps it and the rest are named --
        /// the fix is the user's (rename one, or import them into separate output folders), and dropping
        /// them silently would look like the converter lost a file.
        /// </summary>
        private static List<Job> DedupePlan(List<Job> jobs)
        {
            var kept = new List<Job>();
            var ownerOfOutDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var skipped = new List<string>();

            foreach (var job in jobs)
            {
                if (ownerOfOutDir.TryGetValue(job.OutDir, out string owner))
                {
                    if (!string.Equals(owner, job.Sspj, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.Add($"- {job.Sspj}\n    '{job.OutDir}' is already written by {owner}");
                    }
                    continue;
                }

                ownerOfOutDir[job.OutDir] = job.Sspj;
                kept.Add(job);
            }

            if (skipped.Count > 0)
            {
                Debug.LogWarning("[SSPlayer] Not converted -- their output folders collide within this import. " +
                                 "Rename one .sspj, or import them into separate output folders:\n" +
                                 string.Join("\n", skipped));
            }

            return kept;
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

        /// <summary>
        /// Detects that an existing output originates from a different .sspj (checked against the SourceMap)
        /// and, if so, confirms via dialog. Returns true if it is OK to proceed.
        /// Two jobs of one batch colliding with each other is <see cref="DedupePlan"/>'s, not this dialog's:
        /// there is no version of that the user can say yes to.
        /// </summary>
        private static bool ConfirmNoCollision(List<Job> jobs)
        {
            var messages = new List<string>();

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
