#!/usr/bin/env zsh
set -e

# Regenerates the sample assets that the example projects commit.
#
# Two projects carry Ringo: examples/SampleProject, and examples/Override_Ringo, whose demo drives
# the part overrides against that same asset. Both get the identical conversion, so a Ringo change
# cannot land in one and go stale in the other.
#
# Only Ringo is committed - it is what makes the projects show an animation the moment they open,
# without the reader bringing a .sspj. The other four SDK test projects (overall, allAttributeV7,
# allPartsV7, ParticleEffect) are 8 MB of binaries the submodule already carries, so they stay out;
# drag them onto Window > SpriteStudio > Importer when you need them.
#
# Run this at release time, or whenever SpriteStudio-SDK/tests/Ringo changes: the committed .ssab
# is a build output and goes stale otherwise. It writes only the payloads. The .mat and every .meta
# are Unity's - they stay where they are, which is what keeps each sample scene's GUID references
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
OUT_DIRS=(
    ${ROOTDIR}/examples/SampleProject/Assets/ssab_generated/Ringo
    ${ROOTDIR}/examples/Override_Ringo/Assets/ssab_generated/Ringo
)

if [ ! -f "${SSPJ}" ]; then
    echo "ERROR: ${SSPJ} not found. Run 'git submodule update --init --recursive' first." >&2
    exit 1
fi

# ssconverter-cli's -o is single-mode only, so one .sspj per invocation. Output lands flat in
# -o, which is the layout the Unity importer produces for <baseDir>/<sspj stem>/.
pushd "${SDK_DIR}" > /dev/null
for OUT_DIR in ${OUT_DIRS[@]}; do
    echo "Converting Ringo into ${OUT_DIR} ..."
    mkdir -p "${OUT_DIR}"
    cargo run --release -p ssconverter-cli -- -o "${OUT_DIR}" "${SSPJ}"
done
popd > /dev/null

echo "Done. Open each example project in Unity to let it reimport, then commit the changed .ssab."
