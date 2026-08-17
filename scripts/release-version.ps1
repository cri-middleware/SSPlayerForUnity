# Print the UPM package version -- what a release of this package should be
# tagged as.
#
#   .\scripts\release-version.ps1            # 7.0.0-beta.1
#   "v$(.\scripts\release-version.ps1)"      # v7.0.0-beta.1  -- the git tag
#
# It is `version` in package/package.json, and nothing else. Deliberately NOT
# scripts/SDK_VERSION.txt: that pins which native SDK libs download-sdk fetches,
# which moves on its own schedule and is not what this package is called.
#
# The tag is what names a release, not this -- release.yml is dispatched from a
# v* tag and takes the name from that ref. This is what the tag *should* be, so
# build-release can say so when the two disagree, and so the two of them plus a
# human cutting a release by hand all get the answer from one place.
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments
)

$ErrorActionPreference = "Stop"

$app = Split-Path -Leaf $PSCommandPath
foreach ($argument in $Arguments) {
    switch -Regex ($argument) {
        '^(-h|--help|help)$' {
            Write-Host "Usage: $app"
            Write-Host "Prints ""version"" from package/package.json (no leading 'v')."
            exit 0
        }
        default {
            Write-Error "${app}: unexpected argument '$argument' (see $app --help)"
        }
    }
}

$rootDirectory = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$manifest = "$rootDirectory/package/package.json"

if (-not (Test-Path $manifest)) {
    Write-Error "${app}: $manifest not found -- is this the SSPlayerForUnity repo?"
}

# ConvertFrom-Json rather than node: PowerShell parses JSON natively, so this
# twin has one fewer prerequisite than the shell one (which asks node, because
# node is what packs the tarball there anyway).
$version = (Get-Content -Raw $manifest | ConvertFrom-Json).version

if (-not $version) {
    Write-Error "${app}: package/package.json has no ""version"""
}

Write-Output $version
