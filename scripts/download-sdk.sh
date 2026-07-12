#!/bin/bash
set -e
BASEDIR=$(dirname "$0")
ROOTDIR=$(cd "$BASEDIR/.." && pwd -P)
PKG="${ROOTDIR}/package"
RUNTIME_PLUGINS="${PKG}/Runtime/Plugins"
EDITOR_PLUGINS="${PKG}/Editor/Plugins"
RUNTIME_BINDINGS="${PKG}/Runtime/Bindings"
EDITOR_BINDINGS="${PKG}/Editor/Bindings"
VERSION_FILE="${ROOTDIR}/SDK_VERSION.txt"
# Version stamps are written to both plugin dirs; the runtime one gates the skip.
STAMP="${RUNTIME_PLUGINS}/VERSION"

TARGET_VERSION=$(tr -d ' \r\n' < "$VERSION_FILE")

if [ -f "$STAMP" ] && [ "$(tr -d ' \r\n' < "$STAMP")" = "$TARGET_VERSION" ]; then
    echo "SDK $TARGET_VERSION is already up to date. Skipping download."
    exit 0
fi

BASE_URL="https://github.com/cri-middleware/SpriteStudio-SDK/releases/download/${TARGET_VERSION}"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# The SDK release publishes two zips. Each unzips to output/<kind>/runtime/,
# holding libs/<platform>/ plus include/, VERSION and the license notices.
#   static  : .a / .lib / static xcframework
#   dynamic : .dylib / .so / .dll / dynamic xcframework
echo "Target SDK Version: ${TARGET_VERSION}"
for kind in static dynamic; do
    url="${BASE_URL}/spritestudio7-sdk-${kind}-libs.zip"
    echo "Downloading ${url}"
    curl -fL -o "${TMP}/${kind}.zip" "$url"
    unzip -q -o "${TMP}/${kind}.zip" -d "${TMP}/${kind}"
done

STATIC="${TMP}/static/output/static/runtime"
DYNAMIC="${TMP}/dynamic/output/dynamic/runtime"

# copy <src> <dst-dir>: fail fast if the source is missing so a change in the
# SDK zip layout surfaces here instead of shipping an incomplete package.
# Replaces only the binary itself (removing any stale same-named file/dir first
# so an .xcframework directory doesn't nest); the committed sibling .meta that
# carries the Unity PluginImporter platform settings is left untouched.
copy() {
    local src="$1" dstdir="$2"
    if [ ! -e "$src" ]; then
        echo "ERROR: expected SDK file not found: $src" >&2
        exit 1
    fi
    mkdir -p "$dstdir"
    rm -rf "${dstdir}/$(basename "$src")"
    cp -R "$src" "$dstdir/"
}

echo "Placing ssruntime into Runtime/Plugins (iOS/WebGL static, others dynamic)..."
copy "${DYNAMIC}/libs/macos/ssruntime.bundle"            "${RUNTIME_PLUGINS}/macOS"
copy "${DYNAMIC}/libs/windows/x86_64/ssruntime.dll"          "${RUNTIME_PLUGINS}/Windows/x86_64"
copy "${DYNAMIC}/libs/linux/x86_64/libssruntime.so"          "${RUNTIME_PLUGINS}/Linux/x86_64"
copy "${DYNAMIC}/libs/android/arm64-v8a/libssruntime.so"     "${RUNTIME_PLUGINS}/Android/arm64-v8a"
copy "${DYNAMIC}/libs/android/armeabi-v7a/libssruntime.so"   "${RUNTIME_PLUGINS}/Android/armeabi-v7a"
copy "${DYNAMIC}/libs/android/x86_64/libssruntime.so"        "${RUNTIME_PLUGINS}/Android/x86_64"
copy "${STATIC}/libs/ios/libssruntime.xcframework"           "${RUNTIME_PLUGINS}/iOS"
copy "${STATIC}/libs/web/libssruntime.a"                     "${RUNTIME_PLUGINS}/WebGL"

echo "Placing ssconverter into Editor/Plugins (win/mac, dynamic only)..."
copy "${DYNAMIC}/libs/macos/libssconverter.dylib"            "${EDITOR_PLUGINS}/macOS"
copy "${DYNAMIC}/libs/windows/x86_64/ssconverter.dll"        "${EDITOR_PLUGINS}/Windows/x86_64"

# csbindgen C# FFI bindings (platform-independent). These .g.cs are gitignored
# and are what lets the package compile against the native libs, so a no-build
# release path must place them here (build-runtime.sh does the same locally).
echo "Placing csbindgen FFI bindings into Runtime/Editor Bindings..."
copy "${DYNAMIC}/bindings/SSRuntimeMethods.g.cs"             "${RUNTIME_BINDINGS}"
copy "${DYNAMIC}/bindings/SSConverterMethods.g.cs"           "${EDITOR_BINDINGS}"

# License / third-party notices ride alongside each plugin set.
copy "${DYNAMIC}/LICENSE.md"                             "$RUNTIME_PLUGINS"
copy "${DYNAMIC}/THIRD-PARTY-LICENSES.ssruntime.md"      "$RUNTIME_PLUGINS"
copy "${DYNAMIC}/LICENSE.md"                             "$EDITOR_PLUGINS"
copy "${DYNAMIC}/THIRD-PARTY-LICENSES.ssconverter.md"    "$EDITOR_PLUGINS"

echo "$TARGET_VERSION" > "${RUNTIME_PLUGINS}/VERSION"
echo "$TARGET_VERSION" > "${EDITOR_PLUGINS}/VERSION"

echo "Done."
