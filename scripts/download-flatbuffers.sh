#!/bin/bash -e

BASEDIR=$(dirname $0)
BASEDIR=$(cd $BASEDIR && pwd -P)
ROOTDIR=${BASEDIR}/..
ROOTDIR=$(cd $ROOTDIR && pwd -P)

VERSION="25.12.19"
ZIP_URL="https://github.com/google/flatbuffers/archive/refs/tags/v${VERSION}.zip"
TEMP_DIR="${ROOTDIR}/scripts/.temp_flatbuffers"
OUTPUT_DIR="${ROOTDIR}/package/Runtime/FlatBuffers"

echo "Downloading FlatBuffers v${VERSION}..."
/bin/mkdir -p "${TEMP_DIR}"
curl -L -o "${TEMP_DIR}/flatbuffers.zip" "${ZIP_URL}"

echo "Extracting C# runtime..."
unzip -o -q "${TEMP_DIR}/flatbuffers.zip" -d "${TEMP_DIR}"

/bin/mkdir -p "${OUTPUT_DIR}"
# Clean up: remove any stale files
rm -rf "${OUTPUT_DIR:?}"/*

# Copy only the C# source into the package
cp -r "${TEMP_DIR}/flatbuffers-${VERSION}/net/FlatBuffers/"*.cs "${OUTPUT_DIR}/"

echo "Cleaning up..."
rm -rf "${TEMP_DIR}"

echo "FlatBuffers C# source code has been placed in ${OUTPUT_DIR}"
