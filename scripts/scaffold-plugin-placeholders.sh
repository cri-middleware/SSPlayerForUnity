#!/usr/bin/env bash
set -e

# Maintainer tool for generating the native-plugin .meta.
#
# Unity's PluginImporter settings (which platform a lib is enabled for, and its CPU)
# do not depend on the contents of the binary - only on the file existing at the path.
# CI has no Unity to generate those .meta, and a tarball that ships binaries without
# them is broken: a UPM package is extracted into an immutable folder, where Unity
# ignores any asset that has no .meta. So the .meta have to be committed.
#
# That means every plugin path needs a file present when Unity imports the package -
# including targets this machine cannot build (Windows needs MSVC). This script drops
# placeholders at exactly the paths download-sdk.sh targets, so the .meta can all be
# generated in one pass. Workflow:
#
#   1. ./scripts/scaffold-plugin-placeholders.sh
#   2. Open examples/UnityTestProject in Unity, then run
#      SpriteStudio > Dev > Configure Native Plugin Importers
#   3. ./scripts/scaffold-plugin-placeholders.sh --clean
#   4. git add the .meta (the binaries stay gitignored) and commit
#
# download-sdk.sh replaces only the binary and leaves the committed .meta untouched,
# so the settings still apply once the real libs arrive.
#
# The placeholders are NOT valid libraries. Each carries its format's magic bytes so
# Unity classifies it as a native plugin rather than a managed assembly, plus a marker
# string. --clean only removes files carrying that marker, so it can never delete a
# real binary.

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RUNTIME_PLUGINS="${ROOT}/package/Runtime/Plugins"
EDITOR_PLUGINS="${ROOT}/package/Editor/Plugins"

MARKER="SSPLAYER_PLACEHOLDER not a real binary - see scripts/scaffold-plugin-placeholders.sh"

# <path>|<format>, mirroring the copy list in download-sdk.sh. macOS is omitted on
# purpose: those two libs build here, so their .meta come from the real artifacts.
PLACEHOLDERS=(
    "${RUNTIME_PLUGINS}/Windows/x86_64/ssruntime.dll|pe"
    "${RUNTIME_PLUGINS}/Linux/x86_64/libssruntime.so|elf"
    "${RUNTIME_PLUGINS}/Android/arm64-v8a/libssruntime.so|elf"
    "${RUNTIME_PLUGINS}/Android/armeabi-v7a/libssruntime.so|elf"
    "${RUNTIME_PLUGINS}/Android/x86_64/libssruntime.so|elf"
    "${RUNTIME_PLUGINS}/WebGL/libssruntime.a|ar"
    "${EDITOR_PLUGINS}/Windows/x86_64/ssconverter.dll|pe"
)

# The iOS lib is an .xcframework - a directory. xcodebuild -create-xcframework emits a
# device slice plus a fused simulator slice (see the SDK's release-ios-xcframework.sh),
# so the placeholder mirrors that shape rather than being an empty folder.
XCFRAMEWORK="${RUNTIME_PLUGINS}/iOS/libssruntime.xcframework"
XCF_SLICES=("ios-arm64" "ios-arm64_x86_64-simulator")

write_placeholder() {
    local path="$1" format="$2"
    mkdir -p "$(dirname "$path")"
    case "$format" in
        pe)  printf 'MZ' > "$path" ;;          # DOS/PE stub header
        elf) printf '\177ELF' > "$path" ;;     # ELF magic
        ar)  printf '!<arch>\n' > "$path" ;;   # static archive magic
        *)   echo "ERROR: unknown placeholder format: $format" >&2; exit 1 ;;
    esac
    printf '\n%s\n' "$MARKER" >> "$path"
}

is_placeholder() {
    [ -f "$1" ] && grep -q "SSPLAYER_PLACEHOLDER" "$1" 2>/dev/null
}

if [ "${1:-}" = "--clean" ]; then
    removed=0 kept=0
    for entry in "${PLACEHOLDERS[@]}"; do
        path="${entry%%|*}"
        if is_placeholder "$path"; then
            rm -f "$path"
            removed=$((removed + 1))
        elif [ -e "$path" ]; then
            echo "  keeping (not a placeholder): ${path#${ROOT}/}"
            kept=$((kept + 1))
        fi
    done
    if is_placeholder "${XCFRAMEWORK}/PLACEHOLDER"; then
        rm -rf "$XCFRAMEWORK"
        removed=$((removed + 1))
    elif [ -e "$XCFRAMEWORK" ]; then
        echo "  keeping (not a placeholder): ${XCFRAMEWORK#${ROOT}/}"
        kept=$((kept + 1))
    fi
    echo "Removed ${removed} placeholder(s), kept ${kept} real file(s)."
    echo "The .meta are left in place - 'git status' should now show them as the only additions."
    exit 0
fi

if [ "${1:-}" != "" ]; then
    echo "usage: $(basename "$0") [--clean]" >&2
    exit 1
fi

for entry in "${PLACEHOLDERS[@]}"; do
    path="${entry%%|*}"
    format="${entry#*|}"
    if [ -e "$path" ] && ! is_placeholder "$path"; then
        echo "  skipping (real binary present): ${path#${ROOT}/}"
        continue
    fi
    write_placeholder "$path" "$format"
    echo "  placeholder: ${path#${ROOT}/}"
done

if [ -e "$XCFRAMEWORK" ] && ! is_placeholder "${XCFRAMEWORK}/PLACEHOLDER"; then
    echo "  skipping (real xcframework present): ${XCFRAMEWORK#${ROOT}/}"
else
    rm -rf "$XCFRAMEWORK"
    mkdir -p "$XCFRAMEWORK"
    printf '%s\n' "$MARKER" > "${XCFRAMEWORK}/PLACEHOLDER"
    {
        echo '<?xml version="1.0" encoding="UTF-8"?>'
        echo '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">'
        echo '<plist version="1.0">'
        echo '<dict>'
        echo '  <key>AvailableLibraries</key>'
        echo '  <array>'
        echo '    <dict>'
        echo '      <key>BinaryPath</key><string>libssruntime.a</string>'
        echo '      <key>LibraryIdentifier</key><string>ios-arm64</string>'
        echo '      <key>LibraryPath</key><string>libssruntime.a</string>'
        echo '      <key>SupportedArchitectures</key><array><string>arm64</string></array>'
        echo '      <key>SupportedPlatform</key><string>ios</string>'
        echo '    </dict>'
        echo '    <dict>'
        echo '      <key>BinaryPath</key><string>libssruntime.a</string>'
        echo '      <key>LibraryIdentifier</key><string>ios-arm64_x86_64-simulator</string>'
        echo '      <key>LibraryPath</key><string>libssruntime.a</string>'
        echo '      <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>'
        echo '      <key>SupportedPlatform</key><string>ios</string>'
        echo '      <key>SupportedPlatformVariant</key><string>simulator</string>'
        echo '    </dict>'
        echo '  </array>'
        echo '  <key>CFBundlePackageType</key><string>XFWK</string>'
        echo '  <key>XCFrameworkFormatVersion</key><string>1.0</string>'
        echo '</dict>'
        echo '</plist>'
    } > "${XCFRAMEWORK}/Info.plist"
    for slice in "${XCF_SLICES[@]}"; do
        mkdir -p "${XCFRAMEWORK}/${slice}/Headers"
        write_placeholder "${XCFRAMEWORK}/${slice}/libssruntime.a" ar
    done
    echo "  placeholder: ${XCFRAMEWORK#${ROOT}/} (${#XCF_SLICES[@]} slices)"
fi

cat <<'EOF'

Placeholders written. Next:
  1. Open examples/UnityTestProject in Unity (the package is a local `file:` dependency,
     so Unity imports these paths and generates a .meta for each).
  2. Run SpriteStudio > Dev > Configure Native Plugin Importers and check the console
     for any path reported as missing.
  3. Re-run this script with --clean, then commit the .meta.
EOF
