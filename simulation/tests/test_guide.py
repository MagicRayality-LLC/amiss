from copy import deepcopy
import json
import unittest

from schema.validate import SchemaValidator, semantic_errors
from simulation import GuideRunner, StageLoadError, TrialKernel
from simulation.fake_episode import fake_stage_data


class GuideTestCase(unittest.TestCase):
    def setUp(self):
        self.kernel = TrialKernel(
            "fake-discrete", "fake_discrete", setup_family_ids=("discrete-count",)
        )
        self.kernel.create_trial("trial-a", "discrete-count", {"count": 3})
        self.guide = GuideRunner(self.kernel, fake_stage_data())

    def assert_contract_valid(self, value, schema_name):
        validator = SchemaValidator()
        errors = validator.validate(value, schema_name)
        if not errors:
            errors = semantic_errors(value, schema_name, json.dumps(value))
        self.assertEqual([], errors)

    def test_loader_refuses_missing_pair_and_unverified_content(self):
        missing_pair = fake_stage_data()
        missing_pair["stage_graph"]["nodes"][0]["evidence_pair_rules"] = []
        with self.assertRaises(StageLoadError):
            GuideRunner(self.kernel, missing_pair)
        corrupt = fake_stage_data()
        corrupt["integrity"]["status"] = "corrupt"
        with self.assertRaises(StageLoadError):
            GuideRunner(self.kernel, corrupt)
        self.assertEqual("observe", self.guide.stage_id)

    def test_evidence_pair_required_and_non_evidence_never_advances(self):
        ref = self.kernel.current.ref.to_dict()
        self.assertFalse(self.guide.record_fact("fact-action", "discrete-action", ref))
        self.kernel.advance_free_ticks(100)
        self.assertEqual("observe", self.guide.stage_id)
        self.assertEqual("accepted_setup", self.guide.default_path()["condition"])
        self.assertTrue(self.guide.record_fact("fact-state", "discrete-state", ref))
        self.assertEqual("complete", self.guide.stage_id)
        self.assertEqual(["fact-action", "fact-state"], self.guide.satisfied_evidence_fact_ids)
        entry_kinds = [item["entry_kind"] for item in self.kernel.current.ledger]
        self.assertIn("evidence_pair_satisfied", entry_kinds)

    def test_user_controls_log_choices_without_fabricated_evidence(self):
        self.guide.free_exploration()
        self.assertEqual("free_exploration", self.kernel.session_mode)
        self.assertEqual([], self.guide.satisfied_evidence_fact_ids)
        self.guide.skip()
        self.assertEqual("complete", self.guide.stage_id)
        self.assertEqual([], self.guide.satisfied_evidence_fact_ids)
        self.guide.replay()
        kinds = [item["entry_kind"] for item in self.kernel.current.ledger]
        self.assertEqual(["trial_created", "user_free_exploration", "user_skip", "user_replay"], kinds)

    def test_snapshot_and_context_contain_only_current_token_whitelist(self):
        snapshot = self.kernel.snapshot()
        token_set = snapshot["policy_tokens"]
        self.assertEqual(
            {"episode_id", "stage_id", "policy_version", "refs", "tokens"}, set(token_set)
        )
        allowed_kinds = {
            "stage", "proactive_candidate", "solicitation", "allowed_action", "setup_family", "policy_version"
        }
        self.assertTrue(all(token["kind"] in allowed_kinds for token in token_set["tokens"]))
        self.assertTrue(all(set(token) <= {"token_id", "kind", "value_id", "solicitation_kind"} for token in token_set["tokens"]))
        self.assert_contract_valid(snapshot, "snapshot.schema.json")
        context = self.kernel.build_context_payload("utterance-a", "Synthetic utterance.")
        self.assert_contract_valid(context, "context-payload.schema.json")
        self.assertNotIn("policy_envelope", context)
        self.assertNotIn("rules", context)

    def test_revision_stage_and_comparison_changes_revoke_tokens(self):
        old = self.guide.projection(self.kernel.current, self.kernel.current_refs())["policy_tokens"]["tokens"][0]["token_id"]
        self.assertTrue(self.guide.authorize_token(old))
        self.kernel.close_interaction()
        self.assertFalse(self.guide.authorize_token(old))
        current = self.guide.projection(self.kernel.current, self.kernel.current_refs())["policy_tokens"]["tokens"][0]["token_id"]
        ref = self.kernel.current.ref.to_dict()
        self.guide.record_fact("fact-action", "discrete-action", ref)
        self.guide.record_fact("fact-state", "discrete-state", ref)
        self.assertFalse(self.guide.authorize_token(current))
        current_stage_id = self.guide.projection(self.kernel.current, self.kernel.current_refs())["policy_tokens"]["tokens"][0]["token_id"]
        self.kernel.create_trial("trial-b", "discrete-count", {"count": 4})
        self.assertFalse(self.guide.authorize_token(current_stage_id))

    def test_proactive_candidate_requires_current_token(self):
        token_id = self.guide.issue_proactive_candidate("candidate-a")
        self.assertTrue(self.guide.authorize_token(token_id))
        self.kernel.close_interaction()
        self.assertFalse(self.guide.authorize_token(token_id))
        tokens = self.guide.projection(self.kernel.current, self.kernel.current_refs())["policy_tokens"]["tokens"]
        self.assertFalse(any(token["kind"] == "proactive_candidate" for token in tokens))

    def test_offline_default_path_uses_actual_facts(self):
        path = self.guide.default_path()
        self.assertEqual("offline-observe", path["default_path_id"])
        self.assertFalse(path["dynamic_wording"])
        ref = self.kernel.current.ref.to_dict()
        self.guide.record_fact("fact-action", "discrete-action", ref)
        self.guide.record_fact("fact-state", "discrete-state", ref)
        self.assertEqual("complete", self.guide.default_path()["condition"])
        self.assertEqual("not_used", self.kernel.snapshot()["diagnostics"]["network_state"])


if __name__ == "__main__":
    unittest.main()
