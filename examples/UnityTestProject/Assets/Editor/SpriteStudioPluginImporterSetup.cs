using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SpriteStudioDev
{
    /// <summary>
    /// Maintainer tool: sets the PluginImporter platform settings for every native library the
    /// SpriteStudio package ships, so the resulting .meta can be committed.
    ///
    /// Why this exists: the binaries are gitignored (fetched by download-sdk.sh) but their .meta
    /// must be committed - a UPM package is extracted into an immutable folder, where Unity
    /// ignores any asset that has no .meta, and CI has no Unity to generate one. Without the
    /// .meta the native libs are silently dropped from the installed package, and the three
    /// Android libssruntime.so would additionally collide on filename if imported with the
    /// default "Any Platform" setting.
    ///
    /// Doing it here rather than by hand in the Inspector keeps the settings in one reviewable
    /// table and makes the pass repeatable - re-run it after adding a platform and the .meta
    /// come out the same. Run scripts/scaffold-plugin-placeholders.sh first so a file exists at
    /// every path, including targets this machine cannot build.
    ///
    /// Editor-only dev tool; it lives in the example project and is never part of the package.
    /// </summary>
    public static class SpriteStudioPluginImporterSetup
    {
        private const string PackageRoot = "Packages/jp.co.cri-mw.spritestudio.ssplayer-unity";
        // Matches where the package already puts its editor commands (the package's own
        // "Create or Update Default AddOn Catalog" lives under Window/SpriteStudio too).
        // A top-level SpriteStudio root would be a third convention for no gain.
        private const string MenuRoot = "Window/SpriteStudio/Dev/";

        /// <summary>One native library and the platforms it is enabled for.</summary>
        private sealed class Spec
        {
            /// <summary>Path under the package root. Mirrors the copy list in download-sdk.sh.</summary>
            public string RelPath;

            /// <summary>Player build targets this lib is compatible with.</summary>
            public BuildTarget[] Players = Array.Empty<BuildTarget>();

            /// <summary>Load in the editor too (desktop libs, so play mode works).</summary>
            public bool Editor;

            /// <summary>Editor host OS filter - "OSX" / "Windows" / "Linux". Only read when Editor.</summary>
            public string EditorOS;

            /// <summary>Editor CPU filter. Only read when Editor.</summary>
            public string EditorCpu = "AnyCPU";

            /// <summary>Android ABI - "ARM64" / "ARMv7" / "X86_64". Null for non-Android libs.</summary>
            public string AndroidCpu;
        }

        // Every target touched below is reset before the per-spec flags are applied, so a re-run
        // cannot leave a stale platform enabled from an earlier pass.
        //
        // 32-bit StandaloneWindows is deliberately NOT here. Dropping ssruntime.dll into
        // Windows/x86_64 makes Unity enable both Win and Win64, and on 6000.3.16f1 that Win
        // flag cannot be cleared: SetCompatibleWithPlatform(StandaloneWindows, false) is a
        // no-op, and editing `Win: enabled: 0` into the .meta by hand gets rewritten back to
        // 1 on the next reimport. Unity treats it as the correct state for this folder
        // layout, and a consumer's editor would derive the same thing, so the tool does not
        // pretend to own a flag it cannot set. Consequence is bounded: it only matters to a
        // 32-bit Windows player, which would get the 64-bit lib and fail to load it.
        private static readonly BuildTarget[] ManagedTargets =
        {
            BuildTarget.StandaloneOSX,
            BuildTarget.StandaloneWindows64,
            BuildTarget.StandaloneLinux64,
            BuildTarget.Android,
            BuildTarget.iOS,
            BuildTarget.WebGL,
        };

        private static readonly Spec[] Specs =
        {
            // ssruntime - ships to players, and is loaded in the editor on desktop hosts.
            new Spec
            {
                RelPath = "Runtime/Plugins/macOS/ssruntime.bundle",
                Players = new[] { BuildTarget.StandaloneOSX },
                Editor = true, EditorOS = "OSX",
            },
            new Spec
            {
                RelPath = "Runtime/Plugins/Windows/x86_64/ssruntime.dll",
                Players = new[] { BuildTarget.StandaloneWindows64 },
                Editor = true, EditorOS = "Windows",
            },
            new Spec
            {
                RelPath = "Runtime/Plugins/Linux/x86_64/libssruntime.so",
                Players = new[] { BuildTarget.StandaloneLinux64 },
                Editor = true, EditorOS = "Linux",
            },

            // Android - three same-named .so. The per-ABI CPU is what keeps Unity from
            // reporting "Multiple plugins with the same name".
            new Spec
            {
                RelPath = "Runtime/Plugins/Android/arm64-v8a/libssruntime.so",
                Players = new[] { BuildTarget.Android }, AndroidCpu = "ARM64",
            },
            new Spec
            {
                RelPath = "Runtime/Plugins/Android/armeabi-v7a/libssruntime.so",
                Players = new[] { BuildTarget.Android }, AndroidCpu = "ARMv7",
            },
            new Spec
            {
                RelPath = "Runtime/Plugins/Android/x86_64/libssruntime.so",
                Players = new[] { BuildTarget.Android }, AndroidCpu = "X86_64",
            },

            // Static libs - linked into the player, so the C# side imports them as __Internal.
            new Spec
            {
                RelPath = "Runtime/Plugins/iOS/libssruntime.xcframework",
                Players = new[] { BuildTarget.iOS },
            },
            new Spec
            {
                RelPath = "Runtime/Plugins/WebGL/libssruntime.a",
                Players = new[] { BuildTarget.WebGL },
            },

            // ssconverter - editor-only (it runs at import time and never ships in a player),
            // so no player target is enabled for either lib.
            new Spec
            {
                RelPath = "Editor/Plugins/macOS/libssconverter.dylib",
                Editor = true, EditorOS = "OSX",
            },
            new Spec
            {
                RelPath = "Editor/Plugins/Windows/x86_64/ssconverter.dll",
                Editor = true, EditorOS = "Windows", EditorCpu = "x86_64",
            },
        };

        [MenuItem(MenuRoot + "Configure Native Plugin Importers")]
        private static void Configure()
        {
            var missing = new List<string>();
            int configured = 0;

            // The placeholders are written from a shell script, outside Unity. Until the asset
            // database picks them up, GetAtPath returns null and they get skipped as "missing" -
            // which silently leaves exactly the platforms that need this tool unconfigured. Do
            // not rely on the editor having auto-refreshed (Preferences can turn that off).
            AssetDatabase.Refresh();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var spec in Specs)
                {
                    string path = PackageRoot + "/" + spec.RelPath;
                    if (!(AssetImporter.GetAtPath(path) is PluginImporter importer))
                    {
                        missing.Add(spec.RelPath);
                        continue;
                    }

                    importer.SetCompatibleWithAnyPlatform(false);
                    foreach (var target in ManagedTargets)
                    {
                        importer.SetCompatibleWithPlatform(target, false);
                    }
                    foreach (var target in spec.Players)
                    {
                        importer.SetCompatibleWithPlatform(target, true);
                    }

                    importer.SetCompatibleWithEditor(spec.Editor);
                    if (spec.Editor)
                    {
                        importer.SetEditorData("OS", spec.EditorOS);
                        importer.SetEditorData("CPU", spec.EditorCpu);
                    }

                    if (spec.AndroidCpu != null)
                    {
                        importer.SetPlatformData(BuildTarget.Android, "CPU", spec.AndroidCpu);
                    }

                    importer.SaveAndReimport();
                    configured++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }

            Debug.Log($"[SS-PluginMeta] Configured {configured}/{Specs.Length} plugin importers.");
            if (missing.Count > 0)
            {
                // Either the file is absent (run scripts/scaffold-plugin-placeholders.sh) or Unity
                // did not classify it as a native plugin - which is what to check first if a
                // placeholder was used, since that classification does read the file.
                Debug.LogWarning(
                    "[SS-PluginMeta] Not imported as a PluginImporter:\n  " +
                    string.Join("\n  ", missing) +
                    "\nRun scripts/scaffold-plugin-placeholders.sh, or check whether Unity rejected the file.");
            }
            else
            {
                Debug.Log("[SS-PluginMeta] All paths resolved. Run 'Report' to review, then " +
                          "scaffold-plugin-placeholders.sh --clean and commit the .meta.");
            }
        }

        [MenuItem(MenuRoot + "Report Native Plugin Importers")]
        private static void Report()
        {
            var sb = new StringBuilder("[SS-PluginMeta] Current plugin importer state:\n");
            foreach (var spec in Specs)
            {
                string path = PackageRoot + "/" + spec.RelPath;
                if (!(AssetImporter.GetAtPath(path) is PluginImporter importer))
                {
                    sb.AppendLine($"  MISSING  {spec.RelPath}");
                    continue;
                }

                var enabled = new List<string>();
                var problems = new List<string>();

                if (importer.GetCompatibleWithEditor() != spec.Editor)
                {
                    problems.Add($"Editor should be {spec.Editor}");
                }
                if (importer.GetCompatibleWithEditor())
                {
                    enabled.Add($"Editor({importer.GetEditorData("OS")}/{importer.GetEditorData("CPU")})");
                }

                // Any must stay off - it is what keeps the three same-named Android
                // libssruntime.so from colliding on filename.
                if (importer.GetCompatibleWithAnyPlatform())
                {
                    problems.Add("anyPlatform is ON");
                }

                var wanted = new HashSet<BuildTarget>(spec.Players);
                foreach (var target in ManagedTargets)
                {
                    bool on = importer.GetCompatibleWithPlatform(target);
                    if (on)
                    {
                        string cpu = importer.GetPlatformData(target, "CPU");
                        enabled.Add(string.IsNullOrEmpty(cpu) ? target.ToString() : $"{target}({cpu})");
                    }
                    if (on != wanted.Contains(target))
                    {
                        problems.Add($"{target} should be {wanted.Contains(target)}");
                    }
                }

                sb.AppendLine($"  {(problems.Count == 0 ? "ok  " : "FAIL")}  {spec.RelPath}" +
                              $"  [{string.Join(", ", enabled)}]" +
                              (problems.Count == 0 ? "" : $"\n           -> {string.Join("; ", problems)}"));
            }
            Debug.Log(sb.ToString());
        }
    }
}
