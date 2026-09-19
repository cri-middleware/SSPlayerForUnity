#!/usr/bin/env pwsh
$ErrorActionPreference = "Stop"

$baseDirectory = Split-Path -Parent $PSCommandPath
$rootDirectory = Split-Path -Parent $baseDirectory

$FLATC_CMD = "flatc"
$OUTPUT_DIR = "$rootDirectory/package/Runtime/Format"

New-Item -ItemType Directory -Path $OUTPUT_DIR -Force | Out-Null

# Generate C# from FlatBuffers
foreach ($f in Get-ChildItem "$rootDirectory/SpriteStudio-SDK/libs/ssruntime/fbs/*.fbs") {
    & $FLATC_CMD --csharp --gen-onefile -o $OUTPUT_DIR $f
}

foreach ($f in Get-ChildItem "$rootDirectory/SpriteStudio-SDK/libs/ssab/fbs/*.fbs") {
    & $FLATC_CMD --csharp --gen-onefile -o $OUTPUT_DIR $f
}
