#!/usr/bin/env bash
#
# Print the UPM package version -- what a release of this package should be
# tagged as.
#
#   scripts/release-version.sh          # 7.0.0-beta.1
#   v$(scripts/release-version.sh)      # v7.0.0-beta.1  -- the git tag
#
# It is `version` in package/package.json, and nothing else. Deliberately NOT
# scripts/SDK_VERSION.txt: that pins which native SDK libs download-sdk.sh
# fetches, which moves on its own schedule and is not what this package is
# called.
#
# The tag is what names a release, not this -- release.yml is dispatched from a
# v* tag and takes the name from that ref. This is what the tag *should* be, so
# build-release.sh can say so when the two disagree, and so the two of them plus
# a human cutting a release by hand all get the answer from one place.
set -euo pipefail

APP="$(basename "$0")"
ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"

case "${1:-}" in
  -h|--help|help)
    echo "Usage: $APP"
    echo "Prints \"version\" from package/package.json (no leading 'v')."
    exit 0
    ;;
  "") ;;
  *)
    echo "$APP: unexpected argument '$1' (see $APP --help)" >&2
    exit 2
    ;;
esac

MANIFEST="$ROOT_DIR/package/package.json"
if [ ! -f "$MANIFEST" ]; then
  echo "$APP: $MANIFEST not found -- is this the SSPlayerForUnity repo?" >&2
  exit 1
fi

# node, not jq: package.json is npm's file and `npm pack` is what reads it to
# name the tarball, so ask the same runtime rather than a second JSON parser
# that has to be installed separately. (The workflow used jq; node is already
# required here, jq was not.)
if ! command -v node > /dev/null 2>&1; then
  echo "$APP: node not found -- it is what reads package.json (and what packs the tarball)" >&2
  exit 1
fi

VERSION="$(node -p "require('$MANIFEST').version || ''")"

if [ -z "$VERSION" ]; then
  echo "$APP: package/package.json has no \"version\"" >&2
  exit 1
fi

printf '%s\n' "$VERSION"
