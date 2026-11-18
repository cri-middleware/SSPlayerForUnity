$ErrorActionPreference = "Stop"
# Invoke-WebRequest's progress bar slows the download itself in Windows PowerShell 5.1.
$ProgressPreference = "SilentlyContinue"

$HelpApp = Split-Path -Leaf $PSCommandPath
function Show-Usage {
    Write-Host "Usage: ${HelpApp} [--help]"
    Write-Host "  Install the SpriteStudio-SDK release pinned by scripts/SDK_VERSION.txt into the"
    Write-Host "  package's Plugins and Bindings. Skips the download when that version is in place."
}

foreach ($arg in $args) {
    switch -Regex ($arg) {
        '^(-h|--help|help)$' { Show-Usage; exit 0 }
        default {
            [Console]::Error.WriteLine("${HelpApp}: unknown argument '$arg'")
            Show-Usage
            exit 2
        }
    }
}

$baseDirectory = Split-Path -Parent $PSCommandPath
$rootDirectory = Split-Path -Parent $baseDirectory
$pkg = "$rootDirectory/package"
$runtimePlugins = "$pkg/Runtime/Plugins"
$editorPlugins = "$pkg/Editor/Plugins"
$runtimeBindings = "$pkg/Runtime/Bindings"
$editorBindings = "$pkg/Editor/Bindings"
# Committed .meta for the generated files these scripts place into the package.
$upmMeta = "$baseDirectory/upm-meta"
$versionFile = "$baseDirectory/SDK_VERSION.txt"
# Version stamps are written to both plugin dirs; the runtime one gates the skip.
$stamp = "$runtimePlugins/VERSION"
# Downloaded zips are cached here (gitignored) so a re-run reuses them instead of
# re-downloading; extraction still uses a throwaway temp.
$cacheDir = "$baseDirectory/.sdk-cache"

$targetVersion = (Get-Content $versionFile).Trim()

if (Test-Path $stamp) {
    if ((Get-Content $stamp).Trim() -eq $targetVersion) {
        Write-Host "SDK $targetVersion is already up to date. Skipping download."
        exit 0
    }
}

$baseUrl = "https://github.com/cri-middleware/SpriteStudio-SDK/releases/download/$targetVersion"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path $tmp | Out-Null
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
$sumsFile = "$cacheDir/SHA256SUMS"

try {
    # Get-FileHash returns an upper-case hash; sha256sum writes lower-case, so
    # compare case-insensitively.
    function Get-Sha256($file) { (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLower() }

    # Expected hash for <name> from the release's SHA256SUMS manifest.
    function Get-ExpectedSha256($name) {
        foreach ($line in Get-Content $sumsFile) {
            $parts = $line -split '\s+', 2
            if ($parts.Count -eq 2 -and $parts[1].Trim() -eq $name) { return $parts[0].ToLower() }
        }
        Write-Error "No checksum entry for $name in SHA256SUMS"
    }

    # The SDK release publishes two flat zips: libs/<platform>/ plus include/,
    # bindings/, VERSION and the license notices at the archive root.
    #   static  : .a / .lib / static xcframework
    #   dynamic : .dylib / .so / .dll / dynamic xcframework
    Write-Host "Target SDK Version: $targetVersion"
    # Fetch the checksum manifest (small): it both verifies integrity and decides
    # whether an already-cached zip can be reused without re-downloading.
    Write-Host "Downloading checksum manifest: $baseUrl/SHA256SUMS"
    Invoke-WebRequest -Uri "$baseUrl/SHA256SUMS" -OutFile $sumsFile
    foreach ($kind in @("static", "dynamic")) {
        $asset = "spritestudio-sdk-$kind-libs.zip"
        # Keep the release asset's own name so it matches the SHA256SUMS entry.
        $zip = "$cacheDir/$asset"
        $expected = Get-ExpectedSha256 $asset
        # Reuse the cached zip when it already matches the manifest; else download.
        if ((Test-Path $zip) -and ((Get-Sha256 $zip) -eq $expected)) {
            Write-Host "Reusing cached $asset"
        } else {
            Write-Host "Downloading $baseUrl/$asset"
            Invoke-WebRequest -Uri "$baseUrl/$asset" -OutFile $zip
            if ((Get-Sha256 $zip) -ne $expected) {
                Remove-Item -Force $zip
                Write-Error "Checksum mismatch for $asset"
            }
        }
        Write-Host "Checksum OK: $asset"
        # Expand-Archive is a script-module function: run from a session, it reads
        # the global progress preference rather than this script's.
        $savedProgress = $global:ProgressPreference
        $global:ProgressPreference = "SilentlyContinue"
        try {
            Expand-Archive -Path $zip -DestinationPath "$tmp/$kind" -Force
        } finally {
            $global:ProgressPreference = $savedProgress
        }
    }

    # Flat archives: libs/, include/, bindings/, VERSION sit at the extraction root.
    $static = "$tmp/static"
    $dynamic = "$tmp/dynamic"

    # Copy-Sdk <src> <dst-dir>: fail fast if the source is missing so a change in
    # the SDK zip layout surfaces here instead of shipping an incomplete package.
    # Replaces only the binary itself (removing any stale same-named file/dir
    # first so an .xcframework directory doesn't nest); the committed sibling
    # .meta that carries the Unity PluginImporter platform settings is untouched.
    function Copy-Sdk($src, $dstDir) {
        if (-not (Test-Path $src)) {
            Write-Error "ERROR: expected SDK file not found: $src"
        }
        New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
        $dest = Join-Path $dstDir (Split-Path $src -Leaf)
        if (Test-Path $dest) { Remove-Item -Path $dest -Recurse -Force }
        Copy-Item -Path $src -Destination $dstDir -Recurse -Force
    }

    Write-Host "Placing ssruntime into Runtime/Plugins (iOS/WebGL static, others dynamic)..."
    Copy-Sdk "$dynamic/libs/macos/ssruntime.bundle"              "$runtimePlugins/macOS"
    Copy-Sdk "$dynamic/libs/windows/x86_64/ssruntime.dll"        "$runtimePlugins/Windows/x86_64"
    Copy-Sdk "$dynamic/libs/linux/x86_64/libssruntime.so"        "$runtimePlugins/Linux/x86_64"
    Copy-Sdk "$dynamic/libs/android/arm64-v8a/libssruntime.so"   "$runtimePlugins/Android/arm64-v8a"
    Copy-Sdk "$dynamic/libs/android/armeabi-v7a/libssruntime.so" "$runtimePlugins/Android/armeabi-v7a"
    Copy-Sdk "$dynamic/libs/android/x86_64/libssruntime.so"      "$runtimePlugins/Android/x86_64"
    Copy-Sdk "$static/libs/ios/libssruntime.xcframework"         "$runtimePlugins/iOS"
    Copy-Sdk "$static/libs/web/libssruntime.a"                   "$runtimePlugins/WebGL"

    Write-Host "Placing ssconverter into Editor/Plugins (win/mac, dynamic only)..."
    Copy-Sdk "$dynamic/libs/macos/libssconverter.dylib"          "$editorPlugins/macOS"
    Copy-Sdk "$dynamic/libs/windows/x86_64/ssconverter.dll"      "$editorPlugins/Windows/x86_64"

    # csbindgen C# FFI bindings (platform-independent). These .g.cs are gitignored
    # and are what lets the package compile against the native libs, so a no-build
    # release path must place them here (build-runtime.ps1 does the same locally).
    Write-Host "Placing csbindgen FFI bindings into Runtime/Editor Bindings..."
    Copy-Sdk "$dynamic/bindings/SSRuntimeMethods.g.cs"           $runtimeBindings
    Copy-Sdk "$dynamic/bindings/SSConverterMethods.g.cs"         $editorBindings

    # Each .g.cs also needs its .meta. The .g.cs are gitignored, so their .meta cannot be
    # committed next to them; they live in scripts/upm-meta with fixed GUIDs instead (see
    # the README there). Without a .meta Unity ignores the script inside the immutable
    # package folder, and the package fails to compile for consumers.
    Copy-Sdk "$upmMeta/SSRuntimeMethods.g.cs.meta"               $runtimeBindings
    Copy-Sdk "$upmMeta/SSConverterMethods.g.cs.meta"             $editorBindings

    # License / third-party notices ride alongside each plugin set.
    Copy-Sdk "$dynamic/LICENSE.md"                          $runtimePlugins
    Copy-Sdk "$dynamic/THIRD-PARTY-LICENSES.ssruntime.md"   $runtimePlugins
    Copy-Sdk "$dynamic/LICENSE.md"                          $editorPlugins
    Copy-Sdk "$dynamic/THIRD-PARTY-LICENSES.ssconverter.md" $editorPlugins

    Set-Content -Path "$runtimePlugins/VERSION" -Value $targetVersion
    Set-Content -Path "$editorPlugins/VERSION" -Value $targetVersion

    Write-Host "Done."
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
