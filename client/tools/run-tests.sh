#!/usr/bin/env bash
# Run the Amiss edit-mode test suite headlessly. The tests read
# ../schema/versions.json relative to this Unity project, so run them from
# inside a checkout that still has schema/ next to client/.
set -euo pipefail
cd "$(dirname "$0")/.."
source tools/unity.sh
mkdir -p build
if [ -f "Temp/UnityLockfile" ]; then
    echo "Unity Editor has this project open (Temp/UnityLockfile exists). Close it first." >&2
    exit 1
fi
echo "==> Running Amiss edit-mode tests"
rm -f build/edit-mode-tests.xml
set +e
"$UNITY" -batchmode -nographics -buildTarget Android \
    -projectPath "$(pwd)" -runTests -testPlatform EditMode \
    -testResults build/edit-mode-tests.xml -logFile build/unity-tests.log
TEST_RC=$?
set -e
set +e
"$UNITY" -batchmode -nographics -quit -buildTarget Android \
    -projectPath "$(pwd)" -executeMethod Amiss.EditorTools.AmissProjectSetup.ScrubBuildMachineAddress \
    -logFile build/unity-test-scrub.log
SCRUB_RC=$?
set -e
if [ "$SCRUB_RC" -ne 0 ]; then
    echo "Post-test settings scrub failed (exit $SCRUB_RC)" >&2
    exit 1
fi
if [ "$TEST_RC" -ne 0 ]; then
    echo "Tests failed (exit $TEST_RC) — tail of build/unity-tests.log:" >&2
    tail -30 build/unity-tests.log >&2 || true
    exit 1
fi
grep -o 'testcasecount="[0-9]*"\|passed="[0-9]*"\|failed="[0-9]*"' build/edit-mode-tests.xml | head -3
echo "Test results: build/edit-mode-tests.xml"