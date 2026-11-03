# Amiss

Mixed-reality physics on Quest. Every experiment starts when the expected result is wrong.

The headset renders the scene, tracks the person, and runs the simulation. The simulation is the source of truth for each trial. An instructor changes the next trial through the tools in `schema/`.

This repository contains the headset client, the local simulation, the tool schema, and the public instructor policy in `policy/`.

## Layout

| Path | Contents |
|---|---|
| `client/` | The Unity headset project. `client/README.md` has the frozen build identity and the command list. |
| `simulation/` | The headless Python kernel, the deterministic runtime, the proposal validator, and the evaluation transport mechanics. It is the contract oracle. |
| `schema/` | The JSON Schema contracts, synthetic fixtures, and the shared cross-language vectors. No teaching text lives here. |
| `policy/` | The public instructor supply: system instructions, rules, next questions, allowed actions, mistake classification, and the mechanical stage data. `policy/README.md` documents its versioning and integrity scheme. |
| `.env.example` | Blank environment assignments with comments. The recorded path needs none of them. |

The build copies `policy/` into the bundle at setup and build time, changes no
bytes, and verifies the pack digests on the device. A missing, edited, or
incompatible pack is refused with a readable reason; it is never replaced by a
second supply, and the local cards, evidence pairs, and default path continue
without dynamic wording.

## Debug build

Build the headset client with Unity 6000.6.5f1 and Android Build Support:

```bash
client/tools/build-debug.sh
```

This creates `client/Builds/Amiss-debug.apk` with Unity debug signing. It does
not create a store or release build and needs no company signing credential.
See `client/README.md` for prerequisites and custom Unity installation paths.

To build, install on a headset attached over USB, and launch it in one step:

```bash
client/tools/build-install-debug.sh
```

The script uses the same debug build and installs on the only attached headset
directly; if several are attached it asks which one, and it never installs
without a resolved target.

## Playing the evaluation build

Open the app; the episode hub appears. Run or accept the room scan, then
press LAUNCH. That press loads the room saved on this headset and confirms
it. The wall display, the PA speaker above it, and the red knife switch to
its right then appear on the nearest usable wall, and the trial has not
started yet.

To start episode one, close the knife switch — pinch it, press it with a
fingertip, or point at it and pull the trigger — or press START TRIAL in the
hub. Both do the same thing: the hub closes, the blades render closed, and
the trial runs. A start that is refused, for example by the safety hold,
leaves the room confirmed and the blades open, so you can try again.

Balls fill the room and collide elastically. The wall display shows only the
temperature of the ball group in kelvin, Celsius, and Fahrenheit, with a graph
of that temperature over time. Hitting balls with the bat does work on them,
and the temperature follows their speed.

The hub (menu button, or palm up and pinch) holds the settings. The live
instructor needs a Meta Model API key, and voice also needs a Wit Client
Access Token (create a free app at wit.ai and copy its Client Access Token).
The easiest way to enter both is from the PC that installed the build:
`client/tools/build-install-debug.sh` offers it after install, or run
`client/tools/set-keys.sh` at any time. Paste each value at the hidden
prompt; the hub status then reads "keys imported from PC", and each saved
value shows only a masked tail. Without the PC, use the hub fields instead:
type or PASTE into META MODEL API KEY and press SAVE KEY, and type or PASTE
into WIT CLIENT TOKEN and press SAVE TOKEN or Enter. Then press ASK TUTOR
for the instructor, or MIC for voice. Before the first capture a consent
notice appears; press MIC again to accept it. Voice also needs microphone
access granted to the app on the headset. Keys and tokens are stored only
on the device and are never part of this repository. Without them the
experiment still runs locally, and the voice and model features stay off.

Copyright MagicRayality LLC. All rights reserved. No license is granted to copy, modify, or distribute this repository.
