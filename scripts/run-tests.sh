#!/usr/bin/env bash
set -e

# Detect OS and Project Path
if [ "$(uname)" == "Darwin" ]; then
    OS="mac"
elif [ "$(expr substr $(uname -s) 1 5)" == "MINGW" ] || [ "$(expr substr $(uname -s) 1 4)" == "MSYS" ]; then
    OS="win"
else
    echo "Unsupported OS"
    exit 1
fi

PROJECT_PATH="examples/UnityTestProject"
RESULTS_FILE="test-results.xml"
LOG_FILE="test-run.log"

# Get Unity Version from ProjectSettings
if [ ! -f "$PROJECT_PATH/ProjectSettings/ProjectVersion.txt" ]; then
    echo "Error: ProjectVersion.txt not found at $PROJECT_PATH"
    exit 1
fi
UNITY_VERSION=$(grep 'm_EditorVersion:' "$PROJECT_PATH/ProjectSettings/ProjectVersion.txt" | awk '{print $2}')

# Determine Unity Executable Path
if [ "$OS" == "mac" ]; then
    UNITY_PATH="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
elif [ "$OS" == "win" ]; then
    UNITY_PATH="C:/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe"
fi

if [ ! -f "$UNITY_PATH" ]; then
    echo "Error: Unity executable not found at $UNITY_PATH"
    echo "Please ensure Unity Hub and the required Editor version ($UNITY_VERSION) are installed."
    exit 1
fi

echo "Running Unity Tests (batchmode) for project: $PROJECT_PATH"
echo "Unity Version: $UNITY_VERSION"
echo "Log file: $LOG_FILE"
echo "---------------------------------------------------------"

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
"$UNITY_PATH" -batchmode -runTests -projectPath "$PROJECT_PATH" -testPlatform editmode -testResults "editmode-$RESULTS_FILE" -logFile "editmode-$LOG_FILE" -assemblyNames "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" || {
    echo "EditMode Tests failed! See editmode-$LOG_FILE for details."
    exit 1
}

echo "Running PlayMode Tests..."
"$UNITY_PATH" -batchmode -runTests -projectPath "$PROJECT_PATH" -testPlatform playmode -testResults "playmode-$RESULTS_FILE" -logFile "playmode-$LOG_FILE" -assemblyNames "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" || {
    echo "PlayMode Tests failed! See playmode-$LOG_FILE for details."
    exit 1
}

echo "Running Standalone Player Tests..."
if [ "$OS" == "mac" ]; then
    BUILD_TARGET="StandaloneOSX"
else
    BUILD_TARGET="StandaloneWindows64"
fi

"$UNITY_PATH" -batchmode -runTests -projectPath "$PROJECT_PATH" -testPlatform playmode -buildTarget "$BUILD_TARGET" -testResults "player-$RESULTS_FILE" -logFile "player-$LOG_FILE" -assemblyNames "SpriteStudio.Runtime.Tests;SpriteStudio.Runtime.EditModeTests;SpriteStudio.Editor.Tests" || {
    echo "Standalone Player Tests failed! See player-$LOG_FILE for details."
    exit 1
}

echo "All tests passed successfully!"
echo "Results saved to editmode-$RESULTS_FILE, playmode-$RESULTS_FILE, and player-$RESULTS_FILE"
