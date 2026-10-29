# Amiss public contracts

This directory contains dependency-free JSON Schema 2020-12 contracts and synthetic fixtures. The simulation remains the source of truth: a tool proposal requests an action, and local validation and execution determine the result.

## Contracts

- `versions.json` records independent framework, snapshot, tool, save, stage-data, context-payload, fixture, and provenance versions. Unknown supported-contract majors fail closed; episode model and extension versions remain episode-owned.
- `schemas/tool-proposal.schema.json` defines exactly five tools: `state.read`, `trial.propose`, `trial.orchestrate`, `presentation.request`, and `speech.emit`. Every operation has a closed parameter union.
- `schemas/snapshot.schema.json` defines the immutable snapshot, measurement, display-tuple, prediction, progress, token, diagnostic, and versioned episode-extension records.
- `schemas/validation-result.schema.json` defines validation results, idempotency scopes and records, accepted commands, and execution receipts.
- `schemas/confirmation.schema.json` defines non-executable challenges and single-use grants.
- `schemas/stage-data.schema.json` defines only mechanical stage graphs, evidence flags and pairs, allowed-action ids, default-path ids, and integrity data.
- `schemas/context-payload.schema.json` defines the public-client context boundary. Policy messages, rules, questions, and complete action catalogues are not fields.
- `schemas/model-provenance.schema.json` defines the closed ten-field model provenance record: approved Meta model and endpoint, local request id, UTC timestamps, nullable token usage, SHA-256 request/response hashes, and `live | recorded` mode. It has no body, credential, account, device, or free-form diagnostic fields.
- `schemas/recorded-call-fixture.schema.json` defines the versioned deterministic scenario, sanitized request, structured response, recorded provenance, and integrity envelope. Authorization headers and unknown fields are rejected.
- `schemas/common.schema.json` contains shared identifiers, versions, Trial references, targets, tokens, and typed parameter records.

No contract accepts executable source, arbitrary network requests, per-object state vectors, direct result setting, arbitrary stage advancement, or revision assignment. Text exists only in subtitle and speech operations. `target_parameters` admits only scalar values; the episode's `ToolDeclaration` must still close its keys, types, units, ranges, and one-input delta during proposal validation.

## Validation

Run from the repository root:

```bash
python3 schema/validate.py
```

The validator uses only the Python standard library. It validates every fixture in `fixtures/manifest.json` and enforces the JSON Schema subset used by these documents. It also rejects duplicate object keys, NaN and infinite numbers, excessive JSON nesting and collection sizes, unsupported contract majors, mismatched tool operations, invalid target/ref combinations, masked measurement values, and cross-field envelope inconsistencies. Proposal, speech, context, and utterance byte limits are enforced where applicable.

Recorded-fixture checks recompute SHA-256 over canonical JSON request, response, and payload data; verify request ids, refs, policy and contract identities, timestamp order, and the outer integrity digest; and run the existing context, tool, and speech semantic checks on nested records. Tampering therefore fails closed without any network or model call.

A consumer using another JSON Schema implementation must retain the parser and cross-field checks in `validate.py`; JSON Schema alone does not detect duplicate keys, non-finite JSON numbers, or canonical hash/integrity mismatches. This action does not implement fixture generation, transport, current-state validation, execution, persistence, or runtime behavior.

All fixtures are synthetic. They contain no real stage wording, tutor policy, credentials, personal data, room data, or real device and account identifiers. No live or recorded model call is claimed.
