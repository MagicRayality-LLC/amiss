#!/usr/bin/env bash
# One-shot dev loop for the Amiss shell: build the APK, install it on a
# connected Quest headset, and launch it.
#
# Usage:
#   tools/build-install-launch.sh                    # build + install + launch
#   tools/build-install-launch.sh --no-build        # reuse the existing APK
#   tools/build-install-launch.sh --no-launch       # build + install only
#   tools/build-install-launch.sh --device SERIAL   # pick the target headset
#
# Environment overrides: UNITY (editor path), ADB (adb path),
# ANDROID_SERIAL (same as --device). Only adb 'device' state counts.
set -euo pipefail
cd "$(dirname "$0")/.."

BUILD=1
LAUNCH=1
DEVICE="${ANDROID_SERIAL:-}"
while [ $# -gt 0 ]; do
    case "$1" in
        --no-build) BUILD=0 ;;
        --no-launch) LAUNCH=0 ;;
        --device|-s)
            [ $# -lt 2 ] && { echo "Option $1 needs a device serial" >&2; exit 2; }
            DEVICE="$2"; shift ;;
        --device=*|-s=*) DEVICE="${1#*=}" ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

ROOT="$(pwd)"
APK="$ROOT/Builds/Amiss.apk"
PKG="com.magicrayality.amiss"
ACTIVITY="$PKG/com.unity3d.player.UnityPlayerGameActivity"
ADB="${ADB:-adb}"

SERIALS=()
MODELS=()
while IFS=$'\t' read -r serial model; do
    SERIALS+=("$serial")
    MODELS+=("$model")
done < <("$ADB" devices -l | awk '$2 == "device" {
  model = "-"
  for (i = 3; i <= NF; i++)
    if (substr($i, 1, 6) == "model:") { model = substr($i, 7); break }
  print $1 "\t" model
}')
if [ "${#SERIALS[@]}" -eq 0 ]; then
    echo "No headset attached in adb 'device' state — run 'adb devices' first." >&2
    exit 1
fi
MODEL=""
if [ -n "$DEVICE" ]; then
    found=0
    for i in "${!SERIALS[@]}"; do
        if [ "${SERIALS[$i]}" = "$DEVICE" ]; then
            MODEL="${MODELS[$i]}"; found=1; break
        fi
    done
    [ "$found" = 1 ] || { echo "Device '$DEVICE' is not attached:" >&2
        for i in "${!SERIALS[@]}"; do echo "  ${SERIALS[$i]}  (${MODELS[$i]})" >&2; done
        exit 2; }
elif [ "${#SERIALS[@]}" -gt 1 ]; then
    echo "Multiple headsets attached — pick one with --device SERIAL:" >&2
    for i in "${!SERIALS[@]}"; do echo "  ${SERIALS[$i]}  (${MODELS[$i]})" >&2; done
    exit 2
else
    DEVICE="${SERIALS[0]}"
    MODEL="${MODELS[0]}"
fi
ADB_TARGET=("$ADB" -s "$DEVICE")
echo "==> Target device: $DEVICE ($MODEL)"

if [ "$BUILD" = 1 ]; then
    if [ -f "$ROOT/Temp/UnityLockfile" ]; then
        echo "Unity Editor has this project open (Temp/UnityLockfile exists). Close it first, or re-run with --no-build." >&2
        exit 1
    fi
    tools/build-apk.sh
fi
[ -f "$APK" ] || { echo "APK missing: $APK (build first?)" >&2; exit 1; }

echo "==> Installing $APK"
if ! INSTALL_OUT="$("${ADB_TARGET[@]}" install -r "$APK" 2>&1)"; then
    printf '%s\n' "$INSTALL_OUT"
    if printf '%s' "$INSTALL_OUT" | grep -q "INSTALL_FAILED_UPDATE_INCOMPATIBLE"; then
        echo "==> Key mismatch: installed $PKG is signed with a different key." >&2
        echo "    The one-time fix is an uninstall — this wipes the app's on-device data:" >&2
        echo "    $ADB -s $DEVICE uninstall $PKG && $ADB -s $DEVICE install -r $APK" >&2
    fi
    exit 1
fi
printf '%s\n' "$INSTALL_OUT"

if [ "$LAUNCH" = 1 ]; then
    state="$("${ADB_TARGET[@]}" shell dumpsys power 2>/dev/null \
        | grep -m1 'mWakefulness=' | sed 's/.*mWakefulness=//' | tr -d '\r' || true)"
    case "$state" in
        Asleep|Dozing|"")
            echo "==> Headset is ${state:-asleep} — waking it (a locked screen drops launches)."
            "${ADB_TARGET[@]}" shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1 || true
            "${ADB_TARGET[@]}" shell wm dismiss-keyguard >/dev/null 2>&1 || true
            sleep 3
            ;;
    esac

    echo "==> Launching $ACTIVITY"
    LAUNCH_ANCHOR="$("${ADB_TARGET[@]}" shell "logcat -d -v time" 2>/dev/null \
        | grep -aE '^[0-9][0-9]-' | tail -1 | cut -c1-18 | tr -d '\r' || true)"
    "${ADB_TARGET[@]}" shell am start -n "$ACTIVITY"

    pid=""
    for _ in $(seq 1 15); do
        pid="$("${ADB_TARGET[@]}" shell pidof "$PKG" 2>/dev/null | tr -d '\r' || true)"
        [ -n "$pid" ] && break
        sleep 1
    done
    if [ -z "$pid" ]; then
        cached="$("${ADB_TARGET[@]}" shell "logcat -d -v time" 2>/dev/null \
            | grep -a "Caching launch" || true)"
        if [ -n "$cached" ]; then
            echo "LAUNCH BLOCKED BY THE OS: the launch was cached (a dialog or the lock" >&2
            echo "screen was showing), so $PKG never started. Wake/unlock the headset and" >&2
            echo "tap the app icon again; the cached request is not replayed." >&2
        else
            echo "LAUNCH FAILED: $PKG has no process 15 s after 'am start'." >&2
            echo "Check the headset screen for a system dialog, or run 'adb logcat'." >&2
        fi
        exit 1
    fi
    echo "    Process up: pid $pid"
    sleep 8
    identity="$("${ADB_TARGET[@]}" shell "logcat -d -v time" 2>/dev/null \
        | grep -a "\[Amiss\] build-identity" | tail -1 || true)"
    if [ -n "$identity" ]; then
        echo "    $identity"
    else
        echo "    Warning: no [Amiss] build-identity line in logcat." >&2
    fi
fi
echo "Done."