#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
DEVICE="${1:?Usage: tools/quest-room-setup.sh DEVICE_SERIAL}"
PKG="com.magicrayality.amiss"
ACTIVITY="$PKG/com.unity3d.player.UnityPlayerGameActivity"
ADB=(adb -s "$DEVICE")
EVIDENCE="/sdcard/Android/data/$PKG/files/room-evidence.json"
mkdir -p build

quest-power prepare questpro
trap 'quest-power cleanup questpro' EXIT

"${ADB[@]}" shell am force-stop "$PKG"
"${ADB[@]}" shell rm -f "/sdcard/Android/data/$PKG/files/room-command.txt"
"${ADB[@]}" shell am start -n "$ACTIVITY" >/dev/null
sleep 12
tools/quest-room-command.sh "$DEVICE" setup
echo "Debug setup driver started; app-side transitions are automatic."

OUTCOME=""
for _ in $(seq 1 300); do
    sleep 2
    OUTCOME="$("${ADB[@]}" exec-out cat "$EVIDENCE" 2>/dev/null | python3 -c '
import json, sys
try:
    entries = json.load(sys.stdin).get("entries", [])
except (ValueError, AttributeError):
    entries = []
for entry in reversed(entries):
    if entry.get("scenario_id") == "setup_automation":
        print(entry.get("outcome", ""))
        break
' || true)"
    case "$OUTCOME" in
        complete)
            "${ADB[@]}" exec-out cat "$EVIDENCE" > build/quest-room-setup.json
            echo "Room setup complete; sanitized evidence: build/quest-room-setup.json"
            exit 0
            ;;
        restricted|confirmation_refused|setup_refused)
            "${ADB[@]}" exec-out cat "$EVIDENCE" > build/quest-room-setup.json
            echo "Room setup stopped: $OUTCOME; evidence: build/quest-room-setup.json" >&2
            exit 1
            ;;
    esac
done

"${ADB[@]}" exec-out cat "$EVIDENCE" > build/quest-room-setup.json
echo "Room setup timed out; evidence: build/quest-room-setup.json" >&2
exit 1
