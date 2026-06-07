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
