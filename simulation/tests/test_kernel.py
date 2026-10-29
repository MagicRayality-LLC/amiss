from copy import deepcopy
import json
import unittest

from schema.validate import SchemaValidator, semantic_errors
from simulation import KernelError, TrialKernel


def measurement(measurement_id="measurement-a", *, value=3, display=True, required=()):
    result = {
        "measurement_id": measurement_id,
        "value": value,
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
    if display and required:
        result["display_tuple"] = {
            "display_tuple_id": f"tuple-{measurement_id}",
            "components": [
                {"component_id": component, "value": value, "unit_id": "count"}
                for component in required
            ],
        }
    return result


class KernelTestCase(unittest.TestCase):
    def setUp(self):
        self.kernel = TrialKernel("synthetic-episode", "synthetic_episode")
        self.kernel.create_trial("trial-a", "synthetic-setup", {"level": 1})

    def assert_snapshot_valid(self, snapshot):
        validator = SchemaValidator()
        errors = validator.validate(snapshot, "snapshot.schema.json")
        if not errors:
            errors = semantic_errors(snapshot, "snapshot.schema.json", json.dumps(snapshot))
        self.assertEqual([], errors)

    def test_revision_isolation_and_single_interaction_increment(self):
        initial = deepcopy(self.kernel.current.ref.to_dict())
        self.kernel.advance_free_ticks(8)
        self.assertEqual(initial, self.kernel.sample_held_pose()["trial_ref"])
        first = self.kernel.snapshot()
        second = self.kernel.snapshot()
        self.assertEqual(first["identity"]["global_revision"] + 1, second["identity"]["global_revision"])
        self.kernel.create_trial("trial-b", "synthetic-setup", {"level": 2})
        self.kernel.select_trial("trial-b")
        self.kernel.select_trial("trial-a")
        self.assertEqual(1, self.kernel.current.state["trial_revision"])
        event = self.kernel.close_interaction()
        self.assertEqual(2, self.kernel.current.state["trial_revision"])
        self.assertEqual(self.kernel.current.ref.to_dict(), event["trial_ref"])
        self.assertEqual(2, self.kernel.current.ledger[-1]["sequence"])

    def test_both_stale_directions_and_one_stale_member_reject_whole_unit(self):
        current = self.kernel.current.ref.to_dict()
        below = {"trial_id": "trial-a", "trial_revision": 0}
        above = {"trial_id": "trial-a", "trial_revision": 3}
        self.kernel.close_interaction()
        current = self.kernel.current.ref.to_dict()
        stale = {"trial_id": "trial-a", "trial_revision": 1}
        for refs in ([below], [above], [stale], [current, stale]):
            with self.subTest(refs=refs), self.assertRaises(KernelError) as caught:
                self.kernel.assert_current_refs(refs)
            self.assertEqual("stale_trial_ref", caught.exception.reason_code)
        self.assertEqual(2, self.kernel.current.state["trial_revision"])
        self.assertEqual(2, len(self.kernel.current.ledger))

    def test_proposal_accept_reject_return_and_source_isolation(self):
        self.kernel.start_trial()
        source_ref = self.kernel.current.ref.to_dict()
        source_ledger = deepcopy(self.kernel.current.ledger)
        child_ref = self.kernel.derive_trial("child", source_ref, "level", 2)
        self.assertEqual(1, child_ref["trial_revision"])
        self.assertEqual("trial-a", self.kernel.current_trial_id)
        self.assertEqual(source_ledger, self.kernel.trials["trial-a"].ledger)
        accepted = self.kernel.accept_trial("child")
        self.assertEqual(2, accepted["trial_revision"])
        self.assertEqual("child", self.kernel.current_trial_id)
        self.assertEqual(source_ledger, self.kernel.trials["trial-a"].ledger)

        rejected = TrialKernel("synthetic-episode", "synthetic_episode")
        rejected.create_trial("source", "synthetic-setup", {"level": 1})
        rejected.derive_trial("child", rejected.current.ref.to_dict(), "level", 2)
        rejected.reject_trial("child")
        self.assertEqual("abandoned", rejected.trials["child"].state["phase"])
        self.assertEqual("proposal_rejected", rejected.trials["child"].state["terminal_reason"])
        self.assertEqual("accepted", rejected.trials["source"].state["acceptance"])

        returned = TrialKernel("synthetic-episode", "synthetic_episode")
        returned.create_trial("source", "synthetic-setup", {"level": 1})
        source = returned.current.ref.to_dict()
        returned.derive_trial("child", source, "level", 2)
        self.assertEqual(source, returned.return_to_source("child"))
        self.assertEqual("source", returned.current_trial_id)
        self.assertEqual("abandoned", returned.trials["child"].state["phase"])

    def test_pre_run_prediction_masks_and_unmasks_atomically(self):
        kernel = TrialKernel(
            "synthetic-episode", "synthetic_episode", prediction_attachments=("pre_run",)
        )
        kernel.create_trial("source", "synthetic-setup", {"level": 1})
        kernel.commit_measurement(measurement())
        source_gate_ref = kernel.current.ref.to_dict()
        source_gate = kernel.open_prediction(
            "source-gate", "pre_run", "choices", ["up", "down"], source_gate_ref, ["measurement-a"]
        )
        kernel.resolve_prediction(source_gate, source_gate_ref, choice_id="up")
        kernel.continue_trial()
        source_ref = kernel.current.ref.to_dict()
        kernel.derive_trial("child", source_ref, "level", 2)
        kernel.accept_trial("child")
        gate = kernel.open_prediction(
            "gate-pre-run", "pre_run", "choices", ["up", "down"], source_ref, ["measurement-a"]
        )
        masked = kernel.snapshot()
        self.assertEqual("masked_until_prediction", masked["measurements"][0]["applicability"])
        self.assertIsNone(masked["measurements"][0]["value"])
        self.assertEqual([], masked["predictions"])
        measurement_ref = kernel.trials["source"].measurements[0]["trial_ref"]
        with self.assertRaises(KernelError):
            kernel.admit_speech(kernel.current_refs(), [{"measurement_id": "measurement-a", "trial_ref": measurement_ref}])
        with self.assertRaises(KernelError):
            kernel.resolve_prediction(gate, {"trial_id": "child", "trial_revision": 1}, choice_id="up")
        self.assertTrue(kernel.current.pending_predictions[0]["readout_masked"])
        resolved = kernel.resolve_prediction(gate, kernel.current.ref.to_dict(), choice_id="up")
        self.assertFalse(resolved["readout_masked"])
        unmasked = kernel.snapshot()
        self.assertEqual("valid", unmasked["measurements"][0]["applicability"])
        self.assertEqual(3, unmasked["measurements"][0]["value"])
        self.assertEqual("measurement-a", unmasked["predictions"][0]["measurement_id"])
        self.assert_snapshot_valid(unmasked)
        kernel.continue_trial()
        self.assertEqual("baseline", kernel.current.state["phase"])

    def test_pre_reveal_prediction_skip_unmasks_and_stale_cannot_reveal(self):
        kernel = TrialKernel(
            "synthetic-episode", "synthetic_episode", prediction_attachments=("pre_reveal",)
        )
        kernel.create_trial("trial-a", "synthetic-setup", {"level": 1})
        kernel.start_trial()
        kernel.commit_measurement(measurement())
        target = kernel.current.ref.to_dict()
        gate = kernel.open_prediction(
            "gate-pre-reveal", "pre_reveal", "choices", ["up"], target, ["measurement-a"]
        )
        self.assertEqual(
            "masked_until_prediction", kernel.snapshot()["measurements"][0]["applicability"]
        )
        stale = deepcopy(target)
        stale["trial_revision"] -= 1
        with self.assertRaises(KernelError):
            kernel.resolve_prediction(gate, stale, skip=True)
        kernel.resolve_prediction(gate, kernel.current.ref.to_dict(), skip=True)
        snapshot = kernel.snapshot()
        self.assertEqual("skipped", snapshot["progress"]["trial_state"]["phase"])
        self.assertTrue(snapshot["predictions"][0]["skipped"])
        self.assertEqual("valid", snapshot["measurements"][0]["applicability"])
        self.assert_snapshot_valid(snapshot)
        kernel.continue_trial()
        self.assertEqual("settling", kernel.current.state["phase"])

    def test_display_tuple_conditions_withhold_only_citing_speech(self):
        self.kernel.start_trial()
        self.kernel.commit_measurement(measurement(required=("value", "uncertainty")))
        ref = self.kernel.current.ref.to_dict()
        citation = {
            "measurement_id": "measurement-a",
            "trial_ref": self.kernel.trials["trial-a"].measurements[0]["trial_ref"],
            "display_tuple_id": "tuple-measurement-a",
        }
        self.assertTrue(self.kernel.admit_speech([ref]))
        self.assertTrue(self.kernel.admit_speech([ref], [citation]))
        incomplete = measurement("measurement-b", required=("value", "uncertainty"))
        incomplete["display_tuple"]["components"].pop()
        self.kernel.commit_measurement(incomplete)
        current = self.kernel.current.ref.to_dict()
        bad_citation = {
            "measurement_id": "measurement-b",
            "trial_ref": self.kernel.trials["trial-a"].measurements[1]["trial_ref"],
            "display_tuple_id": "tuple-measurement-b",
        }
        with self.assertRaises(KernelError) as caught:
            self.kernel.admit_speech([current], [bad_citation])
        self.assertEqual("display_tuple_incomplete", caught.exception.reason_code)
        self.assertTrue(self.kernel.admit_speech([current]))

    def test_invalid_measurement_does_not_commit_a_revision(self):
        self.kernel.start_trial()
        self.kernel.commit_measurement(measurement())
        revision = self.kernel.current.state["trial_revision"]
        ledger_length = len(self.kernel.current.ledger)
        with self.assertRaises(KernelError) as caught:
            self.kernel.commit_measurement(measurement())
        self.assertEqual("duplicate_measurement_id", caught.exception.reason_code)
        self.assertEqual(revision, self.kernel.current.state["trial_revision"])
        self.assertEqual(ledger_length + 1, len(self.kernel.current.ledger))
        self.assertEqual("rejection", self.kernel.current.ledger[-1]["entry_kind"])

    def test_pause_resume_and_terminal_rules(self):
        self.kernel.start_trial()
        paused = self.kernel.pause()
        self.assertEqual("paused", self.kernel.current.state["activity"])
        with self.assertRaises(KernelError):
            self.kernel.advance_free_ticks()
        self.kernel.resume()
        self.assertGreater(self.kernel.current.ref.to_dict()["trial_revision"], paused["trial_revision"])
        self.kernel.abandon()
        with self.assertRaises(KernelError):
            self.kernel.pause()


if __name__ == "__main__":
    unittest.main()
