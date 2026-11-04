"""Reader and assembler for the public hackathon instructor policy pack.

The pack is the public supply that approved design 10 places in ``policy/``.
Three files make up one version:

- ``pack.json`` carries the pack version, the file inventory with per-file
  SHA-256 over the exact shipped bytes, and one integrity digest over its own
  canonical payload;
- ``stage-data.json`` is the existing ``StageDataEnvelope`` that
  :class:`simulation.guide.GuideRunner` already loads, with its own content
  digest;
- ``instructor-policy.json`` carries the instructor supply: system
  instructions, rules, the tool catalogue, next questions, allowed actions,
  mistake classification, and declared threshold slots.

This module does not define a second stage format. It verifies the pack, hands
``stage-data.json`` to the existing loader, and then checks that the prose
supply cannot drift away from the mechanical envelope (stage ids, question ids,
action ids, wording ids). Any missing, corrupt, tampered, or incompatible pack
raises :class:`PolicyPackError`; there is no substitute pack and no default.

:func:`assemble_system` builds the ``system`` text of the transport-side
``PolicyEnvelope`` of design 06 section 5.2. It is pure text assembly, not a
second policy interpreter, and it happens only at the send boundary.
"""

from copy import deepcopy
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import sys

if __package__ in {None, ""}:
    # Run as a script: this file imports its sibling packages from the
    # repository root, which is one level above this directory.
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from schema.validate import (
    ContractError,
    SchemaValidator,
    canonical_bytes,
    parse_json,
    semantic_errors,
)
from simulation.guide import GuideRunner, StageLoadError

POLICY_ROOT = Path(__file__).resolve().parent.parent / "policy"
PACK_FILE = "pack.json"
STAGE_DATA_FILE = "stage-data.json"
INSTRUCTOR_FILE = "instructor-policy.json"

PACK_FORMAT_VERSION = "1.0.0"
SUPPORTED_PACK_MAJORS = {1}
SUPPORTED_STAGE_DATA_MAJORS = {1}

PACK_FIELDS = frozenset(
    {
        "pack_format_version",
        "pack_id",
        "policy_version",
        "episode_id",
        "stage_data_format_version",
        "stage_data_file",
        "instructor_policy_file",
        "files",
        "integrity",
    }
)

# A defensive bound, not an approved design number: design 08 bounds every other
# part of an outbound request but lists no limit for the policy body. The pack is
# rejected rather than sent if it ever exceeds this. Registered as an open supply
# for the full P0 pack.
MAX_SYSTEM_BYTES = 64 * 1024


class PolicyPackError(ValueError):
    """A pack that cannot be loaded. Every reason is carried, not the first."""

    def __init__(self, errors):
        errors = tuple(errors)
        super().__init__("; ".join(errors))
        self.errors = errors


def file_sha256(path):
    """SHA-256 over the exact shipped bytes of one file."""
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def stage_data_digest(stage_data):
    """The envelope content digest: canonical payload without ``integrity``.

    This is the same construction the recorded-fixture contract already uses for
    its own integrity field, and the canonical encoder is the one both languages
    are driven against in ``schema/vectors``.
    """
    payload = {key: value for key, value in stage_data.items() if key != "integrity"}
    return hashlib.sha256(canonical_bytes(payload)).hexdigest()


def pack_digest(pack):
    """The pack integrity digest: canonical payload without ``integrity``."""
    payload = {key: value for key, value in pack.items() if key != "integrity"}
    return hashlib.sha256(canonical_bytes(payload)).hexdigest()


def seal_stage_data(stage_data):
    """Return the envelope with a correct digest. Used by the seal entry point."""
    sealed = deepcopy(stage_data)
    sealed.setdefault("integrity", {})
    sealed["integrity"] = {
        "algorithm": "sha256",
        "digest": stage_data_digest(sealed),
        "status": sealed["integrity"].get("status", "verified"),
    }
    return sealed


def seal_pack(pack, root):
    """Return the manifest with correct per-file and pack digests."""
    sealed = deepcopy(pack)
    sealed["files"] = [
        {"path": item["path"], "sha256": file_sha256(Path(root) / item["path"])}
        for item in sealed["files"]
    ]
    sealed["integrity"] = {
        "algorithm": "sha256",
        "digest": pack_digest(sealed),
        "status": sealed["integrity"].get("status", "verified"),
    }
    return sealed


@dataclass(frozen=True)
class PolicyPack:
    """One verified pack. Every field is a value the caller may read freely."""

    source_dir: Path
    pack_format_version: str
    pack_id: str
    policy_version: str
    episode_id: str
    stage_data: dict
    instructor: dict
    system: str
    pack_digest: str
    stage_data_digest: str
    file_digests: tuple

    @property
    def system_bytes(self):
        return len(self.system.encode("utf-8"))

    def envelope(self):
        """The exact transport-side policy record: version and system text only."""
        return {"policy_version": self.policy_version, "system": self.system}

    def summary(self):
        """One bounded line for a panel trace or an evidence record."""
        return (
            f"policy={self.policy_version} pack={self.pack_id} "
            f"stage-data={self.stage_data['stage_data_format_version']} "
            f"system-bytes={self.system_bytes} digest={self.pack_digest[:12]}"
        )


def _read_json(path, errors, label):
    try:
        return parse_json(Path(path).read_text(encoding="utf-8"))
    except (ValueError, ContractError) as error:
        # ContractError and the strict reader's JSONDecodeError both subclass
        # ValueError; a malformed file is reported, never repaired.
        errors.append(f"{label}: {error}")
        return None
    except OSError as error:
        errors.append(f"{label}: unreadable ({error.strerror})")
        return None


def _check_manifest(pack, root, errors):
    if not isinstance(pack, dict):
        errors.append("pack.json: the manifest is not an object")
        return
    unknown = set(pack) - PACK_FIELDS
    if unknown:
        errors.append(f"pack.json: unknown fields {sorted(unknown)}")
    missing = PACK_FIELDS - set(pack)
    if missing:
        errors.append(f"pack.json: missing fields {sorted(missing)}")
        return
    major = str(pack["pack_format_version"]).partition(".")[0]
    if not major.isdigit() or int(major) not in SUPPORTED_PACK_MAJORS:
        errors.append(
            f"pack.json: unsupported pack format major {pack['pack_format_version']}"
        )
    integrity = pack["integrity"]
    if integrity.get("algorithm") != "sha256":
        errors.append("pack.json: integrity algorithm is not sha256")
    if integrity.get("status") != "verified":
        errors.append("pack.json: integrity status is not verified")
    if integrity.get("digest") != pack_digest(pack):
        errors.append("pack.json: integrity digest does not match the canonical payload")
    listed = {item.get("path") for item in pack["files"]}
    expected = {pack.get("stage_data_file"), pack.get("instructor_policy_file")}
    if listed != expected:
        errors.append("pack.json: the file inventory does not match the declared files")
    for item in pack["files"]:
        path = Path(root) / str(item.get("path"))
        if not path.is_file():
            errors.append(f"pack.json: listed file {item.get('path')} is absent")
            continue
        if item.get("sha256") != file_sha256(path):
            errors.append(f"{item.get('path')}: file digest does not match the manifest")


def _check_stage_data(stage_data, pack, errors):
    if not isinstance(stage_data, dict):
        errors.append("stage-data.json: the envelope is not an object")
        return None
    integrity = stage_data.get("integrity") or {}
    if integrity.get("status") != "verified":
        errors.append("stage-data.json: integrity status is not verified")
    if integrity.get("digest") != stage_data_digest(stage_data):
        errors.append("stage-data.json: content digest does not match the canonical payload")
    version = str(stage_data.get("stage_data_format_version", ""))
    if version != pack["stage_data_format_version"]:
        errors.append(
            "stage-data.json: format version differs from the pack manifest"
        )
    major = version.partition(".")[0]
    if not major.isdigit() or int(major) not in SUPPORTED_STAGE_DATA_MAJORS:
        errors.append(f"stage-data.json: unsupported format major {version}")
    if stage_data.get("policy_version") != pack["policy_version"]:
        errors.append("stage-data.json: policy version differs from the pack manifest")
    if stage_data.get("episode_id") != pack["episode_id"]:
        errors.append("stage-data.json: episode id differs from the pack manifest")
    try:
        return GuideRunner.load(stage_data)
    except StageLoadError as error:
        errors.extend(f"stage-data.json: {item}" for item in error.errors)
        return None


def _check_instructor(instructor, stage_data, pack, errors):
    if not isinstance(instructor, dict):
        errors.append("instructor-policy.json: the supply is not an object")
        return
    if instructor.get("policy_version") != pack["policy_version"]:
        errors.append("instructor-policy.json: policy version differs from the pack manifest")
    if instructor.get("episode_id") != pack["episode_id"]:
        errors.append("instructor-policy.json: episode id differs from the pack manifest")

    catalogue = instructor.get("tool_catalogue")
    if not isinstance(catalogue, list) or not catalogue:
        errors.append("instructor-policy.json: the tool catalogue is absent or empty")
    else:
        seen_tools = set()
        for item in catalogue:
            if not isinstance(item, dict):
                errors.append(
                    "instructor-policy.json: a tool catalogue entry is not an object"
                )
                continue
            for field in ("tool_id", "name", "usage"):
                value = item.get(field)
                if not isinstance(value, str) or not value.strip():
                    errors.append(
                        f"instructor-policy.json: a tool entry is missing {field}"
                    )
            tool_id = item.get("tool_id")
            if isinstance(tool_id, str):
                if tool_id in seen_tools:
                    errors.append(
                        f"instructor-policy.json: duplicate tool id {tool_id}"
                    )
                seen_tools.add(tool_id)

    stage_ids = [node["stage_id"] for node in stage_data["stage_graph"]["nodes"]]
    rule_ids = [rule["stage_id"] for rule in instructor.get("stage_rules", [])]
    if rule_ids != stage_ids:
        errors.append(
            "instructor-policy.json: stage rules do not name the stage graph in order"
        )
    question_ids = {item["question_id"] for item in instructor.get("next_questions", [])}
    for rule in instructor.get("stage_rules", []):
        if rule.get("question_id") not in question_ids:
            errors.append(
                f"instructor-policy.json: {rule.get('stage_id')} names an undefined question"
            )
    for question in instructor.get("next_questions", []):
        if question.get("stage_id") not in stage_ids:
            errors.append(
                f"instructor-policy.json: question {question.get('question_id')} names an unknown stage"
            )

    declared = list(stage_data["allowed_action_set"]["action_ids"])
    supplied = [item["action_id"] for item in instructor.get("allowed_actions", {}).get("actions", [])]
    if supplied != declared:
        errors.append(
            "instructor-policy.json: the allowed-action catalogue differs from the envelope"
        )
    for action in instructor.get("allowed_actions", {}).get("actions", []):
        for stage_id in action.get("stages", []):
            if stage_id not in stage_ids:
                errors.append(
                    f"instructor-policy.json: action {action.get('action_id')} names an unknown stage"
                )

    wording_ids = {item["wording_id"] for item in instructor.get("wording", [])}
    referenced = set()
    for category in instructor.get("mistake_classification", {}).get("categories", []):
        if category.get("wording_id") is not None:
            referenced.add(category["wording_id"])
    scope = instructor.get("scope", {}).get("out_of_scope_handling_id")
    if scope is not None:
        referenced.add(scope)
    missing = referenced - wording_ids
    if missing:
        errors.append(f"instructor-policy.json: undefined wording ids {sorted(missing)}")

    threshold_ids = [item["threshold_id"] for item in instructor.get("thresholds", [])]
    if len(set(threshold_ids)) != len(threshold_ids):
        errors.append("instructor-policy.json: duplicate threshold id")
    for threshold in instructor.get("thresholds", []):
        if threshold.get("stage_id") is not None and threshold["stage_id"] not in stage_ids:
            errors.append(
                f"instructor-policy.json: threshold {threshold.get('threshold_id')} names an unknown stage"
            )
        if threshold.get("value") is not None:
            errors.append(
                f"instructor-policy.json: threshold {threshold.get('threshold_id')} claims a value "
                "that this pack has not measured"
            )


def assemble_system(instructor):
    """The one ``system`` text a judge path assembles at the send boundary.

    Order is fixed so the text and its hash are reproducible: instructions,
    series rules, episode rules, tool catalogue, stage intents and questions,
    action catalogue, mistake classification, wording. No field of the pack is
    silently dropped except the volumes already bounded by the caller.
    """
    lines = []

    def block(title, items):
        if items:
            lines.append(title + ":")
            lines.extend("  " + item for item in items)

    instructions = instructor.get("system_instructions", {})
    block("Instructions", instructions.get("text", []))
    block("Series rules", [f"{r['rule_id']}: {r['text']}" for r in instructor.get("series_rules", [])])
    block("Episode rules", [f"{r['rule_id']}: {r['text']}" for r in instructor.get("episode_rules", [])])
    block(
        "Tools and controls",
        [
            f"{item['tool_id']} ({item['name']}): {item['usage']}"
            for item in instructor.get("tool_catalogue", [])
        ],
    )

    stage_lines = []
    questions = {q["stage_id"]: q for q in instructor.get("next_questions", [])}
    for rule in instructor.get("stage_rules", []):
        question = questions.get(rule["stage_id"], {})
        stage_lines.append(
            f"{rule['stage_id']} ({rule['title']}): {rule['intent']} "
            f"Props: {', '.join(rule.get('allowed_props', [])) or 'none'}. "
            f"Proposals: {rule.get('proposal_expectation', 'none')}. "
            f"Prediction: {rule.get('prediction', 'none')}. "
            f"Question: {question.get('text', '')}"
        )
    block("Stages", stage_lines)

    actions = [
        f"{item['action_id']} ({item['tool']} {item['operation']}): {item['purpose']}"
        for item in instructor.get("allowed_actions", {}).get("actions", [])
    ]
    block("Allowed actions", actions)

    categories = [
        f"{item['category_id']}: {item['trigger']} -> {item['instructor_response']}"
        for item in instructor.get("mistake_classification", {}).get("categories", [])
    ]
    block("Reply classification", categories)

    block("Wording", [f"{item['wording_id']}: {item['text']}" for item in instructor.get("wording", [])])

    return "\n".join(lines).strip()


def load_policy_pack(root=POLICY_ROOT):
    """Load the one pack at ``root`` or raise :class:`PolicyPackError`.

    Nothing is substituted on failure: the caller gets every reason and the
    caller decides whether dynamic help is simply absent.
    """
    root = Path(root)
    errors = []
    if not root.is_dir():
        raise PolicyPackError([f"{root}: the policy directory is absent"])
    pack = _read_json(root / PACK_FILE, errors, "pack.json")
    stage_raw = _read_json(root / STAGE_DATA_FILE, errors, "stage-data.json")
    instructor = _read_json(root / INSTRUCTOR_FILE, errors, "instructor-policy.json")
    if errors:
        raise PolicyPackError(errors)

    _check_manifest(pack, root, errors)
    stage_data = _check_stage_data(stage_raw, pack, errors)
    if stage_data is not None:
        _check_instructor(instructor, stage_data, pack, errors)
    if errors:
        raise PolicyPackError(errors)

    system = assemble_system(instructor)
    if not system.strip():
        raise PolicyPackError(["instructor-policy.json: the assembled system text is empty"])
    if len(system.encode("utf-8")) > MAX_SYSTEM_BYTES:
        raise PolicyPackError(
            [
                f"instructor-policy.json: the assembled system text exceeds "
                f"{MAX_SYSTEM_BYTES} bytes"
            ]
        )

    return PolicyPack(
        source_dir=root,
        pack_format_version=pack["pack_format_version"],
        pack_id=pack["pack_id"],
        policy_version=pack["policy_version"],
        episode_id=pack["episode_id"],
        stage_data=stage_data,
        instructor=instructor,
        system=system,
        pack_digest=pack["integrity"]["digest"],
        stage_data_digest=stage_data["integrity"]["digest"],
        file_digests=tuple(
            (item["path"], item["sha256"]) for item in pack["files"]
        ),
    )


def seal(root=POLICY_ROOT):
    """Recompute the stage-data and pack digests in place, then verify.

    This is the one deterministic entry point that keeps the checked-in digests
    honest after an edit. It is not a loader and it never invents content.
    """
    root = Path(root)
    stage_path = root / STAGE_DATA_FILE
    pack_path = root / PACK_FILE
    stage_data = parse_json(stage_path.read_text(encoding="utf-8"))
    sealed_stage = seal_stage_data(stage_data)
    stage_path.write_text(_dump(sealed_stage), encoding="utf-8")
    pack = parse_json(pack_path.read_text(encoding="utf-8"))
    sealed_pack = seal_pack(pack, root)
    pack_path.write_text(_dump(sealed_pack), encoding="utf-8")
    return load_policy_pack(root)


def render(root=POLICY_ROOT):
    """The assembled system text for one verified pack."""
    return load_policy_pack(root).system


VECTOR_PATH = POLICY_ROOT.parent / "schema" / "vectors" / "policy-pack-vector.json"
VECTOR_FORMAT_VERSION = "1.0.0"


def build_vector(root=POLICY_ROOT):
    """The one shared record both the oracle and the device mirror load."""
    pack = load_policy_pack(root)
    return {
        "vector_format_version": VECTOR_FORMAT_VERSION,
        "pack_id": pack.pack_id,
        "pack_format_version": pack.pack_format_version,
        "policy_version": pack.policy_version,
        "episode_id": pack.episode_id,
        "stage_data_format_version": pack.stage_data["stage_data_format_version"],
        "stage_data_digest": pack.stage_data_digest,
        "pack_digest": pack.pack_digest,
        "file_digests": [{"path": path, "sha256": digest} for path, digest in pack.file_digests],
        "system_bytes": pack.system_bytes,
        "system_sha256": hashlib.sha256(pack.system.encode("utf-8")).hexdigest(),
        "stage_ids": [node["stage_id"] for node in pack.stage_data["stage_graph"]["nodes"]],
        "allowed_action_ids": list(pack.stage_data["allowed_action_set"]["action_ids"]),
        "question_ids": [item["question_id"] for item in pack.instructor["next_questions"]],
        "threshold_ids": [item["threshold_id"] for item in pack.instructor["thresholds"]],
        "max_system_bytes": MAX_SYSTEM_BYTES,
    }


def write_vector(path=VECTOR_PATH):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(build_vector(), indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    return path


def _dump(value):
    return json.dumps(value, indent=2, ensure_ascii=False, sort_keys=False) + "\n"


def main(argv):
    command = argv[0] if argv else "verify"
    root = Path(argv[1]) if len(argv) > 1 else POLICY_ROOT
    if command == "seal":
        pack = seal(root)
        print(f"sealed {pack.summary()}")
        return 0
    if command == "verify":
        pack = load_policy_pack(root)
        print(f"loaded {pack.summary()}")
        print(f"stages: {len(pack.stage_data['stage_graph']['nodes'])}")
        print(f"actions: {len(pack.stage_data['allowed_action_set']['action_ids'])}")
        return 0
    if command == "system":
        sys.stdout.write(render(root) + "\n")
        return 0
    if command == "vector":
        print(write_vector(root / VECTOR_PATH.relative_to(POLICY_ROOT)
                           if root != POLICY_ROOT else VECTOR_PATH))
        return 0
    print(f"unknown command: {command} (verify|seal|system|vector)", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
