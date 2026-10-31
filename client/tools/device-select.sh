#!/usr/bin/env bash
# Shared headset resolution for the Amiss client device scripts.
#
# Source this file, then call amiss_select_device or amiss_list_devices. The
# caller provides ADB and may preset DEVICE (a serial from --device or
# ANDROID_SERIAL). Only adb's 'device' state counts; offline/unauthorized
# entries never qualify.
#
# amiss_select_device sets DEVICE and MODEL, or returns nonzero after printing
# the reason. One attached device is used directly. Several attached devices
# get an interactive numbered picker showing each model; in a non-interactive
# shell the serials are listed and the call fails, so nothing is installed
# blindly.
#
# amiss_list_devices prints the attached headsets, one per line, and returns 0
# when at least one is attached.

# Prints "serial<TAB>model" for every attached headset.
amiss_adb_heads() {
    "$ADB" devices -l | awk '$2 == "device" {
        model = "-"
        for (i = 3; i <= NF; i++)
            if (substr($i, 1, 6) == "model:") { model = substr($i, 7); break }
        print $1 "\t" model
    }'
}

amiss_list_devices() {
    local serial model count=0
    while IFS=$'\t' read -r serial model; do
        printf '  %s  (%s)\n' "$serial" "$model"
        count=$((count + 1))
    done < <(amiss_adb_heads)
    if [ "$count" -eq 0 ]; then
        if [ "$("$ADB" devices | awk 'NR > 1 && NF >= 2' | wc -l)" -gt 0 ]; then
            echo "No usable headset: every connected device is offline or unauthorized — run 'adb devices'" >&2
        else
            echo "No headset connected over USB — connect one and run 'adb devices'" >&2
        fi
        return 1
    fi
    return 0
}

amiss_select_device() {
    local serial model i pick found=0
    local -a serials=() models=()

    while IFS=$'\t' read -r serial model; do
        serials+=("$serial")
        models+=("$model")
    done < <(amiss_adb_heads)

    if [ "${#serials[@]}" -eq 0 ]; then
        amiss_list_devices >/dev/null
        return 1
    fi

    if [ -n "${DEVICE:-}" ]; then
        for i in "${!serials[@]}"; do
            if [ "${serials[$i]}" = "$DEVICE" ]; then
                MODEL="${models[$i]}"
                found=1
                break
            fi
        done
        if [ "$found" -ne 1 ]; then
            echo "Device '$DEVICE' is not attached in 'device' state:" >&2
            for i in "${!serials[@]}"; do
                echo "  ${serials[$i]}  (${models[$i]})" >&2
            done
            return 2
        fi
        return 0
    fi

    if [ "${#serials[@]}" -eq 1 ]; then
        DEVICE="${serials[0]}"
        MODEL="${models[0]}"
        return 0
    fi

    if [ -t 0 ]; then
        echo "Several devices attached:"
        for i in "${!serials[@]}"; do
            echo "  $((i + 1))) ${serials[$i]}  (${models[$i]})"
        done
        read -r -p "Target headset [1-${#serials[@]}]: " pick
        case "$pick" in ''|*[!0-9]*) pick=0 ;; esac
        if [ "$pick" -lt 1 ] || [ "$pick" -gt "${#serials[@]}" ]; then
            echo "No valid headset picked" >&2
            return 2
        fi
        DEVICE="${serials[$((pick - 1))]}"
        MODEL="${models[$((pick - 1))]}"
        return 0
    fi

    echo "Several devices attached — pick one with --device SERIAL:" >&2
    for i in "${!serials[@]}"; do
        echo "  ${serials[$i]}  (${models[$i]})" >&2
    done
    return 2
}
