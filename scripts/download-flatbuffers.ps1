#!/usr/bin/env pwsh
$ErrorActionPreference = "Stop"

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
Expand-Archive -Path $zipPath -DestinationPath $TEMP_DIR -Force

New-Item -ItemType Directory -Path $OUTPUT_DIR -Force | Out-Null
# Clean up: remove any stale files
if (Test-Path "$OUTPUT_DIR\*") {
    Remove-Item -Path "$OUTPUT_DIR\*" -Recurse -Force
}

# Copy only the C# source into the package
Copy-Item -Path "$TEMP_DIR\flatbuffers-$VERSION\net\FlatBuffers\*.cs" -Destination $OUTPUT_DIR -Force

Write-Host "Cleaning up..."
Remove-Item -Path $TEMP_DIR -Recurse -Force

Write-Host "FlatBuffers C# source code has been placed in $OUTPUT_DIR"
