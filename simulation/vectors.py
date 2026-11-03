"""Shared cross-language vectors for the device-side validator mirror.

The device cannot run Python, so the seven-step admission semantics and the
canonical JSON encoding are mirrored in C# and both implementations are
checked against one checked-in vector file. Every vector is generated here
from the Python oracle, and the Python suite re-runs the generator to prove
the file still matches the oracle instead of a stale copy.

The file holds four groups:

* ``canonicalisation`` — values and the exact canonical UTF-8 bytes and
  SHA-256 both languages must produce for them.
* ``admission`` — one declarative environment plus one reply per case, with
  the oracle's exact validation outcome.
* ``request`` — the canonical outbound request of one synthetic context and
  its hashes, so both builders agree on the request shape.
* ``transport`` — the deadline and fail-closed boundaries, declared as data.
"""

import hashlib
import json
from copy import deepcopy
from pathlib import Path

from schema.validate import canonical_bytes
from simulation.fake_episode import fake_stage_data
from simulation.guide import GuideRunner
from simulation.kernel import TrialKernel
from simulation.transport import (
    MODEL_DEADLINE_SECONDS,
    EvaluationRequestBuilder,
    PolicyEnvelope,
    build_provenance,
    canonical_request_bytes,
    request_hash,
)
from simulation.validator import (
    InMemoryIdempotencyStore,
    LocalGates,
    ProposalValidator,
    ToolDeclaration,
)

VECTOR_FORMAT_VERSION = "1.0.0"
VECTOR_PATH = (
    Path(__file__).resolve().parent.parent / "schema" / "vectors" / "admission-vectors.json"
)

POLICY = PolicyEnvelope(
    policy_version="synthetic-policy-v1",
    system="Synthetic instructor policy fixture; no teaching content.",
)

TOOLS = {
    "state.read": ("snapshot", "ledger", "measurements"),
    "trial.propose": ("create", "derive"),
    "trial.orchestrate": (
        "start", "pause", "resume", "begin_measurement", "archive", "present",
    ),
    "presentation.request": ("highlight_surface", "replay_slow", "open_chart", "show_subtitle"),
    "speech.emit": ("answer", "proactive", "safety"),
}


def declarations():
    result = []
    for tool, operations in TOOLS.items():
        for operation in operations:
            proposing = tool == "trial.propose"
            result.append(ToolDeclaration(
                action_id=f"action-{tool}-{operation}",
                tool=tool,
                operation=operation,
                setup_family_id="discrete-count" if proposing else None,
                controllable_inputs={"count": {"minimum": 0, "maximum": 10}} if proposing else {},
                default_parameters={"count": 3} if proposing else {},
                downstream_effect_ids=("discrete-state",) if proposing else (),
                invariant_ids=("finite-state", "positive-count") if proposing else ("finite-state",),
            ))
    return result


def environment(*, proposed_setup=False):
    kernel = TrialKernel(
        "fake-discrete",
        "fake_discrete",
        continuous_phases=(),
        setup_family_ids=("discrete-count",),
    )
    kernel.create_trial("trial-a", "discrete-count", {"count": 3})
    if proposed_setup:
        kernel.create_trial("trial-proposed", "discrete-count", {"count": 4}, proposed=True)
    declared = declarations()
    stage = fake_stage_data()
    stage["allowed_action_set"]["action_ids"] = [item.action_id for item in declared]
    guide = GuideRunner(kernel, stage)
    gates = LocalGates()
    store = InMemoryIdempotencyStore()
    validator = ProposalValidator(kernel, declared, store=store, gates=gates)
    return {
        "kernel": kernel,
        "guide": guide,
        "validator": validator,
        "declared": declared,
        "gates": gates,
        "store": store,
    }


def context(env):
    """The declarative environment the device mirror is driven from."""
    kernel = env["kernel"]
    guide = env["guide"]
    gates = env["gates"]
    trials = {}
    for trial_id, trial in kernel.trials.items():
        trials[trial_id] = {
            "trial_id": trial_id,
            "trial_revision": trial.state["trial_revision"],
            "phase": trial.state["phase"],
            "acceptance": trial.state["acceptance"],
            "activity": trial.state["activity"],
            "setup_family_id": trial.configuration["setup_family_id"],
            "target_parameters": deepcopy(trial.configuration["target_parameters"]),
            "measurement_ids": [item["measurement_id"] for item in trial.measurements],
            "pending_prediction_ids": [item["prediction_id"] for item in trial.pending_predictions],
        }
    return {
        "episode_id": kernel.episode_id,
        "current_trial_id": kernel.current_trial_id,
        "comparison_trial_ids": list(kernel.comparison_trial_ids),
        "refs": kernel.current_refs(),
        "trials": trials,
        "preferences": deepcopy(kernel.preferences),
        "global_revision": kernel.global_revision,
        "policy_tokens": guide.projection(kernel.current, kernel.current_refs())["policy_tokens"],
        "declarations": [
            {
                "action_id": item.action_id,
                "tool": item.tool,
                "operation": item.operation,
                "setup_family_id": item.setup_family_id,
                "controllable_inputs": deepcopy(item.controllable_inputs),
                "default_parameters": deepcopy(item.default_parameters),
                "downstream_effect_ids": list(item.downstream_effect_ids),
                "invariant_ids": list(item.invariant_ids),
                "confirmation_required": item.confirmation_required,
            }
            for item in env["declared"]
        ],
        "gates": {
            "user_speaking": gates.user_speaking,
            "user_swinging": gates.user_swinging,
            "cooldown_active": gates.cooldown_active,
            "backoff_active": gates.backoff_active,
            "tts_available": gates.tts_available,
            "scene_valid": gates.scene_valid,
            "numerical_state_ok": gates.numerical_state_ok,
            "storage_available": gates.storage_available,
            "hard_safety_hold": gates.hard_safety_hold,
            "generation_committed": gates.generation_committed,
            "surface_ids": sorted(gates.surface_ids),
            "highlight_ids": sorted(gates.highlight_ids),
            "replay_event_ids": sorted(gates.replay_event_ids),
            "replay_speed_ids": sorted(gates.replay_speed_ids),
            "chart_slot_ids": sorted(gates.chart_slot_ids),
            "chart_ids": sorted(gates.chart_ids),
            "safety_candidate_ids": sorted(gates.safety_candidate_ids),
            "playback_completed_solicitation_ids":
                sorted(gates.playback_completed_solicitation_ids),
        },
        "idempotency_records": [
            {
                "call_id": record["call_id"],
                "idempotency_scope": deepcopy(record["idempotency_scope"]),
                "fingerprint": record["fingerprint"],
                "first_result": deepcopy(record["first_result"]),
            }
            for record in env["store"].export()
        ],
    }


def token(env, kind, value_id=None):
    kernel = env["kernel"]
    guide = env["guide"]
    token_set = guide.projection(kernel.current, kernel.current_refs())["policy_tokens"]
    return next(
        item["token_id"] for item in token_set["tokens"]
        if item["kind"] == kind and (value_id is None or item["value_id"] == value_id)
    )


def ref(env, *, revision=None):
    value = env["kernel"].current.ref.to_dict()
    if revision is not None:
        value = {"trial_id": value["trial_id"], "trial_revision": revision}
    return value


def read_proposal(env, call_id, *, revision=None, trial_id=None):
    target = ref(env, revision=revision)
    if trial_id is not None:
        target = {"trial_id": trial_id, "trial_revision": target["trial_revision"]}
    return {
        "tool_schema_version": "1.0.0",
        "call_id": call_id,
        "origin": "model",
        "tool": "state.read",
        "target": {"kind": "existing", "trial_ref": deepcopy(target)},
        "basis_trial_ref": deepcopy(target),
        "refs": [deepcopy(target)],
        "parameters": {"operation": "snapshot", "target_trial_ref": deepcopy(target)},
    }


def propose_proposal(env, call_id, value=4):
    return {
        "tool_schema_version": "1.0.0",
        "call_id": call_id,
        "origin": "model",
        "tool": "trial.propose",
        "target": {
            "kind": "new_trial",
            "episode_id": env["kernel"].episode_id,
            "source_trial_ref": None,
        },
        "basis_trial_ref": None,
        "refs": [],
        "parameters": {
            "operation": "create",
            "setup_family_token_id": token(env, "setup_family", "discrete-count"),
            "target_parameters": {"count": value},
            "input_delta": {
                "parameter_id": "count",
                "to_value": value,
                "downstream_effect_ids": ["discrete-state"],
            },
            "expected_invariant_ids": ["finite-state", "positive-count"],
        },
    }


def orchestrate_proposal(env, call_id, operation, *, trial_id=None):
    target = ref(env)
    if trial_id is not None:
        target = {"trial_id": trial_id, "trial_revision": target["trial_revision"]}
    return {
        "tool_schema_version": "1.0.0",
        "call_id": call_id,
        "origin": "model",
        "tool": "trial.orchestrate",
        "target": {"kind": "existing", "trial_ref": deepcopy(target)},
        "basis_trial_ref": deepcopy(target),
        "refs": [deepcopy(target)],
        "parameters": {"operation": operation, "target_trial_ref": deepcopy(target)},
    }


def safety_proposal(env, call_id, origin="model"):
    target = ref(env)
    return {
        "tool_schema_version": "1.0.0",
        "call_id": call_id,
        "origin": origin,
        "tool": "speech.emit",
        "target": {"kind": "existing", "trial_ref": deepcopy(target)},
        "basis_trial_ref": deepcopy(target),
        "refs": [deepcopy(target)],
        "parameters": {
            "operation": "safety",
            "text": "Synthetic safety text.",
            "source_global_revision": max(1, env["kernel"].global_revision),
            "citations": [],
            "safety_candidate_id": "safety-a",
        },
    }


def _record(result):
    recorded = {
        "call_id": result["call_id"],
        "fingerprint": result["fingerprint"],
        "outcome": result["outcome"],
        "reason_code": result["reason_code"],
        "rejection_stage": result["rejection_stage"],
        "replay": result["replay"],
    }
    if result["outcome"] == "executed":
        receipt = result["execution_receipt"]
        recorded["trial_revision_delta"] = receipt["trial_revision_delta"]
        recorded["diff_kind"] = receipt["diff"]["kind"]
    return recorded


def _after(env):
    kernel = env["kernel"]
    return {
        "current_trial_id": kernel.current_trial_id,
        "trials": {
            trial_id: {
                "trial_revision": trial.state["trial_revision"],
                "phase": trial.state["phase"],
                "acceptance": trial.state["acceptance"],
                "activity": trial.state["activity"],
                "tick": trial.state["tick"],
            }
            for trial_id, trial in sorted(kernel.trials.items())
        },
    }


def case(name, prepare):
    """Build one admission case.

    ``prepare(env)`` may seed state and must return the reply's proposals. The
    declarative context is snapshotted after ``prepare`` and before validation,
    so it describes exactly the state the validator will see. The context is
    returned separately; callers pool identical contexts by digest instead of
    repeating them in every case.
    """
    env = environment()
    proposals = prepare(env)
    declarative = context(env)
    response = {
        "response_contract_version": "1.0.0",
        "request_id": "request-a",
        "refs": deepcopy(declarative["refs"]),
        "speech_units": [],
        "tool_proposals": deepcopy(proposals),
    }
    results = env["validator"].validate_reply(response["tool_proposals"])
    return declarative, {
        "name": name,
        "response": response,
        "expect": {
            "results": [_record(item) for item in results],
            "after": _after(env),
        },
    }


# --------------------------------------------------------------------------
# Case definitions
# --------------------------------------------------------------------------


def _read_call_is_executed(env):
    return [read_proposal(env, "call-read")]


def _unknown_field(env):
    return [dict(read_proposal(env, "call-unknown-field"),
                 script="synthetic-nonexecutable-text")]


def _unknown_operation(env):
    return [dict(read_proposal(env, "call-unknown-op"),
                 parameters={"operation": "delete", "target_trial_ref": ref(env)})]


def _unsupported_version(env):
    return [dict(read_proposal(env, "call-version"), tool_schema_version="2.0.0")]


def _nonpositive_revision(env):
    return [read_proposal(env, "call-stale-zero", revision=0)]


def _stale_low(env):
    """The Trial moves on, so a one-revision-lower ref is a genuine stale ref."""
    proposal = read_proposal(env, "call-stale-low")
    env["kernel"].pause()
    return [proposal]


def _stale_high(env):
    return [read_proposal(env, "call-stale-high", revision=9)]


def _unknown_trial(env):
    return [read_proposal(env, "call-unknown-trial", trial_id="trial-absent")]


def _accepted_candidate(env):
    return [propose_proposal(env, "call-propose")]


def _replay(env):
    proposal = propose_proposal(env, "call-propose")
    env["validator"].validate(deepcopy(proposal))
    return [proposal]


def _conflict(env):
    env["validator"].validate(propose_proposal(env, "call-propose", value=4))
    return [propose_proposal(env, "call-propose", value=5)]


def _start_refused(env):
    env["kernel"].create_trial("trial-proposed", "discrete-count", {"count": 4}, proposed=True)
    return [orchestrate_proposal(env, "call-start", "start", trial_id="trial-proposed")]


def _safety_from_model(env):
    return [safety_proposal(env, "call-safety")]


def _revoked_token(env):
    proposal = propose_proposal(env, "call-gone")
    proposal["parameters"]["setup_family_token_id"] = "guide-token-absent"
    return [proposal]


def _two_proposals(env):
    return [read_proposal(env, "call-first"), read_proposal(env, "call-second")]


ADMISSION_CASES = (
    ("read snapshot is executed with no revision change", _read_call_is_executed),
    ("unknown field is a step-1 rejection", _unknown_field),
    ("unknown operation is a step-1 rejection", _unknown_operation),
    ("unsupported tool schema major is refused at step 1", _unsupported_version),
    ("non-positive trial revision is malformed at step 1", _nonpositive_revision),
    ("low stale ref is a step-3 rejection", _stale_low),
    ("high stale ref is a step-3 rejection", _stale_high),
    ("unknown trial is a step-3 rejection", _unknown_trial),
    ("accepted proposal creates only a Tick-0 candidate", _accepted_candidate),
    ("exact replay returns the first result without a second candidate", _replay),
    ("changed semantics under one call id is an idempotency conflict", _conflict),
    ("start of a proposed setup is refused at step 5", _start_refused),
    ("safety speech from model origin is a step-4 rejection", _safety_from_model),
    ("an unissued guide token is a step-4 rejection", _revoked_token),
    ("two proposals in one reply are validated in order", _two_proposals),
)

# --------------------------------------------------------------------------
# Canonicalisation corpus
# --------------------------------------------------------------------------

CANONICAL_VALUES = (
    ("object keys are sorted", {"b": 1, "a": 2, "C": 3}),
    ("arrays keep semantic order", {"refs": [
        {"trial_id": "trial-b", "trial_revision": 2},
        {"trial_id": "trial-a", "trial_revision": 1},
    ]}),
    ("non-ascii text is not escaped", {"text": "caf\u00e9 \u00b5K \u2192"}),
    ("shortest round-trip decimals", {"a": 0.1, "b": 1.0, "d": 1e-7, "e": 1234567890123}),
    ("nested empty containers", {"a": {}, "b": [], "c": {"d": [[], {}]}}),
    ("booleans and nulls", {"a": True, "b": False, "c": None}),
    ("escaped control characters", {"text": "line\nbreak\ttab \"quote\" back\\slash"}),
)


def canonicalisation_corpus():
    corpus = []
    for name, value in CANONICAL_VALUES:
        data = canonical_bytes(value)
        corpus.append({
            "name": name,
            "value": deepcopy(value),
            "canonical_json": data.decode("utf-8"),
            "sha256": hashlib.sha256(data).hexdigest(),
        })
    return corpus


# --------------------------------------------------------------------------
# Request anchor and declared transport boundaries
# --------------------------------------------------------------------------


def request_anchor():
    env = environment()
    payload = env["kernel"].build_context_payload("utterance-a", "Synthetic utterance.")
    request = EvaluationRequestBuilder.build(
        payload, POLICY, request_id="request-a", turn_id="turn-a", origin="answer"
    )
    response = {
        "response_contract_version": "1.0.0",
        "request_id": request["request_id"],
        "refs": deepcopy(request["refs"]),
        "speech_units": [],
        "tool_proposals": [read_proposal(env, "call-read")],
    }
    provenance = build_provenance(
        request, response,
        ts_start="2026-11-01T12:00:00Z", ts_end="2026-11-01T12:00:01Z",
        input_tokens=120, output_tokens=40, mode="live",
    )
    return {
        "context_payload": payload,
        "request": request,
        "request_sha256": request_hash(request),
        "request_bytes_sha256": hashlib.sha256(canonical_request_bytes(request)).hexdigest(),
        "context_payload_sha256": hashlib.sha256(canonical_bytes(payload)).hexdigest(),
        "response": response,
        "provenance": provenance,
        "response_sha256": provenance["response_hash"],
    }


def transport_boundaries():
    return {
        "endpoint_host": "api.meta.ai",
        "model": "muse-spark-1.3",
        "model_deadline_seconds": MODEL_DEADLINE_SECONDS,
        "connect_deadline_seconds": 5.0,
        "accepted_at_deadline_seconds": MODEL_DEADLINE_SECONDS,
        "rejected_after_deadline_seconds": MODEL_DEADLINE_SECONDS + 0.5,
        "fail_closed_reason_codes": ["no_credential", "no_policy"],
        "failure_kinds": ["offline", "timeout", "remote_refusal", "malformed_response"],
    }


def build_vectors():
    corpus = canonicalisation_corpus()
    contexts = {}
    cases = []
    for name, prepare in ADMISSION_CASES:
        declarative, case_record = case(name, prepare)
        digest = hashlib.sha256(canonical_bytes(declarative)).hexdigest()
        contexts.setdefault(digest, declarative)
        cases.append(dict(case_record, context_sha256=digest))
    vectors = {
        "vector_format_version": VECTOR_FORMAT_VERSION,
        "contexts": contexts,
        "canonicalisation": corpus,
        "admission": cases,
        "request": request_anchor(),
        "transport": transport_boundaries(),
    }
    return vectors


def render(vectors):
    return json.dumps(vectors, ensure_ascii=False, indent=2, sort_keys=True) + "\n"


def write(path=VECTOR_PATH):
    text = render(build_vectors())
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return path


if __name__ == "__main__":
    print(write())
