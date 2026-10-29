from copy import deepcopy
from dataclasses import dataclass, field
import hashlib
import json
import math
import unicodedata

from schema.validate import (
    ContractError,
    SchemaValidator,
    canonical_bytes,
    parse_json,
    semantic_errors,
)

from .kernel import KernelError


DOMAIN_SEPARATOR = b"amiss.tool-proposal\0"
TOOL_DIFF_KINDS = {
    "state.read": "none",
    "trial.propose": "trial_state",
    "trial.orchestrate": "trial_state",
    "presentation.request": "presentation",
    "speech.emit": "voice",
}
ZERO_REVISION_OPERATIONS = {
    "state.read": {"snapshot", "ledger", "measurements"},
    "trial.propose": {"create", "derive"},
    "trial.orchestrate": {"begin_measurement", "present"},
    "presentation.request": {"highlight_surface", "replay_slow", "open_chart", "show_subtitle"},
    "speech.emit": {"answer", "proactive", "safety"},
}
RESULT_PARAMETER_IDS = {
    "measurement",
    "measurement_value",
    "result",
    "result_value",
    "temperature",
    "conclusion",
}


@dataclass(frozen=True)
class ToolDeclaration:
    action_id: str
    tool: str
    operation: str
    setup_family_id: str | None = None
    controllable_inputs: dict = field(default_factory=dict)
    default_parameters: dict = field(default_factory=dict)
    downstream_effect_ids: tuple = ()
    invariant_ids: tuple = ("finite-state",)
    confirmation_required: bool = False


@dataclass
class LocalGates:
    user_speaking: bool = False
    user_swinging: bool = False
    cooldown_active: bool = False
    backoff_active: bool = False
    tts_available: bool = True
    scene_valid: bool = True
    numerical_state_ok: bool = True
    storage_available: bool = True
    hard_safety_hold: bool = False
    generation_committed: bool = True
    surface_ids: set = field(default_factory=set)
    highlight_ids: set = field(default_factory=set)
    replay_event_ids: set = field(default_factory=set)
    replay_speed_ids: set = field(default_factory=set)
    chart_slot_ids: set = field(default_factory=set)
    chart_ids: set = field(default_factory=set)
    safety_candidate_ids: set = field(default_factory=set)
    playback_completed_solicitation_ids: set = field(default_factory=set)


@dataclass
class ExecutionContext:
    proposal: dict
    declaration: ToolDeclaration
    gates: LocalGates


class ExecutionError(RuntimeError):
    def __init__(self, reason_code="execution_unavailable"):
        super().__init__(reason_code)
        self.reason_code = reason_code


class InMemoryIdempotencyStore:
    def __init__(self, records=()):
        self.records = {}
        for record in records:
            key = self._key(record["idempotency_scope"], record["call_id"])
            self.records[key] = deepcopy(record)

    @staticmethod
    def _key(scope, call_id):
        return scope["episode_id"], scope["target_trial_id"], call_id

    def get(self, scope, call_id):
        record = self.records.get(self._key(scope, call_id))
        return deepcopy(record) if record is not None else None

    def commit(self, record):
        key = self._key(record["idempotency_scope"], record["call_id"])
        if key in self.records:
            raise ContractError("idempotency record already exists")
        self.records[key] = deepcopy(record)

    def export(self):
        return deepcopy(list(self.records.values()))

    @classmethod
    def restore(cls, records):
        return cls(records)


class LocalEffectExecutor:
    def __init__(self):
        self.effects = {
            "highlights": {},
            "replays": [],
            "charts": {},
            "subtitles": [],
            "speech": [],
            "microphone_open": False,
        }

    def checkpoint(self):
        return deepcopy(self.effects)

    def restore(self, checkpoint):
        self.effects = checkpoint

    @staticmethod
    def _receipt(command, before, diff_kind, changed, evidence):
        return {
            "before_trial_ref": deepcopy(before),
            "after_trial_ref": deepcopy(before),
            "trial_revision_delta": 0,
            "global_revision": None,
            "diff": {"kind": diff_kind, "changed_field_ids": changed},
            "postcondition_evidence_ids": evidence,
        }

    def __call__(self, command, context):
        parameters = command["parameters"]
        operation = parameters["operation"]
        before = command["refs"][0]
        if command["tool"] == "presentation.request":
            if operation == "highlight_surface":
                self.effects["highlights"][parameters["highlight_id"]] = parameters["requested_state"]
                changed = ["highlight"]
            elif operation == "replay_slow":
                self.effects["replays"].append({
                    "event_id": parameters["event_id"],
                    "replay_speed_id": parameters["replay_speed_id"],
                })
                changed = ["replay"]
            elif operation == "open_chart":
                self.effects["charts"][parameters["slot_id"]] = parameters["chart_id"]
                changed = ["chart"]
            else:
                self.effects["subtitles"].append({
                    "subtitle_id": parameters["subtitle_id"],
                    "text": parameters["text"],
                })
                changed = ["subtitles"]
            return self._receipt(command, before, "presentation", changed, ["presentation-verified"])
        self.effects["subtitles"].append({
            "subtitle_id": f"speech-{command['call_id']}",
            "text": parameters["text"],
        })
        changed = ["subtitles"]
        evidence = ["subtitle-emitted"]
        if context.gates.tts_available:
            self.effects["speech"].append(parameters["text"])
            changed.append("tts")
            evidence.append("tts-emitted")
        return self._receipt(command, before, "voice", changed, evidence)


class KernelExecutor:
    def __init__(self, kernel):
        self.kernel = kernel

    def _receipt(self, before, after, delta, changed, evidence, kind=None):
        if kind is None:
            kind = "trial_state" if delta or changed != ["read"] else "none"
        return {
            "before_trial_ref": deepcopy(before),
            "after_trial_ref": deepcopy(after),
            "trial_revision_delta": delta,
            "global_revision": self.kernel.global_revision or None,
            "diff": {"kind": kind, "changed_field_ids": changed},
            "postcondition_evidence_ids": evidence,
        }

    @staticmethod
    def _trial_id(call_id):
        return "trial-" + hashlib.sha256(call_id.encode("ascii")).hexdigest()[:24]

    def __call__(self, command, context):
        proposal = context.proposal
        parameters = command["parameters"]
        operation = parameters["operation"]
        if command["tool"] == "state.read":
            ref = proposal["target"]["trial_ref"]
            return self._receipt(ref, ref, 0, ["read"], [])
        if command["tool"] == "trial.propose":
            trial_id = self._trial_id(command["call_id"])
            declaration = context.declaration
            source = proposal["target"]["source_trial_ref"]
            if operation == "create":
                after = self.kernel.create_trial(
                    trial_id,
                    declaration.setup_family_id,
                    parameters["target_parameters"],
                    proposed=True,
                    source_input_delta=parameters["input_delta"],
                    declared_downstream_effect_ids=parameters["input_delta"]["downstream_effect_ids"],
                    actual_initial_invariant_ids=declaration.invariant_ids,
                )
            else:
                delta = parameters["input_delta"]
                after = self.kernel.derive_trial(
                    trial_id,
                    source,
                    delta["parameter_id"],
                    delta["to_value"],
                    unit=delta.get("unit"),
                    downstream_effect_ids=delta["downstream_effect_ids"],
                )
            return self._receipt(source, after, 0, ["trial_created"], list(declaration.invariant_ids))

        target = proposal["target"]["trial_ref"]
        trial_id = target["trial_id"]
        previous_id = self.kernel.current_trial_id
        self.kernel.current_trial_id = trial_id
        try:
            if operation == "present":
                after = self.kernel.select_trial(trial_id)
                return self._receipt(
                    target, after, 0, ["presentation_selection"], ["selection-verified"], "presentation"
                )
            if operation == "start":
                after = self.kernel.start_trial()
            elif operation == "pause":
                after = self.kernel.pause()
            elif operation == "resume":
                after = self.kernel.resume()
            elif operation == "archive":
                after = self.kernel.archive(generation_committed=context.gates.generation_committed)
            else:
                trial = self.kernel.current
                trial.measurement_open = True
                after = trial.ref.to_dict()
            return self._receipt(target, after, after["trial_revision"] - target["trial_revision"], [operation], [f"{operation}-verified"])
        finally:
            if operation != "present":
                self.kernel.current_trial_id = previous_id


class ProposalValidator:
    def __init__(self, kernel, declarations, store=None, executors=None, gates=None):
        self.kernel = kernel
        self.declarations = {(item.tool, item.operation): item for item in declarations}
        if len(self.declarations) != len(declarations):
            raise ValueError("duplicate tool declaration")
        self.store = store or InMemoryIdempotencyStore()
        self.gates = gates or LocalGates()
        self.local_effects = LocalEffectExecutor()
        self.executors = {
            "state.read": KernelExecutor(kernel),
            "trial.propose": KernelExecutor(kernel),
            "trial.orchestrate": KernelExecutor(kernel),
            "presentation.request": self.local_effects,
            "speech.emit": self.local_effects,
        }
        self.executors.update(executors or {})
        self.grants = {}
        self.revoked_challenge_ids = set()
        self.schema = SchemaValidator()

    @staticmethod
    def _normalize_number(value):
        if isinstance(value, float):
            if not math.isfinite(value):
                raise ContractError("non-finite number")
            if value == 0:
                return 0
            if value.is_integer() and abs(value) <= 9007199254740991:
                return int(value)
        return value

    @classmethod
    def _normalize_scalars(cls, value):
        if isinstance(value, dict):
            return {key: cls._normalize_scalars(child) for key, child in value.items()}
        if isinstance(value, list):
            return [cls._normalize_scalars(child) for child in value]
        return cls._normalize_number(value)

    @staticmethod
    def _sorted_ids(values):
        return sorted(set(values), key=lambda item: item.encode("utf-8"))

    def canonicalize(self, proposal):
        if isinstance(proposal, (str, bytes)):
            value = parse_json(proposal)
        else:
            value = deepcopy(proposal)
        if not isinstance(value, dict):
            raise ContractError("proposal must be an object")
        if "refs" in value and isinstance(value["refs"], list):
            unique = {canonical_bytes(item): item for item in value["refs"]}
            value["refs"] = sorted(
                unique.values(),
                key=lambda item: (item["trial_id"].encode("utf-8"), item["trial_revision"]),
            )
        parameters = value.get("parameters")
        if isinstance(parameters, dict):
            if "text" in parameters and isinstance(parameters["text"], str):
                parameters["text"] = unicodedata.normalize("NFC", parameters["text"])
            for field_name in ("measurement_ids", "expected_invariant_ids"):
                if isinstance(parameters.get(field_name), list):
                    parameters[field_name] = self._sorted_ids(parameters[field_name])
            delta = parameters.get("input_delta")
            if isinstance(delta, dict) and isinstance(delta.get("downstream_effect_ids"), list):
                delta["downstream_effect_ids"] = self._sorted_ids(delta["downstream_effect_ids"])
            if "target_parameters" in parameters:
                parameters["target_parameters"] = self._normalize_scalars(parameters["target_parameters"])
            if isinstance(delta, dict):
                for field_name in ("from_value", "to_value"):
                    if field_name in delta:
                        delta[field_name] = self._normalize_number(delta[field_name])
        return value

    def _fingerprint_value(self, canonical):
        return {
            key: deepcopy(canonical[key])
            for key in ("tool_schema_version", "origin", "tool", "target", "basis_trial_ref", "refs", "parameters")
        }

    def fingerprint(self, proposal):
        canonical = self.canonicalize(proposal)
        errors = self._structural_errors(canonical, proposal)
        if errors:
            raise ContractError("; ".join(errors))
        return hashlib.sha256(DOMAIN_SEPARATOR + canonical_bytes(self._fingerprint_value(canonical))).hexdigest()

    def _structural_errors(self, canonical, original):
        raw = original if isinstance(original, str) else json.dumps(canonical, ensure_ascii=False)
        errors = self.schema.validate(canonical, "tool-proposal.schema.json")
        if not errors:
            errors = semantic_errors(canonical, "tool-proposal.schema.json", raw)
            errors = [item for item in errors if item != "$.origin: safety speech requires guide_default"]
        return errors

    def _scope(self, canonical):
        target = canonical["target"]
        if target["kind"] == "existing":
            trial_id = target["trial_ref"]["trial_id"]
            trial = self.kernel.trials.get(trial_id)
            episode_id = trial.identity["episode_id"] if trial is not None else self.kernel.episode_id
            return {"episode_id": episode_id, "target_trial_id": trial_id}
        source = target["source_trial_ref"]
        return {
            "episode_id": target["episode_id"],
            "target_trial_id": source["trial_id"] if source is not None else None,
        }

    def _result(self, canonical, fingerprint, outcome, *, reason=None, stage=None, command_id=None, receipt=None, replay=False):
        return {
            "tool_schema_version": canonical["tool_schema_version"],
            "idempotency_scope": self._scope(canonical),
            "call_id": canonical["call_id"],
            "fingerprint": fingerprint,
            "outcome": outcome,
            "reason_code": reason,
            "target": deepcopy(canonical["target"]),
            "basis_trial_ref": deepcopy(canonical["basis_trial_ref"]),
            "refs": deepcopy(canonical["refs"]),
            "accepted_command_id": command_id,
            "execution_receipt": deepcopy(receipt),
            "rejection_stage": stage,
            "replay": replay,
        }

    def _record(self, result):
        return {
            "idempotency_scope": deepcopy(result["idempotency_scope"]),
            "call_id": result["call_id"],
            "fingerprint": result["fingerprint"],
            "first_result": deepcopy(result),
        }

    def _associated_trial(self, canonical):
        target = canonical["target"]
        if target["kind"] == "existing":
            return self.kernel.trials.get(target["trial_ref"]["trial_id"])
        source = target["source_trial_ref"]
        return self.kernel.trials.get(source["trial_id"]) if source is not None else None

    def _append_result(self, canonical, result):
        if result["outcome"] == "executed" and canonical["tool"] == "state.read":
            return
        trial = self._associated_trial(canonical)
        if trial is None:
            return
        entry_kind = "proposal_executed" if result["outcome"] == "executed" else "proposal_rejected"
        self.kernel._append_ledger(
            trial,
            entry_kind,
            "proposal_validator",
            fact_id=result["call_id"],
            reason_code=result["reason_code"],
        )

    def _persist_rejection(self, canonical, fingerprint, reason, stage):
        result = self._result(canonical, fingerprint, "rejected", reason=reason, stage=stage)
        self._append_result(canonical, result)
        self.store.commit(self._record(result))
        return result

    def _fallback_rejection(self, proposal, error):
        canonical = {
            "tool_schema_version": "1.0.0",
            "call_id": "invalid-call",
            "origin": "model",
            "tool": "state.read",
            "target": {"kind": "new_trial", "episode_id": self.kernel.episode_id, "source_trial_ref": None},
            "basis_trial_ref": None,
            "refs": [],
            "parameters": {"operation": "snapshot", "target_trial_ref": {"trial_id": "invalid", "trial_revision": 1}},
        }
        if isinstance(proposal, dict):
            for field_name in ("tool_schema_version", "call_id", "origin", "tool", "target", "basis_trial_ref", "refs"):
                if field_name in proposal:
                    canonical[field_name] = deepcopy(proposal[field_name])
        fingerprint = hashlib.sha256(DOMAIN_SEPARATOR + canonical_bytes(canonical)).hexdigest()
        try:
            return self._result(canonical, fingerprint, "rejected", reason="malformed_parameters", stage="step_1")
        except (KeyError, TypeError):
            canonical["target"] = {"kind": "new_trial", "episode_id": self.kernel.episode_id, "source_trial_ref": None}
            return self._result(canonical, fingerprint, "rejected", reason="malformed_parameters", stage="step_1")

    def _token_set(self):
        refs = self.kernel.current_refs()
        if self.kernel.guide is not None:
            return self.kernel.guide.projection(self.kernel.current, refs)["policy_tokens"]
        return self.kernel._default_guide_projection(self.kernel.current, refs)["policy_tokens"]

    def _tokens(self, kind=None, value_id=None):
        tokens = self._token_set()["tokens"]
        return [
            item for item in tokens
            if (kind is None or item["kind"] == kind) and (value_id is None or item["value_id"] == value_id)
        ]

    def _check_ref(self, ref):
        trial = self.kernel.trials.get(ref["trial_id"])
        if trial is None:
            return "unknown_trial"
        if ref["trial_id"] not in self.kernel.comparison_trial_ids:
            return "ref_outside_comparison"
        if trial.state["trial_revision"] != ref["trial_revision"]:
            return "stale_trial_ref"
        return None

    def _check_citations(self, parameters):
        try:
            self.kernel.admit_speech(self.kernel.current_refs(), parameters.get("citations", []))
        except KernelError as error:
            if error.reason_code == "measurement_not_citable":
                for citation in parameters.get("citations", []):
                    for trial in self.kernel.trials.values():
                        for measurement in trial.measurements:
                            if measurement["measurement_id"] != citation["measurement_id"]:
                                continue
                            projected = self.kernel._project_measurement(trial, measurement)
                            if projected["applicability"] == "masked_until_prediction":
                                return "masked_measurement_citation"
                return "display_tuple_unavailable"
            mapping = {
                "unknown_measurement_citation": "citation_not_in_context",
                "stale_measurement_citation": "citation_not_in_context",
                "display_tuple_required": "display_tuple_unavailable",
                "display_tuple_incomplete": "display_tuple_unavailable",
                "stale_trial_ref": "stale_trial_ref",
            }
            return mapping.get(error.reason_code, "citation_not_in_context")
        return None

    def _step3(self, canonical):
        target = canonical["target"]
        if target["kind"] == "new_trial" and target["episode_id"] != self.kernel.episode_id:
            return "unknown_trial"
        refs_to_check = list(canonical["refs"])
        if target["kind"] == "existing":
            refs_to_check.append(target["trial_ref"])
        if target["kind"] == "new_trial" and target["source_trial_ref"] is not None:
            refs_to_check.append(target["source_trial_ref"])
        if canonical["basis_trial_ref"] is not None:
            refs_to_check.append(canonical["basis_trial_ref"])
        for ref in refs_to_check:
            reason = self._check_ref(ref)
            if reason is not None:
                return reason
        parameters = canonical["parameters"]
        if canonical["tool"] == "state.read" and parameters["operation"] == "measurements":
            trial = self.kernel.trials[target["trial_ref"]["trial_id"]]
            available = {item["measurement_id"] for item in trial.measurements}
            if not set(parameters["measurement_ids"]) <= available:
                return "citation_not_in_context"
        return self._check_citations(parameters)

    def _step4(self, canonical, declaration, fingerprint):
        parameters = canonical["parameters"]
        operation = parameters["operation"]
        if not self._tokens("allowed_action", declaration.action_id):
            return "token_invalid"
        if canonical["tool"] == "trial.propose":
            tokens = self._tokens("setup_family")
            issued = next((item for item in tokens if item["token_id"] == parameters["setup_family_token_id"]), None)
            if issued is None or issued["value_id"] != declaration.setup_family_id:
                return "token_invalid"
        if operation == "safety" and canonical["origin"] != "guide_default":
            return "safety_boundary_failed"
        if canonical["tool"] == "speech.emit":
            if self.kernel.preferences["mute"]:
                return "preference_blocked"
            if operation == "proactive":
                if not any(item["token_id"] == parameters["candidate_token_id"] for item in self._tokens("proactive_candidate")):
                    return "token_invalid"
                if (
                    self.gates.cooldown_active
                    or self.gates.backoff_active
                    or self.gates.user_speaking
                    or self.gates.user_swinging
                    or not self.kernel.preferences["tips_enabled"]
                ):
                    return "preference_blocked"
            solicitation_id = parameters.get("solicitation_token_id")
            if solicitation_id is not None:
                solicitation = next((item for item in self._tokens("solicitation") if item["token_id"] == solicitation_id), None)
                if solicitation is None:
                    return "token_invalid"
                if solicitation["value_id"] not in self.gates.playback_completed_solicitation_ids:
                    return "preference_blocked"
            if operation == "safety" and parameters["safety_candidate_id"] not in self.gates.safety_candidate_ids:
                return "safety_boundary_failed"
        if declaration.confirmation_required:
            confirmation_id = canonical.get("confirmation_id")
            if confirmation_id is None:
                return "confirmation_required"
            grant = self.grants.get(confirmation_id)
            if grant is None or confirmation_id in self.revoked_challenge_ids:
                return "confirmation_stale"
            if grant["call_id"] != canonical["call_id"]:
                return "confirmation_fingerprint_mismatch"
            if grant["fingerprint"] != fingerprint:
                return "confirmation_fingerprint_mismatch"
            if grant["refs"] != canonical["refs"] or grant["basis_trial_ref"] != canonical["basis_trial_ref"]:
                return "confirmation_stale"
            if grant["refs"] != sorted(self.kernel.current_refs(), key=lambda item: (item["trial_id"], item["trial_revision"])):
                return "confirmation_stale"
        return None

    def _step5(self, canonical, declaration):
        parameters = canonical["parameters"]
        operation = parameters["operation"]
        target = canonical["target"]
        if target["kind"] == "existing":
            trial = self.kernel.trials[target["trial_ref"]["trial_id"]]
            if operation == "start" and trial.state["acceptance"] != "accepted":
                return "proposal_not_accepted"
            legal = {
                "start": trial.state["phase"] == "setup",
                "pause": trial.state["activity"] == "running" and trial.state["phase"] not in {"archived", "abandoned"},
                "resume": trial.state["activity"] == "paused",
                "begin_measurement": trial.state["phase"] not in {"setup", "archived", "abandoned"},
                "archive": trial.state["phase"] == "measured" and self.gates.generation_committed,
                "present": target["trial_ref"]["trial_id"] in self.kernel.comparison_trial_ids,
            }
            if operation in legal and not legal[operation]:
                return "invalid_transition"
        if canonical["tool"] == "presentation.request":
            checks = {
                "highlight_surface": parameters.get("surface_id") in self.gates.surface_ids and parameters.get("highlight_id") in self.gates.highlight_ids,
                "replay_slow": parameters.get("event_id") in self.gates.replay_event_ids and parameters.get("replay_speed_id") in self.gates.replay_speed_ids,
                "open_chart": parameters.get("slot_id") in self.gates.chart_slot_ids and parameters.get("chart_id") in self.gates.chart_ids,
                "show_subtitle": True,
            }
            if not checks[operation]:
                return "episode_precondition_failed"
        if canonical["tool"] != "trial.propose":
            return None
        targets = parameters["target_parameters"]
        if RESULT_PARAMETER_IDS & set(targets):
            return "result_quantity_forbidden"
        if set(targets) != set(declaration.controllable_inputs):
            return "episode_precondition_failed"
        delta = parameters["input_delta"]
        parameter_id = delta["parameter_id"]
        if parameter_id not in targets or delta["to_value"] != targets[parameter_id]:
            return "input_delta_invalid"
        if set(delta["downstream_effect_ids"]) != set(declaration.downstream_effect_ids):
            return "input_delta_invalid"
        if set(parameters["expected_invariant_ids"]) != set(declaration.invariant_ids):
            return "numeric_invariant_failed"
        if operation == "create":
            baseline = declaration.default_parameters
        else:
            source = self.kernel.trials[target["source_trial_ref"]["trial_id"]]
            baseline = source.configuration["target_parameters"]
            if source.configuration["setup_family_id"] != declaration.setup_family_id:
                return "episode_precondition_failed"
            if delta.get("from_value") != baseline.get(parameter_id):
                return "input_delta_invalid"
        expected = deepcopy(baseline)
        expected[parameter_id] = delta["to_value"]
        if targets != expected:
            return "input_delta_invalid"
        return None

    def _step6(self, canonical, declaration):
        if not self.gates.scene_valid or not self.gates.numerical_state_ok:
            return "numeric_invariant_failed"
        if not self.gates.storage_available and canonical["tool"] == "trial.propose":
            return "numeric_invariant_failed"
        if self.gates.hard_safety_hold:
            return "safety_boundary_failed"
        if canonical["tool"] == "trial.propose":
            parameters = canonical["parameters"]
            delta = parameters["input_delta"]
            for parameter_id, value in parameters["target_parameters"].items():
                rule = declaration.controllable_inputs[parameter_id]
                if isinstance(value, float) and not math.isfinite(value):
                    return "numeric_invariant_failed"
                if isinstance(value, (int, float)) and not isinstance(value, bool):
                    if "minimum" in rule and value < rule["minimum"]:
                        return "numeric_invariant_failed"
                    if "maximum" in rule and value > rule["maximum"]:
                        return "numeric_invariant_failed"
                if "unit" in rule and delta.get("unit") != rule["unit"] and parameter_id == delta["parameter_id"]:
                    return "numeric_invariant_failed"
        return None

    def _checkpoint(self):
        executor_states = {}
        for name, executor in self.executors.items():
            if hasattr(executor, "checkpoint"):
                executor_states[name] = executor.checkpoint()
        return deepcopy(self.kernel.__dict__), executor_states, deepcopy(self.grants)

    def _restore(self, checkpoint):
        kernel_state, executor_states, grants = checkpoint
        self.kernel.__dict__ = kernel_state
        if self.kernel.guide is not None:
            self.kernel.guide.kernel = self.kernel
        for name, state in executor_states.items():
            self.executors[name].restore(state)
        self.grants = grants

    def _command(self, canonical, fingerprint):
        digest = hashlib.sha256(canonical_bytes({
            "scope": self._scope(canonical),
            "call_id": canonical["call_id"],
            "fingerprint": fingerprint,
        })).hexdigest()
        return {
            "command_id": "command-" + digest[:24],
            "idempotency_scope": self._scope(canonical),
            "call_id": canonical["call_id"],
            "fingerprint": fingerprint,
            "tool": canonical["tool"],
            "parameters": deepcopy(canonical["parameters"]),
            "refs": deepcopy(canonical["refs"]),
        }

    def _verify_receipt(self, command, receipt):
        operation = command["parameters"]["operation"]
        expected_delta = 0 if operation in ZERO_REVISION_OPERATIONS[command["tool"]] else 1
        if receipt["trial_revision_delta"] != expected_delta:
            return False
        expected_kind = "presentation" if operation == "present" else TOOL_DIFF_KINDS[command["tool"]]
        if receipt["diff"]["kind"] != expected_kind:
            return False
        before = receipt["before_trial_ref"]
        after = receipt["after_trial_ref"]
        if command["tool"] != "trial.propose":
            if before != after and expected_delta == 0:
                return False
            if before is not None and after is not None:
                if after["trial_id"] != before["trial_id"]:
                    return False
                if after["trial_revision"] - before["trial_revision"] != expected_delta:
                    return False
        if command["tool"] == "trial.propose":
            trial = self.kernel.trials.get(KernelExecutor._trial_id(command["call_id"]))
            if trial is None or trial.state["phase"] != "setup" or trial.state["acceptance"] != "proposed" or trial.state["tick"] != 0:
                return False
        receipt_schema = self.schema.documents["validation-result.schema.json"]["$defs"]["ExecutionReceipt"]
        errors = self.schema.validate_value(
            receipt, receipt_schema, "validation-result.schema.json", "$"
        )
        return not errors

    def create_challenge(self, proposal):
        canonical = self.canonicalize(proposal)
        errors = self._structural_errors(canonical, proposal)
        if errors:
            raise ContractError("; ".join(errors))
        fingerprint = hashlib.sha256(DOMAIN_SEPARATOR + canonical_bytes(self._fingerprint_value(canonical))).hexdigest()
        digest = hashlib.sha256(canonical_bytes({"call_id": canonical["call_id"], "fingerprint": fingerprint})).hexdigest()
        return {
            "record_kind": "challenge",
            "challenge_id": "challenge-" + digest[:24],
            "call_id": canonical["call_id"],
            "fingerprint": fingerprint,
            "target": deepcopy(canonical["target"]),
            "basis_trial_ref": deepcopy(canonical["basis_trial_ref"]),
            "refs": deepcopy(canonical["refs"]),
        }

    def register_grant(self, grant):
        errors = self.schema.validate(grant, "confirmation.schema.json")
        if not errors:
            errors = semantic_errors(grant, "confirmation.schema.json", json.dumps(grant))
        if errors or grant.get("record_kind") != "grant":
            raise ContractError("invalid confirmation grant")
        self.grants[grant["challenge_id"]] = deepcopy(grant)
        self.revoked_challenge_ids.discard(grant["challenge_id"])

    def revoke_grant(self, challenge_id):
        self.revoked_challenge_ids.add(challenge_id)

    def validate(self, proposal):
        try:
            canonical = self.canonicalize(proposal)
            errors = self._structural_errors(canonical, proposal)
        except (ContractError, KeyError, TypeError, ValueError) as error:
            return self._fallback_rejection(proposal, error)
        fingerprint = hashlib.sha256(DOMAIN_SEPARATOR + canonical_bytes(self._fingerprint_value(canonical))).hexdigest()
        if errors:
            reason = "unsupported_contract_version" if any("unsupported major version" in item for item in errors) else "malformed_parameters"
            try:
                return self._persist_rejection(canonical, fingerprint, reason, "step_1")
            except (ContractError, KeyError, TypeError) as error:
                raise ContractError("cannot persist admitted proposal rejection") from error
        scope = self._scope(canonical)
        existing = self.store.get(scope, canonical["call_id"])
        if existing is not None:
            if existing["fingerprint"] == fingerprint:
                replay = deepcopy(existing["first_result"])
                replay["replay"] = True
                return replay
            return self._result(canonical, fingerprint, "rejected", reason="idempotency_conflict", stage="step_2")
        reason = self._step3(canonical)
        if reason is not None:
            return self._persist_rejection(canonical, fingerprint, reason, "step_3")
        declaration = self.declarations.get((canonical["tool"], canonical["parameters"]["operation"]))
        if declaration is None:
            return self._persist_rejection(canonical, fingerprint, "episode_precondition_failed", "step_5")
        reason = self._step4(canonical, declaration, fingerprint)
        if reason is not None:
            return self._persist_rejection(canonical, fingerprint, reason, "step_4")
        reason = self._step5(canonical, declaration)
        if reason is not None:
            stage = "step_6" if reason == "numeric_invariant_failed" else "step_5"
            return self._persist_rejection(canonical, fingerprint, reason, stage)
        reason = self._step6(canonical, declaration)
        if reason is not None:
            return self._persist_rejection(canonical, fingerprint, reason, "step_6")
        checkpoint = self._checkpoint()
        command = self._command(canonical, fingerprint)
        context = ExecutionContext(canonical, declaration, deepcopy(self.gates))
        try:
            receipt = self.executors[canonical["tool"]](command, context)
            if not self._verify_receipt(command, receipt):
                raise ExecutionError("execution_postcondition_failed")
            result = self._result(
                canonical,
                fingerprint,
                "executed",
                command_id=command["command_id"],
                receipt=receipt,
            )
            self._append_result(canonical, result)
            if declaration.confirmation_required and "confirmation_id" in canonical:
                del self.grants[canonical["confirmation_id"]]
            self.store.commit(self._record(result))
            return result
        except (ExecutionError, KernelError, ContractError, KeyError, TypeError, ValueError) as error:
            self._restore(checkpoint)
            reason = error.reason_code if isinstance(error, (ExecutionError, KernelError)) else "execution_unavailable"
            if reason not in {"execution_postcondition_failed", "execution_unavailable"}:
                reason = "execution_postcondition_failed"
            result = self._result(canonical, fingerprint, "rejected", reason=reason, stage="step_7")
            self.store.commit(self._record(result))
            return result

    def validate_reply(self, proposals):
        admitted = []
        for proposal in proposals:
            try:
                canonical = self.canonicalize(proposal)
                errors = self._structural_errors(canonical, proposal)
            except (ContractError, KeyError, TypeError, ValueError) as error:
                return [self._fallback_rejection(proposal, error)]
            fingerprint = hashlib.sha256(DOMAIN_SEPARATOR + canonical_bytes(self._fingerprint_value(canonical))).hexdigest()
            if errors:
                return [self.validate(proposal)]
            existing = self.store.get(self._scope(canonical), canonical["call_id"])
            if existing is None:
                reason = self._step3(canonical)
                if reason is not None:
                    return [self._persist_rejection(canonical, fingerprint, reason, "step_3")]
            admitted.append(proposal)
        return [self.validate(proposal) for proposal in admitted]
