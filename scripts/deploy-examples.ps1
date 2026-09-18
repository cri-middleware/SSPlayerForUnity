#!/usr/bin/env pwsh

# Builds the sample assets the example projects need. Nothing it writes is committed, and it has
# to run before the project is opened in Unity.
# See scripts/deploy-examples.sh for the full rationale; this is the Windows mirror of it.

$ErrorActionPreference = "Stop"

$baseDirectory = Split-Path -Parent $PSCommandPath
$rootDirectory = Split-Path -Parent $baseDirectory

$sdkDirectory = Join-Path $rootDirectory "SpriteStudio-SDK"
$sspj = Join-Path $sdkDirectory "tests/Ringo/Ringo.sspj"
$outDirectories = @(
    (Join-Path $rootDirectory "examples/SampleProject/Assets/ssab_generated/Ringo"),
    (Join-Path $rootDirectory "examples/Override_Ringo/Assets/ssab_generated/Ringo")
)

if (-not (Test-Path $sspj)) {
    Write-Error "$sspj not found. Run 'git submodule update --init --recursive' first."
    exit 1
}

Push-Location $sdkDirectory
try {
    foreach ($outDirectory in $outDirectories) {
        Write-Host "Converting Ringo into $outDirectory ..."
        New-Item -ItemType Directory -Force -Path $outDirectory | Out-Null

        cargo run --release -p ssconverter-cli -- -o "$outDirectory" "$sspj"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
} finally {
    Pop-Location
}

Write-Host "Done. Open either example project in Unity; it imports what was just written on first open."
