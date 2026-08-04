$ErrorActionPreference = "Stop"

# Detect OS
$IsMac = $IsMacOS
$IsWin = $IsWindows

if (-not $IsMac -and -not $IsWin) {
    Write-Error "Unsupported OS"
    exit 1
}

$ProjectPath = "examples/UnityTestProject"
$ResultsFile = "test-results.xml"
$LogFile = "test-run.log"

# Get Unity Version from ProjectSettings
$ProjectVersionFile = "$ProjectPath/ProjectSettings/ProjectVersion.txt"
if (-not (Test-Path $ProjectVersionFile)) {
    Write-Error "Error: ProjectVersion.txt not found at $ProjectPath"
    exit 1
}

$VersionLine = Select-String -Path $ProjectVersionFile -Pattern "m_EditorVersion:"
if ($null -eq $VersionLine) {
    Write-Error "Could not find m_EditorVersion in $ProjectVersionFile"
    exit 1
}
$UnityVersion = ($VersionLine.Line -split '\s+')[1]

# Determine Unity Executable Path
if ($IsMac) {
    $UnityPath = "/Applications/Unity/Hub/Editor/$UnityVersion/Unity.app/Contents/MacOS/Unity"
} else {
    $UnityPath = "C:/Program Files/Unity/Hub/Editor/$UnityVersion/Editor/Unity.exe"
}

if (-not (Test-Path $UnityPath)) {
    Write-Error "Error: Unity executable not found at $UnityPath`nPlease ensure Unity Hub and the required Editor version ($UnityVersion) are installed."
    exit 1
}

Write-Host "Running Unity Tests (batchmode) for project: $ProjectPath"
Write-Host "Unity Version: $UnityVersion"
Write-Host "Log file: $LogFile"
Write-Host "---------------------------------------------------------"

# The .ssab fixtures under package/Tests/Runtime/Resources/ are build artifacts converted from the
# SpriteStudio-SDK sources, not committed files. Only three test classes know how to produce them (their
# [UnitySetUp] drives the converter); every other class just asserts "Ringo.ssab is required". NUnit runs
# the consumers first, so on a clean tree the suites below fail long before anything gets generated.
# Convert the fixtures up front so EditMode / PlayMode / Player all start from a warm tree.
$FixtureDir = "package/Tests/Runtime/Resources"
$FixtureFiles = @(
    "$FixtureDir/Ringo/Ringo.ssab",
    "$FixtureDir/Ringo/Ringo_tyu.ssab",
    "$FixtureDir/Sound/Sound.ssab",
    "$FixtureDir/overall/Mask.ssab"
)
$FixtureTests = "SpriteStudio.Runtime.Tests.SpriteStudioRingoPlaybackTests;SpriteStudio.Runtime.Tests.SpriteStudioAudioEventTests;SpriteStudio.Runtime.Tests.SpriteStudioClippingMaskTests"

$MissingFixtures = @($FixtureFiles | Where-Object { -not (Test-Path $_) })
if ($MissingFixtures.Count -gt 0) {
    Write-Host "Generating test fixtures from the SpriteStudio-SDK sources..."
    # The generators are themselves tests, and they fail while the fixture they build is still missing.
    # Ignore this pass's exit code: the real suites below are what report pass/fail.
    Start-Process -FilePath $UnityPath -ArgumentList "-batchmode", "-runTests", "-projectPath", $ProjectPath, "-testPlatform", "playmode", "-testResults", "fixtures-$ResultsFile", "-logFile", "fixtures-$LogFile", "-assemblyNames", "SpriteStudio.Runtime.Tests", "-testFilter", $FixtureTests -Wait -PassThru -NoNewWindow | Out-Null

    $StillMissing = @($FixtureFiles | Where-Object { -not (Test-Path $_) })
    if ($StillMissing.Count -gt 0) {
        Write-Host "Warning: these fixtures could not be generated: $($StillMissing -join ' ')"
        Write-Host "         Conversion needs the SpriteStudio-SDK submodule ('git submodule update --init')."
        Write-Host "         See fixtures-$LogFile. Continuing; the suites below will report the failures."
    }
} else {
    Write-Host "Test fixtures already present; skipping the conversion pass."
}

Write-Host "Running EditMode Tests..."
$processEdit = Start-Process -FilePath $UnityPath -ArgumentList "-batchmode", "-runTests", "-projectPath", $ProjectPath, "-testPlatform", "editmode", "-testResults", "editmode-$ResultsFile", "-logFile", "editmode-$LogFile", "-assemblyNames", "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" -Wait -PassThru -NoNewWindow

if ($processEdit.ExitCode -ne 0) {
    Write-Error "EditMode Tests failed with exit code $($processEdit.ExitCode)! See editmode-$LogFile for details."
    exit $processEdit.ExitCode
}

Write-Host "Running PlayMode Tests..."
$processPlay = Start-Process -FilePath $UnityPath -ArgumentList "-batchmode", "-runTests", "-projectPath", $ProjectPath, "-testPlatform", "playmode", "-testResults", "playmode-$ResultsFile", "-logFile", "playmode-$LogFile", "-assemblyNames", "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" -Wait -PassThru -NoNewWindow

if ($processPlay.ExitCode -ne 0) {
    Write-Error "PlayMode Tests failed with exit code $($processPlay.ExitCode)! See playmode-$LogFile for details."
    exit $processPlay.ExitCode
}

Write-Host "Running Standalone Player Tests..."
if ($IsMac) {
    $BuildTarget = "StandaloneOSX"
} else {
    $BuildTarget = "StandaloneWindows64"
}

$processPlayer = Start-Process -FilePath $UnityPath -ArgumentList "-batchmode", "-runTests", "-projectPath", $ProjectPath, "-testPlatform", "playmode", "-buildTarget", $BuildTarget, "-testResults", "player-$ResultsFile", "-logFile", "player-$LogFile", "-assemblyNames", "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" -Wait -PassThru -NoNewWindow

if ($processPlayer.ExitCode -ne 0) {
    Write-Error "Standalone Player Tests failed with exit code $($processPlayer.ExitCode)! See player-$LogFile for details."
    exit $processPlayer.ExitCode
}

Write-Host "All tests passed successfully!"
Write-Host "Results saved to editmode-$ResultsFile, playmode-$ResultsFile, and player-$ResultsFile"
