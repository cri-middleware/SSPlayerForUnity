$ErrorActionPreference = "Stop"

# Change to the root directory of the repository
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location "$scriptDir\.."

$packageDir = "package"

# Pack the package
if (Get-Command npm -ErrorAction SilentlyContinue) {
    Write-Host "Running 'npm pack'..."
    Set-Location $packageDir
    npm pack
    Move-Item *.tgz ..\
    Set-Location ..
} else {
    Write-Host "Warning: npm is not installed. Falling back to tar."
    Write-Host "Creating tarball using standard tar..."
    $tarName = "com.spritestudio.ssruntime-release.tgz"
    tar -czf $tarName $packageDir\
    Write-Host "Created $tarName"
}

Write-Host "Packing complete!"
