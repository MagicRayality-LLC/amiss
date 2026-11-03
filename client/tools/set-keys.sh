#!/usr/bin/env bash
# Send the evaluation keys to the headset from this PC, so nobody has to
# type a long key on the VR keyboard. The values are read without echo,
# written to a one-time import file in the app's own files directory, and
# deleted by the app on first read; they are never passed as command-line
# arguments, printed, logged, or stored in this repository. The import is
# a debug-build (evaluation) path only.
#
# Usage:
#   tools/set-keys.sh                  # prompt for both keys, send them
#   tools/set-keys.sh --device SERIAL  # target one of several headsets
#   tools/set-keys.sh --list           # show attached headsets, exit
#
# An empty answer skips that key. Environment overrides: ADB (adb path),
# ANDROID_SERIAL (same as --device), and AMISS_META_MODEL_API_KEY /
# AMISS_WIT_CLIENT_TOKEN (when set non-empty, that prompt is skipped;
# useful with a password-manager CLI that supplies either variable).
set -euo pipefail
cd "$(dirname "$0")/.."

LIST=0
DEVICE="${ANDROID_SERIAL:-}"
while [ $# -gt 0 ]; do
    case "$1" in
        --list|-l) LIST=1 ;;
        --device|-s)
            [ $# -lt 2 ] && { echo "Option $1 needs a device serial" >&2; exit 2; }
            DEVICE="$2"; shift ;;
        --device=*|-s=*) DEVICE="${1#*=}" ;;
        --help|-h)
            cat <<'EOF'
Usage:
  tools/set-keys.sh                  # prompt for both keys, send them
  tools/set-keys.sh --device SERIAL  # target one of several headsets
  tools/set-keys.sh --list           # show attached headsets, exit

Prompts (input hidden) for a Meta Model API key and a Wit client token,
then sends both to the debug build over USB as a one-time import file
that the app deletes on first read. An empty answer skips that key.
Values are never arguments, printed, logged, or written to the repo.
EOF
            exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

PKG="com.magicrayality.amiss"
DIR="/sdcard/Android/data/$PKG/files"
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

if [ -z "$("${ADB_TARGET[@]}" shell pm path "$PKG" 2>/dev/null | tr -d '\r')" ]; then
    echo "Amiss ($PKG) is not installed on $DEVICE — run tools/build-install-debug.sh first." >&2
    exit 1
fi

trim() {
    local value="$1"
    value="${value#"${value%%[![:space:]]*}"}"
    value="${value%"${value##*[![:space:]]}"}"
    printf '%s' "$value"
}

# Reject on the PC anything the on-device parser would refuse, so a bad
# paste is reported here instead of only on the headset. Never prints the value.
validate_value() {
    local label="$1" value="$2"
    if [ -n "$value" ] && { [ "${#value}" -gt 512 ] || [[ "$value" =~ [[:space:][:cntrl:]] ]]; }; then
        echo "$label must be a single token of at most 512 characters with no whitespace - nothing sent." >&2
        exit 1
    fi
}

META_VALUE=""
WIT_VALUE=""
META_VALUE="$(trim "${AMISS_META_MODEL_API_KEY-}")"
WIT_VALUE="$(trim "${AMISS_WIT_CLIENT_TOKEN-}")"
if [ -z "$META_VALUE" ]; then
    printf 'Meta Model API key (hidden, Enter to skip): ' >&2
    IFS= read -rs META_VALUE || true
    printf '\n' >&2
    META_VALUE="$(trim "$META_VALUE")"
fi
if [ -z "$WIT_VALUE" ]; then
    printf 'Wit client token (hidden, Enter to skip): ' >&2
    IFS= read -rs WIT_VALUE || true
    printf '\n' >&2
    WIT_VALUE="$(trim "$WIT_VALUE")"
fi
if [ -z "$META_VALUE" ] && [ -z "$WIT_VALUE" ]; then
    echo "Both keys were empty — nothing sent." >&2
    exit 1
fi
validate_value "Meta Model API key" "$META_VALUE"
validate_value "Wit client token" "$WIT_VALUE"

TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT
chmod 600 "$TMP"
if [ -n "$META_VALUE" ]; then
    printf '%s=%s\n' "meta_model_key" "$META_VALUE" >> "$TMP"
fi
if [ -n "$WIT_VALUE" ]; then
    printf '%s=%s\n' "wit_client_token" "$WIT_VALUE" >> "$TMP"
fi
# Drop the shell copies as soon as the file holds the values.
unset META_VALUE WIT_VALUE

"${ADB_TARGET[@]}" shell mkdir -p "$DIR"
"${ADB_TARGET[@]}" push "$TMP" "$DIR/key-import.txt.tmp" >/dev/null
"${ADB_TARGET[@]}" shell mv "$DIR/key-import.txt.tmp" "$DIR/key-import.txt"

if [ -n "$("${ADB_TARGET[@]}" shell pidof "$PKG" 2>/dev/null | tr -d '\r')" ]; then
    echo "Sent. In the headset hub the status turns to 'keys imported from PC' within a second."
else
    echo "Sent. The keys import on the next app launch; check the hub settings status."
fi
echo "The app deletes the import file on first read. Keys live only on the headset."
