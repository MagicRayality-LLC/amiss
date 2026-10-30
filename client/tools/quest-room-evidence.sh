#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
DEVICE="${1:?Usage: tools/quest-room-evidence.sh DEVICE_SERIAL}"
PKG="com.magicrayality.amiss"
ACTIVITY="$PKG/com.unity3d.player.UnityPlayerGameActivity"
ADB=(adb -s "$DEVICE")
mkdir -p build
SEQUENCE=0

quest-power prepare quest3
trap 'quest-power cleanup quest3' EXIT

command_room() {
    SEQUENCE=$((SEQUENCE + 1))
    "${ADB[@]}" shell am start -n "$ACTIVITY" \
        --es amiss_room_command "$1#$SEQUENCE" >/dev/null
    sleep 2
}

save_evidence() {
    "${ADB[@]}" exec-out cat "/sdcard/Android/data/$PKG/files/room-evidence.json" > "$1"
}

"${ADB[@]}" shell pm revoke "$PKG" com.oculus.permission.USE_SCENE || true
"${ADB[@]}" shell am force-stop "$PKG"
"${ADB[@]}" shell am start -n "$ACTIVITY" >/dev/null
sleep 15
command_room select
command_room deny_permission
command_room state
command_room manual
command_room confirm
command_room accept
command_room state
command_room scene_update
command_room relocalize
command_room relocate
command_room safety_hold
command_room reconfirm
command_room resume
command_room focus_lost
command_room focus_return
command_room resume
command_room state
save_evidence build/room-evidence-manual.json

command_room rescan
command_room no_scene
command_room state
save_evidence build/room-evidence-no-scene.json

"${ADB[@]}" shell pm grant "$PKG" com.oculus.permission.USE_SCENE
command_room rescan
command_room resume
command_room primary
command_room state
command_room primary
sleep 3
"${ADB[@]}" shell input keyevent KEYCODE_BACK || true
sleep 5
command_room state
command_room rescan
command_room resume
command_room primary
command_room load_scene
command_room state
save_evidence build/room-evidence-os.json
command_room confirm
command_room accept
command_room state
save_evidence build/room-evidence-final.json

echo "Sanitized evidence written under client/build/room-evidence-*.json"
