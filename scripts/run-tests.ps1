$ErrorActionPreference = "Stop"

$Usage = @"
Usage: run-tests.ps1 [--help]
  Run package/Tests headless in Unity batchmode: EditMode, then PlayMode, then
  PlayMode again in a standalone player build. Close the Editor first: batchmode
  cannot take the project lock (use the Unity Test Runner while it is open).

  Needs the Editor examples/UnityTestProject pins in ProjectVersion.txt, installed
  through Unity Hub (in its default folder or the install location set in its
  preferences) with an activated licence, and the native plugins plus the
  csbindgen binding placed by scripts/build-runtime.ps1 (or scripts/download-sdk.ps1).

  Each suite prints its counts from its NUnit result file. Skipped and
  inconclusive tests are counted there, never as passes.

  Exit status: 0 every suite passed, 1 a suite failed or wrote no results,
               2 bad arguments or a missing prerequisite.
"@

foreach ($arg in $args) {
    if ($arg -eq "-h" -or $arg -eq "--help") {
        Write-Host $Usage
        exit 0
    }
    # Not Write-Error: under "Stop" it throws, and the script would exit 1 instead of 2.
    [Console]::Error.WriteLine("run-tests.ps1: unknown argument: $arg")
    [Console]::Error.WriteLine($Usage)
    exit 2
}

# Every path below is relative to the repository root.
Set-Location (Split-Path -Parent $PSScriptRoot)

# Detect OS
$IsMac = $IsMacOS
$IsWin = $IsWindows

if (-not $IsMac -and -not $IsWin) {
    [Console]::Error.WriteLine("Unsupported OS")
    exit 2
}

$ProjectPath = "examples/UnityTestProject"
$ResultsFile = "test-results.xml"
$LogFile = "test-run.log"

# Get Unity Version from ProjectSettings
$ProjectVersionFile = "$ProjectPath/ProjectSettings/ProjectVersion.txt"
if (-not (Test-Path $ProjectVersionFile)) {
    [Console]::Error.WriteLine("Error: ProjectVersion.txt not found at $ProjectPath")
    exit 2
}

$VersionLine = Select-String -Path $ProjectVersionFile -Pattern "m_EditorVersion:"
if ($null -eq $VersionLine) {
    [Console]::Error.WriteLine("Could not find m_EditorVersion in $ProjectVersionFile")
    exit 2
}
$UnityVersion = ($VersionLine.Line -split '\s+')[1]

# Determine Unity Executable Path: the Hub's default install folder, then the install location set in the
# Hub's preferences, which the Hub keeps in secondaryInstallPath.json as a JSON string ("" when unset).
if ($IsMac) {
    $HubDefault = "/Applications/Unity/Hub/Editor"
    $HubSettings = "$HOME/Library/Application Support/UnityHub/secondaryInstallPath.json"
    $EditorExe = "Unity.app/Contents/MacOS/Unity"
    $NativePlugins = @("package/Runtime/Plugins/macOS/ssruntime.bundle", "package/Editor/Plugins/macOS/libssconverter.dylib")
} else {
    $HubDefault = "C:/Program Files/Unity/Hub/Editor"
    $HubSettings = "$env:APPDATA/UnityHub/secondaryInstallPath.json"
    $EditorExe = "Editor/Unity.exe"
    $NativePlugins = @("package/Runtime/Plugins/Windows/x86_64/ssruntime.dll", "package/Editor/Plugins/Windows/x86_64/ssconverter.dll")
}

$UnityCandidates = @("$HubDefault/$UnityVersion/$EditorExe")
if (Test-Path $HubSettings) {
    $HubInstallDir = $null
    try { $HubInstallDir = Get-Content -Raw $HubSettings | ConvertFrom-Json } catch { }
    if ($HubInstallDir -is [string] -and $HubInstallDir -ne "") {
        $UnityCandidates += "$($HubInstallDir.TrimEnd('\', '/'))/$UnityVersion/$EditorExe"
    }
}
$UnityPath = $UnityCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($null -eq $UnityPath) {
    [Console]::Error.WriteLine("Error: Unity $UnityVersion not found at: $($UnityCandidates -join ', ')`nPlease install that Editor version through Unity Hub.")
    exit 2
}

# Without these the project does not run a red suite -- it fails to compile, and batchmode reports
# "Scripts have compiler errors" (CS0234 on SpriteStudio.SsRuntime) with no hint that the cause is an
# unbuilt prerequisite. The binding is the gitignored csbindgen output; Runtime/Format/*.cs is committed.
$MissingPrereqs = @(($NativePlugins + "package/Runtime/Bindings/SSRuntimeMethods.g.cs") | Where-Object { -not (Test-Path $_) })
if ($MissingPrereqs.Count -gt 0) {
    [Console]::Error.WriteLine("Error: not built or downloaded yet: $($MissingPrereqs -join ' ')")
    [Console]::Error.WriteLine("       Run scripts/build-runtime.ps1 (or scripts/download-sdk.ps1) first.")
    exit 2
}

# Opening the project deletes the tracked .meta of every plugin absent on this machine -- the other
# platforms' binaries, never built here -- and a commit of that deletion ships a package that installs
# them as nothing. The player build also switches Unity Connect off in the project's settings. Put back
# what this run changed; a file that was already dirty is left alone.
$TrackedPaths = @('*.meta', "$ProjectPath/ProjectSettings")
$TrackedBefore = @(git status --porcelain -- $TrackedPaths 2>$null)
function Restore-Tracked {
    $restored = @()
    foreach ($line in @(git status --porcelain -- $TrackedPaths 2>$null)) {
        if ($line -notmatch '^ [MD] ' -or $TrackedBefore -contains $line) { continue }
        $path = $line.Substring(3)
        git checkout -q -- $path
        if ($LASTEXITCODE -eq 0) { $restored += $path }
    }
    if ($restored.Count -gt 0) { Write-Host "Restored the tracked files this run changed: $($restored -join ' ')" }
}

try {
    Write-Host "Running Unity Tests (batchmode) for project: $ProjectPath"
    Write-Host "Unity Version: $UnityVersion"
    Write-Host "Log file: $LogFile"
    Write-Host "---------------------------------------------------------"

    # Unity writes -testResults relative to the project and -logFile relative to the working directory.
    $script:NotPassed = 0
    function Show-Summary($Suite) {
        $xml = "$ProjectPath/$Suite-$ResultsFile"
        if (-not (Test-Path $xml)) {
            Write-Host "  ${Suite}: no result file ($xml) -- the run did not finish; see $Suite-$LogFile"
            return $false
        }
        # The opening tag alone, as the .sh reads it: a file Unity cut short still reports its counts.
        $tag = [regex]::Match((Get-Content -Raw $xml), '<test-run [^>]*>').Value
        $count = { param($name) $m = [regex]::Match($tag, " $name=`"(\d+)`""); if ($m.Success) { [int]$m.Groups[1].Value } else { 0 } }
        $inconclusive = & $count "inconclusive"
        $skipped = & $count "skipped"
        Write-Host "  ${Suite}: $(& $count "total") total, $(& $count "passed") passed, $(& $count "failed") failed, $inconclusive inconclusive, $skipped skipped"
        $script:NotPassed += $inconclusive + $skipped
        return $true
    }
    # A suite that dies before writing its results must not be summarized from the previous run's file.
    function Clear-Results($Suite) {
        Remove-Item -Force -ErrorAction SilentlyContinue "$ProjectPath/$Suite-$ResultsFile"
    }

    # The .ssab fixtures under package/Tests/Runtime/Resources/ are build artifacts converted from the
    # SpriteStudio-SDK sources, not committed files. Only four test classes know how to produce them (their
    # [UnitySetUp] drives the converter); every other class just asserts "Ringo.ssab is required". NUnit runs
    # the consumers first, so on a clean tree the suites below fail long before anything gets generated.
    # Convert the fixtures up front so EditMode / PlayMode / Player all start from a warm tree.
    $FixtureDir = "package/Tests/Runtime/Resources"
    $FixtureFiles = @(
        "$FixtureDir/Ringo/Ringo.ssab",
        "$FixtureDir/Ringo/Ringo_tyu.ssab",
        "$FixtureDir/Sound/Sound.ssab",
        "$FixtureDir/overall/Mask.ssab",
        "$FixtureDir/EventDispatch/UserData.ssab",
        "$FixtureDir/EventDispatch/Signal.ssab"
    )
    $FixtureTests = "SpriteStudio.Runtime.Tests.SpriteStudioRingoPlaybackTests;SpriteStudio.Runtime.Tests.SpriteStudioAudioEventTests;SpriteStudio.Runtime.Tests.SpriteStudioClippingMaskTests;SpriteStudio.Runtime.Tests.SpriteStudioEventDispatchTests"

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
    Clear-Results "editmode"
    $processEdit = Start-Process -FilePath $UnityPath -ArgumentList "-batchmode", "-runTests", "-projectPath", $ProjectPath, "-testPlatform", "editmode", "-testResults", "editmode-$ResultsFile", "-logFile", "editmode-$LogFile", "-assemblyNames", "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" -Wait -PassThru -NoNewWindow
    $ranEdit = Show-Summary "editmode"

    if ($processEdit.ExitCode -ne 0) {
        Write-Error "EditMode Tests failed with exit code $($processEdit.ExitCode)! See editmode-$LogFile for details."
        exit $processEdit.ExitCode
    }
    # Unity can also exit 0 having run nothing -- a startup dialog it cancels in batchmode ends the process
    # before the first test -- so a suite with no result file is a failure too.
    if (-not $ranEdit) {
        Write-Error "EditMode Tests did not run! See editmode-$LogFile for details."
        exit 1
    }

    Write-Host "Running PlayMode Tests..."
    Clear-Results "playmode"
    $processPlay = Start-Process -FilePath $UnityPath -ArgumentList "-batchmode", "-runTests", "-projectPath", $ProjectPath, "-testPlatform", "playmode", "-testResults", "playmode-$ResultsFile", "-logFile", "playmode-$LogFile", "-assemblyNames", "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" -Wait -PassThru -NoNewWindow
    $ranPlay = Show-Summary "playmode"

    if ($processPlay.ExitCode -ne 0) {
        Write-Error "PlayMode Tests failed with exit code $($processPlay.ExitCode)! See playmode-$LogFile for details."
        exit $processPlay.ExitCode
    }
    if (-not $ranPlay) {
        Write-Error "PlayMode Tests did not run! See playmode-$LogFile for details."
        exit 1
    }

    Write-Host "Running Standalone Player Tests..."
    if ($IsMac) {
        $BuildTarget = "StandaloneOSX"
    } else {
        $BuildTarget = "StandaloneWindows64"
    }

    Clear-Results "player"
    # A build target as -testPlatform is what builds and launches a player. "-testPlatform playmode
    # -buildTarget X" only makes X the Editor's active platform and runs PlayMode in the Editor again.
    $processPlayer = Start-Process -FilePath $UnityPath -ArgumentList "-batchmode", "-runTests", "-projectPath", $ProjectPath, "-testPlatform", $BuildTarget, "-testResults", "player-$ResultsFile", "-logFile", "player-$LogFile", "-assemblyNames", "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" -Wait -PassThru -NoNewWindow
    $ranPlayer = Show-Summary "player"

    if ($processPlayer.ExitCode -ne 0) {
        Write-Error "Standalone Player Tests failed with exit code $($processPlayer.ExitCode)! See player-$LogFile for details."
        exit $processPlayer.ExitCode
    }
    if (-not $ranPlayer) {
        Write-Error "Standalone Player Tests did not run! See player-$LogFile for details."
        exit 1
    }

    Write-Host "All tests passed successfully!"
    if ($script:NotPassed -gt 0) {
        Write-Host "But $($script:NotPassed) test(s) were skipped or inconclusive -- a skip is not a pass; see the result files."
    }
    Write-Host "Results saved to $ProjectPath/{editmode,playmode,player}-$ResultsFile"
} finally {
    Restore-Tracked
}
