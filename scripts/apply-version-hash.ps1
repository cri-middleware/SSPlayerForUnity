$ErrorActionPreference = "Stop"

# Change to the root directory of the repository
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location "$scriptDir\.."

$packageJson = "package\package.json"

# 1. Get the current git hash
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host "Warning: git is not installed. Skipping version hash injection."
    exit 0
}

# Check if inside a git repository
$isGitRepo = $false
try {
    $null = git rev-parse --is-inside-work-tree 2>&1
    if ($LASTEXITCODE -eq 0) {
        $isGitRepo = $true
    }
} catch {
    $isGitRepo = $false
}

if (-not $isGitRepo) {
    Write-Host "Warning: Not inside a git repository. Skipping version hash injection."
    exit 0
}

$gitHash = git rev-parse --short HEAD
Write-Host "Current git hash: $gitHash"

# 2. Rewrite the version in package.json to append the hash
# Use regex to replace to avoid ConvertTo-Json formatting changes
$jsonContent = Get-Content $packageJson -Raw
$jsonContent = $jsonContent -replace '"version"\s*:\s*"([^+]+)[^"]*"', ("`"version`": `"`$1+$gitHash`"")
Set-Content -Path $packageJson -Value $jsonContent -NoNewline -Encoding UTF8

Write-Host "Appended +$gitHash to package.json version."
