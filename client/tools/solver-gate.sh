#!/usr/bin/env bash
# Run the development-build solver gate on a Quest headset.
#
# Usage:
#   tools/solver-gate.sh DEVICE_SERIAL              # build, install, run
#   tools/solver-gate.sh DEVICE_SERIAL --no-build   # reuse Builds/Amiss.apk
#   OUT_DIR=build/solver-gate-hard-sphere tools/solver-gate.sh DEVICE_SERIAL
#
# The gate harness is compiled only into DEVELOPMENT_BUILD and starts only
# when its request file appears, so this script builds the development APK,
# installs it, launches the shell, triggers the harness, waits for the
# completion marker, and pulls every JSON gate record into OUT_DIR (default
# build/solver-gate). Stale device records are removed first so one record set
# never mixes two writer runs. The rig is prepared before and cleaned up
# after, including on failure.
set -euo pipefail
cd "$(dirname "$0")/.."

DEVICE="${1:?Usage: tools/solver-gate.sh DEVICE_SERIAL [--no-build]}"
shift
BUILD=1
case "${1:-}" in
    --no-build) BUILD=0 ;;
    "") ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
esac

PKG="com.magicrayality.amiss"
ACTIVITY="$PKG/com.unity3d.player.UnityPlayerGameActivity"
DIR="/sdcard/Android/data/$PKG/files"
OUT="${OUT_DIR:-build/solver-gate}"
WAIT_SECONDS="${WAIT_SECONDS:-900}"
ADB=(adb -s "$DEVICE")

quest-power prepare questpro
TMP="$(mktemp)"
trap 'rm -f "$TMP"; quest-power cleanup questpro' EXIT

if [ "$BUILD" = 1 ]; then
    tools/build-apk.sh --development
fi
[ -f Builds/Amiss.apk ] || { echo "APK missing: Builds/Amiss.apk" >&2; exit 1; }

"${ADB[@]}" install -r Builds/Amiss.apk
"${ADB[@]}" shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1 || true
"${ADB[@]}" shell wm dismiss-keyguard >/dev/null 2>&1 || true
"${ADB[@]}" shell am force-stop "$PKG"
"${ADB[@]}" shell "rm -f $DIR/solver-gate-request.txt $DIR/solver-gate-complete.txt $DIR/solver-gate-*.json" || true
printf 'run\n' > "$TMP"

echo "==> Launching the development build"
"${ADB[@]}" shell am start -n "$ACTIVITY" >/dev/null
sleep 15
"${ADB[@]}" push "$TMP" "$DIR/solver-gate-request.txt.tmp" >/dev/null
"${ADB[@]}" shell mv "$DIR/solver-gate-request.txt.tmp" "$DIR/solver-gate-request.txt"

echo "==> Waiting for the gate (up to ${WAIT_SECONDS}s)"
deadline=$((SECONDS + WAIT_SECONDS))
while [ $SECONDS -lt $deadline ]; do
    if "${ADB[@]}" shell "test -f $DIR/solver-gate-complete.txt" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done
if ! "${ADB[@]}" shell "test -f $DIR/solver-gate-complete.txt" >/dev/null 2>&1; then
    echo "Solver gate did not finish; last Unity log lines:" >&2
    "${ADB[@]}" logcat -d -s Unity:I 2>/dev/null | tail -60 >&2 || true
    exit 1
fi

mkdir -p "$OUT"
"${ADB[@]}" exec-out cat "$DIR/solver-gate-complete.txt" > "$OUT/complete.txt"
for name in $("${ADB[@]}" shell "ls $DIR" | tr -d '\r' | grep '^solver-gate-.*\.json$'); do
    "${ADB[@]}" exec-out cat "$DIR/$name" > "$OUT/$name"
done
# Remove the request file so the next app launch cannot re-trigger the
# harness; one record set per execution, never a silent second run.
"${ADB[@]}" shell "rm -f $DIR/solver-gate-request.txt" || true
"${ADB[@]}" logcat -d -s Unity:I > "$OUT/logcat.txt" 2>/dev/null || true
echo "==> $(cat "$OUT/complete.txt" | head -1)"
echo "Gate records: $OUT"
