#!/usr/bin/env bash
#
# Build the release: the UPM tarball a consumer installs, its stable alias name,
# and the checksums over both -- checked before packing and again afterwards.
#
#   scripts/build-release.sh                              # the whole thing
#   scripts/build-release.sh sdk=build                    # compile the libs instead
#   scripts/build-release.sh out=/tmp/rel                 # somewhere else
#
# release.yml's job runs this script for the assets it uploads, so CI and a
# local run cannot drift. The workflow keeps only what is GitHub's -- the
# checkout, Node, the artifact upload and the Release.
#
# Unlike the SDK's script of the same name, this one really does build the whole
# release: the package is C# plus prebuilt native libraries, so one machine can
# produce the exact tarball that ships. That is the point of it. Run it, install
# the tarball it writes into a Unity project, and you have seen the release.
#
# There are two checks and they catch different things.
#
#   Before packing: every plugin directory has native libraries, every one of
#   them has a committed .meta, and both FFI bindings and their .meta are
#   present. A UPM package is extracted into an immutable folder, so Unity
#   ignores any asset without a .meta -- a lib whose .meta was never committed
#   installs as nothing at all, silently, and CI has no Unity to generate one.
#
#   After packing: the tarball actually contains them. `npm pack` applies the
#   "files" allowlist in package.json, which is a second place a file can be
#   dropped and the first check cannot see -- the tree was right, the archive is
#   not.
#
# What this does NOT do is bump the version or tag. Those are decisions, and a
# script that made them would have to guess which.
set -euo pipefail

APP="$(basename "$0")"
ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
PKG_DIR="$ROOT_DIR/package"

# --- options --------------------------------------------------------------

OUT="release-dist"
SDK="download"
VERIFY="yes"
CLEAN="yes"

usage () {
  echo "Usage: $APP [options]"
  echo "$APP options:"
  echo "  out=<dir>               Where the tarball, its alias and SHA256SUMS land"
  echo "                          (default: $OUT). Relative paths resolve against the"
  echo "                          current directory."
  echo "  sdk=<download|build>    Where the native libraries come from (default: $SDK)."
  echo "                          download = download-sdk.sh, the pre-built release for"
  echo "                          scripts/SDK_VERSION.txt -- every platform, and so the"
  echo "                          only source a shippable package can have. build ="
  echo "                          build-runtime.sh, compiled from the Rust source in the"
  echo "                          submodule (release mode; needs rustup) -- what to use"
  echo "                          when the SDK release the version file names does not"
  echo "                          exist yet, at the cost of ONLY THIS HOST'S PLATFORM."
  echo "  verify=<yes|no>         Run both checks (default: $VERIFY). no packs whatever"
  echo "                          is there, which is not a thing to ship."
  echo "  clean=<yes|no>          Delete <out> before building (default: $CLEAN)."
  echo "  -h | --help             Show this help"
  echo ""
  echo "Output: <out>/ holds <name>-<version>.tgz, the version-less <name>.tgz alias"
  echo "the README and docs link to via releases/latest/download/, and SHA256SUMS over"
  echo "both -- the exact set release.yml attaches to the draft Release."
  echo ""
  echo "There is no option for which release this is: the tag comes from the checkout"
  echo "this runs in (the ref the workflow was dispatched from, or 'git checkout <tag>'"
  echo "locally). The version inside the tarball is package/package.json's, always,"
  echo "because that is the one npm packs -- a disagreement between the two is"
  echo "reported, not resolved."
}

while [ $# -gt 0 ]; do
  item="$1"
  shift

  case "$item" in
    -h|--help|help)
      usage
      exit 0
      ;;
    *=*)
      key="${item%%=*}"
      value="${item#*=}"
      case "$key" in
        out)    OUT="$value" ;;
        # Lower-cased where the option is a keyword; out= is a path.
        sdk)    SDK="$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]')" ;;
        verify) VERIFY="$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]')" ;;
        clean)  CLEAN="$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]')" ;;
        *)
          echo "$APP: unknown option '$key' (see $APP --help)" >&2
          exit 2
          ;;
      esac
      ;;
    *)
      echo "$APP: unknown argument '$item' (options are key=value; see $APP --help)" >&2
      exit 2
      ;;
  esac
done

case "$VERIFY" in yes|no) ;; *) echo "$APP: verify must be yes or no (got '$VERIFY')" >&2; exit 2 ;; esac
case "$CLEAN" in yes|no) ;; *) echo "$APP: clean must be yes or no (got '$CLEAN')" >&2; exit 2 ;; esac
case "$SDK" in
  download|build) ;;
  *) echo "$APP: sdk must be download or build (got '$SDK')" >&2; exit 2 ;;
esac

# Resolve against the invoking directory, before anything cds away from it.
case "$OUT" in /*) ;; *) OUT="$PWD/$OUT" ;; esac

# clean=yes deletes this path, so refuse the ones that would take the repository
# (or the filesystem) with them.
if [ "$OUT" = "/" ] || [ "$OUT" = "$ROOT_DIR" ] || [ "$OUT" = "$PKG_DIR" ]; then
  echo "$APP: refusing to use '$OUT' as out= (it is deleted when clean=yes)" >&2
  exit 2
fi

cd "$ROOT_DIR"

# --- what this release is called ------------------------------------------
#
# A release is a tag: one is pushed, and the workflow is then dispatched from
# it. So the ref this is running on already IS the answer, and reading it here
# is reading the decision, not making it. GITHUB_REF_NAME is the authoritative
# form when there is one; `git describe` is the same question asked locally.
#
# Neither is an error when absent: a run off a branch is a package build, not a
# release (the workflow refuses to create a Release from one).
NAME="$(node -p "require('./package/package.json').name")"
VERSION="$(./scripts/release-version.sh)"
MANIFEST_TAG="v${VERSION}"

if [ "${GITHUB_REF_TYPE:-}" = "tag" ] && [ -n "${GITHUB_REF_NAME:-}" ]; then
  TAG="$GITHUB_REF_NAME"
  TAG_SOURCE="GITHUB_REF_NAME -- the ref this release was dispatched from"
elif TAG="$(git -C "$ROOT_DIR" describe --exact-match --match 'v[0-9]*' --tags HEAD 2>/dev/null)"; then
  TAG_SOURCE="the release tag HEAD is checked out at"
else
  TAG="$MANIFEST_TAG"
  TAG_SOURCE="package.json -- HEAD is not on a release tag, so this is a package build"
fi

VERSIONED="${NAME}-${VERSION}.tgz"
# The version-less alias is not a nicety: README and the docs install the package
# from releases/latest/download/<name>.tgz, a URL that must not change when the
# version does. It is the same bytes under a stable name.
ALIAS="${NAME}.tgz"

echo "options"
echo "  out      => $OUT"
echo "  sdk      => $SDK"
echo "  verify   => $VERIFY"
echo "  clean    => $CLEAN"
echo "  package  => $NAME $VERSION"
echo "  tag      => $TAG"
echo "              ($TAG_SOURCE)"

if [ "$TAG" != "$MANIFEST_TAG" ]; then
  echo ""
  echo "$APP: NOTE: the tag says $TAG, package.json says $MANIFEST_TAG."
  echo "$APP: the tarball is $VERSIONED either way -- npm packs package.json's version."
  echo "$APP: bump it to match if this tag is the release, or the Release will carry a"
  echo "$APP: tarball that does not name it."
fi

# --- preflight ------------------------------------------------------------

if [ ! -f "$PKG_DIR/package.json" ]; then
  echo "$APP: package/package.json not found -- is this the SSPlayerForUnity repo?" >&2
  exit 1
fi

# npm is required, not just preferred: it applies the "files" allowlist and
# produces the `package/` root layout UPM expects. pack-release.sh says the same
# and refuses to fall back to a plain tar.
if ! command -v npm > /dev/null 2>&1; then
  echo "$APP: npm not found -- it is what packs the UPM tarball ('files' allowlist + layout)" >&2
  exit 1
fi

# The runners have sha256sum (coreutils); macOS ships shasum instead. Both write
# the same `<hash>  <name>` lines and both take -c, so SHA256SUMS is identical
# either way -- and matches what download-sdk.sh checks the SDK zips with.
if command -v sha256sum > /dev/null 2>&1; then
  sha256 () { sha256sum "$@"; }
elif command -v shasum > /dev/null 2>&1; then
  sha256 () { shasum -a 256 "$@"; }
else
  echo "$APP: neither sha256sum nor shasum found -- one of them writes SHA256SUMS" >&2
  exit 1
fi

step () {
  echo ""
  echo ">> $*"
}

RUNTIME_PLUGINS="$PKG_DIR/Runtime/Plugins"
EDITOR_PLUGINS="$PKG_DIR/Editor/Plugins"
BINDINGS="$PKG_DIR/Runtime/Bindings/SSRuntimeMethods.g.cs $PKG_DIR/Editor/Bindings/SSConverterMethods.g.cs"

# --- native libraries ------------------------------------------------------
if [ "$SDK" = "download" ]; then
  step "installing the pinned SDK native libraries"
  ./scripts/download-sdk.sh
else
  # build-runtime.sh builds the host platform and nothing else, and it leaves
  # whatever other platforms are already in the plugin directories in place. The
  # checks below cannot tell the difference -- they ask that each tree has
  # libraries, not that it has all of them -- so say it here rather than let a
  # one-platform tarball look like a release.
  step "building the SDK native libraries from source (THIS HOST ONLY)"
  echo "   Other platforms are left as they were, so the tarball carries whatever"
  echo "   happens to be in the plugin directories. The check at the end lists exactly"
  echo "   what is missing. Fine for getting a package to look at, never for a release."
  ./scripts/build-runtime.sh build=release
fi

# --- check the tree --------------------------------------------------------
# Everything at once: finding out about the second missing .meta on the second
# run is not a debugging loop worth having.
FAILED=""
fail () {
  FAILED="${FAILED}
  $1"
}

# find, over both plugin trees, of every native library. A .bundle and an
# .xcframework are directories carrying a sibling .meta, so they are matched as
# directories and their contents excluded -- otherwise every file inside would
# be reported as a lib with no .meta of its own.
native_libs () {
  find "$RUNTIME_PLUGINS" "$EDITOR_PLUGINS" \
    \( -type f \( -name '*.dylib' -o -name '*.so' -o -name '*.dll' \
                  -o -name '*.a' -o -name '*.lib' \) \
       -o -type d \( -name '*.bundle' -o -name '*.xcframework' \) \) \
    -not -path '*.bundle/*' -not -path '*.xcframework/*'
}

if [ "$VERIFY" = "yes" ]; then
  step "checking the package tree"

  for dir in "$RUNTIME_PLUGINS" "$EDITOR_PLUGINS"; do
    if [ ! -d "$dir" ]; then
      fail "${dir#"$ROOT_DIR"/} does not exist"
      continue
    fi
    count="$(find "$dir" \
      \( -type f \( -name '*.dylib' -o -name '*.so' -o -name '*.dll' \
                    -o -name '*.a' -o -name '*.lib' \) \
         -o -type d \( -name '*.bundle' -o -name '*.xcframework' \) \) \
      -not -path '*.bundle/*' -not -path '*.xcframework/*' | wc -l | tr -d ' ')"
    echo "   ${dir#"$ROOT_DIR"/}: $count native lib(s)"
    [ "$count" -gt 0 ] || fail "no native libraries in ${dir#"$ROOT_DIR"/} (run download-sdk.sh or build-runtime.sh)"
  done

  # A UPM package is extracted into an immutable folder (Library/PackageCache),
  # where Unity ignores any asset that has no .meta. A lib whose .meta was never
  # committed therefore installs as nothing -- and the three same-named Android
  # libssruntime.so would collide. CI has no Unity to generate these, so this is
  # a release blocker rather than a warning.
  meta_missing=0
  while IFS= read -r lib; do
    [ -n "$lib" ] || continue
    [ -e "${lib}.meta" ] && continue
    fail "native plugin has no committed .meta: ${lib#"$ROOT_DIR"/}"
    meta_missing=$((meta_missing + 1))
  done <<EOF
$(native_libs)
EOF
  if [ "$meta_missing" -eq 0 ]; then
    echo "   every native plugin has a .meta"
  else
    fail "$meta_missing native plugin(s) missing a .meta -- see scripts/scaffold-plugin-placeholders.sh"
  fi

  # The csbindgen FFI bindings are compile-critical; a tarball without them does
  # not build for consumers. Their .meta matter just as much, and cannot be
  # committed beside them (the .g.cs are gitignored) -- download-sdk.sh places
  # the fixed-GUID copies from scripts/upm-meta.
  for f in $BINDINGS; do
    [ -f "$f" ]        || fail "missing FFI binding: ${f#"$ROOT_DIR"/}"
    [ -f "$f.meta" ]   || fail "missing .meta for FFI binding: ${f#"$ROOT_DIR"/}.meta"
  done
  [ -n "$FAILED" ] || echo "   both FFI bindings present, with their .meta"

  if [ -n "$FAILED" ]; then
    echo "" >&2
    echo "$APP: the package tree is not packable:$FAILED" >&2
    exit 1
  fi
else
  step "verify=no -- the package tree is not checked"
fi

# --- pack ------------------------------------------------------------------
# pack-release.sh copies LICENSE / CHANGELOG / Third Party Notices (and their
# committed .meta) into package/ and runs `npm pack`, leaving the tarball at the
# repo root. It is the one that knows the UPM document names, so it stays the
# thing that packs.
step "packing the UPM tarball"
./scripts/pack-release.sh

if [ ! -f "$ROOT_DIR/$VERSIONED" ]; then
  echo "$APP: pack-release.sh produced no $VERSIONED" >&2
  exit 1
fi

# --- collect ---------------------------------------------------------------
step "collecting into $OUT"

if [ "$CLEAN" = "yes" ]; then
  rm -rf "$OUT"
fi
mkdir -p "$OUT"

mv "$ROOT_DIR/$VERSIONED" "$OUT/$VERSIONED"
cp "$OUT/$VERSIONED" "$OUT/$ALIAS"
echo "   $VERSIONED"
echo "   $ALIAS  (stable releases/latest/download/ name)"

# Checksums over both assets. The package ships native binaries, so give
# consumers the same way to verify a download that download-sdk.sh uses against
# the SDK zips -- same `<sha256>  <filename>` layout, so the same `sha256sum -c`
# works here.
( cd "$OUT" && sha256 "$VERSIONED" "$ALIAS" > SHA256SUMS )
echo "   SHA256SUMS"

# --- check the tarball -----------------------------------------------------
# `npm pack` applies the "files" allowlist, which the tree check above cannot
# see through: the tree can be perfect and the archive still be missing what the
# allowlist forgot to name. Note LICENSE.md is NOT in "files" -- npm always
# includes a LICENSE*, and only its .meta has to be listed. That is subtle
# enough to be worth asserting rather than remembering.
if [ "$VERIFY" = "yes" ]; then
  step "checking $VERSIONED"

  ENTRIES="$(tar -tzf "$OUT/$VERSIONED")"
  has () { printf '%s\n' "$ENTRIES" | grep -qx "package/$1"; }
  has_any () { printf '%s\n' "$ENTRIES" | grep -q "^package/$1"; }

  # UPM requires the manifest at the archive's package/ root.
  has "package.json" || fail "$VERSIONED has no package/package.json"

  # The documents pack-release.sh copies in, each with the .meta Unity needs to
  # show it in the Package Manager's Licenses / Changelog tabs.
  for doc in "LICENSE.md" "CHANGELOG.md" "Third Party Notices.md"; do
    has "$doc"        || fail "$VERSIONED is missing $doc"
    has "$doc.meta"   || fail "$VERSIONED is missing $doc.meta"
  done

  # The bindings, and a .meta for each.
  for b in "Runtime/Bindings/SSRuntimeMethods.g.cs" "Editor/Bindings/SSConverterMethods.g.cs"; do
    has "$b"      || fail "$VERSIONED is missing $b"
    has "$b.meta" || fail "$VERSIONED is missing $b.meta"
  done

  # The native libraries survived the allowlist, in both plugin trees.
  for tree in "Runtime/Plugins" "Editor/Plugins"; do
    has_any "$tree/" || fail "$VERSIONED contains nothing under $tree/"
  done

  # And every native plugin in the archive still has its .meta beside it. The
  # allowlist names directories, so a lib and its .meta are included or excluded
  # together -- but a `!`-negated entry can split them, which installs the lib
  # as nothing.
  #
  # "Plugin" is the unit Unity imports, which for a .bundle or an .xcframework is
  # the directory: it carries one sibling .meta and the libraries inside it carry
  # none. Matching those files would demand a .meta that is not supposed to
  # exist, so they are folded into their root -- the same distinction the tree
  # check above makes with -not -path.
  plugin_units () {
    printf '%s\n' "$ENTRIES" \
      | grep -E '^package/(Runtime|Editor)/Plugins/' \
      | sed -E 's|^(.*\.(bundle\|xcframework))/.*$|\1|' \
      | grep -E '\.(dylib|so|dll|a|lib|bundle|xcframework)$' \
      | sort -u
  }

  while IFS= read -r e; do
    [ -n "$e" ] || continue
    printf '%s\n' "$ENTRIES" | grep -qx "${e}.meta" && continue
    fail "$VERSIONED has $e with no .meta beside it"
  done <<EOF
$(plugin_units)
EOF

  # The other direction, and the one that catches a platform going missing: a
  # committed .meta whose plugin is NOT in the archive. Every platform's .meta is
  # tracked in git, so they are all there whatever the native libraries did --
  # which means an absent lib leaves its .meta behind rather than removing
  # anything, and the checks above, which only ever ask "does this lib have a
  # .meta", see nothing wrong. What ships is a package that names a platform and
  # installs no library for it.
  #
  # A .meta is satisfied by a file entry OR by a directory: tar lists no
  # directory entries, so a folder's .meta (and a .bundle's, and an
  # .xcframework's) is matched by something existing underneath it.
  orphans=""
  while IFS= read -r m; do
    [ -n "$m" ] || continue
    b="${m%.meta}"
    # Escape the regex metacharacters a path can carry, so a '.' cannot match
    # some other character and hide a genuinely missing plugin.
    esc="$(printf '%s' "$b" | sed 's/[.[\*^$]/\\&/g')"
    printf '%s\n' "$ENTRIES" | grep -qx "$esc" && continue
    printf '%s\n' "$ENTRIES" | grep -q "^${esc}/" && continue
    orphans="${orphans}
    ${b#package/}"
  done <<EOF
$(printf '%s\n' "$ENTRIES" | grep -E '^package/(Runtime|Editor)/Plugins/.*\.meta$' || true)
EOF

  if [ -n "$orphans" ]; then
    if [ "$SDK" = "download" ]; then
      # The libraries came from the SDK release, so anything missing means the
      # release's layout and this package's expectations have diverged.
      fail "$VERSIONED names plugins it does not contain (each installs as nothing):$orphans"
    else
      # sdk=build compiles this host and leaves every other platform alone, so
      # this is the expected shortfall -- printed in full, because "host only"
      # is a great deal vaguer than the actual list.
      echo "   sdk=build: NOT in this tarball (not built on this host):$orphans"
    fi
  elif [ "$SDK" = "build" ]; then
    echo "   sdk=build: every platform present -- the plugin directories already held the rest"
  fi

  # The version npm actually packed, which is package.json's and is what the
  # filename claims.
  PACKED_VERSION="$(tar -xzOf "$OUT/$VERSIONED" package/package.json | node -p "JSON.parse(require('fs').readFileSync(0,'utf8')).version")"
  if [ "$PACKED_VERSION" != "$VERSION" ]; then
    fail "$VERSIONED contains version $PACKED_VERSION, expected $VERSION"
  fi

  # The alias is the same bytes, and the manifest covers both.
  ( cd "$OUT" && sha256 -c SHA256SUMS > /dev/null ) || fail "SHA256SUMS does not verify"
  if ! cmp -s "$OUT/$VERSIONED" "$OUT/$ALIAS"; then
    fail "$ALIAS is not identical to $VERSIONED"
  fi

  if [ -n "$FAILED" ]; then
    echo "" >&2
    echo "$APP: the release in $OUT is not shippable:$FAILED" >&2
    exit 1
  fi

  echo "   $(printf '%s\n' "$ENTRIES" | wc -l | tr -d ' ') entries, version $PACKED_VERSION"
  echo "   documents, bindings and both plugin trees present, every lib with its .meta"
  echo "   alias identical, SHA256SUMS verified"
  echo "   all checks passed"
else
  step "verify=no -- the tarball is not checked"
fi

# --- done ------------------------------------------------------------------
echo ""
echo "$APP: done -> $OUT  ($TAG)"
echo ""
( cd "$OUT" && ls -l ./*.tgz SHA256SUMS | sed 's|^|  |' )
echo ""
echo "$APP: this is what release.yml attaches to the draft Release for $TAG."
echo "$APP: install it into a Unity project before shipping it:"
echo "  Window > Package Manager > + > Install package from tarball..."
echo "  $OUT/$VERSIONED"
