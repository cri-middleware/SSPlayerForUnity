#!/usr/bin/env zsh
set -e

# Builds the sample assets the example projects need. Nothing it writes is committed.
#
# Two projects carry Ringo: examples/SampleProject, and examples/Override_Ringo, whose demo drives
# the part overrides against that same asset. Both get the identical conversion, so a Ringo change
# cannot land in one and go stale in the other.
#
# Run it after cloning, and again whenever SpriteStudio-SDK/tests/Ringo changes - and run it
# BEFORE opening either project in Unity. It writes only the payloads; the .mat and every .meta
# are committed and stay where they are, which is what keeps each sample scene's GUID references
# intact across a regeneration. Open the project first and Unity deletes those .meta as orphans,
# and the next conversion lands under fresh GUIDs with the scene's references empty.
#
# The other four SDK test projects (overall, allAttributeV7, allPartsV7, ParticleEffect) are not
# wired in here; drag them onto Window > SpriteStudio > Importer when you need them. That window
# is also how to get Ringo in without a Rust toolchain - this script needs the submodule and
# cargo, unlike download-sdk.sh, which takes prebuilt libraries from a release.

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

echo "Done. Open either example project in Unity; it imports what was just written on first open."
