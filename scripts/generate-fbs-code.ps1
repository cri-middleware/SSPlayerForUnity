#!/usr/bin/env pwsh
$ErrorActionPreference = "Stop"

$HelpApp = Split-Path -Leaf $PSCommandPath
function Show-Usage {
    Write-Host "Usage: ${HelpApp} [--help]"
    Write-Host "  flatc --csharp over the SDK's .fbs schemas, into package/Runtime/Format/. Needs flatc."
    Write-Host "  The csbindgen FFI bindings are NOT this script's output: the SDK's own build emits"
    Write-Host "  them and build-runtime / download-sdk place them."
}

foreach ($arg in $args) {
    switch -Regex ($arg) {
        '^(-h|--help|help)$' { Show-Usage; exit 0 }
        default {
            [Console]::Error.WriteLine("${HelpApp}: unknown argument '$arg'")
            Show-Usage
            exit 2
        }
    }
}

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
