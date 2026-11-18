using System;
using System.IO;
using UnityEditor;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// The one owner of the conversion output folder: the stored setting, what counts as a usable value,
    /// and the <c>&lt;output folder&gt;/&lt;sspj stem&gt;</c> rule that gives one .sspj its destination.
    /// </summary>
    /// <remarks>
    /// The setting lives in <see cref="EditorUserSettings"/> (UserSettings/EditorUserSettings.asset),
    /// so it is per project and per person.
    /// </remarks>
    internal static class SpriteStudioOutputFolder
    {
        public const string DefaultAssetPath = "Assets/ssab_generated";
        private const string ConfigKey = "SpriteStudio_OutputFolder";

        /// <summary>
        /// The configured folder as a project-relative asset path, falling back to (and creating) the
        /// default when nothing usable is stored. Null when even the default cannot be created.
        /// </summary>
        public static string GetAssetPath()
        {
            // Null for a project that has never chosen one; the fallback below covers that and a folder
            // that has since been deleted or moved outside Assets/.
            string stored = EditorUserSettings.GetConfigValue(ConfigKey);
            return IsUsable(stored) ? stored : EnsureDefault();
        }

        /// <summary>Absolute path of <see cref="GetAssetPath"/>. Null when there is no usable folder.</summary>
        public static string GetAbsolutePath()
        {
            string assetPath = GetAssetPath();
            return assetPath == null ? null : Path.GetFullPath(assetPath);
        }

        /// <summary>
        /// Stores the folder when it is one this package can write into, and says why when it is not.
        /// Refusing rather than storing is the point: a value that is kept but ignored at import time
        /// leaves the window showing one destination and the files landing in another.
        /// </summary>
        public static bool TrySet(string assetPath, out string reason)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                reason = "The output folder cannot be empty.";
                return false;
            }

            if (!AssetDatabase.IsValidFolder(assetPath))
            {
                reason = $"'{assetPath}' is not a folder in this project.\nPick a folder under Assets/.";
                return false;
            }

            if (!IsUnderAssets(assetPath))
            {
                // A UPM package is extracted into an immutable folder, and anything outside the project
                // is invisible to the AssetDatabase, so the generated files would never become assets.
                reason = $"'{assetPath}' is outside Assets/.\nThe generated files have to be assets of this project.";
                return false;
            }

            EditorUserSettings.SetConfigValue(ConfigKey, assetPath);
            reason = null;
            return true;
        }

        /// <summary>Restores the default folder, creating it when it is missing. Returns its asset path.</summary>
        public static string ResetToDefault()
        {
            string assetPath = EnsureDefault();
            if (assetPath != null)
            {
                EditorUserSettings.SetConfigValue(ConfigKey, assetPath);
            }
            return assetPath;
        }

        /// <summary>
        /// The folder one .sspj converts into. Null when there is no usable output folder.
        /// A reconvert started from an existing .ssab does not come through here -- it writes back into
        /// the folder that .ssab lives in, which may have been moved or renamed since.
        /// </summary>
        public static string ResolveConversionDir(string sspjPath)
        {
            string baseDir = GetAbsolutePath();
            return baseDir == null ? null : Path.Combine(baseDir, Path.GetFileNameWithoutExtension(sspjPath));
        }

        private static bool IsUsable(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) && AssetDatabase.IsValidFolder(assetPath) && IsUnderAssets(assetPath);
        }

        private static bool IsUnderAssets(string assetPath)
        {
            return assetPath == "Assets" || assetPath.StartsWith("Assets/", StringComparison.Ordinal);
        }

        private static string EnsureDefault()
        {
            if (!AssetDatabase.IsValidFolder(DefaultAssetPath))
            {
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(DefaultAssetPath));
            }
            return AssetDatabase.IsValidFolder(DefaultAssetPath) ? DefaultAssetPath : null;
        }
    }
}
