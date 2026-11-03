import json
import unittest
from copy import deepcopy

from schema.validate import SchemaValidator, semantic_errors
from simulation.fake_episode import fake_stage_data
from simulation.guide import GuideRunner
from simulation.kernel import TrialKernel
from simulation.transport import (
    AdmissionResult,
    EvaluationDirectTransport,
    EvaluationRequestBuilder,
    ModelKeyHandle,
    ModelReplyAdmission,
    PolicyEnvelope,
    RecordedFixtureTransport,
    SenderRefused,
    SenderReply,
    SenderUnavailable,
    TransportError,
    build_provenance,
    canonical_request_bytes,
    provenance_errors,
    redact_request_for_log,
    request_hash,
    response_hash,
)
from simulation.validator import (
    InMemoryIdempotencyStore,
    LocalGates,
    ProposalValidator,
    ToolDeclaration,
)
from simulation.vectors import declarations


def token(guide, kind, value_id=None):
    """Guide-issued token id, read from the guide's own projection."""
    token_set = guide.projection(guide.kernel.current, guide.kernel.current_refs())["policy_tokens"]
    return next(
        item["token_id"] for item in token_set["tokens"]
        if item["kind"] == kind and (value_id is None or item["value_id"] == value_id)
    )

# A key-shaped value with no chance of colliding with a real credential. Every
# scan in this suite looks for it in artifacts the transport produces.
SENTINEL_KEY = "amiss-test-sentinel-key-0000000000000000"
POLICY = PolicyEnvelope(
    policy_version="synthetic-policy-v1",
    system="Synthetic instructor policy fixture; no teaching content.",
)


def environment(*, store=None):
    kernel = TrialKernel(
        "fake-discrete",
        "fake_discrete",
        continuous_phases=(),
        setup_family_ids=("discrete-count",),
    )
    kernel.create_trial("trial-a", "discrete-count", {"count": 3})
    declared = declarations()
    stage = fake_stage_data()
    stage["allowed_action_set"]["action_ids"] = [item.action_id for item in declared]
    guide = GuideRunner(kernel, stage)
    gates = LocalGates()
    validator = ProposalValidator(kernel, declared, store=store or InMemoryIdempotencyStore(),
                                  gates=gates)
    return kernel, guide, validator


def context(kernel, *, utterance="Synthetic utterance."):
    return kernel.build_context_payload("utterance-a", utterance)


def request_for(kernel, *, request_id="request-a", turn_id="turn-a", origin="answer"):
    return build_from(kernel.build_context_payload("utterance-a", "Synthetic utterance."),
                      request_id=request_id, turn_id=turn_id, origin=origin)


def build_from(payload, *, request_id="request-a", turn_id="turn-a", origin="answer"):
    return EvaluationRequestBuilder.build(
        payload, POLICY, request_id=request_id, turn_id=turn_id, origin=origin
    )


def read_proposal(kernel, call_id="call-read"):
    ref = kernel.current.ref.to_dict()
    return {
        "tool_schema_version": "1.0.0",
        "call_id": call_id,
        "origin": "model",
        "tool": "state.read",
        "target": {"kind": "existing", "trial_ref": deepcopy(ref)},
        "basis_trial_ref": deepcopy(ref),
        "refs": [deepcopy(ref)],
        "parameters": {"operation": "snapshot", "target_trial_ref": deepcopy(ref)},
    }


def propose_proposal(kernel, guide, call_id="call-propose", value=4):
    return {
        "tool_schema_version": "1.0.0",
        "call_id": call_id,
        "origin": "model",
        "tool": "trial.propose",
        "target": {
            "kind": "new_trial",
            "episode_id": kernel.episode_id,
            "source_trial_ref": None,
        },
        "basis_trial_ref": None,
        "refs": [],
        "parameters": {
            "operation": "create",
            "setup_family_token_id": token(guide, "setup_family", "discrete-count"),
            "target_parameters": {"count": value},
            "input_delta": {
                "parameter_id": "count",
                "to_value": value,
                "downstream_effect_ids": ["discrete-state"],
            },
            "expected_invariant_ids": ["finite-state", "positive-count"],
        },
    }


def response_for(request, proposals, *, speech=()):
    return {
        "response_contract_version": "1.0.0",
        "request_id": request["request_id"],
        "refs": deepcopy(request["refs"]),
        "speech_units": list(speech),
        "tool_proposals": deepcopy(proposals),
    }


def reply(body, *, elapsed=0.2, tokens=(120, 40)):
    return SenderReply(
        body=body,
        elapsed_seconds=elapsed,
        ts_start="2026-11-01T12:00:00Z",
        ts_end="2026-11-01T12:00:01Z",
        input_tokens=tokens[0],
        output_tokens=tokens[1],
    )


def scripted_sender(reply_value):
    calls = []

    def send(**kwargs):
        calls.append(kwargs)
        if isinstance(reply_value, Exception):
            raise reply_value
        return reply_value

    send.calls = calls
    return send


class CanonicalRequestTest(unittest.TestCase):
    def test_builder_emits_the_closed_request_shape(self):
        kernel, _guide, _validator = environment()
        request = request_for(kernel)
        self.assertEqual(
            sorted(request),
            ["context_payload", "model", "origin", "policy_envelope", "refs",
             "request_contract_version", "request_id", "turn_id"],
        )
        self.assertEqual("muse-spark-1.3", request["model"])
        self.assertEqual({"policy_version", "system"}, set(request["policy_envelope"]))
        self.assertEqual(["trial-a"], [ref["trial_id"] for ref in request["refs"]])

    def test_canonical_bytes_and_hash_are_stable_across_rebuilds(self):
        kernel, _guide, _validator = environment()
        payload = context(kernel)
        first = build_from(payload)
        second = build_from(deepcopy(payload))
        self.assertEqual(canonical_request_bytes(first), canonical_request_bytes(second))
        self.assertEqual(request_hash(first), request_hash(second))
        self.assertEqual(64, len(request_hash(first)))

    def test_object_key_order_does_not_change_the_request_hash(self):
        kernel, _guide, _validator = environment()
        request = request_for(kernel)
        reordered = dict(reversed(list(request.items())))
        reordered["context_payload"] = dict(
            reversed(list(request["context_payload"].items()))
        )
        self.assertEqual(canonical_request_bytes(request), canonical_request_bytes(reordered))
        self.assertEqual(request_hash(request), request_hash(reordered))

    def test_any_semantic_change_changes_the_hash(self):
        kernel, _guide, _validator = environment()
        payload = context(kernel)
        request = build_from(payload)
        baseline = request_hash(request)

        other_id = build_from(payload, request_id="request-b")
        self.assertNotEqual(baseline, request_hash(other_id))

        other_origin = build_from(payload, origin="proactive")
        self.assertNotEqual(baseline, request_hash(other_origin))

        other_policy = EvaluationRequestBuilder.build(
            payload,
            PolicyEnvelope("synthetic-policy-v2", "Synthetic instructor policy fixture."),
            request_id="request-a", turn_id="turn-a", origin="answer",
        )
        self.assertNotEqual(baseline, request_hash(other_policy))

    def test_policy_never_enters_the_context_payload(self):
        kernel, _guide, _validator = environment()
        request = request_for(kernel)
        payload_text = canonical_request_bytes(request["context_payload"]).decode()
        self.assertNotIn("Synthetic instructor policy", payload_text)
        self.assertNotIn("system", request["context_payload"])
        for payload in ({"policy": {}}, {"system": "x"}, {"policy_envelope": {}}):
            with self.assertRaises(TransportError):
                EvaluationRequestBuilder.build(
                    payload, POLICY, request_id="request-a", turn_id=None, origin="answer"
                )

    def test_log_view_redacts_the_policy_body(self):
        kernel, _guide, _validator = environment()
        request = request_for(kernel)
        record = redact_request_for_log(request)
        self.assertNotIn("policy_envelope", record)
        self.assertEqual(POLICY.policy_version, record["policy"]["policy_version"])
        self.assertEqual(POLICY.digest(), record["policy"]["policy_hash"])
        self.assertNotIn(POLICY.system, json.dumps(record))

    def test_builder_refuses_missing_policy_and_unknown_origin(self):
        kernel, _guide, _validator = environment()
        with self.assertRaises(TransportError) as missing:
            EvaluationRequestBuilder.build(
                context(kernel), None, request_id="request-a", turn_id=None, origin="answer"
            )
        self.assertEqual("no_policy", missing.exception.reason_code)
        with self.assertRaises(TransportError) as origin:
            request_for(kernel, origin="script")
        self.assertEqual("malformed_parameters", origin.exception.reason_code)


class KeyHandleTest(unittest.TestCase):
    def test_key_is_not_reachable_from_repr_or_serialization(self):
        handle = ModelKeyHandle(SENTINEL_KEY)
        self.assertTrue(handle.present)
        self.assertNotIn(SENTINEL_KEY, repr(handle))
        self.assertNotIn(SENTINEL_KEY, str(handle))
        self.assertEqual(SENTINEL_KEY, handle.use())
        handle.clear()
        self.assertFalse(handle.present)
        with self.assertRaises(TransportError):
            handle.use()

    def test_key_handle_is_not_picklable(self):
        import pickle

        with self.assertRaises(TypeError):
            pickle.dumps(ModelKeyHandle(SENTINEL_KEY))


class DirectTransportTest(unittest.TestCase):
    def setUp(self):
        self.kernel, self.guide, self.validator = environment()
        self.request = request_for(self.kernel)

    def test_absent_key_fails_closed_with_a_readable_typed_error(self):
        sender = scripted_sender(reply(response_for(self.request, [])))
        transport = EvaluationDirectTransport(sender, ModelKeyHandle(), POLICY)
        outcome = transport.send(self.request)
        self.assertFalse(outcome.ok)
        self.assertEqual("no_credential", outcome.failure.reason_code)
        self.assertIn("key", outcome.failure.readable)
        self.assertEqual([], sender.calls, "no sender call may happen without a key")

    def test_absent_policy_fails_closed(self):
        sender = scripted_sender(reply(response_for(self.request, [])))
        transport = EvaluationDirectTransport(sender, ModelKeyHandle(SENTINEL_KEY), None)
        outcome = transport.send(self.request)
        self.assertFalse(outcome.ok)
        self.assertEqual("no_policy", outcome.failure.reason_code)
        self.assertEqual([], sender.calls)

    def test_send_passes_the_key_only_to_the_sender(self):
        sender = scripted_sender(reply(response_for(self.request, [read_proposal(self.kernel)])))
        transport = EvaluationDirectTransport(sender, ModelKeyHandle(SENTINEL_KEY), POLICY)
        outcome = transport.send(self.request)
        self.assertTrue(outcome.ok)
        self.assertEqual(SENTINEL_KEY, sender.calls[0]["authorization"])
        self.assertEqual(5.0, sender.calls[0]["connect_deadline_seconds"])
        self.assertEqual(30.0, sender.calls[0]["deadline_seconds"])
        self.assertNotIn(SENTINEL_KEY, json.dumps(outcome.response))
        self.assertNotIn(SENTINEL_KEY, json.dumps(outcome.provenance))

    def test_offline_sender_returns_a_typed_failure(self):
        transport = EvaluationDirectTransport(
            scripted_sender(SenderUnavailable()), ModelKeyHandle(SENTINEL_KEY), POLICY
        )
        outcome = transport.send(self.request)
        self.assertFalse(outcome.ok)
        self.assertEqual("offline", outcome.failure.kind)
        self.assertEqual("network_service", outcome.failure.reason_code)

    def test_remote_refusal_returns_a_typed_failure(self):
        transport = EvaluationDirectTransport(
            scripted_sender(SenderRefused()), ModelKeyHandle(SENTINEL_KEY), POLICY
        )
        outcome = transport.send(self.request)
        self.assertEqual("remote_refusal", outcome.failure.kind)

    def test_response_over_the_deadline_is_dropped(self):
        sender = scripted_sender(
            reply(response_for(self.request, [read_proposal(self.kernel)]), elapsed=30.5)
        )
        transport = EvaluationDirectTransport(sender, ModelKeyHandle(SENTINEL_KEY), POLICY)
        outcome = transport.send(self.request)
        self.assertFalse(outcome.ok)
        self.assertEqual("timeout", outcome.failure.kind)
        self.assertEqual("network_service", outcome.failure.reason_code)
        self.assertIsNone(outcome.provenance)

    def test_response_exactly_at_the_deadline_is_accepted(self):
        sender = scripted_sender(
            reply(response_for(self.request, [read_proposal(self.kernel)]), elapsed=30.0)
        )
        transport = EvaluationDirectTransport(sender, ModelKeyHandle(SENTINEL_KEY), POLICY)
        self.assertTrue(transport.send(self.request).ok)

    def test_unreported_usage_stays_null(self):
        sender = scripted_sender(
            reply(response_for(self.request, [read_proposal(self.kernel)]), tokens=(None, None))
        )
        transport = EvaluationDirectTransport(sender, ModelKeyHandle(SENTINEL_KEY), POLICY)
        outcome = transport.send(self.request)
        self.assertIsNone(outcome.provenance["input_tokens"])
        self.assertIsNone(outcome.provenance["output_tokens"])

    def test_malformed_body_is_rejected_before_admission(self):
        sender = scripted_sender(reply("not an object"))
        transport = EvaluationDirectTransport(sender, ModelKeyHandle(SENTINEL_KEY), POLICY)
        outcome = transport.send(self.request)
        self.assertFalse(outcome.ok)
        self.assertEqual("malformed_response", outcome.failure.kind)


class ProvenanceTest(unittest.TestCase):
    def setUp(self):
        self.kernel, _guide, _validator = environment()
        self.request = request_for(self.kernel)
        self.response = response_for(self.request, [read_proposal(self.kernel)])

    def test_provenance_is_schema_valid_and_closed(self):
        record = build_provenance(
            self.request, self.response,
            ts_start="2026-11-01T12:00:00Z", ts_end="2026-11-01T12:00:01Z",
            input_tokens=120, output_tokens=40, mode="live",
        )
        self.assertEqual([], provenance_errors(record))
        validator = SchemaValidator()
        self.assertEqual([], validator.validate(record, "model-provenance.schema.json"))
        self.assertEqual([], semantic_errors(record, "model-provenance.schema.json",
                                             json.dumps(record)))
        self.assertEqual(self.request["request_id"], record["request_id"])
        self.assertEqual(request_hash(self.request), record["prompt_hash"])
        self.assertEqual(response_hash(self.response), record["response_hash"])

    def test_recorded_mode_is_labelled_and_unknown_fields_fail(self):
        record = build_provenance(
            self.request, self.response,
            ts_start="2026-11-01T12:00:00Z", ts_end="2026-11-01T12:00:01Z",
            input_tokens=None, output_tokens=None, mode="recorded",
        )
        self.assertEqual("recorded", record["mode"])
        tampered = deepcopy(record)
        tampered["api_key"] = SENTINEL_KEY
        self.assertTrue(provenance_errors(tampered))

    def test_provenance_has_no_request_or_response_body(self):
        record = build_provenance(
            self.request, self.response,
            ts_start="2026-11-01T12:00:00Z", ts_end="2026-11-01T12:00:01Z",
            input_tokens=1, output_tokens=1, mode="live",
        )
        text = json.dumps(record)
        self.assertNotIn(POLICY.system, text)
        self.assertNotIn("context_payload", text)
        self.assertNotIn("tool_proposals", text)


class RecordedTransportTest(unittest.TestCase):
    def setUp(self):
        self.kernel, self.guide, self.validator = environment()
        self.request = request_for(self.kernel)
        self.response = response_for(self.request, [read_proposal(self.kernel)])
        self.fixture = self._fixture(self.request, self.response)
        self.transport = RecordedFixtureTransport([self.fixture])

    @staticmethod
    def _fixture(request, response):
        from schema.validate import sha256_json

        envelope = {
            "fixture_format_version": "1.0.0",
            "scenario_id": "synthetic-vertical-slice",
            "policy_version": POLICY.policy_version,
            "contract_versions": {
                "framework_api_version": "1.0.0",
                "snapshot_envelope_version": "1.0.0",
                "tool_schema_version": "1.0.0",
                "save_format_version": "1.0.0",
                "stage_data_format_version": "1.0.0",
                "context_payload_version": "1.0.0",
                "fixture_format_version": "1.0.0",
                "provenance_contract_version": "1.0.0",
                "episode_model_version": "1.0.0",
            },
            "deterministic_recipe": {
                "episode_id": "fake-discrete",
                "seed": 0,
                "initial_trial_ref": deepcopy(request["refs"][0]),
                "stage_id": "fake-discrete-setup",
                "input_event_ids": [],
            },
            "request": deepcopy(request),
            "response": deepcopy(response),
            "provenance": {
                "model": "muse-spark-1.3",
                "endpoint_host": "api.meta.ai",
                "request_id": request["request_id"],
                "ts_start": "2026-10-29T12:00:00Z",
                "ts_end": "2026-10-29T12:00:01Z",
                "input_tokens": None,
                "output_tokens": None,
                "prompt_hash": sha256_json(request),
                "response_hash": sha256_json(response),
                "mode": "recorded",
            },
        }
        envelope["integrity"] = {"algorithm": "sha256", "digest": sha256_json(envelope)}
        return envelope

    def test_replay_is_labelled_recorded_and_keeps_capture_time(self):
        outcome = self.transport.send(self.request, scenario_id="synthetic-vertical-slice")
        self.assertTrue(outcome.ok)
        self.assertEqual("recorded", outcome.mode)
        self.assertEqual("recorded", outcome.provenance["mode"])
        self.assertEqual("2026-10-29T12:00:00Z", outcome.provenance["ts_start"])
        self.assertEqual(self.request["request_id"], outcome.provenance["request_id"])

    def test_missing_fixture_fails_closed(self):
        outcome = self.transport.send(self.request, scenario_id="absent")
        self.assertEqual("fixture_unavailable", outcome.failure.reason_code)

    def test_tampered_fixture_fails_closed(self):
        tampered = deepcopy(self.fixture)
        tampered["response"]["tool_proposals"][0]["parameters"]["operation"] = "ledger"
        transport = RecordedFixtureTransport([tampered])
        outcome = transport.send(self.request, scenario_id="synthetic-vertical-slice")
        self.assertEqual("fixture_integrity", outcome.failure.reason_code)

    def test_stale_request_is_not_rebound_to_current_refs(self):
        moved = request_for(self.kernel, request_id="request-a")
        moved["context_payload"]["current_user_utterance"]["text"] = "Different utterance."
        outcome = self.transport.send(moved, scenario_id="synthetic-vertical-slice")
        self.assertEqual("fixture_stale", outcome.failure.reason_code)


class AdmissionTest(unittest.TestCase):
    def setUp(self):
        self.kernel, self.guide, self.validator = environment()
        self.request = request_for(self.kernel)
        self.admission = ModelReplyAdmission(self.validator)

    def _send(self, proposals, *, speech=()):
        response = response_for(self.request, proposals, speech=speech)
        provenance = build_provenance(
            self.request, response,
            ts_start="2026-11-01T12:00:00Z", ts_end="2026-11-01T12:00:01Z",
            input_tokens=10, output_tokens=5, mode="live",
        )
        return self.admission.admit(self.request, response, provenance=provenance)

    def test_accepted_propose_creates_only_a_tick_zero_candidate(self):
        result = self._send([propose_proposal(self.kernel, self.guide)])
        self.assertTrue(result.admitted)
        self.assertEqual(["call-propose"], list(result.accepted_call_ids))
        created = [trial for trial_id, trial in self.kernel.trials.items()
                   if trial_id != "trial-a"]
        self.assertEqual(1, len(created))
        candidate = created[0]
        self.assertEqual("setup", candidate.state["phase"])
        self.assertEqual("proposed", candidate.state["acceptance"])
        self.assertEqual(0, candidate.state["tick"])
        self.assertEqual("trial-a", self.kernel.current_trial_id,
                         "the live Trial is not preempted by a proposal")

    def test_user_acceptance_is_still_a_separate_command(self):
        self._send([propose_proposal(self.kernel, self.guide)])
        candidate = [trial for trial_id, trial in self.kernel.trials.items()
                     if trial_id != "trial-a"][0]
        self.kernel.select_trial(candidate.identity["trial_id"])
        self.assertEqual("proposed", self.kernel.current.state["acceptance"])

    def test_rejected_reply_mutates_nothing(self):
        before = deepcopy(self.kernel.__dict__)
        stale = read_proposal(self.kernel, "call-stale")
        stale["refs"] = [{"trial_id": "trial-a", "trial_revision": 99}]
        stale["basis_trial_ref"] = {"trial_id": "trial-a", "trial_revision": 99}
        stale["target"]["trial_ref"] = {"trial_id": "trial-a", "trial_revision": 99}
        stale["parameters"]["target_trial_ref"] = {"trial_id": "trial-a", "trial_revision": 99}
        result = self._send([stale])
        self.assertTrue(result.admitted)
        self.assertEqual(("call-stale", "stale_trial_ref"), result.rejected_reasons[0])
        self.assertEqual([], list(result.accepted_call_ids))
        self.assertEqual(before["current_trial_id"], self.kernel.current_trial_id)
        self.assertEqual(sortable(before["trials"]), sortable(self.kernel.trials))
        self.assertEqual(0, len(self.validator.local_effects.effects["subtitles"]))

    def test_whole_reply_rejection_blocks_valid_units_too(self):
        valid = read_proposal(self.kernel, "call-read")
        stale = read_proposal(self.kernel, "call-stale")
        stale["refs"] = [{"trial_id": "trial-a", "trial_revision": 42}]
        stale["target"]["trial_ref"] = {"trial_id": "trial-a", "trial_revision": 42}
        stale["parameters"]["target_trial_ref"] = {"trial_id": "trial-a", "trial_revision": 42}
        result = self._send([valid, stale])
        self.assertEqual([], list(result.accepted_call_ids))
        self.assertEqual(1, len(result.rejected_reasons))

    def test_replayed_reply_is_byte_identical_and_mutates_once(self):
        proposal = propose_proposal(self.kernel, self.guide)
        first = self._send([proposal])
        trials_after_first = len(self.kernel.trials)
        records_after_first = len(self.validator.store.export())
        second = self._send([proposal])
        self.assertEqual(len(self.kernel.trials), trials_after_first)
        self.assertEqual(len(self.validator.store.export()), records_after_first,
                         "a replay adds no idempotency record")
        self.assertEqual(first.results[0]["fingerprint"], second.results[0]["fingerprint"])
        self.assertTrue(second.results[0]["replay"])
        self.assertEqual(first.results[0]["outcome"], second.results[0]["outcome"])

    def test_conflicting_reuse_of_a_call_id_executes_nothing(self):
        proposal = propose_proposal(self.kernel, self.guide)
        self._send([proposal])
        trials_after_first = len(self.kernel.trials)
        changed = deepcopy(proposal)
        changed["parameters"]["target_parameters"] = {"count": 6}
        changed["parameters"]["input_delta"] = {
            "parameter_id": "count",
            "to_value": 6,
            "downstream_effect_ids": ["discrete-state"],
        }
        result = self._send([changed])
        self.assertEqual("idempotency_conflict", result.results[0]["reason_code"])
        self.assertEqual(len(self.kernel.trials), trials_after_first)

    def test_identity_mismatch_rejects_the_reply_before_validation(self):
        response = response_for(self.request, [read_proposal(self.kernel)])
        response["request_id"] = "request-other"
        result = self.admission.admit(self.request, response)
        self.assertFalse(result.admitted)
        self.assertEqual("malformed_parameters", result.failure.reason_code)

    def test_ref_mismatch_rejects_the_reply(self):
        response = response_for(self.request, [read_proposal(self.kernel)])
        response["refs"] = [{"trial_id": "trial-a", "trial_revision": 7}]
        self.assertFalse(self.admission.admit(self.request, response).admitted)

    def test_sentinel_key_never_reaches_results_or_provenance(self):
        result = self._send([read_proposal(self.kernel)])
        text = json.dumps({
            "results": list(result.results),
            "provenance": result.provenance,
        })
        self.assertNotIn(SENTINEL_KEY, text)
        self.assertNotIn(SENTINEL_KEY, json.dumps(redact_request_for_log(self.request)))


def sortable(trials):
    return {
        trial_id: {
            "state": trial.state,
            "configuration": trial.configuration,
        }
        for trial_id, trial in trials.items()
    }


class RecordedParityTest(unittest.TestCase):
    """A recorded reply and a live reply admit identically."""

    def test_recorded_and_live_replies_produce_the_same_admission(self):
        kernel, guide, validator = environment()
        request = request_for(kernel)
        proposals = [propose_proposal(kernel, guide)]
        response = response_for(request, proposals)
        admission = ModelReplyAdmission(validator)
        live = admission.admit(request, deepcopy(response), mode="live")
        recorded = admission.admit(request, deepcopy(response), mode="recorded")
        self.assertEqual(live.admitted, recorded.admitted)
        self.assertEqual(live.accepted_call_ids, recorded.accepted_call_ids)
        self.assertEqual(live.rejected_reasons, recorded.rejected_reasons)
        self.assertEqual("live", live.mode)
        self.assertEqual("recorded", recorded.mode)


if __name__ == "__main__":
    unittest.main()
