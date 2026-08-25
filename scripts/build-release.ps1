# Build the release: the UPM tarball a consumer installs, its stable alias name,
# and the checksums over both -- checked before packing and again afterwards.
#
#   .\scripts\build-release.ps1                              # the whole thing
#   .\scripts\build-release.ps1 sdk=build                    # compile the libs instead
#   .\scripts\build-release.ps1 out=C:\tmp\rel               # somewhere else
#
# release.yml's job runs the .sh twin for the assets it uploads, so CI and a
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
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments
)

$ErrorActionPreference = "Stop"

$app = Split-Path -Leaf $PSCommandPath
$rootDirectory = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$packageDirectory = "$rootDirectory/package"

# --- options ---------------------------------------------------------------

$out = "release-dist"
$sdk = "download"
$verify = "yes"
$clean = "yes"

function Show-Usage {
    Write-Host "Usage: $app [options]"
    Write-Host "$app options:"
    Write-Host "  out=<dir>               Where the tarball, its alias and SHA256SUMS land"
    Write-Host "                          (default: release-dist). Relative paths resolve"
    Write-Host "                          against the current directory."
    Write-Host "  sdk=<download|build>    Where the native libraries come from (default: download)."
    Write-Host "                          download = download-sdk.ps1, the pre-built release for"
    Write-Host "                          scripts/SDK_VERSION.txt -- every platform, and so the"
    Write-Host "                          only source a shippable package can have. build ="
    Write-Host "                          build-runtime.ps1, compiled from the Rust source in the"
    Write-Host "                          submodule (release mode; needs rustup) -- what to use"
    Write-Host "                          when the SDK release the version file names does not"
    Write-Host "                          exist yet, at the cost of ONLY THIS HOST'S PLATFORM."
    Write-Host "  verify=<yes|no>         Run both checks (default: yes). no packs whatever is"
    Write-Host "                          there, which is not a thing to ship."
    Write-Host "  clean=<yes|no>          Delete <out> before building (default: yes)."
    Write-Host "  -h | --help             Show this help"
    Write-Host ""
    Write-Host "Output: <out>/ holds <name>-<version>.tgz, the version-less <name>.tgz alias"
    Write-Host "the README and docs link to via releases/latest/download/, and SHA256SUMS over"
    Write-Host "both -- the exact set release.yml attaches to the draft Release."
    Write-Host ""
    Write-Host "There is no option for which release this is: the tag comes from the checkout"
    Write-Host "this runs in (the ref the workflow was dispatched from, or 'git checkout <tag>'"
    Write-Host "locally). The version inside the tarball is package/package.json's, always,"
    Write-Host "because that is the one npm packs -- a disagreement between the two is"
    Write-Host "reported, not resolved."
    Write-Host ""
    Write-Host "Signing is not an option here either, it is opt-in through the environment: set"
    Write-Host "UNITY_ORGANIZATION_ID (and UPM_SERVICE_ACCOUNT_KEY_ID / UPM_SERVICE_ACCOUNT_KEY_SECRET"
    Write-Host "for a service account holding that organization's `"Package Manager Package Signer`""
    Write-Host "role) and the tarball is packed by Unity's 'upm pack' instead of 'npm pack', so it"
    Write-Host "carries a package signature. Unset, the tarball is unsigned -- which Unity 6.3 tells"
    Write-Host "whoever installs it."
}

foreach ($argument in $Arguments) {
    switch -Regex ($argument) {
        '^(-h|--help|help)$' { Show-Usage; exit 0 }
        # Lower-cased where the option is a keyword; out= is a path.
        '^out='    { $out = $argument.Substring(4) }
        '^sdk='    { $sdk = $argument.Substring(4).ToLower() }
        '^verify=' { $verify = $argument.Substring(7).ToLower() }
        '^clean='  { $clean = $argument.Substring(6).ToLower() }
        default {
            Write-Error "${app}: unknown argument '$argument' (options are key=value; see $app --help)"
        }
    }
}

if ($verify -notin @("yes", "no")) { Write-Error "${app}: verify must be yes or no (got '$verify')" }
if ($clean  -notin @("yes", "no")) { Write-Error "${app}: clean must be yes or no (got '$clean')" }
if ($sdk -notin @("download", "build")) {
    Write-Error "${app}: sdk must be download or build (got '$sdk')"
}

# Resolve against the invoking directory, before anything cds away from it.
# GetFullPath(path, basePath) rather than Join-Path: Join-Path concatenates
# unconditionally, so an out= that is already absolute would be appended to the
# current directory instead of used as given.
$out = [System.IO.Path]::GetFullPath($out, (Get-Location).Path)

# clean=yes deletes this path, so refuse the ones that would take the repository
# (or the filesystem) with them.
$rootFull = [System.IO.Path]::GetFullPath($rootDirectory)
$packageFull = [System.IO.Path]::GetFullPath($packageDirectory)
if (($out -eq $rootFull) -or ($out -eq $packageFull) -or ($out -eq [System.IO.Path]::GetPathRoot($out))) {
    Write-Error "${app}: refusing to use '$out' as out= (it is deleted when clean=yes)"
}

Set-Location $rootDirectory

# --- what this release is called -------------------------------------------
#
# A release is a tag: one is pushed, and the workflow is then dispatched from
# it. So the ref this is running on already IS the answer, and reading it here
# is reading the decision, not making it. GITHUB_REF_NAME is the authoritative
# form when there is one; `git describe` is the same question asked locally.
#
# Neither is an error when absent: a run off a branch is a package build, not a
# release (the workflow refuses to create a Release from one).
$name = (Get-Content -Raw "$packageDirectory/package.json" | ConvertFrom-Json).name
$version = (& "$rootDirectory/scripts/release-version.ps1").Trim()
$manifestTag = "v$version"

if (($env:GITHUB_REF_TYPE -eq "tag") -and $env:GITHUB_REF_NAME) {
    $tag = $env:GITHUB_REF_NAME
    $tagSource = "GITHUB_REF_NAME -- the ref this release was dispatched from"
} else {
    $described = & git -C $rootDirectory describe --exact-match --match 'v[0-9]*' --tags HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $described) {
        $tag = $described.Trim()
        $tagSource = "the release tag HEAD is checked out at"
    } else {
        $tag = $manifestTag
        $tagSource = "package.json -- HEAD is not on a release tag, so this is a package build"
    }
}

$versioned = "$name-$version.tgz"
# The version-less alias is not a nicety: README and the docs install the package
# from releases/latest/download/<name>.tgz, a URL that must not change when the
# version does. It is the same bytes under a stable name.
$alias = "$name.tgz"

Write-Host "options"
Write-Host "  out      => $out"
Write-Host "  sdk      => $sdk"
Write-Host "  verify   => $verify"
Write-Host "  clean    => $clean"
Write-Host "  package  => $name $version"
Write-Host "  tag      => $tag"
Write-Host "              ($tagSource)"

# A tag that disagrees with package.json is an error, not a note: npm packs
# package.json's version whatever the tag says, so the Release would be called
# one thing and carry a tarball called another -- and the tarball is what a
# consumer installs. This cannot fire off a branch: $tag falls back to
# $manifestTag when HEAD is not on a release tag.
if ($tag -ne $manifestTag) {
    Write-Host ""
    Write-Error "${app}: tag $tag does not match package.json ($manifestTag). Bump the version, or retag -- npm packs package.json's, so the Release would carry $versioned."
}

# --- preflight -------------------------------------------------------------

if (-not (Test-Path "$packageDirectory/package.json")) {
    Write-Error "${app}: package/package.json not found -- is this the SSPlayerForUnity repo?"
}

# npm is required, not just preferred: it applies the "files" allowlist and
# produces the `package/` root layout UPM expects. pack-release says the same
# and refuses to fall back to a plain tar.
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Error "${app}: npm not found -- it is what packs the UPM tarball ('files' allowlist + layout)"
}

# Get-FileHash returns an upper-case hash; sha256sum writes lower-case. Emit the
# lower-case form so SHA256SUMS is byte-identical to the one the shell twin (and
# CI) writes, and so `sha256sum -c` accepts it.
function Get-Sha256($file) { (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLower() }

function Write-Step($message) {
    Write-Host ""
    Write-Host ">> $message"
}

$runtimePlugins = "$packageDirectory/Runtime/Plugins"
$editorPlugins = "$packageDirectory/Editor/Plugins"
$bindings = @(
    "$packageDirectory/Runtime/Bindings/SSRuntimeMethods.g.cs",
    "$packageDirectory/Editor/Bindings/SSConverterMethods.g.cs"
)

# --- native libraries ------------------------------------------------------
if ($sdk -eq "download") {
    Write-Step "installing the pinned SDK native libraries"
    & "$rootDirectory/scripts/download-sdk.ps1"
} else {
    # build-runtime builds the host platform and nothing else, and it leaves
    # whatever other platforms are already in the plugin directories in place.
    # The checks below cannot tell the difference -- they ask that each tree has
    # libraries, not that it has all of them -- so say it here rather than let a
    # one-platform tarball look like a release.
    Write-Step "building the SDK native libraries from source (THIS HOST ONLY)"
    Write-Host "   sdk=build compiles one platform. Any others in the plugin directories are"
    Write-Host "   left as they were, so what lands in the tarball is whatever happens to be"
    Write-Host "   there -- fine for getting a package to look at, never for cutting a release."
    & "$rootDirectory/scripts/build-runtime.ps1" build=release
}

# --- check the tree --------------------------------------------------------
# Everything at once: finding out about the second missing .meta on the second
# run is not a debugging loop worth having.
$failed = New-Object System.Collections.Generic.List[string]

$libExtensions = @(".dylib", ".so", ".dll", ".a", ".lib")
$bundleExtensions = @(".bundle", ".xcframework")

# Every native plugin under a tree. A .bundle and an .xcframework are
# directories carrying a sibling .meta, so they are matched as directories and
# their contents excluded -- otherwise every file inside would be reported as a
# lib with no .meta of its own.
function Get-NativePlugins($tree) {
    if (-not (Test-Path $tree)) { return @() }
    Get-ChildItem -Path $tree -Recurse -Force | Where-Object {
        $inWrapper = $false
        foreach ($extension in $bundleExtensions) {
            if ($_.FullName -match [regex]::Escape($extension) + "[\\/]") { $inWrapper = $true }
        }
        if ($inWrapper) { return $false }
        if ($_.PSIsContainer) { return $bundleExtensions -contains $_.Extension }
        return $libExtensions -contains $_.Extension
    }
}

if ($verify -eq "yes") {
    Write-Step "checking the package tree"

    foreach ($tree in @($runtimePlugins, $editorPlugins)) {
        $relative = $tree.Substring($rootFull.Length + 1)
        if (-not (Test-Path $tree)) {
            $failed.Add("$relative does not exist")
            continue
        }
        $plugins = @(Get-NativePlugins $tree)
        Write-Host "   ${relative}: $($plugins.Count) native lib(s)"
        if ($plugins.Count -eq 0) {
            $failed.Add("no native libraries in $relative (run download-sdk.ps1 or build-runtime.ps1)")
        }
    }

    # A UPM package is extracted into an immutable folder (Library/PackageCache),
    # where Unity ignores any asset that has no .meta. A lib whose .meta was never
    # committed therefore installs as nothing -- and the three same-named Android
    # libssruntime.so would collide. CI has no Unity to generate these, so this is
    # a release blocker rather than a warning.
    $metaMissing = 0
    foreach ($tree in @($runtimePlugins, $editorPlugins)) {
        foreach ($plugin in Get-NativePlugins $tree) {
            if (Test-Path "$($plugin.FullName).meta") { continue }
            $failed.Add("native plugin has no committed .meta: $($plugin.FullName.Substring($rootFull.Length + 1))")
            $metaMissing++
        }
    }
    if ($metaMissing -eq 0) {
        Write-Host "   every native plugin has a .meta"
    } else {
        $failed.Add("$metaMissing native plugin(s) missing a .meta -- see scripts/scaffold-plugin-placeholders.sh")
    }

    # The csbindgen FFI bindings are compile-critical; a tarball without them does
    # not build for consumers. Their .meta matter just as much, and cannot be
    # committed beside them (the .g.cs are gitignored) -- download-sdk places the
    # fixed-GUID copies from scripts/upm-meta.
    foreach ($binding in $bindings) {
        $relative = $binding.Substring($rootFull.Length + 1)
        if (-not (Test-Path $binding))         { $failed.Add("missing FFI binding: $relative") }
        if (-not (Test-Path "$binding.meta"))  { $failed.Add("missing .meta for FFI binding: $relative.meta") }
    }
    if ($failed.Count -eq 0) { Write-Host "   both FFI bindings present, with their .meta" }

    if ($failed.Count -gt 0) {
        Write-Host ""
        Write-Error "${app}: the package tree is not packable:`n  $($failed -join "`n  ")"
    }
} else {
    Write-Step "verify=no -- the package tree is not checked"
}

# --- pack ------------------------------------------------------------------
# pack-release copies LICENSE / CHANGELOG / Third Party Notices (and their
# committed .meta) into package/ and packs, leaving the tarball at the repo
# root. It is the one that knows the UPM document names, so it stays the thing
# that packs -- including the choice of packer: `upm pack` when
# UNITY_ORGANIZATION_ID is set, which signs as it packs, `npm pack` otherwise.
Write-Step "packing the UPM tarball"
& "$rootDirectory/scripts/pack-release.ps1"

if (-not (Test-Path "$rootDirectory/$versioned")) {
    Write-Error "${app}: pack-release.ps1 produced no $versioned"
}

# --- collect ---------------------------------------------------------------
Write-Step "collecting into $out"

if (($clean -eq "yes") -and (Test-Path $out)) {
    Remove-Item -Recurse -Force $out
}
New-Item -ItemType Directory -Force -Path $out | Out-Null

Move-Item -Force "$rootDirectory/$versioned" "$out/$versioned"
Copy-Item -Force "$out/$versioned" "$out/$alias"
Write-Host "   $versioned"
Write-Host "   $alias  (stable releases/latest/download/ name)"

# Checksums over both assets. The package ships native binaries, so give
# consumers the same way to verify a download that download-sdk uses against the
# SDK zips -- same `<sha256>  <filename>` layout, so the same `sha256sum -c`
# works here. Two spaces, LF, no BOM: that is the format, on every platform.
$sumLines = foreach ($file in @($versioned, $alias)) { "$(Get-Sha256 "$out/$file")  $file" }
[System.IO.File]::WriteAllText("$out/SHA256SUMS", ($sumLines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding $false))
Write-Host "   SHA256SUMS"

# --- check the tarball -----------------------------------------------------
# `npm pack` applies the "files" allowlist, which the tree check above cannot
# see through: the tree can be perfect and the archive still be missing what the
# allowlist forgot to name. Note LICENSE.md is NOT in "files" -- npm always
# includes a LICENSE*, and only its .meta has to be listed. That is subtle
# enough to be worth asserting rather than remembering.
if ($verify -eq "yes") {
    Write-Step "checking $versioned"

    $entries = & tar -tzf "$out/$versioned"
    function Test-Entry($path) { $entries -contains "package/$path" }
    function Test-EntryPrefix($prefix) { ($entries | Where-Object { $_.StartsWith("package/$prefix") }).Count -gt 0 }

    # UPM requires the manifest at the archive's package/ root.
    if (-not (Test-Entry "package.json")) { $failed.Add("$versioned has no package/package.json") }

    # The documents pack-release copies in, each with the .meta Unity needs to
    # show it in the Package Manager's Licenses / Changelog tabs.
    foreach ($document in @("LICENSE.md", "CHANGELOG.md", "Third Party Notices.md")) {
        if (-not (Test-Entry $document))         { $failed.Add("$versioned is missing $document") }
        if (-not (Test-Entry "$document.meta"))  { $failed.Add("$versioned is missing $document.meta") }
    }

    # The bindings, and a .meta for each.
    foreach ($binding in @("Runtime/Bindings/SSRuntimeMethods.g.cs", "Editor/Bindings/SSConverterMethods.g.cs")) {
        if (-not (Test-Entry $binding))          { $failed.Add("$versioned is missing $binding") }
        if (-not (Test-Entry "$binding.meta"))   { $failed.Add("$versioned is missing $binding.meta") }
    }

    # The native libraries survived the allowlist, in both plugin trees.
    foreach ($tree in @("Runtime/Plugins", "Editor/Plugins")) {
        if (-not (Test-EntryPrefix "$tree/")) { $failed.Add("$versioned contains nothing under $tree/") }
    }

    # And every native plugin in the archive still has its .meta beside it. The
    # allowlist names directories, so a lib and its .meta are included or
    # excluded together -- but a `!`-negated entry can split them, which
    # installs the lib as nothing.
    #
    # "Plugin" is the unit Unity imports, which for a .bundle or an .xcframework
    # is the directory: it carries one sibling .meta and the libraries inside it
    # carry none. Folding those into their root is the same distinction the tree
    # check above makes.
    $units = $entries |
        Where-Object { $_ -match '^package/(Runtime|Editor)/Plugins/' } |
        ForEach-Object { $_ -replace '^(.*\.(bundle|xcframework))/.*$', '$1' } |
        Where-Object { $_ -match '\.(dylib|so|dll|a|lib|bundle|xcframework)$' } |
        Sort-Object -Unique
    foreach ($unit in $units) {
        if ($entries -contains "$unit.meta") { continue }
        $failed.Add("$versioned has $unit with no .meta beside it")
    }

    # The other direction, and the one that catches a platform going missing: a
    # committed .meta whose plugin is NOT in the archive. Every platform's .meta
    # is tracked in git, so they are all there whatever the native libraries did
    # -- which means an absent lib leaves its .meta behind rather than removing
    # anything, and the checks above, which only ever ask "does this lib have a
    # .meta", see nothing wrong. What ships is a package that names a platform
    # and installs no library for it.
    #
    # A .meta is satisfied by a file entry OR by a directory: tar lists no
    # directory entries, so a folder's .meta (and a .bundle's, and an
    # .xcframework's) is matched by something existing underneath it.
    $orphans = New-Object System.Collections.Generic.List[string]
    foreach ($meta in ($entries | Where-Object { $_ -match '^package/(Runtime|Editor)/Plugins/.*\.meta$' })) {
        $base = $meta.Substring(0, $meta.Length - 5)
        if ($entries -contains $base) { continue }
        if (($entries | Where-Object { $_.StartsWith("$base/") }).Count -gt 0) { continue }
        $orphans.Add($base.Substring("package/".Length))
    }

    if ($orphans.Count -gt 0) {
        if ($sdk -eq "download") {
            # The libraries came from the SDK release, so anything missing means
            # the release's layout and this package's expectations have diverged.
            $failed.Add("$versioned names plugins it does not contain (each installs as nothing):`n    $($orphans -join "`n    ")")
        } else {
            # sdk=build compiles this host and leaves every other platform alone,
            # so this is the expected shortfall -- printed in full, because "host
            # only" is a great deal vaguer than the actual list.
            Write-Host "   sdk=build: NOT in this tarball (not built on this host):"
            foreach ($orphan in $orphans) { Write-Host "    $orphan" }
        }
    } elseif ($sdk -eq "build") {
        Write-Host "   sdk=build: every platform present -- the plugin directories already held the rest"
    }

    # The signature, when one was asked for. pack-release.ps1 signs only if
    # UNITY_ORGANIZATION_ID is set, so this reads the same gate from the same
    # environment -- and it is the only thing that separates a signed release from
    # a run where the credentials quietly did nothing. Unity carries the signature
    # as an encrypted .attestation.p7m inside the archive; its path in there is not
    # documented, so match the name wherever it sits.
    if (-not [string]::IsNullOrEmpty($env:UNITY_ORGANIZATION_ID)) {
        if ($entries -match '\.attestation\.p7m$') {
            Write-Host "   signed -- .attestation.p7m present"
        } else {
            $failed.Add("$versioned carries no .attestation.p7m -- it was packed unsigned")
        }
    } else {
        Write-Host "   unsigned -- UNITY_ORGANIZATION_ID not set (Unity 6.3 says so on install)"
    }

    # The version npm actually packed, which is package.json's and is what the
    # filename claims.
    $packedVersion = ((& tar -xzOf "$out/$versioned" package/package.json) -join "`n" | ConvertFrom-Json).version
    if ($packedVersion -ne $version) {
        $failed.Add("$versioned contains version $packedVersion, expected $version")
    }

    # The alias is the same bytes, and the manifest covers both.
    foreach ($line in Get-Content "$out/SHA256SUMS") {
        $parts = $line -split '\s+', 2
        if ($parts.Count -ne 2) { continue }
        if ((Get-Sha256 "$out/$($parts[1].Trim())") -ne $parts[0].ToLower()) {
            $failed.Add("SHA256SUMS does not verify for $($parts[1].Trim())")
        }
    }
    if ((Get-Sha256 "$out/$versioned") -ne (Get-Sha256 "$out/$alias")) {
        $failed.Add("$alias is not identical to $versioned")
    }

    if ($failed.Count -gt 0) {
        Write-Host ""
        Write-Error "${app}: the release in $out is not shippable:`n  $($failed -join "`n  ")"
    }

    Write-Host "   $($entries.Count) entries, version $packedVersion"
    Write-Host "   documents, bindings and both plugin trees present, every lib with its .meta"
    Write-Host "   alias identical, SHA256SUMS verified"
    Write-Host "   all checks passed"
} else {
    Write-Step "verify=no -- the tarball is not checked"
}

# --- done ------------------------------------------------------------------
Write-Host ""
Write-Host "${app}: done -> $out  ($tag)"
Write-Host ""
Get-ChildItem -Path $out | ForEach-Object { Write-Host ("  {0,12}  {1}" -f $_.Length, $_.Name) }
Write-Host ""
Write-Host "${app}: this is what release.yml attaches to the draft Release for $tag."
Write-Host "${app}: install it into a Unity project before shipping it:"
Write-Host "  Window > Package Manager > + > Install package from tarball..."
Write-Host "  $out/$versioned"
