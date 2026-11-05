#!/usr/bin/env bash
# Report what this machine has and what it still needs to build or verify
# Amiss. Installs nothing, downloads nothing, and checks no network service.
set -uo pipefail

cd "$(dirname "$0")/.."
root=$(cd .. && pwd)
UNITY_VERSION="6000.6.5f1"

missing=0
say_ok()   { printf '%-28s %s\n' "ok" "$1"; }
say_miss() { printf '%-28s %s\n' "missing" "$1"; missing=$((missing + 1)); }

echo "== Python (simulation tests, schema validator)"
if command -v python3 >/dev/null 2>&1; then
    py=$(python3 -c 'import sys; print("%d.%d.%d" % sys.version_info[:3])' 2>/dev/null || echo unknown)
    say_ok "python3 $py (standard library only)"
else
    say_miss "python3 (no third-party packages are needed)"
fi

echo
echo "== Unity editor $UNITY_VERSION (client build)"
if [ -n "${UNITY:-}" ] && [ -x "${UNITY}" ]; then
    unity_path="$UNITY"
else
    unity_path=""
    for c in \
        "$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity" \
        "$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe" \
        "/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity" \
        "/c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe"; do
        if [ -x "$c" ]; then unity_path="$c"; break; fi
    done
fi

if [ -n "$unity_path" ]; then
    say_ok "editor: $unity_path"
    editor_dir=$(cd "$(dirname "$unity_path")" && pwd)
    if [ -d "$editor_dir/Data/PlaybackEngines/AndroidPlayer" ] || \
       [ -d "$editor_dir/../Data/PlaybackEngines/AndroidPlayer" ]; then
        say_ok "Android Build Support module"
    else
        say_miss "Android Build Support with OpenJDK and Android SDK & NDK Tools"
    fi
else
    say_miss "Unity $UNITY_VERSION (set UNITY to the executable if installed elsewhere)"
fi

echo
echo "== Android platform-tools (install on a headset)"
if command -v adb >/dev/null 2>&1; then
    say_ok "adb $(adb version 2>/dev/null | head -1 | sed 's/.*version //;s/ .*//')"
else
    say_miss "adb (Android platform-tools)"
fi

echo
echo "== Repository entry points"
for f in "simulation/tests" "schema/validate.py" "client/Packages/packages-lock.json" "client/README.md"; do
    if [ -e "$root/$f" ]; then say_ok "$f"; else say_miss "$f"; fi
done

echo
if [ "$missing" -eq 0 ]; then
    echo "All prerequisites present. Run the suites with:"
    echo "  PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s simulation/tests"
    echo "  PYTHONDONTWRITEBYTECODE=1 python3 schema/validate.py"
    exit 0
fi
echo "$missing item(s) missing. The suites above still run with only python3."
echo "Exact Unity package versions: client/Packages/packages-lock.json"
echo "Frozen build identity: client/README.md"
exit 1
