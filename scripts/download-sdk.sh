#!/bin/bash
set -e
BASEDIR=$(dirname "$0")
SCRIPTDIR=$(cd "$BASEDIR" && pwd -P)
ROOTDIR=$(cd "$BASEDIR/.." && pwd -P)
PKG="${ROOTDIR}/package"
RUNTIME_PLUGINS="${PKG}/Runtime/Plugins"
EDITOR_PLUGINS="${PKG}/Editor/Plugins"
RUNTIME_BINDINGS="${PKG}/Runtime/Bindings"
EDITOR_BINDINGS="${PKG}/Editor/Bindings"
VERSION_FILE="${SCRIPTDIR}/SDK_VERSION.txt"
# Version stamps are written to both plugin dirs; the runtime one gates the skip.
STAMP="${RUNTIME_PLUGINS}/VERSION"
# Downloaded zips are cached here (gitignored) so a re-run reuses them instead of
# re-downloading; extraction still uses a throwaway temp.
CACHE_DIR="${SCRIPTDIR}/.sdk-cache"

TARGET_VERSION=$(tr -d ' \r\n' < "$VERSION_FILE")

if [ -f "$STAMP" ] && [ "$(tr -d ' \r\n' < "$STAMP")" = "$TARGET_VERSION" ]; then
    echo "SDK $TARGET_VERSION is already up to date. Skipping download."
    exit 0
fi

BASE_URL="https://github.com/cri-middleware/SpriteStudio-SDK/releases/download/${TARGET_VERSION}"
# Persistent cache for the downloaded zips (kept across runs); extraction uses a
# throwaway temp removed on exit.
mkdir -p "$CACHE_DIR"
SUMS_FILE="${CACHE_DIR}/SHA256SUMS"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

# The SDK release publishes two flat zips: libs/<platform>/ plus include/,
# bindings/, VERSION and the license notices at the archive root.
#   static  : .a / .lib / static xcframework
#   dynamic : .dylib / .so / .dll / dynamic xcframework
echo "Target SDK Version: ${TARGET_VERSION}"
# Fetch the checksum manifest (small): it both verifies integrity and decides
# whether an already-cached zip can be reused without re-downloading.
echo "Downloading checksum manifest: ${BASE_URL}/SHA256SUMS"
curl -fL -o "$SUMS_FILE" "${BASE_URL}/SHA256SUMS"
for kind in static dynamic; do
    asset="spritestudio-sdk-${kind}-libs.zip"
    # Keep the release asset's own name so it matches the SHA256SUMS entry.
    zip="${CACHE_DIR}/${asset}"
    expected=$(awk -v n="$asset" '$2 == n {print $1}' "$SUMS_FILE")
    if [ -z "$expected" ]; then
        echo "ERROR: no checksum entry for ${asset} in SHA256SUMS" >&2
        exit 1
    fi
    # Reuse the cached zip when it already matches the manifest; else download.
    if [ -f "$zip" ] && [ "$(sha256_of "$zip")" = "$expected" ]; then
        echo "Reusing cached ${asset}"
    else
        echo "Downloading ${BASE_URL}/${asset}"
        curl -fL -o "$zip" "${BASE_URL}/${asset}"
        if [ "$(sha256_of "$zip")" != "$expected" ]; then
            echo "ERROR: checksum mismatch for ${asset}" >&2
            rm -f "$zip"
            exit 1
        fi
    fi
    echo "Checksum OK: ${asset}"
    unzip -q -o "$zip" -d "${TMP}/${kind}"
done

# Flat archives: libs/, include/, bindings/, VERSION sit at the extraction root.
STATIC="${TMP}/static"
DYNAMIC="${TMP}/dynamic"

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
