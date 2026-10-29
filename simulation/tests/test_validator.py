from copy import deepcopy
import json
import unittest

from schema.validate import ContractError, SchemaValidator, semantic_errors
from simulation.fake_episode import FakeDiscreteEpisode, fake_stage_data
from simulation.guide import GuideRunner
from simulation.kernel import TrialKernel
from simulation.validator import (
    InMemoryIdempotencyStore,
    LocalGates,
    ProposalValidator,
    ToolDeclaration,
)


TOOLS = {
    "state.read": ("snapshot", "ledger", "measurements"),
    "trial.propose": ("create", "derive"),
    "trial.orchestrate": ("start", "pause", "resume", "begin_measurement", "archive", "present"),
    "presentation.request": ("highlight_surface", "replay_slow", "open_chart", "show_subtitle"),
    "speech.emit": ("answer", "proactive", "safety"),
}


def declarations(confirm_derive=False):
    result = []
    for tool, operations in TOOLS.items():
        for operation in operations:
            result.append(ToolDeclaration(
                action_id=f"action-{tool}-{operation}",
                tool=tool,
                operation=operation,
                setup_family_id="discrete-count" if tool == "trial.propose" else None,
                controllable_inputs={"count": {"minimum": 0, "maximum": 10}} if tool == "trial.propose" else {},
                default_parameters={"count": 3} if tool == "trial.propose" else {},
                downstream_effect_ids=("discrete-state",) if tool == "trial.propose" else (),
                invariant_ids=("finite-state", "positive-count") if tool == "trial.propose" else ("finite-state",),
                confirmation_required=confirm_derive and tool == "trial.propose" and operation == "derive",
            ))
    return result


def measurement(measurement_id="measurement-a", *, required=()):
    result = {
        "measurement_id": measurement_id,
        "value": 3,
        "unit_id": "count",
        "coordinate_frame_id": "trial-local",
        "quantity_convention_id": "discrete-count",
        "window": {
            "window_id": f"window-{measurement_id}",
            "start_tick": 0,
            "end_tick": 0,
            "duration_seconds": 0.0,
        },
        "sample_count": 1,
        "uncertainty": None,
        "sufficient": True,
        "applicability": "valid",
        "calculation_version": "1.0.0",
        "required_display_components": required,
    }
    if required:
        result["display_tuple"] = {
            "display_tuple_id": f"tuple-{measurement_id}",
            "components": [
                {"component_id": item, "value": 3, "unit_id": "count"} for item in required
            ],
        }
    return result


def environment(*, discrete=True, prediction_attachments=(), confirm_derive=False, store=None):
    kernel = TrialKernel(
        "fake-discrete",
        "fake_discrete",
        continuous_phases=() if discrete else ("baseline", "intervention", "settling"),
        prediction_attachments=prediction_attachments,
        setup_family_ids=("discrete-count",),
    )
    kernel.create_trial("trial-a", "discrete-count", {"count": 3})
    declared = declarations(confirm_derive)
    stage = fake_stage_data()
    stage["allowed_action_set"]["action_ids"] = [item.action_id for item in declared]
    guide = GuideRunner(kernel, stage)
    gates = LocalGates(
        surface_ids={"surface-a"},
        highlight_ids={"highlight-a"},
        replay_event_ids={"event-a"},
        replay_speed_ids={"slow-half"},
        chart_slot_ids={"slot-a"},
        chart_ids={"chart-a"},
        safety_candidate_ids={"safety-a"},
    )
    validator = ProposalValidator(kernel, declared, store=store, gates=gates)
    return kernel, guide, validator


def token(guide, kind, value_id=None):
    token_set = guide.projection(guide.kernel.current, guide.kernel.current_refs())["policy_tokens"]
    return next(
        item["token_id"] for item in token_set["tokens"]
        if item["kind"] == kind and (value_id is None or item["value_id"] == value_id)
    )


def existing_proposal(kernel, tool, operation, call_id, *, origin="model", target_ref=None, **fields):
    target_ref = deepcopy(target_ref or kernel.current.ref.to_dict())
    basis = kernel.current.ref.to_dict()
    refs = [target_ref]
    if basis not in refs:
        refs.append(basis)
    parameters = {"operation": operation, **fields}
    if tool in {"state.read", "trial.orchestrate"}:
        parameters["target_trial_ref"] = deepcopy(target_ref)
    return {
        "tool_schema_version": "1.0.0",
        "call_id": call_id,
        "origin": origin,
        "tool": tool,
        "target": {"kind": "existing", "trial_ref": target_ref},
        "basis_trial_ref": basis,
        "refs": refs,
        "parameters": parameters,
    }


def new_proposal(kernel, guide, operation, call_id, *, value=4, origin="model", parameter_id="count"):
    source = None if operation == "create" else kernel.current.ref.to_dict()
    delta = {
        "parameter_id": parameter_id,
        "to_value": value,
        "downstream_effect_ids": ["discrete-state"],
    }
    if source is not None:
        delta["from_value"] = kernel.current.configuration["target_parameters"][parameter_id]
    return {
        "tool_schema_version": "1.0.0",
        "call_id": call_id,
        "origin": origin,
        "tool": "trial.propose",
        "target": {"kind": "new_trial", "episode_id": kernel.episode_id, "source_trial_ref": source},
        "basis_trial_ref": deepcopy(source),
        "refs": [] if source is None else [deepcopy(source)],
        "parameters": {
            "operation": operation,
            "setup_family_token_id": token(guide, "setup_family", "discrete-count"),
            "target_parameters": {"count": value},
            "input_delta": delta,
            "expected_invariant_ids": ["finite-state", "positive-count"],
        },
    }


def speech_proposal(kernel, operation, call_id, *, origin="model", citations=(), **fields):
    return existing_proposal(
        kernel,
        "speech.emit",
        operation,
        call_id,
        origin=origin,
        text="Synthetic count is three.",
        source_global_revision=max(1, kernel.global_revision),
        citations=list(citations),
        **fields,
    )


def grant_for(validator, proposal):
    challenge = validator.create_challenge(proposal)
    grant = {
        "record_kind": "grant",
        "challenge_id": challenge["challenge_id"],
        "call_id": challenge["call_id"],
        "fingerprint": challenge["fingerprint"],
        "basis_trial_ref": deepcopy(challenge["basis_trial_ref"]),
        "refs": deepcopy(challenge["refs"]),
    }
    validator.register_grant(grant)
    return challenge["challenge_id"]


class ProposalValidatorTest(unittest.TestCase):
    def assert_result_valid(self, result):
        schema = SchemaValidator()
        errors = schema.validate(result, "validation-result.schema.json")
        if not errors:
            errors = semantic_errors(result, "validation-result.schema.json", json.dumps(result))
        self.assertEqual([], errors)

    def test_01_closed_schema_rejects_forbidden_capabilities_without_effects(self):
        kernel, guide, validator = environment()
        base = existing_proposal(kernel, "state.read", "snapshot", "call-closed")
        variants = []
        for field, value in (
            ("script", "synthetic-nonexecutable-text"),
            ("url", "network-request-is-not-a-tool-capability"),
            ("position", [0, 0, 0]),
            ("temperature", 3),
            ("unknown", True),
        ):
            proposal = deepcopy(base)
            proposal["parameters"][field] = value
            variants.append(proposal)
        wrong_operation = deepcopy(base)
        wrong_operation["parameters"]["operation"] = "execute"
        variants.append(wrong_operation)
        variants.append(json.dumps(base).replace('"call_id":', '"duplicate": 1, "call_id":', 1))
        variants.append(json.dumps(base).replace('"tool_schema_version": "1.0.0"', '"tool_schema_version": 1.0'))
        variants.append(json.dumps(base).replace('"tool_schema_version": "1.0.0"', '"tool_schema_version": NaN'))
        for index, proposal in enumerate(variants):
            if isinstance(proposal, dict):
                proposal["call_id"] = f"call-closed-{index}"
            else:
                proposal = proposal.replace('"call_id": "call-closed"', f'"call_id": "call-closed-{index}"')
                variants[index] = proposal
        revision = kernel.current.state["trial_revision"]
        for index, proposal in enumerate(variants):
            with self.subTest(index=index):
                result = validator.validate(proposal)
                self.assertEqual("step_1", result["rejection_stage"])
                self.assertEqual(revision, kernel.current.state["trial_revision"])
                self.assertFalse(validator.local_effects.effects["microphone_open"])
                self.assertEqual([], validator.local_effects.effects["speech"])
        unsupported = deepcopy(base)
        unsupported["call_id"] = "call-version"
        unsupported["tool_schema_version"] = "2.0.0"
        result = validator.validate(unsupported)
        self.assertEqual("unsupported_contract_version", result["reason_code"])

    def test_02_every_operation_has_only_its_declared_authority(self):
        for tool, operations in TOOLS.items():
            for operation in operations:
                with self.subTest(tool=tool, operation=operation):
                    kernel, guide, validator = environment(discrete=False)
                    if tool == "state.read":
                        fields = {}
                        if operation == "ledger":
                            fields = {"after_sequence": 0, "max_entries": 10}
                        elif operation == "measurements":
                            fields = {"measurement_ids": []}
                        proposal = existing_proposal(kernel, tool, operation, f"call-{operation}", **fields)
                    elif tool == "trial.propose":
                        proposal = new_proposal(kernel, guide, operation, f"call-{operation}")
                    elif tool == "trial.orchestrate":
                        if operation == "resume":
                            kernel.pause()
                        elif operation in {"begin_measurement", "archive"}:
                            kernel.start_trial()
                            if operation == "archive":
                                kernel.current.state["phase"] = "measured"
                        target = None
                        refs = None
                        if operation == "present":
                            kernel.create_trial("trial-b", "discrete-count", {"count": 3})
                            target = kernel.trials["trial-b"].ref.to_dict()
                        proposal = existing_proposal(kernel, tool, operation, f"call-{operation}", target_ref=target)
                        if operation == "present":
                            proposal["refs"] = [kernel.trials["trial-a"].ref.to_dict(), target]
                    elif tool == "presentation.request":
                        fields = {
                            "highlight_surface": {"surface_id": "surface-a", "highlight_id": "highlight-a", "requested_state": "on"},
                            "replay_slow": {"event_id": "event-a", "replay_speed_id": "slow-half"},
                            "open_chart": {"slot_id": "slot-a", "chart_id": "chart-a"},
                            "show_subtitle": {"subtitle_id": "subtitle-a", "text": "Synthetic subtitle.", "source_global_revision": 1, "citations": []},
                        }[operation]
                        proposal = existing_proposal(kernel, tool, operation, f"call-{operation}", **fields)
                    else:
                        fields = {}
                        origin = "model"
                        if operation == "proactive":
                            fields["candidate_token_id"] = guide.issue_proactive_candidate("candidate-a")
                        elif operation == "safety":
                            fields["safety_candidate_id"] = "safety-a"
                            origin = "guide_default"
                        proposal = speech_proposal(kernel, operation, f"call-{operation}", origin=origin, **fields)
                    before = kernel.current.state["trial_revision"]
                    result = validator.validate(proposal)
                    self.assert_result_valid(result)
                    self.assertEqual("executed", result["outcome"], result)
                    self.assertFalse(validator.local_effects.effects["microphone_open"])
                    if tool in {"state.read", "trial.propose", "presentation.request", "speech.emit"} or operation in {"present", "begin_measurement"}:
                        self.assertEqual(0, result["execution_receipt"]["trial_revision_delta"])
                    else:
                        self.assertEqual(1, result["execution_receipt"]["trial_revision_delta"])
                    if tool == "state.read":
                        self.assertEqual(before, kernel.trials["trial-a"].state["trial_revision"])
                        self.assertEqual("none", result["execution_receipt"]["diff"]["kind"])

    def test_03_proposal_lifecycle_requires_user_acceptance_and_one_delta(self):
        kernel, guide, validator = environment(discrete=False)
        source = kernel.current.ref.to_dict()
        created = validator.validate(new_proposal(kernel, guide, "create", "call-create"))
        self.assertEqual("executed", created["outcome"])
        created_trial = kernel.trials[created["execution_receipt"]["after_trial_ref"]["trial_id"]]
        self.assertEqual("proposed", created_trial.state["acceptance"])
        self.assertEqual(0, created_trial.state["tick"])
        self.assertEqual("trial-a", kernel.current_trial_id)
        self.assertEqual(source, kernel.current.ref.to_dict())

        derived = validator.validate(new_proposal(kernel, guide, "derive", "call-derive"))
        child_ref = derived["execution_receipt"]["after_trial_ref"]
        proposed_start = existing_proposal(kernel, "trial.orchestrate", "start", "call-start-proposed", target_ref=child_ref)
        proposal = validator.validate(proposed_start)
        self.assertEqual("proposal_not_accepted", proposal["reason_code"])
        accepted = kernel.accept_trial(child_ref["trial_id"])
        started = validator.validate(existing_proposal(kernel, "trial.orchestrate", "start", "call-start", target_ref=accepted))
        self.assertEqual("executed", started["outcome"])
        self.assertEqual(source, kernel.trials["trial-a"].ref.to_dict())

        rejected = new_proposal(kernel, guide, "derive", "call-reject")
        rejected["parameters"]["target_parameters"] = {"count": 5}
        rejected["parameters"]["input_delta"]["to_value"] = 4
        result = validator.validate(rejected)
        self.assertEqual("input_delta_invalid", result["reason_code"])
        forbidden = new_proposal(kernel, guide, "derive", "call-result-target")
        forbidden["parameters"]["target_parameters"] = {"temperature": 4}
        forbidden["parameters"]["input_delta"]["parameter_id"] = "temperature"
        result = validator.validate(forbidden)
        self.assertEqual("result_quantity_forbidden", result["reason_code"])

    def test_04_fingerprint_canonicalization_and_semantic_sensitivity(self):
        kernel, guide, validator = environment()
        kernel.create_trial("trial-b", "discrete-count", {"count": 3})
        ref_a = kernel.trials["trial-a"].ref.to_dict()
        ref_b = kernel.trials["trial-b"].ref.to_dict()
        proposal = existing_proposal(kernel, "state.read", "measurements", "call-fingerprint", measurement_ids=["m-b", "m-a"])
        proposal["refs"] = [ref_b, ref_a]
        digest = validator.fingerprint(proposal)
        reordered = deepcopy(proposal)
        reordered["refs"] = [ref_a, ref_b, ref_a]
        reordered["parameters"]["measurement_ids"] = ["m-a", "m-b", "m-a"]
        self.assertEqual(digest, validator.fingerprint(reordered))
        self.assertEqual(digest, validator.fingerprint(json.dumps(reordered, indent=2)))
        mutations = []
        for field, value in (("origin", "guide_default"), ("tool", "trial.orchestrate"), ("basis_trial_ref", ref_b)):
            changed = deepcopy(proposal)
            changed[field] = value
            mutations.append(changed)
        changed = deepcopy(proposal)
        changed["target"]["trial_ref"] = ref_b
        mutations.append(changed)
        changed = deepcopy(proposal)
        changed["parameters"]["operation"] = "snapshot"
        changed["parameters"].pop("measurement_ids")
        mutations.append(changed)
        for changed in mutations:
            with self.subTest(changed=changed):
                try:
                    other = validator.fingerprint(changed)
                except ContractError:
                    other = None
                self.assertNotEqual(digest, other)
        text_a = speech_proposal(kernel, "answer", "call-text")
        text_b = deepcopy(text_a)
        text_b["parameters"]["text"] = "Synthetic count is 3."
        self.assertNotEqual(validator.fingerprint(text_a), validator.fingerprint(text_b))
        numeric_a = new_proposal(kernel, guide, "derive", "call-number")
        numeric_b = deepcopy(numeric_a)
        numeric_b["parameters"]["target_parameters"]["count"] = 5
        numeric_b["parameters"]["input_delta"]["to_value"] = 5
        self.assertNotEqual(validator.fingerprint(numeric_a), validator.fingerprint(numeric_b))

    def test_05_durable_replay_after_restart_and_stale_movement(self):
        kernel, guide, validator = environment()
        executed = existing_proposal(kernel, "state.read", "snapshot", "call-replay-executed")
        first = validator.validate(executed)
        rejected = speech_proposal(kernel, "proactive", "call-replay-rejected", candidate_token_id="unissued")
        first_rejected = validator.validate(rejected)
        self.assertEqual("token_invalid", first_rejected["reason_code"])
        kernel.close_interaction()
        restored = ProposalValidator(
            kernel,
            declarations(),
            store=InMemoryIdempotencyStore.restore(json.loads(json.dumps(validator.store.export()))),
            gates=validator.gates,
        )
        replay = restored.validate(executed)
        expected = deepcopy(first)
        expected["replay"] = True
        self.assertEqual(expected, replay)
        replay_rejected = restored.validate(rejected)
        expected = deepcopy(first_rejected)
        expected["replay"] = True
        self.assertEqual(expected, replay_rejected)
        self.assertEqual("token_invalid", replay_rejected["reason_code"])
        self.assert_result_valid(replay)
        self.assert_result_valid(replay_rejected)

    def test_06_conflict_precedes_freshness_episode_and_safety(self):
        kernel, guide, validator = environment()
        proposal = existing_proposal(kernel, "state.read", "snapshot", "call-conflict")
        self.assertEqual("executed", validator.validate(proposal)["outcome"])
        changed = existing_proposal(kernel, "state.read", "ledger", "call-conflict", after_sequence=0, max_entries=1)
        kernel.close_interaction()
        validator.gates.hard_safety_hold = True
        result = validator.validate(changed)
        self.assertEqual("idempotency_conflict", result["reason_code"])
        self.assertEqual("step_2", result["rejection_stage"])

    def test_07_freshness_citations_and_whole_reply_mask_rejection(self):
        kernel, guide, validator = environment()
        original = existing_proposal(kernel, "state.read", "snapshot", "call-stale")
        kernel.close_interaction()
        self.assertEqual("stale_trial_ref", validator.validate(original)["reason_code"])
        current = kernel.current.ref.to_dict()
        higher = deepcopy(current)
        higher["trial_revision"] += 1
        high = existing_proposal(kernel, "state.read", "snapshot", "call-high", target_ref=higher)
        self.assertEqual("stale_trial_ref", validator.validate(high)["reason_code"])
        unknown = deepcopy(current)
        unknown["trial_id"] = "missing"
        missing = existing_proposal(kernel, "state.read", "snapshot", "call-unknown", target_ref=unknown)
        self.assertEqual("unknown_trial", validator.validate(missing)["reason_code"])
        kernel.create_trial("trial-b", "discrete-count", {"count": 3})
        outside = kernel.trials["trial-b"].ref.to_dict()
        kernel.comparison_trial_ids.remove("trial-b")
        outside_proposal = existing_proposal(kernel, "state.read", "snapshot", "call-outside", target_ref=outside)
        self.assertEqual("ref_outside_comparison", validator.validate(outside_proposal)["reason_code"])

        masked_kernel, masked_guide, masked_validator = environment(discrete=False, prediction_attachments=("pre_reveal",))
        masked_kernel.start_trial()
        masked_kernel.commit_measurement(measurement())
        target = masked_kernel.current.ref.to_dict()
        masked_kernel.open_prediction("gate-a", "pre_reveal", "choices", ["up"], target, ["measurement-a"])
        citation = {"measurement_id": "measurement-a", "trial_ref": deepcopy(target)}
        masked_speech = speech_proposal(masked_kernel, "answer", "call-masked", citations=[citation])
        highlight = existing_proposal(
            masked_kernel,
            "presentation.request",
            "highlight_surface",
            "call-highlight-first",
            surface_id="surface-a",
            highlight_id="highlight-a",
            requested_state="on",
        )
        results = masked_validator.validate_reply([highlight, masked_speech])
        self.assertEqual("masked_measurement_citation", results[0]["reason_code"])
        self.assertEqual({}, masked_validator.local_effects.effects["highlights"])
        self.assertEqual([], masked_validator.local_effects.effects["speech"])

        tuple_kernel, tuple_guide, tuple_validator = environment(discrete=False)
        tuple_kernel.start_trial()
        tuple_kernel.commit_measurement(measurement(required=("value", "uncertainty")))
        ref = tuple_kernel.current.ref.to_dict()
        bad_citation = {"measurement_id": "measurement-a", "trial_ref": ref, "display_tuple_id": "wrong"}
        result = tuple_validator.validate(speech_proposal(tuple_kernel, "answer", "call-tuple", citations=[bad_citation]))
        self.assertEqual("display_tuple_unavailable", result["reason_code"])
        unknown_citation = {"measurement_id": "missing", "trial_ref": ref}
        result = tuple_validator.validate(speech_proposal(tuple_kernel, "answer", "call-citation", citations=[unknown_citation]))
        self.assertEqual("citation_not_in_context", result["reason_code"])

    def test_08_preferences_origin_and_solicitation_gates_never_open_mic(self):
        kernel, guide, validator = environment()
        proactive = speech_proposal(kernel, "proactive", "call-no-token", candidate_token_id="unissued")
        self.assertEqual("token_invalid", validator.validate(proactive)["reason_code"])
        candidate = guide.issue_proactive_candidate("candidate-a")
        for gate in ("cooldown_active", "backoff_active", "user_speaking", "user_swinging"):
            setattr(validator.gates, gate, True)
            proposal = speech_proposal(kernel, "proactive", f"call-{gate}", candidate_token_id=candidate)
            self.assertEqual("preference_blocked", validator.validate(proposal)["reason_code"])
            setattr(validator.gates, gate, False)
        kernel.preferences["tips_enabled"] = False
        proposal = speech_proposal(kernel, "proactive", "call-tips", candidate_token_id=candidate)
        self.assertEqual("preference_blocked", validator.validate(proposal)["reason_code"])
        kernel.preferences["tips_enabled"] = True
        kernel.preferences["mute"] = True
        proposal = speech_proposal(kernel, "answer", "call-mute")
        self.assertEqual("preference_blocked", validator.validate(proposal)["reason_code"])
        kernel.preferences["mute"] = False
        safety = speech_proposal(kernel, "safety", "call-model-safety", safety_candidate_id="safety-a")
        result = validator.validate(safety)
        self.assertEqual("safety_boundary_failed", result["reason_code"])
        self.assertEqual("step_4", result["rejection_stage"])

        prediction_kernel, prediction_guide, prediction_validator = environment(prediction_attachments=("pre_run",))
        prediction_kernel.commit_measurement(measurement())
        ref = prediction_kernel.current.ref.to_dict()
        prediction_id = prediction_kernel.open_prediction("gate-a", "pre_run", "choices", ["up"], ref, ["measurement-a"])
        solicitation = token(prediction_guide, "solicitation")
        answer = speech_proposal(prediction_kernel, "answer", "call-playback", solicitation_token_id=solicitation)
        self.assertEqual("preference_blocked", prediction_validator.validate(answer)["reason_code"])
        prediction_validator.gates.playback_completed_solicitation_ids.add(prediction_id)
        answer["call_id"] = "call-playback-complete"
        self.assertEqual("executed", prediction_validator.validate(answer)["outcome"])
        self.assertFalse(prediction_validator.local_effects.effects["microphone_open"])

    def test_09_confirmation_is_exact_single_use_and_revocable(self):
        kernel, guide, validator = environment(discrete=False, confirm_derive=True)
        missing = new_proposal(kernel, guide, "derive", "call-confirm-missing")
        self.assertEqual("confirmation_required", validator.validate(missing)["reason_code"])

        proposal = new_proposal(kernel, guide, "derive", "call-confirm")
        challenge_id = grant_for(validator, proposal)
        proposal["confirmation_id"] = challenge_id
        executed = validator.validate(proposal)
        self.assertEqual("executed", executed["outcome"], executed)
        mismatch_proposal = new_proposal(kernel, guide, "derive", "call-confirm-mismatch", value=5)
        mismatch_grant = grant_for(validator, mismatch_proposal)
        changed = deepcopy(mismatch_proposal)
        changed["parameters"]["target_parameters"]["count"] = 6
        changed["parameters"]["input_delta"]["to_value"] = 6
        changed["confirmation_id"] = mismatch_grant
        mismatch = validator.validate(changed)
        self.assertEqual("confirmation_fingerprint_mismatch", mismatch["reason_code"])
        changed_call = deepcopy(mismatch_proposal)
        changed_call["call_id"] = "call-other"
        changed_call["confirmation_id"] = mismatch_grant
        self.assertEqual("confirmation_fingerprint_mismatch", validator.validate(changed_call)["reason_code"])
        self.assertNotIn(challenge_id, validator.grants)
        replay = validator.validate(proposal)
        self.assertTrue(replay["replay"])
        self.assertEqual("executed", replay["outcome"])

        revoked_proposal = new_proposal(kernel, guide, "derive", "call-revoked", value=5)
        revoked = grant_for(validator, revoked_proposal)
        validator.revoke_grant(revoked)
        revoked_proposal["confirmation_id"] = revoked
        self.assertEqual("confirmation_stale", validator.validate(revoked_proposal)["reason_code"])

        for direction in ("lower", "higher"):
            stale = new_proposal(kernel, guide, "derive", f"call-stale-{direction}", value=6)
            grant = grant_for(validator, stale)
            stale["confirmation_id"] = grant
            if direction == "lower":
                kernel.close_interaction()
            else:
                for ref in stale["refs"]:
                    ref["trial_revision"] += 1
                stale["target"]["source_trial_ref"]["trial_revision"] += 1
                stale["basis_trial_ref"]["trial_revision"] += 1
                validator.grants[grant]["refs"] = deepcopy(stale["refs"])
                validator.grants[grant]["basis_trial_ref"] = deepcopy(stale["basis_trial_ref"])
            self.assertEqual("stale_trial_ref", validator.validate(stale)["reason_code"])

    def test_10_executor_and_commit_failures_are_atomic_with_degraded_speech(self):
        kernel, guide, validator = environment(discrete=False)

        class FailOnceStore(InMemoryIdempotencyStore):
            def __init__(self):
                super().__init__()
                self.fail = True

            def commit(self, record):
                if self.fail:
                    self.fail = False
                    raise ContractError("injected commit failure")
                super().commit(record)

        store = FailOnceStore()
        validator.store = store
        before = deepcopy(kernel.current.state)
        ledger = deepcopy(kernel.current.ledger)
        paused = existing_proposal(kernel, "trial.orchestrate", "pause", "call-commit-failure")
        result = validator.validate(paused)
        self.assertEqual("execution_unavailable", result["reason_code"])
        self.assertEqual(before, kernel.current.state)
        self.assertEqual(ledger, kernel.current.ledger)

        effects = validator.local_effects

        class BadPresentation:
            def checkpoint(self):
                return deepcopy(effects.effects)

            def restore(self, checkpoint):
                effects.restore(checkpoint)

            def __call__(self, command, context):
                receipt = effects(command, context)
                receipt["trial_revision_delta"] = 1
                return receipt

        validator.executors["presentation.request"] = BadPresentation()
        highlight = existing_proposal(
            kernel,
            "presentation.request",
            "highlight_surface",
            "call-bad-receipt",
            surface_id="surface-a",
            highlight_id="highlight-a",
            requested_state="on",
        )
        result = validator.validate(highlight)
        self.assertEqual("execution_postcondition_failed", result["reason_code"])
        self.assertEqual({}, effects.effects["highlights"])

        answer_validator = ProposalValidator(kernel, declarations(), gates=LocalGates(tts_available=False))
        answer = speech_proposal(kernel, "answer", "call-tts-unavailable")
        result = answer_validator.validate(answer)
        self.assertEqual("executed", result["outcome"])
        self.assertEqual(["subtitles"], result["execution_receipt"]["diff"]["changed_field_ids"])
        self.assertEqual([], answer_validator.local_effects.effects["speech"])
        self.assertEqual(1, len(answer_validator.local_effects.effects["subtitles"]))

    def test_11_context_and_policy_boundary_has_no_writeback(self):
        kernel, guide, validator = environment()
        context = kernel.build_context_payload("utterance-a", "Synthetic utterance.")
        before = deepcopy(context)
        schema = SchemaValidator()
        self.assertEqual([], schema.validate(context, "context-payload.schema.json"))
        proposal = speech_proposal(kernel, "answer", "call-context")
        result = validator.validate(proposal)
        self.assertEqual("executed", result["outcome"])
        self.assertEqual(before, context)
        forbidden = {"policy_envelope", "system", "rules", "next_questions", "raw_prompt", "authorization"}

        def walk(value):
            if isinstance(value, dict):
                self.assertFalse(forbidden & set(value))
                for child in value.values():
                    walk(child)
            elif isinstance(value, list):
                for child in value:
                    walk(child)

        walk(context)
        walk(result)
        context["compressed_session_context"]["minimal_profile"] = None
        self.assertNotIn("derived_summaries", context["compressed_session_context"])
        self.assertNotIn("policy_envelope", kernel.build_context_payload("utterance-b", "Offline."))

    def test_12_offline_default_and_fake_episode_use_identical_validation(self):
        first_kernel, first_guide, first = environment()
        second_kernel, second_guide, second = environment()
        self.assertEqual(first_guide.default_path(), second_guide.default_path())
        first_proposal = new_proposal(first_kernel, first_guide, "derive", "call-offline", origin="guide_default")
        second_proposal = new_proposal(second_kernel, second_guide, "derive", "call-offline", origin="guide_default")
        first_result = first.validate(first_proposal)
        second_result = second.validate(second_proposal)
        self.assertEqual(first_result, second_result)
        self.assertEqual(first_kernel.current.ledger, second_kernel.current.ledger)
        self.assertEqual("not_used", first_kernel.snapshot()["diagnostics"]["network_state"])
        self.assertEqual("unavailable", first_kernel.snapshot()["diagnostics"]["voice_state"])
        invalid = new_proposal(first_kernel, first_guide, "derive", "call-offline-invalid", origin="guide_default", value=99)
        other_invalid = new_proposal(second_kernel, second_guide, "derive", "call-offline-invalid", origin="guide_default", value=99)
        self.assertEqual(first.validate(invalid), second.validate(other_invalid))
        self.assertEqual("numeric_invariant_failed", first.validate(invalid)["reason_code"])
        episode = FakeDiscreteEpisode()
        self.assertEqual("runnable", episode.load_gate(["fake-discrete-asset"])["status"])


if __name__ == "__main__":
    unittest.main()
