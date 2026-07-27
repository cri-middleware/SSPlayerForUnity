$ErrorActionPreference = "Stop"

# Absolute repo root: the finally block can run while the working directory is still
# inside package\ (a failed `npm pack`), so every path below has to be root-anchored.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path "$scriptDir\..").Path
Set-Location $root

$packageDir = Join-Path $root "package"
$metaDir = Join-Path $root "scripts\upm-meta"

# Repo-root document -> name it takes inside the package. UPM shows LICENSE.md and
# CHANGELOG.md in the Package Manager's Licenses / Changelog tabs, and expects the
# third-party notices under this exact spelling.
$docs = @(
    @{ Src = "LICENSE.md";             Dst = "LICENSE.md" },
    @{ Src = "THIRD_PARTY_NOTICES.md"; Dst = "Third Party Notices.md" },
    @{ Src = "CHANGELOG.md";           Dst = "CHANGELOG.md" }
)

try {
    # Copy the license / changelog documents into the package dir for UPM before packing.
    # Each one also needs its sibling .meta: a UPM package is extracted into an immutable
    # folder (Library/PackageCache), and Unity ignores assets there that have no .meta.
    # The .meta carry fixed GUIDs and live in scripts\upm-meta (see the README there).
    Write-Host "Copying license / changelog documents to package directory..."
    foreach ($doc in $docs) {
        Copy-Item (Join-Path $root $doc.Src) -Destination (Join-Path $packageDir $doc.Dst)
        Copy-Item (Join-Path $metaDir "$($doc.Dst).meta") -Destination (Join-Path $packageDir "$($doc.Dst).meta")
    }

    # Pack the package.
    # npm pack is required, not just preferred: it applies the "files" allowlist in package.json and produces a
    # tarball with the `package/` root folder structure UPM expects. A plain `tar` fallback
    # would honour neither and sweep in Tests\ and other dev-only content, so refuse to
    # produce a tarball at all rather than a subtly broken one.
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
        Write-Error "npm is required to pack the UPM package (it applies the 'files' allowlist). Install Node.js / npm and re-run."
    }

    Write-Host "Running 'npm pack'..."
    Set-Location $packageDir
    npm pack
    Move-Item *.tgz $root
    Set-Location $root
} finally {
    # Cleanup the copied documents and their .meta
    Set-Location $root
    foreach ($doc in $docs) {
        Remove-Item (Join-Path $packageDir $doc.Dst) -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $packageDir "$($doc.Dst).meta") -ErrorAction SilentlyContinue
    }
}

Write-Host "Packing complete!"
