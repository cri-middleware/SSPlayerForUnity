#!/bin/bash -e

BASEDIR=$(dirname $0)
BASEDIR=$(cd $BASEDIR && pwd -P)
ROOTDIR=${BASEDIR}/..
ROOTDIR=$(cd $ROOTDIR && pwd -P)

FLATC=flatc
OUTPUT_DIR=${ROOTDIR}/package/Runtime/Format

/bin/mkdir -p ${OUTPUT_DIR}

# Generate C# from FlatBuffers
for f in ${ROOTDIR}/SpriteStudio7-SDK/libs/ssruntime/fbs/*.fbs; do
    ${FLATC} --csharp --gen-onefile -o ${OUTPUT_DIR} $f
done

for f in ${ROOTDIR}/SpriteStudio7-SDK/libs/ssab/fbs/*.fbs; do
    ${FLATC} --csharp --gen-onefile -o ${OUTPUT_DIR} $f
done
