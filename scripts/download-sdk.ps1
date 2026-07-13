$ErrorActionPreference = "Stop"

$baseDirectory = Split-Path -Parent $PSCommandPath
$rootDirectory = Split-Path -Parent $baseDirectory
$pkg = "$rootDirectory/package"
$runtimePlugins = "$pkg/Runtime/Plugins"
$editorPlugins = "$pkg/Editor/Plugins"
$runtimeBindings = "$pkg/Runtime/Bindings"
$editorBindings = "$pkg/Editor/Bindings"
$versionFile = "$baseDirectory/SDK_VERSION.txt"
# Version stamps are written to both plugin dirs; the runtime one gates the skip.
$stamp = "$runtimePlugins/VERSION"

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
$sumsFile = "$tmp/SHA256SUMS"

try {
    # Verify <file> against the release's SHA256SUMS manifest entry for
    # <asset-name> before extracting, so a corrupted or tampered download fails
    # loudly. Get-FileHash returns an upper-case hash; sha256sum writes
    # lower-case, so compare case-insensitively.
    function Assert-Sha256($file, $name) {
        $expected = $null
        foreach ($line in Get-Content $sumsFile) {
            $parts = $line -split '\s+', 2
            if ($parts.Count -eq 2 -and $parts[1].Trim() -eq $name) { $expected = $parts[0].ToLower(); break }
        }
        if (-not $expected) {
            Write-Error "No checksum entry for $name in SHA256SUMS"
        }
        $actual = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLower()
        if ($expected -ne $actual) {
            Write-Error "Checksum mismatch for $name`n  expected: $expected`n  actual:   $actual"
        }
        Write-Host "Checksum OK: $name"
    }

    # The SDK release publishes two zips. Each unzips to output/<kind>/runtime/,
    # holding libs/<platform>/ plus include/, VERSION and the license notices.
    #   static  : .a / .lib / static xcframework
    #   dynamic : .dylib / .so / .dll / dynamic xcframework
    Write-Host "Target SDK Version: $targetVersion"
    Write-Host "Downloading checksum manifest: $baseUrl/SHA256SUMS"
    Invoke-WebRequest -Uri "$baseUrl/SHA256SUMS" -OutFile $sumsFile
    foreach ($kind in @("static", "dynamic")) {
        $asset = "spritestudio-sdk-$kind-libs.zip"
        Write-Host "Downloading $baseUrl/$asset"
        $zip = "$tmp/$kind.zip"
        Invoke-WebRequest -Uri "$baseUrl/$asset" -OutFile $zip
        Assert-Sha256 $zip $asset
        Expand-Archive -Path $zip -DestinationPath "$tmp/$kind" -Force
    }

    $static = "$tmp/static/output/static/runtime"
    $dynamic = "$tmp/dynamic/output/dynamic/runtime"

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
