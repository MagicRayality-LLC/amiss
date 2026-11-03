import json
import unittest
from copy import deepcopy

from schema.validate import canonical_bytes
from simulation import vectors


class SharedVectorTest(unittest.TestCase):
    """The checked-in vector file must never drift from the Python oracle."""

    @classmethod
    def setUpClass(cls):
        cls.path = vectors.VECTOR_PATH
        cls.text = cls.path.read_text(encoding="utf-8")
        cls.document = json.loads(cls.text)

    def test_checked_in_file_matches_a_fresh_generation(self):
        self.assertTrue(self.path.exists(),
                        "schema/vectors/admission-vectors.json is the shared oracle export")
        self.assertEqual(vectors.render(vectors.build_vectors()), self.text,
                         "regenerate the vectors: python3 -c "
                         "'import sys; sys.path.insert(0, \".\"); "
                         "from simulation import vectors; vectors.write()'")

    def test_canonical_corpus_matches_the_shared_encoder(self):
        corpus = self.document["canonicalisation"]
        self.assertGreaterEqual(len(corpus), 7)
        for entry in corpus:
            data = canonical_bytes(entry["value"])
            self.assertEqual(entry["canonical_json"], data.decode("utf-8"), entry["name"])
            import hashlib

            self.assertEqual(entry["sha256"], hashlib.sha256(data).hexdigest(), entry["name"])

    def test_negative_zero_is_encoded_with_its_sign(self):
        """CPython keeps -0.0; a JSON text parser may only keep the value."""
        self.assertEqual(b'{"c":-0.0}', canonical_bytes({"c": -0.0}))

    def test_every_case_declares_a_pooled_context_and_the_same_refs(self):
        contexts = self.document["contexts"]
        for case in self.document["admission"]:
            digest = case["context_sha256"]
            self.assertIn(digest, contexts, case["name"])
            import hashlib

            self.assertEqual(digest, hashlib.sha256(canonical_bytes(contexts[digest])).hexdigest())
            self.assertEqual(canonical_bytes(contexts[digest]["refs"]),
                             canonical_bytes(case["response"]["refs"]))

    def test_every_case_outcome_is_reproduced_by_the_oracle(self):
        for case in self.document["admission"]:
            fresh = vectors.case(case["name"], dict(vectors.ADMISSION_CASES)[case["name"]])[1]
            self.assertEqual(fresh["expect"], case["expect"], case["name"])
            self.assertEqual(fresh["response"], case["response"], case["name"])

    def test_accepted_proposal_case_proves_tick_zero_and_no_live_preemption(self):
        case = next(item for item in self.document["admission"]
                    if item["name"].startswith("accepted proposal"))
        after = case["expect"]["after"]
        self.assertEqual("trial-a", after["current_trial_id"])
        candidate_ids = [trial_id for trial_id in after["trials"] if trial_id != "trial-a"]
        self.assertEqual(1, len(candidate_ids))
        candidate = after["trials"][candidate_ids[0]]
        self.assertEqual("setup", candidate["phase"])
        self.assertEqual("proposed", candidate["acceptance"])
        self.assertEqual(0, candidate["tick"])

    def test_replay_case_returns_the_first_result_flagged_replay(self):
        case = next(item for item in self.document["admission"]
                    if item["name"].startswith("exact replay"))
        self.assertTrue(case["expect"]["results"][0]["replay"])
        self.assertEqual("executed", case["expect"]["results"][0]["outcome"])

    def test_request_anchor_matches_the_builder(self):
        anchor = self.document["request"]
        import hashlib

        from simulation.transport import request_hash

        self.assertEqual(request_hash(anchor["request"]), anchor["request_sha256"])
        self.assertEqual(hashlib.sha256(canonical_bytes(anchor["request"])).hexdigest(),
                         anchor["request_bytes_sha256"])
        self.assertEqual(hashlib.sha256(canonical_bytes(anchor["context_payload"])).hexdigest(),
                         anchor["context_payload_sha256"])
        self.assertEqual("muse-spark-1.3", anchor["request"]["model"])
        self.assertEqual("api.meta.ai", anchor["provenance"]["endpoint_host"])

    def test_transport_boundaries_are_the_frozen_numbers(self):
        boundaries = self.document["transport"]
        self.assertEqual(30.0, boundaries["model_deadline_seconds"])
        self.assertEqual(5.0, boundaries["connect_deadline_seconds"])
        self.assertEqual(30.0, boundaries["accepted_at_deadline_seconds"])
        self.assertGreater(boundaries["rejected_after_deadline_seconds"],
                           boundaries["model_deadline_seconds"])
        self.assertEqual(["no_credential", "no_policy"],
                         boundaries["fail_closed_reason_codes"])

    def test_vectors_carry_no_key_or_policy_body_outside_the_request_anchor(self):
        """Policy text exists only as the labelled synthetic fixture string."""
        self.assertNotIn("api_key", self.text)
        self.assertNotIn("authorization", self.text.lower())
        policy_body = self.document["request"]["request"]["policy_envelope"]["system"]
        self.assertTrue(policy_body.startswith("Synthetic instructor policy fixture"),
                        "the request anchor may carry only the synthetic fixture policy")
        for case in self.document["admission"]:
            self.assertNotIn("system", case["response"])
            self.assertNotIn("policy_envelope", json.dumps(case["response"]))
            self.assertNotIn(policy_body, json.dumps(case["response"]))
        for declarative in self.document["contexts"].values():
            self.assertNotIn("system", declarative)
            self.assertNotIn(policy_body, json.dumps(declarative))


if __name__ == "__main__":
    unittest.main()
