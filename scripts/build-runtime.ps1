#!/usr/bin/env pwsh

$baseDirectory = Split-Path -Parent $PSCommandPath
$rootDirectory = Split-Path -Parent $baseDirectory
$rawArch = (Get-Item Env:PROCESSOR_ARCHITECTURE).Value
if ($rawArch -match "ARM64") {
    $HOST_ARCH = "arm64"
} else {
    $HOST_ARCH = "x86_64"
}
$HOST_PLATFORM = "windows"

$default_opts = @{
    arch = $HOST_ARCH
    platform = $HOST_PLATFORM
    build = "debug"
    hostbuild = "auto"
}

$opts = @{}
foreach ($key in $default_opts.Keys) { $opts[$key] = $default_opts[$key] }

$APP = Split-Path -Leaf $PSCommandPath
function usage() {
    echo "Usage: $APP [options]"
    echo "Options:"
    echo "  platform=<platform>    Target platform (windows, macos, linux, android, ios, web)"
    echo "  arch=<arch>            Target architecture (x86_64, arm64, universal, etc.)"
    echo "  build=<build>          Build mode (debug, release)"
    echo "  hostbuild=<auto|false> Set to false to force cross-compile scripts even on host"
}

foreach ($item in $Args) {
    if ($item -match "=") {
        $kv = $item -split "="
        $opts[$kv[0]] = $kv[1].ToLower()
    } elseif ($item -match "help" -or $item -eq "-h" -or $item -eq "--help") {
        usage
        exit 0
    }
}

$PLATFORM = $opts.platform
$ARCH = $opts.arch
$BUILD_MODE = $opts.build
$HOSTBUILD = $opts.hostbuild

$IS_HOST_BUILD = $false
if ($HOSTBUILD -ne "false" -and $PLATFORM -eq $HOST_PLATFORM -and $ARCH -eq $HOST_ARCH -and $BUILD_MODE -eq "debug") {
    $IS_HOST_BUILD = $true
}

echo "Building for $PLATFORM ($ARCH) in $BUILD_MODE mode (Host Build: $IS_HOST_BUILD)..."
echo ""

$SDK_DIR = "$rootDirectory/SpriteStudio-SDK"

pushd $SDK_DIR

# A native command's failure does not stop PowerShell, so without this a failed build went on to copy
# whatever an earlier build left in target/ and exited 0: the tests then ran against stale libraries.
# Stop as build-runtime.sh does under set -e.
function Assert-Built([string]$what) {
    $code = $LASTEXITCODE
    if ($null -ne $code -and $code -ne 0) {
        popd
        [Console]::Error.WriteLine("Error: $what failed (exit $code). Nothing was copied.")
        exit $code
    }
}

$CARGO_FLAGS = @()
if ($BUILD_MODE -eq "release") { $CARGO_FLAGS = @("--release") }

if ($IS_HOST_BUILD) {
    # Windows host: invoke cargo directly so artifacts land in target/$BUILD_MODE/
    & cargo build @CARGO_FLAGS
    Assert-Built "cargo build"
    & cargo build @CARGO_FLAGS -p ssruntime-ffi --features libc_alloc,panic-handler
    Assert-Built "cargo build -p ssruntime-ffi"
} else {
    # Cross-compilation: dispatch to SDK release scripts
    if ($PLATFORM -eq "windows") {
        $script = "./scripts/release-windows.ps1"
        if ($ARCH -eq "arm64") { $script = "./scripts/release-windows-arm64.ps1" }
        echo "Executing $script $BUILD_MODE..."
        & $script $BUILD_MODE
        Assert-Built $script
    } elseif ($PLATFORM -eq "ios") {
        & sh ./scripts/release-ios-xcframework.sh $BUILD_MODE
        Assert-Built "release-ios-xcframework.sh"
    } elseif ($PLATFORM -eq "web") {
        & sh ./scripts/release-emscripten.sh $BUILD_MODE
        Assert-Built "release-emscripten.sh"
    } else {
        & sh "./scripts/release-$PLATFORM.sh" $BUILD_MODE
        Assert-Built "release-$PLATFORM.sh"
    }
}
popd

# Plugins/VERSION names the SDK release download-sdk installed, and everything
# below replaces files with this local build. Stamp both "local" first, so a
# later download-sdk sees a mismatch and reinstalls instead of skipping as up to
# date. Overwritten rather than removed: each has a committed .meta, which Unity
# deletes when its asset is gone.
foreach ($pluginsDir in @("$rootDirectory/package/Runtime/Plugins", "$rootDirectory/package/Editor/Plugins")) {
    New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null
    Set-Content -Path "$pluginsDir/VERSION" -Value "local"
}

# 1. Collect C# Bindings. Each .g.cs also gets its committed .meta from scripts/upm-meta:
# the .g.cs are gitignored so their .meta cannot live next to them, and a script with no
# .meta is ignored by Unity inside an immutable package folder (see that README).
echo "Copying C# bindings..."
# The generating build scripts live in the *-ffi crates and write beside
# themselves, so these are `libs/<crate>-ffi/target/`.
Copy-Item "$SDK_DIR/libs/ssruntime-ffi/target/SSRuntimeMethods.g.cs" "$rootDirectory/package/Runtime/Bindings/" -Force
Copy-Item "$baseDirectory/upm-meta/SSRuntimeMethods.g.cs.meta" "$rootDirectory/package/Runtime/Bindings/" -Force
$editorBindingsDir = "$rootDirectory/package/Editor/Bindings"
New-Item -ItemType Directory -Path $editorBindingsDir -Force | Out-Null
if (Test-Path "$SDK_DIR/libs/ssconverter-ffi/target/SSConverterMethods.g.cs") {
    Copy-Item "$SDK_DIR/libs/ssconverter-ffi/target/SSConverterMethods.g.cs" "$editorBindingsDir/" -Force
    Copy-Item "$baseDirectory/upm-meta/SSConverterMethods.g.cs.meta" "$editorBindingsDir/" -Force
}

# 1.5. Collect Licenses
$RUNTIME_OUTPUT = "$rootDirectory/package/Runtime/Plugins"
$EDITOR_OUTPUT = "$rootDirectory/package/Editor/Plugins"
New-Item -ItemType Directory -Path $RUNTIME_OUTPUT -Force | Out-Null
New-Item -ItemType Directory -Path $EDITOR_OUTPUT -Force | Out-Null
if (Test-Path "$SDK_DIR/LICENSE.md") {
    Copy-Item "$SDK_DIR/LICENSE.md" "$RUNTIME_OUTPUT/" -Force
}
if (Test-Path "$SDK_DIR/target/licenses/THIRD-PARTY-LICENSES.ssruntime.md") {
    Copy-Item "$SDK_DIR/target/licenses/THIRD-PARTY-LICENSES.ssruntime.md" "$RUNTIME_OUTPUT/" -Force
}

if ($PLATFORM -eq "macos" -or $PLATFORM -eq "windows" -or $PLATFORM -eq "linux") {
    if (Test-Path "$SDK_DIR/LICENSE.md") {
        Copy-Item "$SDK_DIR/LICENSE.md" "$EDITOR_OUTPUT/" -Force
    }
    if (Test-Path "$SDK_DIR/target/licenses/THIRD-PARTY-LICENSES.ssconverter.md") {
        Copy-Item "$SDK_DIR/target/licenses/THIRD-PARTY-LICENSES.ssconverter.md" "$EDITOR_OUTPUT/" -Force
    }
}

# Convert PLATFORM to Unity plugin folder name
$UNITY_PLATFORM = $PLATFORM
switch ($PLATFORM) {
    "windows" { $UNITY_PLATFORM = "Windows" }
    "macos"   { $UNITY_PLATFORM = "macOS" }
    "linux"   { $UNITY_PLATFORM = "Linux" }
    "ios"     { $UNITY_PLATFORM = "iOS" }
    "android" { $UNITY_PLATFORM = "Android" }
    "web"     { $UNITY_PLATFORM = "WebGL" }
}

# Convert ARCH to Unity plugin folder name (especially for Android)
$UNITY_ARCH = $ARCH
if ($PLATFORM -eq "android") {
    if ($ARCH -eq "arm64") { $UNITY_ARCH = "arm64-v8a" }
    elseif ($ARCH -eq "arm32") { $UNITY_ARCH = "armeabi-v7a" }
}

# 2. Collect Libraries
if ($PLATFORM -eq "macos" -or $PLATFORM -eq "ios" -or $PLATFORM -eq "web") {
    $RUNTIME_LIB_OUT = "$rootDirectory/package/Runtime/Plugins/$UNITY_PLATFORM"
    $EDITOR_LIB_OUT = "$rootDirectory/package/Editor/Plugins/$UNITY_PLATFORM"
} else {
    $RUNTIME_LIB_OUT = "$rootDirectory/package/Runtime/Plugins/$UNITY_PLATFORM/$UNITY_ARCH"
    $EDITOR_LIB_OUT = "$rootDirectory/package/Editor/Plugins/$UNITY_PLATFORM/$UNITY_ARCH"
}
New-Item -ItemType Directory -Path $RUNTIME_LIB_OUT -Force | Out-Null
if ($PLATFORM -eq "macos" -or $PLATFORM -eq "windows" -or $PLATFORM -eq "linux") {
    New-Item -ItemType Directory -Path $EDITOR_LIB_OUT -Force | Out-Null
}

if ($PLATFORM -eq "ios") {
    # iOS is distributed as an XCFramework directory (device arm64 + simulator
    # arm64/x86_64), which Unity links statically (__Internal).
    $iosXc = "$SDK_DIR/target/xcframework/static/libssruntime.xcframework"
    echo "Copying XCFramework from $iosXc to $RUNTIME_LIB_OUT..."
    Remove-Item -Recurse -Force "$RUNTIME_LIB_OUT/libssruntime.xcframework" -ErrorAction SilentlyContinue
    Copy-Item -Recurse -Force $iosXc "$RUNTIME_LIB_OUT/"
    exit 0
}

if ($PLATFORM -eq "android") {
    echo "Copying Android libraries..."
    $android_targets = @(
        @{ triple = "aarch64-linux-android"; unity_arch = "arm64-v8a" },
        @{ triple = "armv7-linux-androideabi"; unity_arch = "armeabi-v7a" },
        @{ triple = "x86_64-linux-android"; unity_arch = "x86_64" }
    )
    foreach ($t in $android_targets) {
        $src = "$SDK_DIR/target/$($t.triple)/$BUILD_MODE/libssruntime.so"
        $dest_dir = "$rootDirectory/package/Runtime/Plugins/Android/$($t.unity_arch)"
        New-Item -ItemType Directory -Path $dest_dir -Force | Out-Null
        if (Test-Path $src) {
            Copy-Item $src "$dest_dir/libssruntime.so" -Force
        }
    }
    exit 0
}

$LIB_EXT = ".so"
$LIB_PREFIX = "lib"
if ($PLATFORM -eq "windows") { 
    $LIB_EXT = ".dll"
    $LIB_PREFIX = ""
} elseif ($PLATFORM -eq "macos") {
    $LIB_EXT = ".dylib"
} elseif ($PLATFORM -eq "web") {
    $LIB_EXT = ".a"
}

# Determine source directory
if ($IS_HOST_BUILD) {
    $SRC_DIR = "$SDK_DIR/target/$BUILD_MODE"
} else {
    # Architecture mapping for cross-compilation
    $TARGET_TRIPLE = ""
    if ($PLATFORM -eq "web") {
        $TARGET_TRIPLE = "wasm32-unknown-unknown"
    } elseif ($PLATFORM -eq "macos" -and $ARCH -eq "universal") {
        $TARGET_TRIPLE = "universal-apple-darwin"
    } elseif ($PLATFORM -eq "windows" -and $ARCH -eq "x86_64") {
        $TARGET_TRIPLE = "x86_64-pc-windows-msvc"
    }

    if ($TARGET_TRIPLE -ne "") {
        $SRC_DIR = "$SDK_DIR/target/$TARGET_TRIPLE/$BUILD_MODE"
    } else {
        $SRC_DIR = "$SDK_DIR/target/$BUILD_MODE"
    }
}

echo "Copying libraries from $SRC_DIR to $RUNTIME_LIB_OUT and $EDITOR_LIB_OUT..."

if ($PLATFORM -eq "macos") {
    # Runtime ships as a .bundle (Unity's documented macOS native-plugin format).
    # Prefer the bundle release-macos.sh already built; otherwise wrap the
    # freshly built host/debug dylib into an equivalent one.
    $srcBundle = "$SRC_DIR/ssruntime.bundle"
    $destBundle = "$RUNTIME_LIB_OUT/ssruntime.bundle"
    if (Test-Path $srcBundle) {
        if (Test-Path $destBundle) { Remove-Item -Recurse -Force $destBundle }
        Copy-Item -Recurse -Force $srcBundle "$RUNTIME_LIB_OUT/"
    } elseif (Test-Path "$SRC_DIR/libssruntime.dylib") {
        # Wrap via the SDK's shared packaging helper (single source of truth).
        # This branch only runs on macOS, so `sh` is available.
        $sdkVer = ""
        $m = Select-String -Path "$SDK_DIR/Cargo.toml" -Pattern '^version = "(.*)"' | Select-Object -First 1
        if ($m) { $sdkVer = $m.Matches[0].Groups[1].Value }
        & sh "$SDK_DIR/scripts/make-macos-bundle.sh" "$SRC_DIR/libssruntime.dylib" $destBundle $sdkVer
    }
} elseif (Test-Path "$SRC_DIR/$($LIB_PREFIX)ssruntime$LIB_EXT") {
    Copy-Item "$SRC_DIR/$($LIB_PREFIX)ssruntime$LIB_EXT" "$RUNTIME_LIB_OUT/" -Force
} elseif (Test-Path "$SRC_DIR/ssruntime$LIB_EXT") {
    Copy-Item "$SRC_DIR/ssruntime$LIB_EXT" "$RUNTIME_LIB_OUT/ssruntime$LIB_EXT" -Force
}

if ($PLATFORM -eq "macos" -or $PLATFORM -eq "windows" -or $PLATFORM -eq "linux") {
    if (Test-Path "$SRC_DIR/$($LIB_PREFIX)ssconverter$LIB_EXT") {
        Copy-Item "$SRC_DIR/$($LIB_PREFIX)ssconverter$LIB_EXT" "$EDITOR_LIB_OUT/" -Force
    } elseif (Test-Path "$SRC_DIR/ssconverter$LIB_EXT") {
        Copy-Item "$SRC_DIR/ssconverter$LIB_EXT" "$EDITOR_LIB_OUT/ssconverter$LIB_EXT" -Force
    }
}
