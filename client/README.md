# Headset client

The Unity Quest Pro headset shell. This directory is the Unity project root:
open it with Unity Hub or build it from the command line with the scripts in
`tools/`. The shell owns the session, the episode library and load gate, the
world-anchored panel, input normalization, and the platform adapters; pure
contract logic lives in `../simulation/` and `../schema/`, and this tree
never duplicates it.

## What this build contains

- The library-first session: entry is always the episode library, cards are
  projections of episode declarations plus local progress, and no episode
  code runs before the load gate passes.
- The episode load gate (compatibility, capabilities, space decidability,
  critical assets, save versions, availability) with readable, ordered
  rejections. Nothing behind the gate is executed with checks disabled.
- The framework contract fixtures (`fake-discrete`) mirroring
  `../simulation/fake_episode.py`; they are explicitly marked as non-product
  content and mount nothing in this build.
- Build identity logging: every app start logs one `[Amiss] build-identity`
  line naming the app build and all framework contract versions.
- The remaining runtime responsibilities (room preparation, the on-device
  simulation writer, guide, tutor transport, persistence) are exposed as
  silent ports and arrive with their owning build steps.

## Frozen build identity

| Piece | Version |
|---|---|
| Unity Editor | 6000.6.5f1 (revision 3ff58d469c8a) |
| Android backend | IL2CPP, ARM64, Vulkan, ASTC, API 32/34 |
| XR path | Unity OpenXR Plugin 1.16.1 (Meta XR SDKs v74+ path) |
| Meta XR Core SDK | 207.0.1 (`com.meta.xr.sdk.core`) |
| Meta XR Interaction SDK | 207.0.1 (`com.meta.xr.sdk.interaction.ovr`) |
| URP | 17.6.0 |
| XR Plugin Management | 4.7.0 |
| Unity Test Framework | 1.8.0 |
| Application id | `com.magicrayality.amiss` |

Exact versions live in `Packages/manifest.json` and
`Packages/packages-lock.json`. Do not drift Editor or package versions without
reopening the build identity contract; later build steps add packages at
pinned versions rather than bumping existing ones.

## Commands

For a public checkout, `tools/build-debug.sh` builds only
`Builds/Amiss-debug.apk`. It uses Unity debug signing with the custom keystore
disabled; it does not build a store or release package and needs no company
credential. Install Unity 6000.6.5f1 with Android Build Support, IL2CPP, and an
Android SDK/JDK. Standard Unity Hub paths on Linux, macOS, and Windows Git
Bash are detected automatically. On any other setup, set `UNITY` to the
editor executable first:

```bash
UNITY=/path/to/Unity client/tools/build-debug.sh
```

Run the other commands from this directory (or from the repository root with
the path prefix `client/`):

```bash
tools/build-debug.sh            # debug only: Builds/Amiss-debug.apk
tools/build-install-debug.sh    # debug only: build + install + launch on a headset
tools/setup-project.sh          # idempotent: configure the Unity project once
tools/run-tests.sh               # edit-mode test suite, headless
tools/build-apk.sh              # Builds/Amiss.apk for Quest Pro
tools/build-install-launch.sh   # build + adb install + launch on a headset
```

All Unity invocations are batchmode `-nographics`. The scripts refuse to run
while the Editor holds the project open. `ADB`/`UNITY` environment variables
and `--device SERIAL` select overrides; only adb `device` state counts.

`tools/build-install-debug.sh` is the judge-facing one-shot: it builds with
Unity debug signing only (custom keystore disabled, no store or release
package), installs on the attached headset, and launches it. Device selection
is shared with `tools/build-install-launch.sh` through
`tools/device-select.sh`: one attached headset is used directly, several get
an interactive numbered picker, and a non-interactive shell lists the serials
and exits instead of guessing.

Typical runs, from this directory:

```bash
tools/build-install-debug.sh --list            # show attached headsets, exit
tools/build-install-debug.sh                   # build + install + launch
tools/build-install-debug.sh --device <serial> # pick one of several headsets
tools/build-install-debug.sh --no-build        # reinstall the existing APK
tools/build-install-debug.sh --no-launch       # build + install only
```

With one headset attached the serial is not needed. With several, the script
numbers them and asks for one:

```
Several devices attached:
  1) 1WMHH8600V1106  (Quest_2)
  2) 230YC01DCC00YM  (Quest_Pro)
  3) 2G0YC5ZF9301NX  (Quest_3)
Target headset [1-3]:
```

A failed install because the on-device copy carries a different signing key is
reported with the manual uninstall command; the script never uninstalls by
itself. If the launch reports no process, wake and unlock the headset and check
its screen for a system dialog.

## Layout

- `Assets/Amiss/Runtime/Amiss.Framework/` — pure C# contracts: episode
  declarations, the shell capability manifest, the load gate, library card
  projections, contract versions. No engine references.
- `Assets/Amiss/Runtime/Amiss.Shell/` — the shell session, episode module
  host, library panel, input scaffold, the six-responsibility ports, and
  build identity.
- `Assets/Amiss/Editor/` — deterministic project setup and the build method.
- `Assets/Amiss/Tests/` — edit-mode tests; they read `../schema/versions.json`
  so the C# contract constants cannot drift from the schema tree.
- `Assets/Amiss/Shell.unity` — generated by setup; the scene stays minimal
  (OVRCameraRig plus the shell systems) because the panel builds itself.

## Tests

`tools/run-tests.sh` runs the suite headlessly and writes
`build/edit-mode-tests.xml`. The suite covers the load-gate order and every
rejection category, the capability manifest, the no-conclusion card
projection by construction, version identity against `../schema/versions.json`,
the library session state machine, and the contract fixtures.