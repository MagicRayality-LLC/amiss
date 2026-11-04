#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
DEVICE="${1:?Usage: tools/quest-room-command.sh DEVICE_SERIAL COMMAND}"
COMMAND="${2:?Usage: tools/quest-room-command.sh DEVICE_SERIAL COMMAND}"
case "$COMMAND" in
    setup|stop_setup|select|primary|deny_permission|no_scene|no_current_room|cancel_setup|load_scene|confirm|accept|manual|supplement|rescan|scene_update|relocalize|relocate|wall_fixture|toggle_walls|panel_state|list_state|panel_probe|head_panel|overlay_probe|panel_texture|passthrough_off|passthrough_on|render_probe|focus_lost|focus_return|resume|safety_hold|reconfirm|state|exit) ;;
    *) echo "Unknown room command: $COMMAND" >&2; exit 2 ;;
esac
PKG="com.magicrayality.amiss"
DIR="/sdcard/Android/data/$PKG/files"
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT
printf '%s#%s\n' "$COMMAND" "$(date +%s%N)" > "$TMP"
adb -s "$DEVICE" shell mkdir -p "$DIR"
adb -s "$DEVICE" push "$TMP" "$DIR/room-command.txt.tmp" >/dev/null
adb -s "$DEVICE" shell mv "$DIR/room-command.txt.tmp" "$DIR/room-command.txt"
