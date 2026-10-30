#!/usr/bin/env bash
# Idempotent Amiss Unity project setup: configures player settings, the
# OpenXR/Meta XR wiring, the URP asset, the Android manifest, and the shell
# scene. Run once after cloning, and after any manifest change.
set -euo pipefail
cd "$(dirname "$0")/.."
source tools/unity.sh
mkdir -p build
echo "==> Setting up the Amiss Unity project ($(basename "$(dirname "$(dirname "$UNITY")")"))"
"$UNITY" -batchmode -nographics -quit -buildTarget Android \
    -projectPath "$(pwd)" -executeMethod Amiss.EditorTools.AmissProjectSetup.Setup \
    -logFile build/unity-setup.log