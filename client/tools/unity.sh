#!/usr/bin/env bash
# Shared Unity editor resolution for the Amiss client build scripts.
# The editor version is part of the frozen build identity: pass UNITY to
# override only for experiments, never for delivered builds.

if [ -z "${UNITY:-}" ]; then
    if [ -x "$HOME/Unity/Hub/Editor/6000.6.5f1/Editor/Unity" ]; then
        UNITY="$HOME/Unity/Hub/Editor/6000.6.5f1/Editor/Unity"
    else
        UNITY="$(ls -1d "$HOME"/Unity/Hub/Editor/*/Editor/Unity 2>/dev/null | sort -V | tail -1 || true)"
    fi
fi
if [ -z "${UNITY:-}" ] || [ ! -x "$UNITY" ]; then
    echo "No Unity editor found: set UNITY or install Unity 6000.6.5f1 via Unity Hub" >&2
    exit 1
fi