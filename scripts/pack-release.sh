#!/usr/bin/env bash
set -e

# Absolute repo root: the cleanup trap can fire while the working directory is still
# inside package/ (a failed `npm pack`), so every path below has to be root-anchored.
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "${ROOT}"

PACKAGE_DIR="${ROOT}/package"
META_DIR="${ROOT}/scripts/upm-meta"

# Repo-root document -> name it takes inside the package. UPM shows LICENSE.md and
# CHANGELOG.md in the Package Manager's Licenses / Changelog tabs, and expects the
# third-party notices under this exact spelling.
DOCS=(
    "LICENSE.md:LICENSE.md"
    "THIRD_PARTY_NOTICES.md:Third Party Notices.md"
    "CHANGELOG.md:CHANGELOG.md"
)

# Cleanup function to ensure temporary files are removed even on error
cleanup() {
    for entry in "${DOCS[@]}"; do
        dst="${entry#*:}"
        rm -f "${PACKAGE_DIR}/${dst}" "${PACKAGE_DIR}/${dst}.meta"
    done
}
trap cleanup EXIT

# Copy the license / changelog documents into the package dir for UPM before packing.
# Each one also needs its sibling .meta: a UPM package is extracted into an immutable
# folder (Library/PackageCache), and Unity ignores assets there that have no .meta.
# The .meta carry fixed GUIDs and live in scripts/upm-meta (see the README there).
echo "Copying license / changelog documents to package directory..."
for entry in "${DOCS[@]}"; do
    src="${entry%%:*}"
    dst="${entry#*:}"
    cp "${ROOT}/${src}" "${PACKAGE_DIR}/${dst}"
    cp "${META_DIR}/${dst}.meta" "${PACKAGE_DIR}/${dst}.meta"
done

# Pack the package.
# npm pack is required, not just preferred: it applies the "files" allowlist in package.json
# and produces a tarball with the `package/` root folder structure UPM expects. A plain `tar`
# fallback would honour neither and sweep in Tests/ and other dev-only content, so refuse to
# produce a tarball at all rather than a subtly broken one.
if ! command -v npm > /dev/null; then
    echo "ERROR: npm is required to pack the UPM package (it applies the 'files' allowlist)." >&2
    echo "       Install Node.js / npm and re-run." >&2
    exit 1
fi

echo "Running 'npm pack'..."
cd "${PACKAGE_DIR}"
npm pack
# Move the generated tarball to the repository root
mv ./*.tgz "${ROOT}/"
cd "${ROOT}"

echo "Packing complete!"
