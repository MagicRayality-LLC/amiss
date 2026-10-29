# Local simulation

The simulation is the source of truth for each Trial. This directory contains a standard-library Python kernel that runs without rendering, network, voice, model, device-storage, or headset dependencies. Its only contract dependency is `schema/`.

## Implemented contracts

- Trial creation, explicit proposal acceptance, rejection, return to source, derivation, lifecycle transitions, pause/resume, and immutable setup projections.
- Exact Trial revision changes for semantic events, with free Ticks, held-pose samples, panel refreshes, and comparison selection leaving the Trial revision unchanged.
- Immutable-by-value snapshot projections, complete measurements and display tuples, structured prediction gates, target masking, and stale-reference refusal in both directions.
- Append-only Trial and guide ledgers with scientific facts and reason categories rather than tutor wording.
- Versioned mechanical stage loading, current-token whitelists, independent evidence pairs, user skip/replay/free-exploration controls, and a deterministic offline default path.
- A pure proposal validator for all five closed tools, canonical SHA-256 fingerprints, seven ordered checks, durable first-result replay, exact single-use confirmation, and atomic execution receipts.
- Deterministic headless runtime mechanics: a fixed-Tick scheduler bounded by `max_ticks_per_scheduling_slice` with shed-before-hold degradation and no Tick debt, admission-before-effect command logging with late-assignment and idempotency resolution, named SHA-256 RNG streams derived from stable identifiers, validated one-primary-per-surface collision representations pinned per Trial, a single `ScientificWriterSlot` holding one headless test writer per Trial, swept/state contact closure with one work entry and one semantic commit, immutable-by-value `BatchStateView` publications, unit round trips with direct-relative-summation thermal measurement and `not_applicable` small samples, held-object accounting with clamped state-derived release, one live Trial slot with the full handoff matrix, recompute recipes with verified checkpoints and `state_playback` labelling, atomic batch groups that commit exactly once and retain completed samples when cancelled, controlled-comparison records projected identically everywhere, typed failure reports, and symbolic runtime budget hooks carrying declared initial limits, not measured claims.
- A non-product fake discrete episode covering declaration, load gate, all eleven behaviour faces, setup, measurement, derivation, guide completion, and residue-free unload.

Unresolved prediction gates are held inside the kernel. Published `predictions[]` contains resolved records, while targeted measurements carry the active mask. This keeps every published prediction consistent with the public snapshot contract.

`ProposalValidator` receives an injected idempotency store and executors. The in-memory store and local effect executor are deterministic headless fixtures, not device persistence or headset presentation. They export and restore records for restart tests without file, network, or device access.

`SimulationRuntime` coordinates with the `TrialKernel` for lifecycle and revision algebra and owns the physical records itself. `HeadlessBallWriter` is the headless test writer behind the single `ScientificWriterSlot`; the Quest writer choice is not made here. `RuntimeBudgetProfile` values are declared initial limits awaiting device evidence, and no Unity physics, live-model, or device performance result is claimed.

Device persistence, rendering, transport, and device integration belong to later actions. No device or model result is claimed here.

## Validation

Run from the repository root:

```bash
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s simulation/tests -v
PYTHONDONTWRITEBYTECODE=1 python3 schema/validate.py
```

The tests cover Trial/guide scenarios 1–7, the non-persistence portion of scenario 9, all twelve proposal-validator headless scenarios, and the headless portions of runtime scenarios 1–4 and 6–14 at varied request rates. Frozen-writer scenario 5 and device-budget scenario 15 remain with their owning device actions; save/recovery scenario 8 and fake-episode device persistence remain outside these actions.
