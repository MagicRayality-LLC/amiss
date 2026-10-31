# Amiss

Mixed-reality physics on Quest. Every experiment starts when the expected result is wrong.

The headset renders the scene, tracks the person, and runs the simulation. The simulation is the source of truth for each trial. An instructor changes the next trial through the tools in `schema/`.

This repository contains the headset client, the local simulation, and the tool schema.

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

Copyright MagicRayality LLC. All rights reserved. No license is granted to copy, modify, or distribute this repository.
