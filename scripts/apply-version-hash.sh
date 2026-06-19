#!/usr/bin/env bash
set -e

# Change to the root directory of the repository
cd "$(dirname "$0")/.."

PACKAGE_JSON="package/package.json"

# 1. Get the current git hash
if ! command -v git > /dev/null; then
    echo "Warning: git is not installed. Skipping version hash injection."
    exit 0
fi

# Check if inside a git repository
if ! git rev-parse --is-inside-work-tree > /dev/null 2>&1; then
    echo "Warning: Not inside a git repository. Skipping version hash injection."
    exit 0
fi

GIT_HASH=$(git rev-parse --short HEAD)
echo "Current git hash: ${GIT_HASH}"

# 2. Rewrite the version in package.json to append the hash
# Using awk or a safe sed replacement to avoid macOS/Linux -i differences
sed -E "s/\"version\":[[:space:]]*\"([^+]+)[^\"]*\"/\"version\": \"\1+${GIT_HASH}\"/" "${PACKAGE_JSON}" > "${PACKAGE_JSON}.tmp"
mv "${PACKAGE_JSON}.tmp" "${PACKAGE_JSON}"

echo "Appended +${GIT_HASH} to package.json version."
