#!/usr/bin/env python3
import argparse
from datetime import datetime
import hashlib
import json
import math
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SCHEMA_DIR = ROOT / "schemas"
MAX_DEPTH = 16
MAX_OBJECT_MEMBERS = 128
MAX_ARRAY_ITEMS = 256
SUPPORTED_MAJORS = {
    "framework_api_version": 1,
    "snapshot_envelope_version": 1,
    "tool_schema_version": 1,
    "save_format_version": 1,
    "stage_data_format_version": 1,
    "context_payload_version": 1,
    "fixture_format_version": 1,
    "provenance_contract_version": 1,
}
TOOL_OPERATIONS = {
    "state.read": {"snapshot", "ledger", "measurements"},
    "trial.propose": {"create", "derive"},
    "trial.orchestrate": {
        "start",
        "pause",
        "resume",
        "begin_measurement",
        "archive",
        "present",
    },
    "presentation.request": {
        "highlight_surface",
        "replay_slow",
        "open_chart",
        "show_subtitle",
    },
    "speech.emit": {"answer", "proactive", "safety"},
}
METADATA_KEYWORDS = {
    "$schema",
    "$id",
    "$defs",
    "title",
    "description",
    "$comment",
    "default",
    "examples",
}


class ContractError(ValueError):
    pass


def reject_constant(value):
    raise ContractError(f"non-finite number: {value}")


def parse_float(value):
    number = float(value)
    if not math.isfinite(number):
        raise ContractError(f"non-finite number: {value}")
    return number


def no_duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ContractError(f"duplicate object key: {key}")
        result[key] = value
    return result


def parse_json(text):
    return json.loads(
        text,
        object_pairs_hook=no_duplicate_keys,
        parse_constant=reject_constant,
        parse_float=parse_float,
    )


def canonical_bytes(value):
    return json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")


def check_resource_bounds(value, path="$", depth=0):
    errors = []
    if depth > MAX_DEPTH:
        return [f"{path}: nesting exceeds {MAX_DEPTH}"]
    if isinstance(value, dict):
        if len(value) > MAX_OBJECT_MEMBERS:
            errors.append(f"{path}: object has more than {MAX_OBJECT_MEMBERS} members")
        for key, child in value.items():
            errors.extend(check_resource_bounds(child, f"{path}.{key}", depth + 1))
    elif isinstance(value, list):
        if len(value) > MAX_ARRAY_ITEMS:
            errors.append(f"{path}: array has more than {MAX_ARRAY_ITEMS} entries")
        for index, child in enumerate(value):
            errors.extend(check_resource_bounds(child, f"{path}[{index}]", depth + 1))
    elif isinstance(value, float) and not math.isfinite(value):
        errors.append(f"{path}: non-finite number")
    return errors


def json_type_matches(value, expected):
    matches = {
        "object": lambda item: isinstance(item, dict),
        "array": lambda item: isinstance(item, list),
        "string": lambda item: isinstance(item, str),
        "boolean": lambda item: isinstance(item, bool),
        "null": lambda item: item is None,
        "integer": lambda item: isinstance(item, int) and not isinstance(item, bool),
        "number": lambda item: isinstance(item, (int, float))
        and not isinstance(item, bool)
        and (not isinstance(item, float) or math.isfinite(item)),
    }
    return matches[expected](value)


def equal_json(left, right):
    return canonical_bytes(left) == canonical_bytes(right)


class SchemaValidator:
    def __init__(self, schema_dir=SCHEMA_DIR):
        self.schema_dir = schema_dir
        self.documents = {}
        for path in sorted(schema_dir.glob("*.schema.json")):
            self.documents[path.name] = parse_json(path.read_text(encoding="utf-8"))

    def resolve(self, ref, document_name):
        if ref.startswith("#"):
            target_name = document_name
            fragment = ref[1:]
        else:
            file_part, separator, fragment_part = ref.partition("#")
            target_name = file_part
            fragment = fragment_part if separator else ""
            if not target_name or Path(target_name).name != target_name:
                raise ContractError(f"unsupported schema reference: {ref}")
        if target_name not in self.documents:
            raise ContractError(f"unknown schema reference: {ref}")
        target = self.documents[target_name]
        if fragment:
            if not fragment.startswith("/"):
                raise ContractError(f"unsupported schema fragment: {ref}")
            for part in fragment[1:].split("/"):
                part = part.replace("~1", "/").replace("~0", "~")
                target = target[part]
        return target, target_name

    def validate(self, value, schema_name):
        schema = self.documents[schema_name]
        return self.validate_value(value, schema, schema_name, "$")

    def validate_value(self, value, schema, document_name, path):
        if isinstance(schema, bool):
            return [] if schema else [f"{path}: schema is false"]
        if not isinstance(schema, dict):
            raise ContractError(f"invalid schema at {path}")
        errors = []
        if "$ref" in schema:
            target, target_name = self.resolve(schema["$ref"], document_name)
            errors.extend(self.validate_value(value, target, target_name, path))

        if "type" in schema:
            expected_types = schema["type"]
            if isinstance(expected_types, str):
                expected_types = [expected_types]
            if not any(json_type_matches(value, item) for item in expected_types):
                errors.append(f"{path}: expected type {schema['type']}")

        if "enum" in schema and not any(equal_json(value, item) for item in schema["enum"]):
            errors.append(f"{path}: value is not in enum")
        if "const" in schema and not equal_json(value, schema["const"]):
            errors.append(f"{path}: value does not match const")

        if "allOf" in schema:
            for child in schema["allOf"]:
                errors.extend(self.validate_value(value, child, document_name, path))
        if "anyOf" in schema:
            alternatives = [
                self.validate_value(value, child, document_name, path)
                for child in schema["anyOf"]
            ]
            if not any(not item for item in alternatives):
                errors.append(f"{path}: no anyOf alternative matched")
        if "oneOf" in schema:
            alternatives = [
                self.validate_value(value, child, document_name, path)
                for child in schema["oneOf"]
            ]
            matches = sum(not item for item in alternatives)
            if matches != 1:
                errors.append(f"{path}: oneOf matched {matches} alternatives")

        if isinstance(value, dict):
            errors.extend(self.validate_object(value, schema, document_name, path))
        if isinstance(value, list):
            errors.extend(self.validate_array(value, schema, document_name, path))
        if isinstance(value, str):
            if "minLength" in schema and len(value) < schema["minLength"]:
                errors.append(f"{path}: string is shorter than minLength")
            if "maxLength" in schema and len(value) > schema["maxLength"]:
                errors.append(f"{path}: string is longer than maxLength")
            if "pattern" in schema and re.search(schema["pattern"], value) is None:
                errors.append(f"{path}: string does not match pattern")
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            if "minimum" in schema and value < schema["minimum"]:
                errors.append(f"{path}: number is below minimum")
            if "maximum" in schema and value > schema["maximum"]:
                errors.append(f"{path}: number is above maximum")
            if "exclusiveMinimum" in schema and value <= schema["exclusiveMinimum"]:
                errors.append(f"{path}: number is below exclusiveMinimum")
            if "exclusiveMaximum" in schema and value >= schema["exclusiveMaximum"]:
                errors.append(f"{path}: number is above exclusiveMaximum")

        supported = {
            "$ref",
            "type",
            "enum",
            "const",
            "allOf",
            "anyOf",
            "oneOf",
            "properties",
            "required",
            "additionalProperties",
            "propertyNames",
            "minProperties",
            "maxProperties",
            "items",
            "minItems",
            "maxItems",
            "uniqueItems",
            "minLength",
            "maxLength",
            "pattern",
            "minimum",
            "maximum",
            "exclusiveMinimum",
            "exclusiveMaximum",
        } | METADATA_KEYWORDS
        unsupported = set(schema) - supported
        if unsupported:
            raise ContractError(f"unsupported schema keywords at {path}: {sorted(unsupported)}")
        return errors

    def validate_object(self, value, schema, document_name, path):
        errors = []
        properties = schema.get("properties", {})
        for key in schema.get("required", []):
            if key not in value:
                errors.append(f"{path}: missing required field {key}")
        if "minProperties" in schema and len(value) < schema["minProperties"]:
            errors.append(f"{path}: too few object members")
        if "maxProperties" in schema and len(value) > schema["maxProperties"]:
            errors.append(f"{path}: too many object members")
        if "propertyNames" in schema:
            for key in value:
                errors.extend(
                    self.validate_value(key, schema["propertyNames"], document_name, path)
                )
        additional = schema.get("additionalProperties", True)
        for key, child in value.items():
            child_path = f"{path}.{key}"
            if key in properties:
                errors.extend(
                    self.validate_value(child, properties[key], document_name, child_path)
                )
            elif additional is False:
                errors.append(f"{child_path}: unknown field")
            elif isinstance(additional, dict):
                errors.extend(
                    self.validate_value(child, additional, document_name, child_path)
                )
        return errors

    def validate_array(self, value, schema, document_name, path):
        errors = []
        if "minItems" in schema and len(value) < schema["minItems"]:
            errors.append(f"{path}: too few array entries")
        if "maxItems" in schema and len(value) > schema["maxItems"]:
            errors.append(f"{path}: too many array entries")
        if schema.get("uniqueItems"):
            encoded = [canonical_bytes(item) for item in value]
            if len(set(encoded)) != len(encoded):
                errors.append(f"{path}: array entries are not unique")
        if "items" in schema:
            for index, child in enumerate(value):
                errors.extend(
                    self.validate_value(
                        child, schema["items"], document_name, f"{path}[{index}]"
                    )
                )
        return errors


def ref_key(ref):
    return ref["trial_id"], ref["trial_revision"]


def contains_ref(refs, expected):
    return expected is not None and any(ref_key(ref) == ref_key(expected) for ref in refs)


def check_versions(value, path="$"):
    errors = []
    if isinstance(value, dict):
        for key, child in value.items():
            if key in SUPPORTED_MAJORS and isinstance(child, str):
                major = int(child.partition(".")[0])
                if major != SUPPORTED_MAJORS[key]:
                    errors.append(f"{path}.{key}: unsupported major version")
            errors.extend(check_versions(child, f"{path}.{key}"))
    elif isinstance(value, list):
        for index, child in enumerate(value):
            errors.extend(check_versions(child, f"{path}[{index}]"))
    return errors


def check_token_set(token_set, path):
    errors = []
    for index, entry in enumerate(token_set["tokens"]):
        if entry["kind"] == "solicitation":
            if "solicitation_kind" not in entry:
                errors.append(f"{path}.tokens[{index}]: solicitation kind is required")
        elif "solicitation_kind" in entry:
            errors.append(f"{path}.tokens[{index}]: unexpected solicitation kind")
    return errors


def check_proposal(value, raw):
    errors = []
    if len(raw.encode("utf-8")) > 16 * 1024:
        errors.append("$: encoded proposal exceeds 16 KiB")
    tool = value["tool"]
    operation = value["parameters"]["operation"]
    if operation not in TOOL_OPERATIONS[tool]:
        errors.append("$.parameters.operation: operation does not belong to tool")
    target = value["target"]
    basis = value["basis_trial_ref"]
    refs = value["refs"]
    parameters = value["parameters"]

    if target["kind"] == "new_trial":
        if tool != "trial.propose":
            errors.append("$.target: new_trial is valid only for trial.propose")
        source = target["source_trial_ref"]
        if operation == "create":
            if source is not None or basis is not None:
                errors.append("$: create requires null source and basis refs")
            if "from_value" in parameters["input_delta"]:
                errors.append("$.parameters.input_delta.from_value: forbidden for create")
        elif operation == "derive":
            if source is None or basis is None:
                errors.append("$: derive requires source and basis refs")
            if "from_value" not in parameters["input_delta"]:
                errors.append("$.parameters.input_delta.from_value: required for derive")
            if source is not None and basis is not None and ref_key(source) != ref_key(basis):
                errors.append("$.basis_trial_ref: derive basis must equal source")
        if not contains_ref(refs, source):
            if source is not None:
                errors.append("$.refs: source ref is absent")
    else:
        if tool == "trial.propose":
            errors.append("$.target: trial.propose requires new_trial")
        target_ref = target["trial_ref"]
        if basis is None:
            errors.append("$.basis_trial_ref: required for an existing target")
        if not contains_ref(refs, target_ref):
            errors.append("$.refs: target ref is absent")
        if "target_trial_ref" in parameters and ref_key(parameters["target_trial_ref"]) != ref_key(target_ref):
            errors.append("$.parameters.target_trial_ref: must equal envelope target")

    if basis is not None and not contains_ref(refs, basis):
        errors.append("$.refs: basis ref is absent")
    for citation in parameters.get("citations", []):
        if not contains_ref(refs, citation["trial_ref"]):
            errors.append("$.refs: citation ref is absent")
    if "text" in parameters and len(parameters["text"].encode("utf-8")) > 4 * 1024:
        errors.append("$.parameters.text: text exceeds 4 KiB UTF-8")
    if operation == "safety" and value["origin"] != "guide_default":
        errors.append("$.origin: safety speech requires guide_default")
    return errors


def check_snapshot(value):
    errors = []
    identity = value["identity"]
    refs = identity["refs"]
    current = identity["current_trial_ref"]
    trial_identity = identity["trial_identity"]
    if not contains_ref(refs, current):
        errors.append("$.identity.refs: current ref is absent")
    if trial_identity["trial_id"] != current["trial_id"]:
        errors.append("$.identity.trial_identity.trial_id: does not match current ref")
    if trial_identity["episode_id"] != identity["episode_id"]:
        errors.append("$.identity.trial_identity.episode_id: does not match identity")
    state = value["progress"]["trial_state"]
    if state["trial_revision"] != current["trial_revision"] or state["tick"] != identity["tick"]:
        errors.append("$.progress.trial_state: revision or tick does not match identity")
    guide_ref = value["progress"]["guide"]["trial_ref"]
    if not contains_ref(refs, guide_ref):
        errors.append("$.progress.guide.trial_ref: ref is absent from comparison set")

    for index, measurement in enumerate(value["measurements"]):
        path = f"$.measurements[{index}]"
        if not contains_ref(refs, measurement["trial_ref"]):
            errors.append(f"{path}.trial_ref: ref is absent from comparison set")
        if measurement["window"]["end_tick"] < measurement["window"]["start_tick"]:
            errors.append(f"{path}.window: end tick precedes start tick")
        if measurement["applicability"] == "valid":
            if measurement["value"] is None:
                errors.append(f"{path}.value: valid measurement requires a value")
        else:
            if measurement["value"] is not None:
                errors.append(f"{path}.value: unavailable measurement must withhold value")
            if "display_tuple" in measurement:
                errors.append(f"{path}.display_tuple: unavailable measurement must withhold tuple")

    for index, prediction in enumerate(value["predictions"]):
        path = f"$.predictions[{index}]"
        for field in ("subject_trial_ref", "target_trial_ref"):
            if not contains_ref(refs, prediction[field]):
                errors.append(f"{path}.{field}: ref is absent from comparison set")
        if prediction["skipped"] == (prediction["choice_id"] is not None):
            errors.append(f"{path}: exactly one explicit choice or skip is required")
    token_set = value["policy_tokens"]
    if token_set["stage_id"] != value["progress"]["guide"]["stage_id"]:
        errors.append("$.policy_tokens.stage_id: does not match guide progress")
    for ref in token_set["refs"]:
        if not contains_ref(refs, ref):
            errors.append("$.policy_tokens.refs: ref is absent from comparison set")
    errors.extend(check_token_set(token_set, "$.policy_tokens"))
    return errors


def check_stage_data(value):
    errors = []
    nodes = value["stage_graph"]["nodes"]
    node_ids = [node["stage_id"] for node in nodes]
    if len(set(node_ids)) != len(node_ids):
        errors.append("$.stage_graph.nodes: duplicate stage id")
    if value["stage_graph"]["initial_stage_id"] not in node_ids:
        errors.append("$.stage_graph.initial_stage_id: unknown initial stage")
    definitions = {
        item["fact_kind"]: item for item in value["evidence_flag_definitions"]
    }
    if len(definitions) != len(value["evidence_flag_definitions"]):
        errors.append("$.evidence_flag_definitions: duplicate fact kind")
    rule_ids = []
    for node_index, node in enumerate(nodes):
        path = f"$.stage_graph.nodes[{node_index}]"
        if not node["terminal"] and not node["evidence_pair_rules"]:
            errors.append(f"{path}: nonterminal stage has no evidence pair")
        for rule_index, rule in enumerate(node["evidence_pair_rules"]):
            rule_path = f"{path}.evidence_pair_rules[{rule_index}]"
            rule_ids.append(rule["rule_id"])
            first = definitions.get(rule["first_fact_kind"])
            second = definitions.get(rule["second_fact_kind"])
            if first is None or second is None:
                errors.append(f"{rule_path}: unknown evidence fact kind")
                continue
            if first["fact_kind"] == second["fact_kind"]:
                errors.append(f"{rule_path}: evidence facts must be different")
            elif first["fact_class"] == second["fact_class"]:
                if first["measurement_path_id"] == second["measurement_path_id"]:
                    errors.append(f"{rule_path}: same-class facts need independent paths")
    if len(set(rule_ids)) != len(rule_ids):
        errors.append("$.stage_graph.nodes: duplicate evidence rule id")
    return errors


def check_result(value):
    errors = []
    if value["outcome"] == "executed":
        if value["reason_code"] is not None or value["rejection_stage"] is not None:
            errors.append("$: executed result cannot carry rejection data")
        if value["accepted_command_id"] is None or value["execution_receipt"] is None:
            errors.append("$: executed result requires command and receipt")
    else:
        if value["reason_code"] is None or value["rejection_stage"] is None:
            errors.append("$: rejected result requires reason and stage")
        if value["accepted_command_id"] is not None or value["execution_receipt"] is not None:
            errors.append("$: rejected result cannot carry execution data")
    return errors


def check_confirmation(value):
    errors = []
    if not contains_ref(value["refs"], value["basis_trial_ref"]):
        if value["basis_trial_ref"] is not None:
            errors.append("$.refs: basis ref is absent")
    if value["record_kind"] == "challenge":
        target = value["target"]
        if target["kind"] == "existing" and not contains_ref(value["refs"], target["trial_ref"]):
            errors.append("$.refs: target ref is absent")
    return errors


def check_context(value, raw):
    errors = []
    if len(raw.encode("utf-8")) > 256 * 1024:
        errors.append("$: ContextPayload exceeds 256 KiB")
    if len(canonical_bytes(value["compressed_session_context"])) > 32 * 1024:
        errors.append("$.compressed_session_context: context exceeds 32 KiB")
    if len(value["current_user_utterance"]["text"].encode("utf-8")) > 8 * 1024:
        errors.append("$.current_user_utterance.text: utterance exceeds 8 KiB")
    refs = value["refs"]
    keys = [ref_key(ref) for ref in refs]
    if len(set(keys)) != len(keys):
        errors.append("$.refs: refs must be canonical and unique")
    snapshots = value["comparison_package"]
    if len(snapshots) != len(refs):
        errors.append("$.comparison_package: snapshot count must equal ref count")
    snapshot_keys = [ref_key(item["identity"]["current_trial_ref"]) for item in snapshots]
    if set(snapshot_keys) != set(keys):
        errors.append("$.comparison_package: snapshots do not match refs")
    if [ref_key(ref) for ref in value["policy_tokens"]["refs"]] != keys:
        errors.append("$.policy_tokens.refs: must equal canonical payload refs")
    errors.extend(check_token_set(value["policy_tokens"], "$.policy_tokens"))
    for index, snapshot in enumerate(snapshots):
        errors.extend(check_snapshot(snapshot))
    return errors


def sha256_json(value):
    return hashlib.sha256(canonical_bytes(value)).hexdigest()


def check_model_provenance(value):
    errors = []
    try:
        start = datetime.fromisoformat(value["ts_start"].replace("Z", "+00:00"))
        end = datetime.fromisoformat(value["ts_end"].replace("Z", "+00:00"))
    except ValueError:
        return ["$.timestamps: invalid UTC date or time"]
    if end < start:
        errors.append("$.ts_end: precedes ts_start")
    return errors


def check_recorded_fixture(value):
    errors = []
    request = value["request"]
    response = value["response"]
    provenance = value["provenance"]
    versions = value["contract_versions"]
    recipe = value["deterministic_recipe"]

    if provenance["mode"] != "recorded":
        errors.append("$.provenance.mode: recorded fixture requires recorded mode")
    if request["model"] != provenance["model"]:
        errors.append("$.provenance.model: does not match request model")
    if request["request_id"] != response["request_id"]:
        errors.append("$.response.request_id: does not match request")
    if request["request_id"] != provenance["request_id"]:
        errors.append("$.provenance.request_id: does not match request")
    if canonical_bytes(request["refs"]) != canonical_bytes(response["refs"]):
        errors.append("$.response.refs: do not match request refs")
    if not contains_ref(request["refs"], recipe["initial_trial_ref"]):
        errors.append("$.deterministic_recipe.initial_trial_ref: absent from request refs")
    if value["policy_version"] != request["policy_envelope"]["policy_version"]:
        errors.append("$.request.policy_envelope.policy_version: does not match fixture")
    if versions["fixture_format_version"] != value["fixture_format_version"]:
        errors.append("$.contract_versions.fixture_format_version: does not match fixture")
    if provenance["prompt_hash"] != sha256_json(request):
        errors.append("$.provenance.prompt_hash: does not match canonical request")
    if provenance["response_hash"] != sha256_json(response):
        errors.append("$.provenance.response_hash: does not match canonical response")
    payload = {key: child for key, child in value.items() if key != "integrity"}
    if value["integrity"]["digest"] != sha256_json(payload):
        errors.append("$.integrity.digest: does not match canonical fixture payload")

    errors.extend(check_model_provenance(provenance))
    errors.extend(check_context(request["context_payload"], json.dumps(request["context_payload"])))
    for index, proposal in enumerate(response["tool_proposals"]):
        errors.extend(check_proposal(proposal, json.dumps(proposal)))
    for index, unit in enumerate(response["speech_units"]):
        path = f"$.response.speech_units[{index}]"
        if canonical_bytes(unit["refs"]) != canonical_bytes(request["refs"]):
            errors.append(f"{path}.refs: do not match request refs")
        for citation in unit["citations"]:
            if not contains_ref(request["refs"], citation["trial_ref"]):
                errors.append(f"{path}.citations: ref is absent from request refs")
        if unit["operation"] == "safety" and request["origin"] != "guide_default":
            errors.append(f"{path}: safety speech requires guide_default origin")
    return errors


def semantic_errors(value, schema_name, raw):
    errors = check_versions(value)
    if schema_name == "tool-proposal.schema.json":
        errors.extend(check_proposal(value, raw))
    elif schema_name == "snapshot.schema.json":
        errors.extend(check_snapshot(value))
    elif schema_name == "stage-data.schema.json":
        errors.extend(check_stage_data(value))
    elif schema_name == "validation-result.schema.json":
        errors.extend(check_result(value))
    elif schema_name == "confirmation.schema.json":
        errors.extend(check_confirmation(value))
    elif schema_name == "context-payload.schema.json":
        errors.extend(check_context(value, raw))
    elif schema_name == "model-provenance.schema.json":
        errors.extend(check_model_provenance(value))
    elif schema_name == "recorded-call-fixture.schema.json":
        errors.extend(check_recorded_fixture(value))
    return errors


def validate_document(validator, path, schema_name):
    raw = path.read_text(encoding="utf-8")
    value = parse_json(raw)
    errors = check_resource_bounds(value)
    errors.extend(validator.validate(value, schema_name))
    if not errors:
        errors.extend(semantic_errors(value, schema_name, raw))
    return errors


def check_contract_inventory(validator, manifest):
    failures = []
    proposal_schema = validator.documents["tool-proposal.schema.json"]
    tools = set(proposal_schema["properties"]["tool"]["enum"])
    if tools != set(TOOL_OPERATIONS):
        failures.append(f"tool inventory differs from five closed tools: {sorted(tools)}")
    operations = set()
    for reference in proposal_schema["$defs"]["Parameters"]["oneOf"]:
        definition, _ = validator.resolve(reference["$ref"], "tool-proposal.schema.json")
        operation = definition["properties"]["operation"]
        if "const" in operation:
            operations.add(operation["const"])
        else:
            operations.update(operation["enum"])
    expected_operations = set().union(*TOOL_OPERATIONS.values())
    if operations != expected_operations:
        failures.append("operation inventory differs from the closed operation set")
    versions = parse_json((ROOT / "versions.json").read_text(encoding="utf-8"))
    if set(versions) != set(SUPPORTED_MAJORS):
        failures.append("version inventory differs from supported contract versions")
    for field, expected_major in SUPPORTED_MAJORS.items():
        if int(versions[field].partition(".")[0]) != expected_major:
            failures.append(f"versions.json has unsupported major for {field}")
    provenance_schema = validator.documents["model-provenance.schema.json"]
    provenance_fields = {
        "model", "endpoint_host", "request_id", "ts_start", "ts_end",
        "input_tokens", "output_tokens", "prompt_hash", "response_hash", "mode",
    }
    if set(provenance_schema["required"]) != provenance_fields:
        failures.append("ModelProvenance required-field inventory differs from the closed record")
    if set(provenance_schema["properties"]) != provenance_fields:
        failures.append("ModelProvenance property inventory differs from the closed record")
    fixture_schema = validator.documents["recorded-call-fixture.schema.json"]
    fixture_fields = {
        "fixture_format_version", "scenario_id", "policy_version", "contract_versions",
        "deterministic_recipe", "request", "response", "provenance", "integrity",
    }
    if set(fixture_schema["required"]) != fixture_fields:
        failures.append("RecordedCallFixture required-field inventory differs from the closed envelope")
    if set(fixture_schema["properties"]) != fixture_fields:
        failures.append("RecordedCallFixture property inventory differs from the closed envelope")
    files = [case["file"] for case in manifest["cases"]]
    names = [case["name"] for case in manifest["cases"]]
    if len(set(files)) != len(files) or len(set(names)) != len(names):
        failures.append("fixture manifest contains duplicate names or files")
    actual_files = {
        str(path.relative_to(ROOT / "fixtures"))
        for path in (ROOT / "fixtures").glob("*/*.json")
    }
    if set(files) != actual_files:
        failures.append("fixture manifest does not cover every fixture exactly once")
    return failures


def run_fixtures():
    validator = SchemaValidator()
    manifest = parse_json((ROOT / "fixtures" / "manifest.json").read_text(encoding="utf-8"))
    failures = check_contract_inventory(validator, manifest)
    for case in manifest["cases"]:
        path = ROOT / "fixtures" / case["file"]
        try:
            errors = validate_document(validator, path, case["schema"])
        except (ContractError, json.JSONDecodeError, UnicodeError) as error:
            errors = [str(error)]
        if case["valid"] and errors:
            failures.append(f"{case['name']}: expected valid, got {errors}")
        elif not case["valid"] and not errors:
            failures.append(f"{case['name']}: expected rejection, document passed")
        elif not case["valid"] and case.get("error"):
            if not any(case["error"] in error for error in errors):
                failures.append(
                    f"{case['name']}: expected error containing {case['error']!r}, got {errors}"
                )
    if failures:
        for failure in failures:
            print(f"FAIL {failure}")
        print(f"{len(failures)} of {len(manifest['cases'])} fixture cases failed")
        return 1
    print(f"All {len(manifest['cases'])} schema fixture cases passed")
    print(f"Validated {len(validator.documents)} dependency-free schema documents")
    return 0


def main():
    parser = argparse.ArgumentParser(description="Validate Amiss schema fixtures")
    parser.parse_args()
    return run_fixtures()


if __name__ == "__main__":
    sys.exit(main())
