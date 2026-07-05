#!/usr/bin/env zsh
set -e

BASEDIR=$(dirname $0)
BASEDIR=$(cd $BASEDIR && pwd -P)
ROOTDIR=${BASEDIR}/..
ROOTDIR=$(cd $ROOTDIR && pwd -P)

if [ "$OSTYPE" = "msys" ]; then
    HOST_PLATFORM=windows
elif [[ "$OSTYPE" = "darwin"* ]]; then
    HOST_PLATFORM=macos
else
    HOST_PLATFORM=linux
fi
HOST_ARCH=$(uname -m)
[[ "$HOST_ARCH" == "aarch64" ]] && HOST_ARCH="arm64"

typeset -A default_opts
default_opts=(
    arch "${HOST_ARCH}"
    platform "${HOST_PLATFORM}"
    build "debug"
    hostbuild "auto"
)

typeset -A opts
for k v in "${(@kv)default_opts}"; do
    opts[$k]=$v
done

APP=$(basename $0)
func usage() {
    echo "Usage: $APP [options]"
    echo "Options:"
    echo "  platform=<platform>    Target platform (windows, macos, linux, android, ios, web)"
    echo "  arch=<arch>            Target architecture (x86_64, arm64, universal, etc.)"
    echo "  build=<build>          Build mode (debug, release)"
    echo "  hostbuild=<auto|false> Set to false to force cross-compile scripts even on host"
}

while (( $# > 0 )); do
    item="$1"
    shift
    if [[ $item = *"="* ]]; then
        kv=(${(@s/=/)item})
        opts[$kv[1]]=$kv[2]:l
    elif [[ $item = *"help"* || $item == "-h" || $item == "--h" ]]; then
        usage
        exit 0
    fi
done

PLATFORM=${opts[platform]}
ARCH=${opts[arch]}
BUILD_MODE=${opts[build]}
HOSTBUILD=${opts[hostbuild]}

IS_HOST_BUILD=false
if [[ "$HOSTBUILD" != "false" && "$PLATFORM" == "$HOST_PLATFORM" && "$ARCH" == "$HOST_ARCH" && "$BUILD_MODE" == "debug" ]]; then
    IS_HOST_BUILD=true
fi

echo "Building for $PLATFORM ($ARCH) in $BUILD_MODE mode (Host Build: $IS_HOST_BUILD)..."

SDK_DIR=${ROOTDIR}/SpriteStudio-SDK
pushd ${SDK_DIR} > /dev/null

CARGO_FLAGS=""
[[ "$BUILD_MODE" == "release" ]] && CARGO_FLAGS="--release"

# 1. Build Phase
if [[ "$IS_HOST_BUILD" == "true" ]]; then
    if [[ "$PLATFORM" == "macos" ]]; then
        cargo build $CARGO_FLAGS
        cargo build $CARGO_FLAGS -p ssruntime --features libc_alloc,panic-handler
    elif [[ "$PLATFORM" == "linux" ]]; then
        ./scripts/release-linux.sh $BUILD_MODE
    elif [[ "$PLATFORM" == "windows" ]]; then
        cargo build $CARGO_FLAGS
        cargo build $CARGO_FLAGS -p ssruntime --features libc_alloc,panic-handler
    fi
    SRC_DIR="target/$BUILD_MODE"
else
    # Cross-compilation or a special build
    case "$PLATFORM" in
        macos)
            ./scripts/release-macos.sh $BUILD_MODE
            SRC_DIR="target/universal-apple-darwin/$BUILD_MODE"
            ;;
        ios)
            # iOS ships as a single XCFramework (device + simulator slices);
            # it is copied wholesale below, so SRC_DIR / copy_lib are not used.
            ./scripts/release-ios-xcframework.sh $BUILD_MODE
            SRC_DIR=""
            ;;
        android)
            ./scripts/release-android.sh $BUILD_MODE
            case "$ARCH" in
                arm64|arm64-v8a) RUST_TARGET="aarch64-linux-android" ;;
                arm32|armv7|armeabi-v7a) RUST_TARGET="armv7-linux-androideabi" ;;
                x86_64) RUST_TARGET="x86_64-linux-android" ;;
                x86_32|x86) RUST_TARGET="i686-linux-android" ;;
            esac
            SRC_DIR="target/$RUST_TARGET/$BUILD_MODE"
            ;;
        linux)
            ./scripts/release-linux.sh $BUILD_MODE
            SRC_DIR="target/$BUILD_MODE"
            ;;
        web)
            ./scripts/release-emscripten.sh $BUILD_MODE
            SRC_DIR="target/wasm32-unknown-unknown/$BUILD_MODE"
            ;;
        windows)
            if command -v pwsh &> /dev/null; then
                pwsh ./scripts/release-windows.ps1 $BUILD_MODE
            fi
            SRC_DIR="target/x86_64-pc-windows-msvc/$BUILD_MODE"
            ;;
    esac
fi

popd > /dev/null

# 2. Artifact Collection Phase
echo "Collecting artifacts from $SRC_DIR..."
RUNTIME_DIR=${ROOTDIR}/package/Runtime/Plugins
EDITOR_DIR=${ROOTDIR}/package/Editor/Plugins
mkdir -p ${RUNTIME_DIR}
mkdir -p ${EDITOR_DIR}

# C# Bindings
echo "Copying C# bindings..."
cp ${SDK_DIR}/libs/ssruntime/target/SSRuntimeMethods.g.cs ${ROOTDIR}/package/Runtime/Bindings/
mkdir -p ${ROOTDIR}/package/Editor/Bindings/
if [[ -f "${SDK_DIR}/libs/ssconverter/target/SSConverterMethods.g.cs" ]]; then
    cp ${SDK_DIR}/libs/ssconverter/target/SSConverterMethods.g.cs ${ROOTDIR}/package/Editor/Bindings/
fi

# Licenses
cp ${SDK_DIR}/LICENSE.md ${RUNTIME_DIR}/
if [[ -f "${SDK_DIR}/target/licenses/THIRD-PARTY-LICENSES.ssruntime.md" ]]; then
    cp ${SDK_DIR}/target/licenses/THIRD-PARTY-LICENSES.ssruntime.md ${RUNTIME_DIR}/
fi

if [[ "$PLATFORM" == "macos" || "$PLATFORM" == "windows" || "$PLATFORM" == "linux" ]]; then
    cp ${SDK_DIR}/LICENSE.md ${EDITOR_DIR}/
    if [[ -f "${SDK_DIR}/target/licenses/THIRD-PARTY-LICENSES.ssconverter.md" ]]; then
        cp ${SDK_DIR}/target/licenses/THIRD-PARTY-LICENSES.ssconverter.md ${EDITOR_DIR}/
    fi
fi

# Convert PLATFORM to Unity plugin folder name
case "$PLATFORM" in
    windows) UNITY_PLATFORM="Windows" ;;
    macos) UNITY_PLATFORM="macOS" ;;
    linux) UNITY_PLATFORM="Linux" ;;
    ios) UNITY_PLATFORM="iOS" ;;
    android) UNITY_PLATFORM="Android" ;;
    web) UNITY_PLATFORM="WebGL" ;;
    *) UNITY_PLATFORM="$PLATFORM" ;;
esac

# Convert ARCH to Unity plugin folder name (especially for Android)
UNITY_ARCH=$ARCH
if [[ "$PLATFORM" == "android" ]]; then
    case "$ARCH" in
        arm64) UNITY_ARCH="arm64-v8a" ;;
        arm32) UNITY_ARCH="armeabi-v7a" ;;
    esac
fi

# Libs Destination
if [[ "$PLATFORM" == "macos" || "$PLATFORM" == "ios" || "$PLATFORM" == "web" ]]; then
    RUNTIME_LIB_OUT=${RUNTIME_DIR}/${UNITY_PLATFORM}
    EDITOR_LIB_OUT=${EDITOR_DIR}/${UNITY_PLATFORM}
else
    RUNTIME_LIB_OUT=${RUNTIME_DIR}/${UNITY_PLATFORM}/${UNITY_ARCH}
    EDITOR_LIB_OUT=${EDITOR_DIR}/${UNITY_PLATFORM}/${UNITY_ARCH}
fi
mkdir -p ${RUNTIME_LIB_OUT}
if [[ "$PLATFORM" == "macos" || "$PLATFORM" == "windows" || "$PLATFORM" == "linux" ]]; then
    mkdir -p ${EDITOR_LIB_OUT}
fi

# Copy function
function copy_lib() {
    local src_name=$1
    local dest_dir=$2
    local dest_name=$3
    local full_src="${SDK_DIR}/${SRC_DIR}/${src_name}"
    if [[ -f "${full_src}" ]]; then
        cp "${full_src}" "${dest_dir}/${dest_name}"
        return 0
    fi
    return 1
}

if [[ "$PLATFORM" == "windows" ]]; then
    copy_lib "ssruntime.dll" "${RUNTIME_LIB_OUT}" "ssruntime.dll" || copy_lib "libssruntime.dll" "${RUNTIME_LIB_OUT}" "ssruntime.dll"
    copy_lib "ssconverter.dll" "${EDITOR_LIB_OUT}" "ssconverter.dll" || copy_lib "libssconverter.dll" "${EDITOR_LIB_OUT}" "ssconverter.dll"
elif [[ "$PLATFORM" == "macos" ]]; then
    copy_lib "libssruntime.dylib" "${RUNTIME_LIB_OUT}" "libssruntime.dylib"
    copy_lib "libssconverter.dylib" "${EDITOR_LIB_OUT}" "libssconverter.dylib"
elif [[ "$PLATFORM" == "linux" ]]; then
    copy_lib "libssruntime.so" "${RUNTIME_LIB_OUT}" "libssruntime.so"
    copy_lib "libssconverter.so" "${EDITOR_LIB_OUT}" "libssconverter.so"
elif [[ "$PLATFORM" == "android" ]]; then
    for rust_arch in "aarch64-linux-android:arm64-v8a" "armv7-linux-androideabi:armeabi-v7a" "x86_64-linux-android:x86_64"; do
        arr=(${(s/:/)rust_arch})
        target=${arr[1]}
        unity_arch=${arr[2]}
        src="${SDK_DIR}/target/${target}/${BUILD_MODE}/libssruntime.so"
        dest_dir="${RUNTIME_DIR}/Android/${unity_arch}"
        mkdir -p "${dest_dir}"
        if [[ -f "${src}" ]]; then
            cp "${src}" "${dest_dir}/libssruntime.so"
        fi
    done
elif [[ "$PLATFORM" == "ios" ]]; then
    # iOS is distributed as an XCFramework directory (device arm64 + simulator
    # arm64/x86_64). Unity links it statically (__Internal). Replace wholesale.
    /bin/rm -rf "${RUNTIME_LIB_OUT}/libssruntime.xcframework"
    cp -R "${SDK_DIR}/target/xcframework/static/libssruntime.xcframework" "${RUNTIME_LIB_OUT}/"
elif [[ "$PLATFORM" == "web" ]]; then
    copy_lib "libssruntime.a" "${RUNTIME_LIB_OUT}" "libssruntime.a"
fi

echo "Done."
