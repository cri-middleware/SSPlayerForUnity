using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Project ブラウザの右クリックメニューから .ssab を再変換 (Reconvert) する。
    /// SourceMap にリンクが無い/切れている場合は元 .sspj を選び直すダイアログを出し、
    /// 同じ出力フォルダ内の他の .ssab も自動で再リンクする (スマート再リンク)。
    /// 1 出力サブフォルダ = 1 .sspj 由来、という変換規約を利用する。
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

            var sspjToReconvert = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirsHandled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ssab in ssabPaths)
            {
                string sspj = SpriteStudioSourceMap.GetSspjForSsab(ssab);
                if (!string.IsNullOrEmpty(sspj) && File.Exists(sspj))
                {
                    sspjToReconvert.Add(Path.GetFullPath(sspj));
                    continue;
                }

                // リンク欠落 → 出力フォルダ単位で 1 度だけ再リンクを試みる
                string dir = (Path.GetDirectoryName(ssab) ?? string.Empty).Replace('\\', '/');
                if (!dirsHandled.Add(dir)) continue;

                string relinked = RelinkDirectory(ssab);
                if (!string.IsNullOrEmpty(relinked))
                {
                    sspjToReconvert.Add(Path.GetFullPath(relinked));
                }
            }

            foreach (var sspj in sspjToReconvert)
            {
                SpriteStudioImporter.ImportPaths(new[] { sspj }, interactive: false);
            }
        }

        [MenuItem("Assets/SpriteStudio/Reconvert", true)]
        private static bool ReconvertSelectedValidate()
        {
            return Selection.objects.Any(o => IsSsab(AssetDatabase.GetAssetPath(o)));
        }

        /// <summary>
        /// 指定 .ssab のリンクを修復して再変換する共有エントリ。
        /// ユーザに .sspj を選ばせ、同じ出力フォルダ内の全 .ssab を再リンクしてから再変換する。
        /// Inspector の "Relink SSPJ..." ボタンからも呼ばれる。
        /// </summary>
        public static void RelinkSsab(string ssabAssetPath)
        {
            if (string.IsNullOrEmpty(ssabAssetPath)) return;
            string sspj = RelinkDirectory(ssabAssetPath);
            if (!string.IsNullOrEmpty(sspj))
            {
                SpriteStudioImporter.ImportPaths(new[] { Path.GetFullPath(sspj) }, interactive: false);
            }
        }

        /// <summary>
        /// .ssab の出力フォルダに対して .sspj を選ばせ、フォルダ内全 .ssab を再リンク
        /// (SourceMap 登録) する。選んだ .sspj の絶対パスを返す (キャンセル時は null)。
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

            // 同じ出力フォルダ内の全 .ssab を、選んだ .sspj に再リンク (スマート再リンク)
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
