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
    @{ Src = "LICENSE.ja.md";          Dst = "LICENSE.ja.md" },
    @{ Src = "THIRD_PARTY_NOTICES.md"; Dst = "Third Party Notices.md" },
    @{ Src = "CHANGELOG.md";           Dst = "CHANGELOG.md" }
)

# The documentation tree rides along: it matches the installed version, and it reads with no
# network. Unity skips a folder whose name ends in `~`, so none of it needs a .meta. Only
# Markdown goes in (the images are most of docs\'s weight), and the mkdocs snippet includes
# (`--8<-- "LICENSE.md"`) are resolved while copying, because only mkdocs understands them.
# scripts\upm-docs holds the package's own index page, named after the package as UPM expects.
$docsTree = Join-Path $packageDir "Documentation~"
$includePattern = '(?m)^--8<-- "([^"]+)"[ \t]*(?=\r?$)'

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

    Write-Host "Copying the documentation tree to package directory..."
    Remove-Item -LiteralPath $docsTree -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $docsTree | Out-Null
    Copy-Item (Join-Path $root "scripts\upm-docs\*.md") -Destination $docsTree
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    foreach ($lang in @("en", "ja")) {
        $langRoot = Join-Path $root "docs\$lang"
        foreach ($file in Get-ChildItem -LiteralPath $langRoot -Recurse -File -Filter "*.md") {
            $rel = $file.FullName.Substring($langRoot.Length).TrimStart('\', '/')
            $dst = Join-Path (Join-Path $docsTree $lang) $rel
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
            $text = [System.IO.File]::ReadAllText($file.FullName)
            $text = [regex]::Replace($text, $includePattern, {
                param($m)
                $include = Join-Path $root $m.Groups[1].Value
                if (-not (Test-Path -LiteralPath $include)) { throw "snippet include not found: $include" }
                [System.IO.File]::ReadAllText($include) -replace '\r?\n\z', ''
            })
            [System.IO.File]::WriteAllText($dst, $text, $utf8NoBom)
        }
    }

    # Pack the package.
    # The packer is npm, or Unity's upm when signing (below) -- never a plain `tar`.
    # npm pack applies the "files" allowlist in package.json and produces a tarball with the
    # `package/` root folder structure UPM expects; a tar fallback would honour neither and
    # sweep in Tests\ and other dev-only content, so refuse to produce a tarball at all
    # rather than a subtly broken one. build-release.ps1 checks the finished archive either
    # way, which is what establishes that upm pack applied the allowlist too.
    #
    # --- Package signature (opt-in via env) ---------------------------------
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
    # map to the GitHub secrets of the same names -- see .github\workflows\release.yml for
    # the CI wiring. Install the CLI with:
    #   irm https://cdn.packages.unity.com/upm-cli/install.ps1 | iex
    if (-not [string]::IsNullOrEmpty($env:UNITY_ORGANIZATION_ID)) {
        # Once the gate is open, anything missing is an error rather than a fall back to
        # npm: a release that goes out unsigned while the run reports success is the exact
        # failure a signature is there to prevent.
        if (-not (Get-Command upm -ErrorAction SilentlyContinue)) {
            Write-Error "UNITY_ORGANIZATION_ID is set but the UPM CLI (upm) is not installed. Install it with: irm https://cdn.packages.unity.com/upm-cli/install.ps1 | iex"
        }
        if ([string]::IsNullOrEmpty($env:UPM_SERVICE_ACCOUNT_KEY_ID) -or [string]::IsNullOrEmpty($env:UPM_SERVICE_ACCOUNT_KEY_SECRET)) {
            Write-Error "UNITY_ORGANIZATION_ID is set but UPM_SERVICE_ACCOUNT_KEY_ID / UPM_SERVICE_ACCOUNT_KEY_SECRET are not -- upm pack cannot sign."
        }

        # upm pack takes the package directory and writes where it is told, so unlike
        # npm pack it needs neither the Set-Location nor the Move-Item afterwards.
        Write-Host "Running 'upm pack' (signing as organization $env:UNITY_ORGANIZATION_ID)..."
        upm pack $packageDir --organization-id $env:UNITY_ORGANIZATION_ID --destination $root
    }
    else {
        if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
            Write-Error "npm is required to pack the UPM package (it applies the 'files' allowlist). Install Node.js / npm and re-run."
        }

        Write-Host "Skipping the UPM signature (UNITY_ORGANIZATION_ID not set)."
        Write-Host "Running 'npm pack'..."
        Set-Location $packageDir
        npm pack
        Move-Item *.tgz $root
        Set-Location $root
    }
} finally {
    # Cleanup the copied documents and their .meta
    Set-Location $root
    foreach ($doc in $docs) {
        Remove-Item (Join-Path $packageDir $doc.Dst) -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $packageDir "$($doc.Dst).meta") -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $docsTree -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Packing complete!"
