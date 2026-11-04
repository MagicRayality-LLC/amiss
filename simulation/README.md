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
- A pure temperature episode model: declared uncalibrated space/count sizing with the even-clamped density rule and large-room option, one immutable temperature measurement with a single-source display tuple, and a Trial energy ledger with signed prop work and separate sandbag absorption.
- A pure frozen-writer solver-gate classifier: the locked 0.1 % full-run energy envelope is recomputed from the recorded per-Tick kinetic-energy series, the declared elasticity tolerances are applied without widening them, and a run without valid ball-ball or ball-boundary collision samples is incomplete evidence rather than a pass.
- The evaluation transport mechanics of the hackathon judge paths: one canonical request builder that assembles the policy envelope only at the send boundary, an in-memory-only model key handle, the direct-Meta deadline and typed offline/timeout/refusal failures, a recorded-fixture replay that refuses a stale or modified fixture, whole-reply admission, and one closed provenance record per admitted reply. The socket sits behind an injected sender, so no suite opens a connection and no live model call is claimed.

Unresolved prediction gates are held inside the kernel. Published `predictions[]` contains resolved records, while targeted measurements carry the active mask. This keeps every published prediction consistent with the public snapshot contract.

`ProposalValidator` receives an injected idempotency store and executors. The in-memory store and local effect executor are deterministic headless fixtures, not device persistence or headset presentation. They export and restore records for restart tests without file, network, or device access.

`EvaluationDirectTransport` and `RecordedFixtureTransport` are the two hackathon bindings proven here. `simulation/transport.py` builds the one canonical request, holds the key in memory only, enforces the 5 s connect and 30 s complete-response deadlines, and returns a typed failure instead of a response. `simulation/vectors.py` exports the shared cross-language vectors in `schema/vectors/admission-vectors.json`; the client suite drives its device mirror over the same file, so the two validator implementations cannot drift silently.

`SimulationRuntime` coordinates with the `TrialKernel` for lifecycle and revision algebra and owns the physical records itself. `HeadlessBallWriter` is the headless test writer behind the single `ScientificWriterSlot`; the Quest writer choice is not made here. `RuntimeBudgetProfile` values are declared initial limits awaiting device evidence, and no Unity physics, live-model, or device performance result is claimed.

`simulation/policy_pack.py` reads and verifies the public instructor policy pack in `policy/`. It does not define a second stage format: `stage-data.json` goes through the existing `GuideRunner.load`, and the module then checks that the prose supply cannot drift away from that envelope (stage ids, question ids, the action catalogue, and wording ids must line up) and that the tool catalogue is non-empty and complete. Missing, corrupt, edited, or incompatible packs fail closed with every reason listed and no substitute; `pack.json` and `stage-data.json` carry SHA-256 digests over the exact shipped bytes and their own canonical payloads. `assemble_system` builds the one `system` text of the transport-side `PolicyEnvelope` at the send boundary, including the tools and controls block, and `schema/vectors/policy-pack-vector.json` records the identity, digests, and system hash so the device mirror is checked against the same file.

Device persistence, rendering, and device integration belong to later actions. The evaluation transport mechanics are proven headlessly against an injected sender and the existing synthetic fixtures; no live model call, hosted path, or device model result is claimed here.

## Validation

Run from the repository root:

```bash
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s simulation/tests -v
PYTHONDONTWRITEBYTECODE=1 python3 schema/validate.py
```

The tests cover Trial/guide scenarios 1–7, the non-persistence portion of scenario 9, all twelve proposal-validator headless scenarios, and the headless portions of runtime scenarios 1–4 and 6–14 at varied request rates. `solver_gate` also classifies development-build device records for frozen-writer scenario 5 (`pass`/`fail`/`incomplete`, with a missing collision sample incomplete and never a pass); harvesting those records is a device action. Device-budget scenario 15 remains with its owning device action; save/recovery scenario 8 and fake-episode device persistence remain outside these actions.

The transport suites cover the canonical request bytes and hash, policy-only-at-the-send-boundary separation, the in-memory key handle, the 30 s deadline including the exact boundary, offline and refusal failures, fail-closed absence of a key or policy envelope, recorded-fixture replay and tamper refusal, accept/reject/stale/replay admission over the shared vectors, and a key-absence scan over every artifact this slice produces.
