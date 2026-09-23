#!/usr/bin/env bash
set -e

usage() {
    cat <<'EOF'
Usage: run-tests.sh [--help]
  Run package/Tests headless in Unity batchmode: EditMode, then PlayMode, then
  PlayMode again in a standalone player build. Close the Editor first: batchmode
  cannot take the project lock (use the Unity Test Runner while it is open).

  Needs the Editor examples/UnityTestProject pins in ProjectVersion.txt, with an
  activated licence, and the native plugins plus the csbindgen binding placed by
  scripts/build-runtime.sh (or scripts/download-sdk.sh).

  Each suite prints its counts from its NUnit result file. Skipped and
  inconclusive tests are counted there, never as passes.

  Exit status: 0 every suite passed, 1 a suite failed,
               2 bad arguments or a missing prerequisite.
EOF
}

for arg in "$@"; do
    case "$arg" in
        -h|--help) usage; exit 0 ;;
        *) echo "run-tests.sh: unknown argument: $arg" >&2; usage >&2; exit 2 ;;
    esac
done

# Every path below is relative to the repository root.
cd "$(dirname "$0")/.."

# Detect OS and Project Path
if [ "$(uname)" == "Darwin" ]; then
    OS="mac"
elif [ "$(expr substr $(uname -s) 1 5)" == "MINGW" ] || [ "$(expr substr $(uname -s) 1 4)" == "MSYS" ]; then
    OS="win"
else
    echo "Unsupported OS"
    exit 2
fi

PROJECT_PATH="examples/UnityTestProject"
RESULTS_FILE="test-results.xml"
LOG_FILE="test-run.log"

# Get Unity Version from ProjectSettings
if [ ! -f "$PROJECT_PATH/ProjectSettings/ProjectVersion.txt" ]; then
    echo "Error: ProjectVersion.txt not found at $PROJECT_PATH"
    exit 2
fi
UNITY_VERSION=$(grep 'm_EditorVersion:' "$PROJECT_PATH/ProjectSettings/ProjectVersion.txt" | awk '{print $2}')

# Determine Unity Executable Path
if [ "$OS" == "mac" ]; then
    UNITY_PATH="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
    NATIVE_PLUGINS="package/Runtime/Plugins/macOS/ssruntime.bundle package/Editor/Plugins/macOS/libssconverter.dylib"
elif [ "$OS" == "win" ]; then
    UNITY_PATH="C:/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe"
    NATIVE_PLUGINS="package/Runtime/Plugins/Windows/x86_64/ssruntime.dll package/Editor/Plugins/Windows/x86_64/ssconverter.dll"
fi

if [ ! -f "$UNITY_PATH" ]; then
    echo "Error: Unity executable not found at $UNITY_PATH"
    echo "Please ensure Unity Hub and the required Editor version ($UNITY_VERSION) are installed."
    exit 2
fi

# Without these the project does not run a red suite -- it fails to compile, and batchmode reports
# "Scripts have compiler errors" (CS0234 on SpriteStudio.SsRuntime) with no hint that the cause is an
# unbuilt prerequisite. The binding is the gitignored csbindgen output; Runtime/Format/*.cs is committed.
MISSING_PREREQS=""
for prereq in $NATIVE_PLUGINS package/Runtime/Bindings/SSRuntimeMethods.g.cs; do
    [ -e "$prereq" ] || MISSING_PREREQS="$MISSING_PREREQS $prereq"
done
if [ -n "$MISSING_PREREQS" ]; then
    echo "Error: not built or downloaded yet:$MISSING_PREREQS"
    echo "       Run scripts/build-runtime.sh (or scripts/download-sdk.sh) first."
    exit 2
fi

# Opening the project deletes the tracked .meta of every plugin absent on this machine -- the other
# platforms' binaries, never built here -- and a commit of that deletion ships a package that installs
# them as nothing. Put back what this run changed; a .meta that was already dirty is left alone.
META_BEFORE=$(git status --porcelain -- '*.meta' 2>/dev/null || true)
restore_meta() {
    local f restored=""
    while IFS= read -r f; do
        [ -n "$f" ] || continue
        printf '%s\n' "$META_BEFORE" | grep -qxF -- "$f" && continue
        git checkout -q -- "${f:3}" && restored="$restored ${f:3}"
    done <<<"$(git status --porcelain -- '*.meta' 2>/dev/null | grep -E '^ [MD] ' || true)"
    [ -z "$restored" ] || echo "Restored the tracked .meta this run changed:$restored"
}
trap restore_meta EXIT

echo "Running Unity Tests (batchmode) for project: $PROJECT_PATH"
echo "Unity Version: $UNITY_VERSION"
echo "Log file: $LOG_FILE"
echo "---------------------------------------------------------"

# Unity writes -testResults relative to the project and -logFile relative to the working directory.
NOT_PASSED=0
summarize() {
    local suite="$1" xml="$PROJECT_PATH/$1-$RESULTS_FILE" run total passed failed inconclusive skipped
    run=$(grep -o -m 1 '<test-run [^>]*>' "$xml" 2>/dev/null) || {
        echo "  $suite: no result file ($xml) -- the run did not finish; see $suite-$LOG_FILE"
        return 0
    }
    attr() { echo "$run" | grep -o " $1=\"[0-9]*\"" | grep -o '[0-9][0-9]*' || echo 0; }
    total=$(attr total); passed=$(attr passed); failed=$(attr failed)
    inconclusive=$(attr inconclusive); skipped=$(attr skipped)
    echo "  $suite: $total total, $passed passed, $failed failed, $inconclusive inconclusive, $skipped skipped"
    NOT_PASSED=$((NOT_PASSED + inconclusive + skipped))
}
# A suite that dies before writing its results must not be summarized from the previous run's file.
clear_results() { rm -f "$PROJECT_PATH/$1-$RESULTS_FILE"; }

# The .ssab fixtures under package/Tests/Runtime/Resources/ are build artifacts converted from the
# SpriteStudio-SDK sources, not committed files. Only three test classes know how to produce them (their
# [UnitySetUp] drives the converter); every other class just asserts "Ringo.ssab is required". NUnit runs
# the consumers first, so on a clean tree the suites below fail long before anything gets generated.
# Convert the fixtures up front so EditMode / PlayMode / Player all start from a warm tree.
FIXTURE_DIR="package/Tests/Runtime/Resources"
FIXTURE_FILES="$FIXTURE_DIR/Ringo/Ringo.ssab $FIXTURE_DIR/Ringo/Ringo_tyu.ssab $FIXTURE_DIR/Sound/Sound.ssab $FIXTURE_DIR/overall/Mask.ssab"
FIXTURE_TESTS="SpriteStudio.Runtime.Tests.SpriteStudioRingoPlaybackTests;SpriteStudio.Runtime.Tests.SpriteStudioAudioEventTests;SpriteStudio.Runtime.Tests.SpriteStudioClippingMaskTests"

MISSING_FIXTURES=""
for fixture in $FIXTURE_FILES; do
    [ -f "$fixture" ] || MISSING_FIXTURES="$MISSING_FIXTURES $fixture"
done

if [ -n "$MISSING_FIXTURES" ]; then
    echo "Generating test fixtures from the SpriteStudio-SDK sources..."
    # The generators are themselves tests, and they fail while the fixture they build is still missing.
    # Ignore this pass's exit code: the real suites below are what report pass/fail.
    "$UNITY_PATH" -batchmode -runTests -projectPath "$PROJECT_PATH" -testPlatform playmode -testResults "fixtures-$RESULTS_FILE" -logFile "fixtures-$LOG_FILE" -assemblyNames "SpriteStudio.Runtime.Tests" -testFilter "$FIXTURE_TESTS" || true

    STILL_MISSING=""
    for fixture in $FIXTURE_FILES; do
        [ -f "$fixture" ] || STILL_MISSING="$STILL_MISSING $fixture"
    done
    if [ -n "$STILL_MISSING" ]; then
        echo "Warning: these fixtures could not be generated:$STILL_MISSING"
        echo "         Conversion needs the SpriteStudio-SDK submodule ('git submodule update --init')."
        echo "         See fixtures-$LOG_FILE. Continuing; the suites below will report the failures."
    fi
else
    echo "Test fixtures already present; skipping the conversion pass."
fi

echo "Running EditMode Tests..."
clear_results editmode
"$UNITY_PATH" -batchmode -runTests -projectPath "$PROJECT_PATH" -testPlatform editmode -testResults "editmode-$RESULTS_FILE" -logFile "editmode-$LOG_FILE" -assemblyNames "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" || {
    summarize editmode
    echo "EditMode Tests failed! See editmode-$LOG_FILE for details."
    exit 1
}
summarize editmode

echo "Running PlayMode Tests..."
clear_results playmode
"$UNITY_PATH" -batchmode -runTests -projectPath "$PROJECT_PATH" -testPlatform playmode -testResults "playmode-$RESULTS_FILE" -logFile "playmode-$LOG_FILE" -assemblyNames "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" || {
    summarize playmode
    echo "PlayMode Tests failed! See playmode-$LOG_FILE for details."
    exit 1
}
summarize playmode

echo "Running Standalone Player Tests..."
if [ "$OS" == "mac" ]; then
    BUILD_TARGET="StandaloneOSX"
else
    BUILD_TARGET="StandaloneWindows64"
fi
clear_results player

"$UNITY_PATH" -batchmode -runTests -projectPath "$PROJECT_PATH" -testPlatform playmode -buildTarget "$BUILD_TARGET" -testResults "player-$RESULTS_FILE" -logFile "player-$LOG_FILE" -assemblyNames "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" || {
    summarize player
    echo "Standalone Player Tests failed! See player-$LOG_FILE for details."
    exit 1
}
summarize player

echo "All tests passed successfully!"
if [ "$NOT_PASSED" -gt 0 ]; then
    echo "But $NOT_PASSED test(s) were skipped or inconclusive -- a skip is not a pass; see the result files."
fi
echo "Results saved to $PROJECT_PATH/{editmode,playmode,player}-$RESULTS_FILE"
