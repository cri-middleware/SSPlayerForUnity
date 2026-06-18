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

# Godot options
$default_opts = @{
    arch = $HOST_ARCH
    platform = $HOST_PLATFORM
    build = "debug"
    ios_simulator = "no"
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
    echo "  ios_simulator=<yes|no> Build for iOS simulator (default: no)"
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
$IOS_SIMULATOR = $opts.ios_simulator

$IS_HOST_BUILD = $false
if ($PLATFORM -eq $HOST_PLATFORM -and $ARCH -eq $HOST_ARCH) {
    $IS_HOST_BUILD = $true
}

echo "Building for $PLATFORM ($ARCH) in $BUILD_MODE mode (iOS Sim: $IOS_SIMULATOR, Host Build: $IS_HOST_BUILD)..."
echo ""

$SDK_DIR = "$rootDirectory/SpriteStudio-SDK"

pushd $SDK_DIR

$CARGO_FLAGS = @()
if ($BUILD_MODE -eq "release") { $CARGO_FLAGS = @("--release") }

if ($IS_HOST_BUILD) {
    # Windows host: invoke cargo directly so artifacts land in target/$BUILD_MODE/
    & cargo build @CARGO_FLAGS
    & cargo build @CARGO_FLAGS -p ssruntime --features libc_alloc,panic-handler
} else {
    # Cross-compilation: dispatch to SDK release scripts
    if ($PLATFORM -eq "windows") {
        $script = "./scripts/release-windows.ps1"
        if ($ARCH -eq "arm64") { $script = "./scripts/release-windows-arm64.ps1" }
        echo "Executing $script $BUILD_MODE..."
        & $script $BUILD_MODE
    } elseif ($PLATFORM -eq "ios" -and $IOS_SIMULATOR -eq "yes") {
        & sh ./scripts/release-ios-sim.sh $BUILD_MODE
    } elseif ($PLATFORM -eq "web") {
        & sh ./scripts/release-emscripten.sh $BUILD_MODE
    } else {
        & sh "./scripts/release-$PLATFORM.sh" $BUILD_MODE
    }
}
popd

# 1. Collect C# Bindings
echo "Copying C# bindings..."
Copy-Item "$SDK_DIR/libs/ssruntime/target/SSRuntimeMethods.g.cs" "$rootDirectory/package/Runtime/Bindings/" -Force
$editorBindingsDir = "$rootDirectory/package/Editor/Bindings"
New-Item -ItemType Directory -Path $editorBindingsDir -Force | Out-Null
if (Test-Path "$SDK_DIR/libs/ssconverter/target/SSConverterMethods.g.cs") {
    Copy-Item "$SDK_DIR/libs/ssconverter/target/SSConverterMethods.g.cs" "$editorBindingsDir/" -Force
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

$LIB_EXT = ".so"
$LIB_PREFIX = "lib"
if ($PLATFORM -eq "windows") { 
    $LIB_EXT = ".dll"
    $LIB_PREFIX = ""
} elseif ($PLATFORM -eq "macos") {
    $LIB_EXT = ".dylib"
} elseif ($PLATFORM -eq "ios" -or $PLATFORM -eq "web") {
    $LIB_EXT = ".a"
}

# Determine source directory
if ($IS_HOST_BUILD) {
    $SRC_DIR = "$SDK_DIR/target/$BUILD_MODE"
} else {
    # Architecture mapping for cross-compilation
    $TARGET_TRIPLE = ""
    if ($PLATFORM -eq "android") {
        if ($ARCH -eq "arm64" -or $ARCH -eq "arm64-v8a") { $TARGET_TRIPLE = "aarch64-linux-android" }
        elseif ($ARCH -eq "arm32" -or $ARCH -eq "armeabi-v7a") { $TARGET_TRIPLE = "armv7-linux-androideabi" }
        elseif ($ARCH -eq "x86_64") { $TARGET_TRIPLE = "x86_64-linux-android" }
    } elseif ($PLATFORM -eq "web") {
        $TARGET_TRIPLE = "wasm32-unknown-unknown"
    } elseif ($PLATFORM -eq "ios") {
        if ($IOS_SIMULATOR -eq "yes") {
            $TARGET_TRIPLE = "universal-apple-ios-sim"
        } else {
            $TARGET_TRIPLE = "universal-apple-ios"
        }
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

if (Test-Path "$SRC_DIR/$($LIB_PREFIX)ssruntime$LIB_EXT") {
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
