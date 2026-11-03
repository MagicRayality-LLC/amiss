import json
import re
import unittest
from pathlib import Path

from simulation import transport

# One key-shaped value with no chance of colliding with a real credential. It is
# declared in the suites that inject it and in nothing else; every artifact this
# slice produces is scanned for it.
SENTINEL = "amiss-test-sentinel-key-0000000000000000"

ROOT = Path(__file__).resolve().parent.parent.parent

# Artifacts this slice produces or generates. None of them may carry the key,
# an authorization header, a bearer token, a fleet address, or CJK text.
PRODUCED = (
    "schema/vectors/admission-vectors.json",
    "schema/vectors/policy-pack-vector.json",
    "simulation/transport.py",
    "simulation/policy_pack.py",
    "simulation/vectors.py",
    "simulation/README.md",
    "policy/pack.json",
    "policy/stage-data.json",
    "policy/instructor-policy.json",
    "policy/README.md",
    ".env.example",
)

# The suites that must inject the sentinel to prove it is absent elsewhere.
INJECTING = (
    "simulation/tests/test_transport.py",
    "simulation/tests/test_sentinel.py",
)

PUBLIC_TREE = ("simulation", "schema", "policy")
PUBLIC_FILES = (".env.example",)

# Negative fixtures exist to prove a forbidden field is rejected, so they hold
# the forbidden field on purpose. They are not produced artifacts.
NEGATIVE_FIXTURES = "schema/fixtures/invalid/"


def files_under(*roots):
    for root in roots:
        for path in sorted((ROOT / root).rglob("*")):
            if not path.is_file() or path.suffix not in {".py", ".json", ".md"}:
                continue
            relative = str(path.relative_to(ROOT))
            if relative.startswith(NEGATIVE_FIXTURES):
                continue
            yield path


def public_files():
    """Every text artifact a judge receives: the trees plus the two root files."""
    for path in files_under(*PUBLIC_TREE):
        yield path
    for name in PUBLIC_FILES:
        path = ROOT / name
        if path.is_file():
            yield path


class SentinelAbsenceTest(unittest.TestCase):
    """The evaluation key never lands in a fixture, log, or produced artifact."""

    def test_sentinel_never_appears_in_a_produced_artifact(self):
        for relative in PRODUCED:
            path = ROOT / relative
            self.assertTrue(path.exists(), relative)
            self.assertNotIn(SENTINEL, path.read_text(encoding="utf-8"), relative)

    def test_sentinel_is_declared_only_where_it_is_injected(self):
        declared = {
            str(path.relative_to(ROOT))
            for path in files_under(*PUBLIC_TREE)
            if SENTINEL in path.read_text(encoding="utf-8")
        }
        self.assertEqual(set(INJECTING), declared,
                         "the sentinel belongs only in the suites that inject it")

    def test_key_handle_outputs_never_carry_the_key(self):
        key = transport.ModelKeyHandle(SENTINEL)
        for name, value in {
            "repr": repr(key),
            "str": str(key),
            "redacted": key.redacted(),
        }.items():
            self.assertNotIn(SENTINEL, value, name)

    def test_no_public_artifact_carries_a_credential_or_a_fleet_address(self):
        credential = re.compile(
            r"(?i)(sk-[A-Za-z0-9]{16,}|bearer\s+[A-Za-z0-9._-]{12,}|"
            r"api[_-]?key\s*[:=]\s*[A-Za-z0-9\"'])"
        )
        fleet = re.compile(r"\b10\.0\.0\.\d{1,3}\b")
        cjk = re.compile(r"[\u3000-\u9fff\uff00-\uffef]")
        for path in public_files():
            relative = str(path.relative_to(ROOT))
            text = path.read_text(encoding="utf-8")
            self.assertIsNone(credential.search(text), relative)
            self.assertIsNone(fleet.search(text), relative)
            self.assertIsNone(cjk.search(text), relative)

    def test_the_env_template_holds_only_blank_assignments(self):
        """Design 10 section 1: `.env.example` documents blank shapes only."""
        text = (ROOT / ".env.example").read_text(encoding="utf-8")
        assignments = [
            line.strip()
            for line in text.splitlines()
            if re.match(r"^\s*[A-Za-z_][A-Za-z0-9_]*\s*=", line)
        ]
        self.assertTrue(assignments, ".env.example must document at least one variable")
        for line in assignments:
            self.assertTrue(line.endswith("="), f"assigned a value: {line}")
        # The fleet-address target is assembled so this file never itself
        # contains an address; the check below is unchanged.
        fleet_prefix = ".".join(("10", "0", "0")) + "."
        for forbidden in ("http://", "https://", "api.meta.ai", "Bearer", "sk-",
                          "password", fleet_prefix, "gitea"):
            self.assertNotIn(forbidden, text, forbidden)

    def test_the_policy_pack_holds_no_secret_or_private_reference(self):
        for path in files_under("policy"):
            relative = str(path.relative_to(ROOT))
            text = path.read_text(encoding="utf-8")
            for forbidden in ('"authorization"', '"api_key"', "private/",
                              "AGENTS.md", "Bearer "):
                self.assertNotIn(forbidden, text, f"{relative}: {forbidden}")

    def test_no_fixture_or_vector_record_carries_an_authorization_field(self):
        """Sanitized fixtures and requests carry no credential field at all."""
        for path in files_under("schema"):
            if path.suffix != ".json" or path.name.endswith(".schema.json"):
                continue
            text = path.read_text(encoding="utf-8").lower()
            self.assertNotIn('"authorization"', text, str(path.relative_to(ROOT)))
            self.assertNotIn('"api_key"', text, str(path.relative_to(ROOT)))

    def test_the_vector_file_carries_no_policy_body_outside_its_request_anchor(self):
        document = json.loads(
            (ROOT / "schema/vectors/admission-vectors.json").read_text(encoding="utf-8")
        )
        body = document["request"]["request"]["policy_envelope"]["system"]
        self.assertTrue(body.startswith("Synthetic instructor policy fixture"))
        for case in document["admission"]:
            self.assertNotIn(body, json.dumps(case["response"]))
            self.assertNotIn("policy_envelope", json.dumps(case["response"]))
        for declarative in document["contexts"].values():
            self.assertNotIn(body, json.dumps(declarative))

    def test_the_vector_file_holds_no_request_or_response_body_field_allowlist_breach(self):
        document = json.loads(
            (ROOT / "schema/vectors/admission-vectors.json").read_text(encoding="utf-8")
        )
        for case in document["admission"]:
            response = case["response"]
            self.assertEqual(
                {"response_contract_version", "request_id", "refs", "speech_units",
                 "tool_proposals"},
                set(response),
                case["name"],
            )


if __name__ == "__main__":
    unittest.main()
