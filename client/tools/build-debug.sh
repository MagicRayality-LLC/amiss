#!/usr/bin/env bash
# Build only the debug APK from the public Unity project. No custom keystore,
# store package, release build, connected device, or adb installation is used.
set -euo pipefail

if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; then
    echo "Usage: tools/build-debug.sh"
    echo "Build Builds/Amiss-debug.apk with Unity debug signing."
    echo "Set UNITY to the Unity 6000.6.5f1 executable if auto-detection fails."
    exit 0
fi
if [ $# -gt 0 ]; then
    echo "Unknown option: $1 (this script builds only the debug APK)" >&2
    exit 2
fi

cd "$(dirname "$0")/.."
UNITY_VERSION="6000.6.5f1"
if [ -n "${UNITY:-}" ]; then
    CANDIDATES=("$UNITY")
else
    CANDIDATES=(
        "$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity"
        "$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe"
        "/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
        "/c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe"
    )
fi

UNITY_PATH=""
for CANDIDATE in "${CANDIDATES[@]}"; do
    if [ -x "$CANDIDATE" ]; then
        UNITY_PATH="$CANDIDATE"
        break
    fi
done
if [ -z "$UNITY_PATH" ]; then
    echo "Unity $UNITY_VERSION not found." >&2
    echo "Set UNITY to its executable path, then re-run tools/build-debug.sh." >&2
    exit 1
fi
if [ -f "Temp/UnityLockfile" ]; then
    echo "Unity Editor has this project open (Temp/UnityLockfile exists). Close it first." >&2
    exit 1
fi

mkdir -p build
echo "==> Building Builds/Amiss-debug.apk (debug signing only)"
set +e
"$UNITY_PATH" -batchmode -nographics -quit -buildTarget Android \
    -projectPath "$(pwd)" \
    -executeMethod Amiss.EditorTools.AmissBuild.ConfigureAndBuildDebug \
    -logFile build/unity-debug-build.log
BUILD_RC=$?
set -e
if [ "$BUILD_RC" -ne 0 ] || ! grep -q "Build result: Succeeded" build/unity-debug-build.log 2>/dev/null; then
    echo "Unity debug build failed (exit $BUILD_RC) — tail of build/unity-debug-build.log:" >&2
    tail -30 build/unity-debug-build.log >&2 || true
    exit 1
fi
[ -f "Builds/Amiss-debug.apk" ] || {
    echo "APK missing: Builds/Amiss-debug.apk" >&2
    exit 1
}
echo "Debug APK: Builds/Amiss-debug.apk (Unity debug signing; no custom keystore)"
