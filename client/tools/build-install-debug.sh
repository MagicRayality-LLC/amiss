#!/usr/bin/env bash
# Public one-shot loop: build the debug APK, install it on the attached
# headset, and launch it. This is the judge-facing path and needs no company
# credential: the APK is Builds/Amiss-debug.apk, built by tools/build-debug.sh
# with Unity debug signing and the custom keystore disabled. No store or
# release package is produced or installed.
#
# Device selection resolves before the build, so a wrong or ambiguous target
# fails fast: one attached headset is used directly, several get an
# interactive numbered picker, and nothing is installed without one target.
# --list prints the attached headsets and exits without building anything.
#
# Usage:
#   tools/build-install-debug.sh                  # build + install + launch
#   tools/build-install-debug.sh --no-build       # reuse Builds/Amiss-debug.apk
#   tools/build-install-debug.sh --no-launch      # build + install only
#   tools/build-install-debug.sh --device SERIAL  # target one of several headsets
#   tools/build-install-debug.sh --list           # show attached headsets, exit
#
# Environment overrides: ADB (adb path), UNITY (editor path), and
# ANDROID_SERIAL (same as --device).
set -euo pipefail
cd "$(dirname "$0")/.."

BUILD=1
LAUNCH=1
LIST=0
DEVICE="${ANDROID_SERIAL:-}"
while [ $# -gt 0 ]; do
    case "$1" in
        --no-build) BUILD=0 ;;
        --no-launch) LAUNCH=0 ;;
        --list|-l) LIST=1 ;;
        --device|-s)
            [ $# -lt 2 ] && { echo "Option $1 needs a device serial" >&2; exit 2; }
            DEVICE="$2"; shift ;;
        --device=*|-s=*) DEVICE="${1#*=}" ;;
        --help|-h)
            cat <<'EOF'
Usage:
  tools/build-install-debug.sh                  # build + install + launch
  tools/build-install-debug.sh --no-build       # reuse Builds/Amiss-debug.apk
  tools/build-install-debug.sh --no-launch      # build + install only
  tools/build-install-debug.sh --device SERIAL  # target one of several headsets
  tools/build-install-debug.sh --list           # show attached headsets, exit

Builds Builds/Amiss-debug.apk with Unity debug signing, then installs it on the
attached headset and launches it. No store or release package is built or
installed, and no company credential is used. Set UNITY or ADB to override the
editor or adb path.
EOF
            exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

ROOT="$(pwd)"
APK="$ROOT/Builds/Amiss-debug.apk"
PKG="com.magicrayality.amiss"
ACTIVITY="$PKG/com.unity3d.player.UnityPlayerGameActivity"
ADB="${ADB:-adb}"

source tools/device-select.sh
if [ "$LIST" = 1 ]; then
    echo "Attached headsets:"
    amiss_list_devices || exit 1
    exit 0
fi
amiss_select_device || exit $?
ADB_TARGET=("$ADB" -s "$DEVICE")
echo "==> Target device: $DEVICE ($MODEL)"

if [ "$BUILD" = 1 ]; then
    tools/build-debug.sh
fi
[ -f "$APK" ] || { echo "APK missing: $APK (build first?)" >&2; exit 1; }

echo "==> Installing $APK"
if ! INSTALL_OUT="$("${ADB_TARGET[@]}" install -r "$APK" 2>&1)"; then
    printf '%s\n' "$INSTALL_OUT"
    if printf '%s' "$INSTALL_OUT" | grep -q "INSTALL_FAILED_UPDATE_INCOMPATIBLE"; then
        echo "==> Key mismatch: the installed $PKG was signed with a different key." >&2
        echo "    Android cannot update across keys. The one-time fix is a manual uninstall," >&2
        echo "    which wipes the app's on-device data — this script never does it for you:" >&2
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
    "${ADB_TARGET[@]}" shell am start -n "$ACTIVITY"

    pid=""
    for _ in $(seq 1 15); do
        pid="$("${ADB_TARGET[@]}" shell pidof "$PKG" 2>/dev/null | tr -d '\r' || true)"
        [ -n "$pid" ] && break
        sleep 1
    done
    if [ -z "$pid" ]; then
        echo "LAUNCH FAILED: $PKG has no process 15 s after 'am start'." >&2
        echo "Check the headset screen for a system dialog, or run 'adb logcat'." >&2
        exit 1
    fi
    echo "    Process up: pid $pid"
fi
echo "Done."
