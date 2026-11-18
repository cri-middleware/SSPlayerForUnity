using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Reconverts a .ssab from the Project browser's right-click menu.
    /// When the SourceMap link is missing/broken, it shows a dialog to re-pick the source .sspj
    /// and automatically re-links the other .ssab files in the same output folder (smart relink).
    /// This relies on the conversion convention that one output subfolder derives from one .sspj.
    /// </summary>
    public static class SpriteStudioReconvertMenu
    {
        [MenuItem("Assets/SpriteStudio/Reconvert", false, 11)]
        private static void ReconvertSelected()
        {
            var ssabPaths = Selection.objects
                .Select(AssetDatabase.GetAssetPath)
                .Where(IsSsab)
                .Distinct()
                .ToList();

            if (ssabPaths.Count == 0) return;

            // Keyed by the pair, not by the .sspj: several .ssab in one folder are one conversion, while the
            // same .sspj converted into two folders (a copied output) is two, and each writes where it is.
            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targets = new List<(string Sspj, string Dir)>();
            var dirsHandled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ssab in ssabPaths)
            {
                string dir = (Path.GetDirectoryName(ssab) ?? string.Empty).Replace('\\', '/');

                string sspj = SpriteStudioSourceMap.GetSspjForSsab(ssab);
                if (string.IsNullOrEmpty(sspj) || !File.Exists(sspj))
                {
                    // Missing link → attempt a relink once per output folder
                    if (!dirsHandled.Add(dir)) continue;

                    sspj = RelinkDirectory(ssab);
                    if (string.IsNullOrEmpty(sspj)) continue;
                }

                sspj = Path.GetFullPath(sspj);
                if (planned.Add($"{sspj}|{dir}"))
                {
                    targets.Add((sspj, dir));
                }
            }

            foreach (var target in targets)
            {
                SpriteStudioImporter.ReconvertInto(target.Sspj, target.Dir);
            }
        }

        [MenuItem("Assets/SpriteStudio/Reconvert", true)]
        private static bool ReconvertSelectedValidate()
        {
            return Selection.objects.Any(o => IsSsab(AssetDatabase.GetAssetPath(o)));
        }

        /// <summary>
        /// Shared entry point that repairs the link for a given .ssab and reconverts it.
        /// Prompts the user to pick a .sspj, re-links every .ssab in the same output folder, then reconverts.
        /// Also called from the Inspector's "Relink SSPJ..." button.
        /// </summary>
        public static void RelinkSsab(string ssabAssetPath)
        {
            if (string.IsNullOrEmpty(ssabAssetPath)) return;
            string sspj = RelinkDirectory(ssabAssetPath);
            if (!string.IsNullOrEmpty(sspj))
            {
                SpriteStudioImporter.ReconvertInto(sspj, Path.GetDirectoryName(ssabAssetPath));
            }
        }

        /// <summary>
        /// Prompts for a .sspj for the .ssab's output folder and re-links every .ssab in that folder
        /// (registers them in the SourceMap). Returns the absolute path of the chosen .sspj (null if cancelled).
        /// </summary>
        private static string RelinkDirectory(string ssabAssetPath)
        {
            string dir = Path.GetDirectoryName(ssabAssetPath);
            if (string.IsNullOrEmpty(dir)) return null;

            string title = $"Relink SSPJ for '{Path.GetFileName(dir)}'";
            string picked = EditorUtility.OpenFilePanel(title, "", "sspj");
            if (string.IsNullOrEmpty(picked) || !File.Exists(picked))
            {
                return null;
            }

            // Re-link every .ssab in the same output folder to the chosen .sspj (smart relink)
            foreach (var ssab in Directory.GetFiles(dir, "*.ssab", SearchOption.AllDirectories))
            {
                SpriteStudioSourceMap.Register(Path.GetFullPath(ssab), Path.GetFullPath(picked));
            }

            return Path.GetFullPath(picked);
        }

        private static bool IsSsab(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) &&
                   assetPath.EndsWith(".ssab", StringComparison.OrdinalIgnoreCase);
        }
    }
}
