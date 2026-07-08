#!/usr/bin/env bash
set -e

# Change to the root directory of the repository
cd "$(dirname "$0")/.."

PACKAGE_DIR="package"

# Cleanup function to ensure temporary files are removed even on error
cleanup() {
    rm -f "${PACKAGE_DIR}/LICENSE.md" "${PACKAGE_DIR}/LICENSE.md.meta"
    rm -f "${PACKAGE_DIR}/Third Party Notices.md" "${PACKAGE_DIR}/Third Party Notices.md.meta"
}
trap cleanup EXIT

# Copy license files to package dir for UPM before packing
echo "Copying license files to package directory..."
cp LICENSE.md "${PACKAGE_DIR}/LICENSE.md"
cp THIRD_PARTY_NOTICES.md "${PACKAGE_DIR}/Third Party Notices.md"

# Pack the package
# UPM packages are best packed with `npm pack` since it strictly follows .npmignore 
# and produces a tarball with the required `package/` root folder structure.
if command -v npm > /dev/null; then
    echo "Running 'npm pack'..."
    cd "${PACKAGE_DIR}"
    npm pack
    # Move the generated tarball to the repository root
    mv *.tgz ../
    cd ..
else
    echo "Warning: npm is not installed. Falling back to tar."
    echo "Creating tarball using standard tar..."
    TAR_NAME="jp.co.cri-mw.optpix.ssplayer-unity-release.tgz"
    tar -czf "${TAR_NAME}" "${PACKAGE_DIR}/"
    echo "Created ${TAR_NAME}"
fi

echo "Packing complete!"
