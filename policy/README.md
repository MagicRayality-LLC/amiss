# Instructor policy

This directory is the public instructor supply for the hackathon build. It is
content, not contracts: `../schema/` still holds every mechanical envelope, and
`../simulation/` holds the interpreter. Nothing here redefines a stage format.

## Files

| File | What it holds |
|---|---|
| `pack.json` | The pack manifest: format version, pack id, policy version, the file inventory with one SHA-256 per file, and the pack integrity digest. |
| `stage-data.json` | The mechanical `StageDataEnvelope` the existing `GuideRunner` loads: stage graph, evidence flags and pairs, and the allowed-action ids. |
| `instructor-policy.json` | The instructor supply: system instructions, series and episode rules, per-stage intent and next questions, the allowed-action catalogue, mistake classification, wording, and declared threshold slots. |

## Versioning

One `policy_version` (`amiss-hackathon-policy-v1`) is shared by all three files
and must match in each. `pack_format_version` and `stage_data_format_version`
are separate axes; an unsupported major on either is refused. The pack id names
this slice, not the full episode.

## Integrity

- Each file's SHA-256 is recorded in `pack.json` and covers the exact shipped
  bytes.
- `stage-data.json` also carries its own canonical content digest inside the
  envelope's `integrity` field, which is the shape the schema already requires.
- `pack.json` carries one digest over its own canonical payload, excluding the
  integrity field itself.

Both digests are SHA-256 over the canonical JSON encoding that
`schema/vectors/admission-vectors.json` already freezes for both languages.
Recompute them with:

```bash
python3 simulation/policy_pack.py seal
python3 simulation/policy_pack.py verify
```

`seal` recomputes and re-verifies; it never edits content. `verify` loads the
pack the way the client does and prints the version, stage count, and action
count. `system` prints the assembled system text, and `vector` regenerates
`schema/vectors/policy-pack-vector.json`.

## Failure behaviour

A missing, corrupt, edited, or incompatible pack is refused with every reason
listed at once. There is no second pack, no bundled default, and no partial
load. The caller may then run without dynamic wording; the local cards,
evidence pairs, predictions, skips, and completion are unaffected.

## Boundaries

- The assembled system text is built only at the model send boundary. It is
  never a `ContextPayload` field, a snapshot field, a ledger entry, a log line,
  or a Trial export field.
- The pack contains no credential, no endpoint, no private-tree reference, and
  no account or device identifier.
- Threshold values that this slice has not measured are declared with a `null`
  value and a `pending_measurement` status. The loader refuses a threshold that
  claims a value while still marked unmeasured, so a guess cannot ship
  silently.
- The P1 mass stage (`stage-6-mass`) is not in this pack: P0 routes stage 5 to
  stage 7 directly.
