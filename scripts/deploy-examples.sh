#!/usr/bin/env zsh
set -e

# Regenerates the sample assets that examples/SampleProject commits.
#
# Only Ringo is committed - it is what makes the project show an animation the moment it opens,
# without the reader bringing a .sspj. The other four SDK test projects (overall, allAttributeV7,
# allPartsV7, ParticleEffect) are 8 MB of binaries the submodule already carries, so they stay out;
# drag them onto Window > SpriteStudio > Importer when you need them.
#
# Run this at release time, or whenever SpriteStudio-SDK/tests/Ringo changes: the committed .ssab
# is a build output and goes stale otherwise. It writes only the payloads. The .mat and every .meta
# are Unity's - they stay where they are, which is what keeps the sample scene's GUID references
# intact across a regeneration.
#
# For SDK/Player developers: this needs the submodule and a Rust toolchain, unlike download-sdk.sh,
# which takes prebuilt libraries from a release.

BASEDIR=$(dirname $0)
BASEDIR=$(cd $BASEDIR && pwd -P)
ROOTDIR=${BASEDIR}/..
ROOTDIR=$(cd $ROOTDIR && pwd -P)

SDK_DIR=${ROOTDIR}/SpriteStudio-SDK
SSPJ=${SDK_DIR}/tests/Ringo/Ringo.sspj
OUT_DIR=${ROOTDIR}/examples/SampleProject/Assets/ssab_generated/Ringo

if [ ! -f "${SSPJ}" ]; then
    echo "ERROR: ${SSPJ} not found. Run 'git submodule update --init --recursive' first." >&2
    exit 1
fi

echo "Converting Ringo into ${OUT_DIR} ..."
mkdir -p "${OUT_DIR}"

# ssconverter-cli's -o is single-mode only, so one .sspj per invocation. Output lands flat in
# -o, which is the layout the Unity importer produces for <baseDir>/<sspj stem>/.
pushd "${SDK_DIR}" > /dev/null
cargo run --release -p ssconverter-cli -- -o "${OUT_DIR}" "${SSPJ}"
popd > /dev/null

echo "Done. Open examples/SampleProject in Unity to let it reimport, then commit the changed .ssab."
