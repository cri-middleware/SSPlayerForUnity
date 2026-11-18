#!/bin/bash -e

HELP_APP="$(basename "$0")"
usage () {
    cat <<USAGE
Usage: ${HELP_APP} [--help]
  flatc --csharp over the SDK's .fbs schemas, into package/Runtime/Format/. Needs flatc.
  The csbindgen FFI bindings are NOT this script's output: the SDK's own build emits
  them and build-runtime / download-sdk place them.
USAGE
}

for arg in "$@"; do
  case "$arg" in
    -h|--help|help) usage; exit 0 ;;
    *) echo "${HELP_APP}: unknown argument '${arg}'" >&2; usage >&2; exit 2 ;;
  esac
done

BASEDIR=$(dirname $0)
BASEDIR=$(cd $BASEDIR && pwd -P)
ROOTDIR=${BASEDIR}/..
ROOTDIR=$(cd $ROOTDIR && pwd -P)

FLATC=flatc
OUTPUT_DIR=${ROOTDIR}/package/Runtime/Format

/bin/mkdir -p ${OUTPUT_DIR}

# Generate C# from FlatBuffers
for f in ${ROOTDIR}/SpriteStudio-SDK/libs/ssruntime/fbs/*.fbs; do
    ${FLATC} --csharp --gen-onefile -o ${OUTPUT_DIR} $f
done

for f in ${ROOTDIR}/SpriteStudio-SDK/libs/ssab/fbs/*.fbs; do
    ${FLATC} --csharp --gen-onefile -o ${OUTPUT_DIR} $f
done
