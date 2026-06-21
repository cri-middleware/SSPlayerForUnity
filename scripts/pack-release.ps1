$ErrorActionPreference = "Stop"

# Change to the root directory of the repository
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location "$scriptDir\.."

$packageDir = "package"

try {
    # Copy license files to package dir for UPM before packing
    Write-Host "Copying license files to package directory..."
    Copy-Item "LICENSE.md" -Destination "$packageDir\LICENSE.md"
    Copy-Item "THIRD_PARTY_NOTICES.md" -Destination "$packageDir\Third Party Notices.md"

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
} finally {
    # Cleanup copied licenses and any Unity-generated meta files
    Remove-Item "$packageDir\LICENSE.md" -ErrorAction SilentlyContinue
    Remove-Item "$packageDir\LICENSE.md.meta" -ErrorAction SilentlyContinue
    Remove-Item "$packageDir\Third Party Notices.md" -ErrorAction SilentlyContinue
    Remove-Item "$packageDir\Third Party Notices.md.meta" -ErrorAction SilentlyContinue
}

Write-Host "Packing complete!"
