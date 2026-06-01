$ErrorActionPreference = "Stop"

$baseDirectory = Split-Path -Parent $PSCommandPath
$rootDirectory = Split-Path -Parent $baseDirectory
$targetDir = "$rootDirectory/package"
$versionFile = "$targetDir/SDK_VERSION.txt"
$currentVersionFile = "$targetDir/Runtime/Plugins/VERSION"

$targetVersion = (Get-Content $versionFile).Trim()

if (Test-Path $currentVersionFile) {
    $currentVersion = (Get-Content $currentVersionFile).Trim()
    if ($currentVersion -eq $targetVersion) {
        Write-Host "SDK $targetVersion is already up to date. Skipping download."
        exit 0
    }
}

$url = "https://github.com/SpriteStudio/SpriteStudio7-SDK/releases/download/$targetVersion/spritestudio7-sdk-static-libs.zip"
$zipFile = "$targetDir/sdk.zip"

Write-Host "Target SDK Version: $targetVersion"
Write-Host "Download URL: $url"
Write-Host "Downloading SDK..."
Invoke-WebRequest -Uri $url -OutFile $zipFile

Write-Host "Extracting SDK..."
Expand-Archive -Path $zipFile -DestinationPath $targetDir -Force
Remove-Item $zipFile

Set-Content -Path $currentVersionFile -Value $targetVersion

Write-Host "Done."
