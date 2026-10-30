#!/usr/bin/env bash
# Build the Amiss Quest 3 APK (IL2CPP, ARM64) from the command line.
set -euo pipefail
cd "$(dirname "$0")/.."
source tools/unity.sh
mkdir -p build
if [ -f "Temp/UnityLockfile" ]; then
    echo "Unity Editor has this project open (Temp/UnityLockfile exists). Close it first." >&2
    exit 1
fi
echo "==> Building Builds/Amiss.apk (Unity batchmode, $(basename "$(dirname "$(dirname "$UNITY")")"))"
set +e
"$UNITY" -batchmode -nographics -quit -buildTarget Android \
    -projectPath "$(pwd)" -executeMethod Amiss.EditorTools.AmissBuild.ConfigureAndBuild \
    -logFile build/unity-build.log
BUILD_RC=$?
set -e
if [ "$BUILD_RC" -ne 0 ] || ! grep -q "Build result: Succeeded" build/unity-build.log 2>/dev/null; then
    echo "Unity build failed (exit $BUILD_RC) — tail of build/unity-build.log:" >&2
    tail -30 build/unity-build.log >&2 || true
    exit 1
fi
grep -E "^\[Amiss\] Build result" build/unity-build.log | sed 's/^/    /'
[ -f Builds/Amiss.apk ] || { echo "APK missing: Builds/Amiss.apk" >&2; exit 1; }
echo "APK: Builds/Amiss.apk"