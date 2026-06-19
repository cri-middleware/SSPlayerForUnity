#!/usr/bin/env bash
set -e

# Change to the root directory of the repository
cd "$(dirname "$0")/.."

PACKAGE_DIR="package"

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
    TAR_NAME="com.spritestudio.ssruntime-release.tgz"
    tar -czf "${TAR_NAME}" "${PACKAGE_DIR}/"
    echo "Created ${TAR_NAME}"
fi

echo "Packing complete!"
