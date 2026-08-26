#!/usr/bin/env pwsh

# Regenerates the sample assets that examples/SampleProject commits.
# See scripts/deploy-examples.sh for the full rationale; this is the Windows mirror of it.

$ErrorActionPreference = "Stop"

$baseDirectory = Split-Path -Parent $PSCommandPath
$rootDirectory = Split-Path -Parent $baseDirectory

$sdkDirectory = Join-Path $rootDirectory "SpriteStudio-SDK"
$sspj = Join-Path $sdkDirectory "tests/Ringo/Ringo.sspj"
$outDirectory = Join-Path $rootDirectory "examples/SampleProject/Assets/ssab_generated/Ringo"

if (-not (Test-Path $sspj)) {
    Write-Error "$sspj not found. Run 'git submodule update --init --recursive' first."
    exit 1
}

Write-Host "Converting Ringo into $outDirectory ..."
New-Item -ItemType Directory -Force -Path $outDirectory | Out-Null

Push-Location $sdkDirectory
try {
    cargo run --release -p ssconverter-cli -- -o "$outDirectory" "$sspj"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    Pop-Location
}

Write-Host "Done. Open examples/SampleProject in Unity to let it reimport, then commit the changed .ssab."
