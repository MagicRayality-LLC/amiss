"""The public policy pack loads, refuses substitutes, and stays in step.

These suites cover the `E-10-01` portions that exist as fixtures: the pack
loads through the existing versioned stage-data loader, version and integrity
checks fail closed, and no alternate supply is substituted. They also prove
that the prose supply cannot drift away from the mechanical envelope, which is
the only cross-file coupling this pack introduces.
"""

from copy import deepcopy
import json
from pathlib import Path
import shutil
import tempfile
import unittest

from schema.validate import ContractError, canonical_bytes, parse_json
from simulation import GuideRunner, PolicyPackError, TrialKernel
from simulation import policy_pack
from simulation.policy_pack import (
    MAX_SYSTEM_BYTES,
    POLICY_ROOT,
    assemble_system,
    load_policy_pack,
    stage_data_digest,
)

ROOT = POLICY_ROOT.parent


def copy_pack():
    """One throwaway copy of the checked-in pack, sealed as shipped."""
    directory = tempfile.mkdtemp(prefix="amiss-policy-")
    target = Path(directory) / "policy"
    shutil.copytree(POLICY_ROOT, target)
    return target


class PackLoadTest(unittest.TestCase):
    """The shipped pack is one version, one integrity result, one loader pass."""

    def setUp(self):
        self.pack = load_policy_pack()

    def test_shipped_pack_loads_through_the_existing_stage_loader(self):
        # The existing loader is the only stage interpreter: it must accept the
        # shipped envelope unchanged, not a copy this module reshaped.
        loaded = GuideRunner.load(self.pack.stage_data)
        self.assertEqual("what_is_temperature", loaded["episode_id"])
        self.assertEqual("amiss-hackathon-policy-v1", loaded["policy_version"])
        self.assertEqual(7, len(loaded["stage_graph"]["nodes"]))

    def test_pack_identity_matches_every_declared_version_axis(self):
        self.assertEqual("1.0.0", self.pack.pack_format_version)
        self.assertEqual("1.0.0", self.pack.stage_data["stage_data_format_version"])
        versions = parse_json((ROOT / "schema" / "versions.json").read_text(encoding="utf-8"))
        self.assertEqual(
            versions["stage_data_format_version"],
            self.pack.stage_data["stage_data_format_version"],
        )

    def test_integrity_covers_both_files_and_the_manifest(self):
        for path, digest in self.pack.file_digests:
            self.assertEqual(
                policy_pack.file_sha256(self.pack.source_dir / path), digest, path
            )
        self.assertEqual(
            stage_data_digest(self.pack.stage_data), self.pack.stage_data_digest
        )
        manifest = parse_json(
            (self.pack.source_dir / "pack.json").read_text(encoding="utf-8")
        )
        self.assertEqual(policy_pack.pack_digest(manifest), self.pack.pack_digest)

    def test_envelope_carries_only_version_and_system_text(self):
        envelope = self.pack.envelope()
        self.assertEqual({"policy_version", "system"}, set(envelope))
        self.assertEqual(self.pack.policy_version, envelope["policy_version"])
        self.assertTrue(envelope["system"].strip())
        self.assertLessEqual(self.pack.system_bytes, MAX_SYSTEM_BYTES)

    def test_system_assembly_is_deterministic_and_ordered(self):
        again = assemble_system(self.pack.instructor)
        self.assertEqual(self.pack.system, again)
        first = self.pack.system.index("Instructions:")
        for later in ("Series rules:", "Episode rules:", "Stages:",
                      "Allowed actions:", "Reply classification:", "Wording:"):
            self.assertLess(first, self.pack.system.index(later), later)

    def test_the_supply_names_every_stage_and_no_forbidden_stage(self):
        stage_ids = [node["stage_id"] for node in
                     self.pack.stage_data["stage_graph"]["nodes"]]
        # P0 routes 5 -> 7; the P1 mass stage is not part of this pack.
        self.assertEqual(
            ["stage-0-entry", "stage-1-track-one", "stage-2-heat", "stage-3-cool",
             "stage-4-group", "stage-5-drift", "stage-7-transfer"],
            stage_ids,
        )
        self.assertNotIn("stage-6-mass", stage_ids)

    def test_the_temperature_episode_completion_stage_is_the_pack_terminal(self):
        terminals = [node["stage_id"] for node in
                     self.pack.stage_data["stage_graph"]["nodes"] if node["terminal"]]
        self.assertEqual(["stage-7-transfer"], terminals)


class NoFallbackTest(unittest.TestCase):
    """Missing, corrupt, tampered, and incompatible packs all fail closed."""

    def setUp(self):
        self.root = copy_pack()
        self.addCleanup(shutil.rmtree, self.root.parent, True)

    def rewrite(self, name, mutate):
        path = self.root / name
        value = parse_json(path.read_text(encoding="utf-8"))
        mutate(value)
        path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    def assert_refused(self, fragment):
        with self.assertRaises(PolicyPackError) as caught:
            load_policy_pack(self.root)
        joined = " | ".join(caught.exception.errors)
        self.assertIn(fragment, joined)
        return caught.exception

    def test_absent_directory_is_refused_not_replaced(self):
        shutil.rmtree(self.root)
        with self.assertRaises(PolicyPackError) as caught:
            load_policy_pack(self.root)
        self.assertIn("absent", str(caught.exception))

    def test_absent_file_is_refused(self):
        (self.root / "stage-data.json").unlink()
        self.assert_refused("stage-data.json")

    def test_corrupt_json_is_refused(self):
        (self.root / "instructor-policy.json").write_text("{", encoding="utf-8")
        self.assert_refused("instructor-policy.json")

    def test_edited_stage_content_breaks_the_content_digest(self):
        def mutate(value):
            value["stage_graph"]["nodes"][0]["default_path_id"] = "other-default"
        self.rewrite("stage-data.json", mutate)
        self.assert_refused("file digest does not match the manifest")

    def test_edited_prose_breaks_the_pack_digest(self):
        def mutate(value):
            value["series_rules"][0]["text"] = "A different rule."
        self.rewrite("instructor-policy.json", mutate)
        self.assert_refused("file digest does not match the manifest")

    def test_unverified_integrity_status_is_refused(self):
        def mutate(value):
            value["integrity"]["status"] = "corrupt"
        self.rewrite("stage-data.json", mutate)
        # The status is covered by nothing, so it is still a sealed file; the
        # loader must reject on the status itself rather than on the digest.
        self.assert_refused("integrity status is not verified")

    def test_unsupported_stage_data_major_is_refused(self):
        def mutate(value):
            value["stage_data_format_version"] = "2.0.0"
        self.rewrite("stage-data.json", mutate)
        self.assert_refused("unsupported format major")

    def test_unsupported_pack_major_is_refused(self):
        def mutate(value):
            value["pack_format_version"] = "9.0.0"
        self.rewrite("pack.json", mutate)
        self.assert_refused("unsupported pack format major")

    def test_policy_version_mismatch_between_files_is_refused(self):
        def mutate(value):
            value["policy_version"] = "amiss-hackathon-policy-v2"
        self.rewrite("instructor-policy.json", mutate)
        self.assert_refused("policy version differs from the pack manifest")

    def test_a_stage_without_an_evidence_pair_is_refused_by_the_loader(self):
        def mutate(value):
            value["stage_graph"]["nodes"][3]["evidence_pair_rules"] = []
            value["integrity"]["digest"] = stage_data_digest(value)
        self.rewrite("stage-data.json", mutate)
        self.assert_refused("nonterminal stage has no evidence pair")

    def test_a_single_signal_pair_is_refused(self):
        def mutate(value):
            rule = value["stage_graph"]["nodes"][4]["evidence_pair_rules"][0]
            rule["second_fact_kind"] = rule["first_fact_kind"]
            value["integrity"]["digest"] = stage_data_digest(value)
        self.rewrite("stage-data.json", mutate)
        self.assert_refused("evidence facts must be different")

    def test_an_action_added_to_the_envelope_alone_is_refused(self):
        def mutate(value):
            value["allowed_action_set"]["action_ids"].append("action-invented")
            value["integrity"]["digest"] = stage_data_digest(value)
            pack = parse_json((self.root / "pack.json").read_text(encoding="utf-8"))
            pack["files"] = [
                {"path": "stage-data.json",
                 "sha256": policy_pack.file_sha256(self.root / "stage-data.json")}
                if item["path"] == "stage-data.json" else item
                for item in pack["files"]
            ]
            pack["integrity"]["digest"] = policy_pack.pack_digest(pack)
            (self.root / "pack.json").write_text(
                json.dumps(pack, indent=2) + "\n", encoding="utf-8")
        self.rewrite("stage-data.json", mutate)
        self.assert_refused("allowed-action catalogue differs from the envelope")

    def test_an_unmeasured_threshold_value_is_refused(self):
        def mutate(value):
            value["thresholds"][0]["value"] = 0.25
        self.rewrite("instructor-policy.json", mutate)
        self.assert_refused("claims a value that this pack has not measured")

    def test_an_undefined_wording_reference_is_refused(self):
        def mutate(value):
            value["mistake_classification"]["categories"][0]["wording_id"] = "absent"
        self.rewrite("instructor-policy.json", mutate)
        self.assert_refused("undefined wording ids")

    def test_a_question_pointing_at_an_unknown_stage_is_refused(self):
        def mutate(value):
            value["next_questions"][2]["stage_id"] = "stage-unknown"
        self.rewrite("instructor-policy.json", mutate)
        self.assert_refused("names an unknown stage")

    def test_a_pack_directory_with_no_manifest_is_refused(self):
        (self.root / "pack.json").unlink()
        self.assert_refused("pack.json")

    def test_a_manifest_that_lists_an_absent_file_is_refused(self):
        def mutate(value):
            value["files"].append({"path": "extra.json", "sha256": "0" * 64})
            value["integrity"]["digest"] = policy_pack.pack_digest(value)
        self.rewrite("pack.json", mutate)
        self.assert_refused("listed file extra.json is absent")

    def test_the_manifest_itself_is_not_a_listed_file(self):
        # The manifest carries its own digest instead of listing itself, so a
        # manifest that lists itself is a different inventory.
        def mutate(value):
            value["files"].append({"path": "pack.json",
                                   "sha256": policy_pack.file_sha256(self.root / "pack.json")})
            value["integrity"]["digest"] = policy_pack.pack_digest(value)
        self.rewrite("pack.json", mutate)
        self.assert_refused("file inventory does not match the declared files")

    def test_an_unknown_manifest_field_is_refused(self):
        def mutate(value):
            value["extra"] = True
            value["integrity"]["digest"] = policy_pack.pack_digest(value)
        self.rewrite("pack.json", mutate)
        self.assert_refused("unknown fields")

    def test_a_pack_over_the_system_size_bound_is_refused(self):
        def mutate(value):
            value["series_rules"][0]["text"] = "x" * (MAX_SYSTEM_BYTES + 1)
        self.rewrite("instructor-policy.json", mutate)
        # Seal with the digest helpers directly: `seal()` runs the loader, which
        # already refuses this pack, so it cannot be used to build the case.
        pack = parse_json((self.root / "pack.json").read_text(encoding="utf-8"))
        pack["files"] = [
            {"path": item["path"],
             "sha256": policy_pack.file_sha256(self.root / item["path"])}
            for item in pack["files"]
        ]
        pack["integrity"]["digest"] = policy_pack.pack_digest(pack)
        (self.root / "pack.json").write_text(
            json.dumps(pack, indent=2) + "\n", encoding="utf-8")
        self.assert_refused("exceeds")

    def test_seal_refuses_to_write_an_oversized_pack(self):
        def mutate(value):
            value["series_rules"][0]["text"] = "x" * (MAX_SYSTEM_BYTES + 1)
        self.rewrite("instructor-policy.json", mutate)
        with self.assertRaises(PolicyPackError):
            policy_pack.seal(self.root)


class AlternativeSupplyTest(unittest.TestCase):
    """Two packs cannot be blended, and one pack cannot be half-loaded."""

    def test_a_second_pack_directory_is_not_consulted(self):
        # The loader takes exactly the directory it is given. A sibling pack
        # with a different version must not become a fallback for a broken one.
        directory = tempfile.mkdtemp(prefix="amiss-policy-")
        self.addCleanup(shutil.rmtree, directory, True)
        good = Path(directory) / "policy"
        shutil.copytree(POLICY_ROOT, good)
        broken = Path(directory) / "policy-broken"
        shutil.copytree(POLICY_ROOT, broken)
        (broken / "stage-data.json").unlink()

        self.assertEqual("amiss-hackathon-vertical-slice", load_policy_pack(good).pack_id)
        with self.assertRaises(PolicyPackError):
            load_policy_pack(broken)

    def test_the_fake_contract_fixture_is_a_different_supply_entirely(self):
        # The non-product fake episode carries its own stage data. It must never
        # satisfy the real episode's pack, and the real pack must never stand in
        # for the fake one.
        from simulation.fake_episode import fake_stage_data

        fake = fake_stage_data()
        real = load_policy_pack()
        self.assertNotEqual(fake["episode_id"], real.episode_id)
        self.assertTrue(
            set(fake["allowed_action_set"]["action_ids"]).isdisjoint(
                real.stage_data["allowed_action_set"]["action_ids"]
            )
        )


class PackSealTest(unittest.TestCase):
    """The seal entry point is deterministic and never invents content."""

    def test_sealing_twice_changes_nothing(self):
        root = copy_pack()
        self.addCleanup(shutil.rmtree, root.parent, True)
        first = policy_pack.seal(root)
        bytes_before = {
            name: (root / name).read_bytes()
            for name in ("pack.json", "stage-data.json", "instructor-policy.json")
        }
        second = policy_pack.seal(root)
        self.assertEqual(first.pack_digest, second.pack_digest)
        self.assertEqual(first.system, second.system)
        for name, data in bytes_before.items():
            self.assertEqual(data, (root / name).read_bytes(), name)

    def test_the_checked_in_pack_is_already_sealed(self):
        # A dirty digest here means someone edited content without re-sealing.
        root = copy_pack()
        self.addCleanup(shutil.rmtree, root.parent, True)
        before = {name: (root / name).read_bytes() for name in
                  ("pack.json", "stage-data.json")}
        policy_pack.seal(root)
        for name, data in before.items():
            self.assertEqual(data, (root / name).read_bytes(), name)


class PackBoundaryTest(unittest.TestCase):
    """The pack is public content and the envelope is not a payload field."""

    def test_the_assembled_policy_is_not_a_context_payload_field(self):
        from schema.validate import SchemaValidator, semantic_errors

        pack = load_policy_pack()
        payload = parse_json(
            (ROOT / "schema/fixtures/valid/context-payload.json").read_text(encoding="utf-8")
        )
        validator = SchemaValidator()
        errors = validator.validate(payload, "context-payload.schema.json")
        if not errors:
            errors = semantic_errors(payload, "context-payload.schema.json", "")
        self.assertEqual([], errors)
        self.assertNotIn("policy_envelope", payload)
        self.assertNotIn("system", payload)
        self.assertNotIn(pack.system, json.dumps(payload))

    def test_the_envelope_builds_a_request_and_redacts_to_a_hash(self):
        from simulation import EvaluationRequestBuilder, PolicyEnvelope, redact_request_for_log

        pack = load_policy_pack()
        payload = parse_json(
            (ROOT / "schema/fixtures/valid/context-payload.json").read_text(encoding="utf-8")
        )
        envelope = PolicyEnvelope(**pack.envelope())
        request = EvaluationRequestBuilder.build(
            payload, envelope, request_id="pack-request-1", turn_id="turn-1", origin="answer"
        )
        self.assertEqual(pack.policy_version, request["policy_envelope"]["policy_version"])
        self.assertEqual(pack.system, request["policy_envelope"]["system"])

        redacted = canonical_bytes(redact_request_for_log(request)).decode("utf-8")
        self.assertNotIn("mixed-reality physics experiment", redacted)
        self.assertIn(pack.policy_version, redacted)

    def test_the_pack_holds_no_private_tree_reference(self):
        for name in ("pack.json", "stage-data.json", "instructor-policy.json"):
            text = (POLICY_ROOT / name).read_text(encoding="utf-8")
            for forbidden in ("private/", "AGENTS.md", "D-0066", "fleet"):
                self.assertNotIn(forbidden, text, f"{name}: {forbidden}")

    def test_the_pack_parses_strictly(self):
        # Duplicate keys, NaN, and non-finite numbers are rejected by the same
        # strict reader every other contract artifact uses.
        with self.assertRaises(ContractError):
            parse_json('{"a": 1, "a": 2}')
        with self.assertRaises(ContractError):
            parse_json("NaN")


class CleanPublicTreeTest(unittest.TestCase):
    """`E-10-01`: the pack loads from the public paths alone.

    The tree below holds only what a judge receives. There is no `private/`,
    no `.gitea/`, and no `scripts/`, and the loader still verifies and loads.
    """

    def test_the_pack_loads_from_public_paths_with_no_private_tree(self):
        import subprocess
        import sys

        directory = Path(tempfile.mkdtemp(prefix="amiss-clean-"))
        self.addCleanup(shutil.rmtree, directory, True)
        for name in ("simulation", "schema", "policy"):
            shutil.copytree(ROOT / name, directory / name)
        shutil.copy2(ROOT / ".env.example", directory / ".env.example")
        self.assertFalse((directory / "private").exists())

        script = (
            "from simulation.policy_pack import load_policy_pack\n"
            "p = load_policy_pack()\n"
            "print(p.policy_version, p.pack_id, len(p.stage_data['stage_graph']['nodes']))\n"
        )
        result = subprocess.run(
            [sys.executable, "-c", script],
            cwd=directory,
            capture_output=True,
            text=True,
            env={"PYTHONPATH": str(directory), "PYTHONDONTWRITEBYTECODE": "1",
                 "PATH": "/usr/bin:/bin"},
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            "amiss-hackathon-policy-v1 amiss-hackathon-vertical-slice 7\n",
            result.stdout,
        )


class PackDeviceMirrorTest(unittest.TestCase):
    """The device mirror is driven from one exported vector file."""

    def test_the_mirror_vector_file_exists_and_names_the_pack_version(self):
        document = parse_json(
            (ROOT / "schema/vectors/policy-pack-vector.json").read_text(encoding="utf-8")
        )
        pack = load_policy_pack()
        self.assertEqual(pack.policy_version, document["policy_version"])
        self.assertEqual(pack.pack_id, document["pack_id"])
        self.assertEqual(pack.pack_digest, document["pack_digest"])

    def test_the_mirror_vector_carries_the_exact_assembled_system_hash(self):
        import hashlib

        document = parse_json(
            (ROOT / "schema/vectors/policy-pack-vector.json").read_text(encoding="utf-8")
        )
        pack = load_policy_pack()
        expected = hashlib.sha256(pack.system.encode("utf-8")).hexdigest()
        self.assertEqual(expected, document["system_sha256"])
        self.assertEqual(pack.system_bytes, document["system_bytes"])

    def test_the_mirror_vector_is_a_fresh_generation(self):
        checked = parse_json(
            (ROOT / "schema/vectors/policy-pack-vector.json").read_text(encoding="utf-8")
        )
        self.assertEqual(policy_pack.build_vector(), checked)

    def test_the_mirror_vector_matches_the_stage_data_it_names(self):
        document = parse_json(
            (ROOT / "schema/vectors/policy-pack-vector.json").read_text(encoding="utf-8")
        )
        pack = load_policy_pack()
        self.assertEqual(pack.stage_data_digest, document["stage_data_digest"])
        self.assertEqual(
            list(pack.stage_data["stage_graph"]["nodes"][i]["stage_id"]
                 for i in range(len(pack.stage_data["stage_graph"]["nodes"]))),
            document["stage_ids"],
        )
        self.assertEqual(
            list(pack.stage_data["allowed_action_set"]["action_ids"]),
            document["allowed_action_ids"],
        )


if __name__ == "__main__":
    unittest.main()
