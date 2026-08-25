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
# The packer is npm, or Unity's upm when signing (below) -- never a plain `tar`.
# npm pack applies the "files" allowlist in package.json and produces a tarball with
# the `package/` root folder structure UPM expects; a tar fallback would honour neither
# and sweep in Tests/ and other dev-only content, so refuse to produce a tarball at all
# rather than a subtly broken one. build-release.sh checks the finished archive either
# way, which is what establishes that upm pack applied the allowlist too.
#
# --- Package signature (opt-in via env) -------------------------------------
# Unity 6.3's Package Manager checks every tarball for a signature and tells whoever
# installs an unsigned one so ("Unity cannot verify this package because it lacks a
# signature"). Signing is Unity's `upm pack`, which packs AND signs in one step -- there
# is no documented way to sign a tarball npm has already written -- so what the env gate
# switches is the packer itself, not a step after it.
#
# Signing runs only when UNITY_ORGANIZATION_ID is set, the same shape the SDK's macOS
# codesign uses: the identity in the environment is what turns it on. The UPM CLI reads
# the service account credentials itself, from
#   UPM_SERVICE_ACCOUNT_KEY_ID     -- the key ID
#   UPM_SERVICE_ACCOUNT_KEY_SECRET -- the secret key
# of an account holding the organization's "Package Manager Package Signer" role. These
# map to the GitHub secrets of the same names -- see .github/workflows/release.yml for
# the CI wiring. Install the CLI with:
#   curl -fsSL https://cdn.packages.unity.com/upm-cli/install.sh | bash
if [ -n "${UNITY_ORGANIZATION_ID:-}" ]; then
    # Once the gate is open, anything missing is an error rather than a fall back to
    # npm: a release that goes out unsigned while the run reports success is the exact
    # failure a signature is there to prevent.
    if ! command -v upm > /dev/null; then
        echo "ERROR: UNITY_ORGANIZATION_ID is set but the UPM CLI (upm) is not installed." >&2
        echo "       curl -fsSL https://cdn.packages.unity.com/upm-cli/install.sh | bash" >&2
        exit 1
    fi
    if [ -z "${UPM_SERVICE_ACCOUNT_KEY_ID:-}" ] || [ -z "${UPM_SERVICE_ACCOUNT_KEY_SECRET:-}" ]; then
        echo "ERROR: UNITY_ORGANIZATION_ID is set but UPM_SERVICE_ACCOUNT_KEY_ID /" >&2
        echo "       UPM_SERVICE_ACCOUNT_KEY_SECRET are not -- upm pack cannot sign." >&2
        exit 1
    fi

    # upm pack takes the package directory and writes where it is told, so unlike
    # npm pack it needs neither the cd nor the mv afterwards.
    echo "Running 'upm pack' (signing as organization ${UNITY_ORGANIZATION_ID})..."
    upm pack "${PACKAGE_DIR}" --organization-id "${UNITY_ORGANIZATION_ID}" --destination "${ROOT}"
else
    if ! command -v npm > /dev/null; then
        echo "ERROR: npm is required to pack the UPM package (it applies the 'files' allowlist)." >&2
        echo "       Install Node.js / npm and re-run." >&2
        exit 1
    fi

    echo "Skipping the UPM signature (UNITY_ORGANIZATION_ID not set)."
    echo "Running 'npm pack'..."
    cd "${PACKAGE_DIR}"
    npm pack
    # Move the generated tarball to the repository root
    mv ./*.tgz "${ROOT}/"
    cd "${ROOT}"
fi

echo "Packing complete!"
