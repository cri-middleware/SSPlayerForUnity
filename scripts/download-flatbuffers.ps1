#!/usr/bin/env pwsh
$ErrorActionPreference = "Stop"
# Invoke-WebRequest's progress bar slows the download itself in Windows PowerShell 5.1.
$ProgressPreference = "SilentlyContinue"

$baseDirectory = Split-Path -Parent $PSCommandPath
$rootDirectory = Split-Path -Parent $baseDirectory

$VERSION = "25.12.19"
$ZIP_URL = "https://github.com/google/flatbuffers/archive/refs/tags/v$VERSION.zip"
$TEMP_DIR = Join-Path $rootDirectory "scripts/.temp_flatbuffers"
$OUTPUT_DIR = Join-Path $rootDirectory "package/Runtime/FlatBuffers"

Write-Host "Downloading FlatBuffers v$VERSION..."
New-Item -ItemType Directory -Path $TEMP_DIR -Force | Out-Null
$zipPath = Join-Path $TEMP_DIR "flatbuffers.zip"
Invoke-WebRequest -Uri $ZIP_URL -OutFile $zipPath

Write-Host "Extracting C# runtime..."
# Expand-Archive is a script-module function: run from a session, it reads the
# global progress preference rather than this script's.
$savedProgress = $global:ProgressPreference
$global:ProgressPreference = "SilentlyContinue"
try {
    Expand-Archive -Path $zipPath -DestinationPath $TEMP_DIR -Force
} finally {
    $global:ProgressPreference = $savedProgress
}

New-Item -ItemType Directory -Path $OUTPUT_DIR -Force | Out-Null
# $OUTPUT_DIR is a verbatim mirror of upstream net/FlatBuffers: never put project code
# here, it would be wiped below. Only the .cs files are refreshed - the .meta files stay
# so Unity keeps the same asset GUIDs across updates.
Get-ChildItem -Path $OUTPUT_DIR -File |
    Where-Object { $_.Extension -eq ".cs" } |
    Remove-Item -Force

# Copy only the C# source into the package
Copy-Item -Path "$TEMP_DIR\flatbuffers-$VERSION\net\FlatBuffers\*.cs" -Destination $OUTPUT_DIR -Force

Write-Host "Cleaning up..."
Remove-Item -Path $TEMP_DIR -Recurse -Force

Write-Host "FlatBuffers C# source code has been placed in $OUTPUT_DIR"
