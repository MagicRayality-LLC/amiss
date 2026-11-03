"""Evaluation transport mechanics for the hackathon judge paths.

This module owns one canonical evaluation request builder, an in-memory model
key handle, the typed outcome of a direct-Meta send, the recorded-fixture
replay, and the provenance record of an admitted reply. It performs no network
I/O: the socket lives behind an injected sender, so the headless suite proves
every deadline and failure class without opening a connection and no live
model call is claimed.

The policy envelope is assembled at the send boundary only. It never enters a
snapshot, ledger, idempotency result, ordinary log, export, or fixture body;
only its version and hash are recorded.
"""

from copy import deepcopy
from dataclasses import dataclass, field
import hashlib
import re

from schema.validate import (
    ContractError,
    SchemaValidator,
    canonical_bytes,
    parse_json,
    semantic_errors,
)

REQUEST_CONTRACT_VERSION = "1.0.0"
RESPONSE_CONTRACT_VERSION = "1.0.0"
ENDPOINT_ADDRESS = "https://api.meta.ai/v1"
ENDPOINT_HOST = "api.meta.ai"
MODEL_ID = "muse-spark-1.3"
CONNECT_DEADLINE_SECONDS = 5.0
MODEL_DEADLINE_SECONDS = 30.0
ORIGINS = ("answer", "proactive", "guide_default")
MAX_RESPONSE_BYTES = 64 * 1024
MAX_PROPOSALS = 32
MAX_SPEECH_UNITS = 2
MAX_SPEECH_TEXT_BYTES = 4 * 1024

REQUEST_FIELDS = (
    "request_contract_version",
    "turn_id",
    "request_id",
    "origin",
    "model",
    "refs",
    "context_payload",
    "policy_envelope",
)
RESPONSE_FIELDS = (
    "response_contract_version",
    "request_id",
    "refs",
    "speech_units",
    "tool_proposals",
)
_IDENTIFIER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]*$")

_SCRIPT_CAPABILITY_FIELDS = frozenset(
    {"script", "source", "expression", "code", "shell", "command", "url", "method", "headers"}
)


class TransportError(ValueError):
    """A request that cannot be built; it never reaches a sender."""

    def __init__(self, reason_code, readable):
        super().__init__(readable)
        self.reason_code = reason_code
        self.readable = readable


class SenderUnavailable(Exception):
    """The injected sender reports that no network path exists."""


class SenderRefused(Exception):
    """The injected sender reports a remote refusal."""


class ModelKeyHandle:
    """In-memory model key for this slice only.

    The key is never written to disk, never serialized, never included in a
    request record, and never rendered by ``repr``/``str``. A caller reads it
    once at the send boundary through :meth:`use` and drops the reference.
    """

    __slots__ = ("_key",)

    def __init__(self, key=None):
        self._key = key if key else None

    @property
    def present(self):
        return self._key is not None

    def use(self):
        """The key for one send; raises when no key is held."""
        if self._key is None:
            raise TransportError(
                "no_credential",
                "No evaluation model key is held, so no model request is sent.",
            )
        return self._key

    def clear(self):
        self._key = None

    def redacted(self):
        return "[key present]" if self._key is not None else "[key absent]"

    def __repr__(self):
        return "ModelKeyHandle(" + self.redacted() + ")"

    __str__ = __repr__

    def __reduce__(self):
        raise TypeError("a model key handle is not serializable")


@dataclass(frozen=True)
class PolicyEnvelope:
    """The instructor supply, versioned and assembled at the send boundary."""

    policy_version: str
    system: str

    def __post_init__(self):
        if not isinstance(self.policy_version, str) or not _IDENTIFIER.match(self.policy_version):
            raise TransportError("no_policy", "The policy envelope has no usable version.")
        if not isinstance(self.system, str) or not self.system.strip():
            raise TransportError("no_policy", "The policy envelope has no system instruction.")

    def to_record(self):
        return {"policy_version": self.policy_version, "system": self.system}

    def digest(self):
        return hashlib.sha256(canonical_bytes(self.to_record())).hexdigest()


# --------------------------------------------------------------------------
# Canonical request builder
# --------------------------------------------------------------------------


def _schema():
    return SchemaValidator()


def validate_context_payload(context_payload):
    """Reject a context payload that is not the closed public shape."""
    validator = _schema()
    errors = validator.validate(context_payload, "context-payload.schema.json")
    if not errors:
        errors = semantic_errors(
            context_payload, "context-payload.schema.json", canonical_bytes(context_payload).decode()
        )
    return list(errors)


def assert_no_policy_in_payload(context_payload):
    """The payload carries no assembled policy, secret, or capability field."""
    if not isinstance(context_payload, dict):
        raise TransportError("malformed_parameters", "The context payload is not an object.")
    for key in context_payload:
        if key.lower() in _SCRIPT_CAPABILITY_FIELDS or key in {"policy", "system", "policy_envelope"}:
            raise TransportError(
                "malformed_parameters",
                "The context payload may not carry policy, capability, or script fields.",
            )
    return context_payload


class EvaluationRequestBuilder:
    """Builds the one canonical request every judge path shares."""

    @staticmethod
    def build(context_payload, policy_envelope, *, request_id, turn_id, origin):
        if policy_envelope is None:
            raise TransportError(
                "no_policy",
                "No policy envelope was supplied, so no model request is built.",
            )
        if not isinstance(policy_envelope, PolicyEnvelope):
            envelope = PolicyEnvelope(**policy_envelope)
        else:
            envelope = policy_envelope

        assert_no_policy_in_payload(context_payload)
        errors = validate_context_payload(context_payload)
        if errors:
            raise TransportError("malformed_parameters", "; ".join(errors))

        if not isinstance(request_id, str) or not _IDENTIFIER.match(request_id):
            raise TransportError("malformed_parameters", "The request id is not a usable identifier.")
        if turn_id is not None and (not isinstance(turn_id, str) or not _IDENTIFIER.match(turn_id)):
            raise TransportError("malformed_parameters", "The turn id is not a usable identifier.")
        if origin not in ORIGINS:
            raise TransportError("malformed_parameters", "The request origin is not declared.")

        return {
            "request_contract_version": REQUEST_CONTRACT_VERSION,
            "turn_id": turn_id,
            "request_id": request_id,
            "origin": origin,
            "model": MODEL_ID,
            "refs": deepcopy(context_payload["refs"]),
            "context_payload": deepcopy(context_payload),
            "policy_envelope": envelope.to_record(),
        }


def canonical_request_bytes(request):
    return canonical_bytes(request)


def request_hash(request):
    return hashlib.sha256(canonical_request_bytes(request)).hexdigest()


def response_hash(response):
    return hashlib.sha256(canonical_bytes(response)).hexdigest()


def redact_request_for_log(request):
    """A log-safe view: policy body replaced by its version and hash."""
    record = {key: deepcopy(value) for key, value in request.items() if key != "policy_envelope"}
    envelope = request.get("policy_envelope") or {}
    record["policy"] = {
        "policy_version": envelope.get("policy_version"),
        "policy_hash": hashlib.sha256(canonical_bytes(envelope)).hexdigest(),
    }
    return record


def request_record_errors(request):
    """Checks the built request against the sanitized request envelope."""
    errors = []
    if tuple(sorted(request)) != tuple(sorted(REQUEST_FIELDS)):
        errors.append("the request record does not carry exactly the sanitized fields")
    if request.get("model") != MODEL_ID:
        errors.append("the request model differs from the frozen evaluation model")
    if request.get("origin") not in ORIGINS:
        errors.append("the request origin is not declared")
    envelope = request.get("policy_envelope") or {}
    if set(envelope) != {"policy_version", "system"}:
        errors.append("the policy envelope does not carry exactly version and system")
    errors.extend(validate_context_payload(request.get("context_payload")))
    if canonical_bytes(request.get("refs")) != canonical_bytes(
        (request.get("context_payload") or {}).get("refs")
    ):
        errors.append("the request refs differ from the context payload refs")
    return errors


# --------------------------------------------------------------------------
# Typed transport outcome
# --------------------------------------------------------------------------


@dataclass(frozen=True)
class TransportFailure:
    """One typed, readable transport outcome. It never carries the key."""

    kind: str
    reason_code: str
    readable: str
    request_id: str = ""
    deadline_seconds: float = MODEL_DEADLINE_SECONDS

    @property
    def offline(self):
        return self.kind in {"offline", "timeout", "remote_refusal"}

    def to_record(self):
        return {
            "kind": self.kind,
            "reason_code": self.reason_code,
            "readable": self.readable,
            "request_id": self.request_id,
            "deadline_seconds": self.deadline_seconds,
        }


@dataclass(frozen=True)
class SenderReply:
    """What an injected sender returns: the body plus its own clock facts."""

    body: object
    elapsed_seconds: float
    ts_start: str
    ts_end: str
    input_tokens: int | None = None
    output_tokens: int | None = None


@dataclass(frozen=True)
class TransportOutcome:
    mode: str
    request_id: str
    response: dict | None = None
    provenance: dict | None = None
    failure: TransportFailure | None = None

    @property
    def ok(self):
        return self.failure is None and self.response is not None


# --------------------------------------------------------------------------
# Provenance
# --------------------------------------------------------------------------


def build_provenance(request, response, *, ts_start, ts_end, input_tokens, output_tokens, mode):
    """One closed ModelProvenance record for one admitted reply."""
    if mode not in {"live", "recorded"}:
        raise TransportError("malformed_parameters", "Provenance mode is not live or recorded.")
    record = {
        "model": MODEL_ID,
        "endpoint_host": ENDPOINT_HOST,
        "request_id": request["request_id"],
        "ts_start": ts_start,
        "ts_end": ts_end,
        "input_tokens": input_tokens,
        "output_tokens": output_tokens,
        "prompt_hash": request_hash(request),
        "response_hash": response_hash(response),
        "mode": mode,
    }
    errors = provenance_errors(record)
    if errors:
        raise TransportError("malformed_parameters", "; ".join(errors))
    return record


def provenance_errors(record):
    validator = _schema()
    errors = validator.validate(record, "model-provenance.schema.json")
    if not errors:
        errors = semantic_errors(record, "model-provenance.schema.json", canonical_bytes(record).decode())
    return list(errors)


# --------------------------------------------------------------------------
# Direct-Meta transport
# --------------------------------------------------------------------------


class EvaluationDirectTransport:
    """Local-live judge path (C) behind the tutor transport port.

    The transport holds the person's key in memory, assembles the policy
    envelope at the send boundary, enforces the 30 s complete-response
    deadline, and returns either one bounded response or one typed failure.
    """

    def __init__(self, sender, key_handle, policy_envelope, *,
                 deadline_seconds=MODEL_DEADLINE_SECONDS,
                 connect_deadline_seconds=CONNECT_DEADLINE_SECONDS,
                 url=ENDPOINT_ADDRESS):
        if sender is None:
            raise TransportError("execution_unavailable", "No evaluation sender is bound.")
        self.sender = sender
        self.key_handle = key_handle or ModelKeyHandle()
        self.policy_envelope = policy_envelope
        self.deadline_seconds = deadline_seconds
        self.connect_deadline_seconds = connect_deadline_seconds
        self.url = url
        self.mode = "live"

    def build_request(self, context_payload, *, request_id, turn_id, origin):
        return EvaluationRequestBuilder.build(
            context_payload,
            self.policy_envelope,
            request_id=request_id,
            turn_id=turn_id,
            origin=origin,
        )

    def send(self, request) -> TransportOutcome:
        request_id = (request or {}).get("request_id", "")
        if self.policy_envelope is None or not request.get("policy_envelope"):
            return TransportOutcome(
                mode=self.mode,
                request_id=request_id,
                failure=TransportFailure(
                    kind="no_policy",
                    reason_code="no_policy",
                    readable="No policy envelope is supplied, so the live path is closed.",
                    request_id=request_id,
                    deadline_seconds=self.deadline_seconds,
                ),
            )
        if not self.key_handle.present:
            return TransportOutcome(
                mode=self.mode,
                request_id=request_id,
                failure=TransportFailure(
                    kind="no_credential",
                    reason_code="no_credential",
                    readable="No evaluation model key is held; return to recorded mode.",
                    request_id=request_id,
                    deadline_seconds=self.deadline_seconds,
                ),
            )

        try:
            key = self.key_handle.use()
        except TransportError as error:
            return TransportOutcome(
                mode=self.mode,
                request_id=request_id,
                failure=TransportFailure(
                    kind="no_credential",
                    reason_code=error.reason_code,
                    readable=error.readable,
                    request_id=request_id,
                    deadline_seconds=self.deadline_seconds,
                ),
            )

        body = canonical_request_bytes(request)
        try:
            reply = self.sender(
                url=self.url,
                body=body,
                authorization=key,
                connect_deadline_seconds=self.connect_deadline_seconds,
                deadline_seconds=self.deadline_seconds,
            )
        except SenderUnavailable:
            return TransportOutcome(
                mode=self.mode,
                request_id=request_id,
                failure=self._failure("offline", "network_service",
                                      "No network path is available; the tutor stays silent.",
                                      request_id),
            )
        except SenderRefused:
            return TransportOutcome(
                mode=self.mode,
                request_id=request_id,
                failure=self._failure("remote_refusal", "network_service",
                                      "The model service refused the request.",
                                      request_id),
            )
        finally:
            key = None

        if reply.elapsed_seconds > self.deadline_seconds:
            return TransportOutcome(
                mode=self.mode,
                request_id=request_id,
                failure=self._failure(
                    "timeout", "network_service",
                    "The complete model response missed the 30 s deadline and was dropped.",
                    request_id),
            )

        response = reply.body
        if isinstance(response, (bytes, str)):
            try:
                response = parse_json(response)
            except (ContractError, ValueError):
                return TransportOutcome(
                    mode=self.mode,
                    request_id=request_id,
                    failure=self._failure("malformed_response", "network_service",
                                          "The model response is not canonical JSON.", request_id),
                )
        if not isinstance(response, dict) or len(canonical_bytes(response)) > MAX_RESPONSE_BYTES:
            return TransportOutcome(
                mode=self.mode,
                request_id=request_id,
                failure=self._failure("malformed_response", "network_service",
                                      "The model response is not a bounded structured value.",
                                      request_id),
            )

        provenance = build_provenance(
            request,
            response,
            ts_start=reply.ts_start,
            ts_end=reply.ts_end,
            input_tokens=reply.input_tokens,
            output_tokens=reply.output_tokens,
            mode="live",
        )
        return TransportOutcome(mode=self.mode, request_id=request_id,
                                response=response, provenance=provenance)

    def _failure(self, kind, reason_code, readable, request_id):
        return TransportFailure(
            kind=kind,
            reason_code=reason_code,
            readable=readable,
            request_id=request_id,
            deadline_seconds=self.deadline_seconds,
        )


# --------------------------------------------------------------------------
# Recorded-fixture transport
# --------------------------------------------------------------------------


class RecordedFixtureTransport:
    """Judge path A: replay the exact captured request and response.

    Replay recomputes the live request, verifies the fixture envelope and its
    integrity digest, and refuses a stale or modified fixture instead of
    rebinding it to current refs. The returned provenance keeps
    ``mode = recorded`` and the original capture timestamps.
    """

    def __init__(self, fixtures, *, deadline_seconds=MODEL_DEADLINE_SECONDS):
        self.fixtures = {}
        for fixture in fixtures or ():
            key = fixture.get("scenario_id")
            if not key:
                continue
            self.fixtures[key] = deepcopy(fixture)
        self.deadline_seconds = deadline_seconds
        self.mode = "recorded"

    @staticmethod
    def fixture_errors(fixture):
        validator = _schema()
        errors = validator.validate(fixture, "recorded-call-fixture.schema.json")
        if not errors:
            errors = semantic_errors(
                fixture, "recorded-call-fixture.schema.json", canonical_bytes(fixture).decode()
            )
        return list(errors)

    def send(self, request, *, scenario_id) -> TransportOutcome:
        request_id = (request or {}).get("request_id", "")
        fixture = self.fixtures.get(scenario_id)
        if fixture is None:
            return TransportOutcome(
                mode=self.mode,
                request_id=request_id,
                failure=TransportFailure(
                    kind="fixture_unavailable",
                    reason_code="fixture_unavailable",
                    readable="No recorded fixture is bundled for this scenario.",
                    request_id=request_id,
                    deadline_seconds=self.deadline_seconds,
                ),
            )
        errors = self.fixture_errors(fixture)
        if errors:
            return TransportOutcome(
                mode=self.mode,
                request_id=request_id,
                failure=TransportFailure(
                    kind="fixture_integrity",
                    reason_code="fixture_integrity",
                    readable="The recorded fixture failed its integrity or contract check.",
                    request_id=request_id,
                    deadline_seconds=self.deadline_seconds,
                ),
            )
        if canonical_bytes(fixture["request"]) != canonical_request_bytes(request):
            return TransportOutcome(
                mode=self.mode,
                request_id=request_id,
                failure=TransportFailure(
                    kind="fixture_stale",
                    reason_code="fixture_stale",
                    readable="The recorded request does not match the current context; "
                              "the fixture was not rebound.",
                    request_id=request_id,
                    deadline_seconds=self.deadline_seconds,
                ),
            )
        return TransportOutcome(
            mode=self.mode,
            request_id=request_id,
            response=deepcopy(fixture["response"]),
            provenance=deepcopy(fixture["provenance"]),
        )


# --------------------------------------------------------------------------
# Whole-reply admission
# --------------------------------------------------------------------------


@dataclass(frozen=True)
class AdmissionResult:
    admitted: bool
    request_id: str
    results: tuple = ()
    failure: TransportFailure | None = None
    provenance: dict | None = None
    mode: str = "live"

    @property
    def accepted_call_ids(self):
        return tuple(
            result["call_id"] for result in self.results if result["outcome"] == "executed"
        )

    @property
    def rejected_reasons(self):
        return tuple(
            (result["call_id"], result["reason_code"])
            for result in self.results
            if result["outcome"] != "executed"
        )


class ModelReplyAdmission:
    """Admits one complete reply, then runs the seven-step validator.

    The order is the one `06` section 6 freezes: identity and bounds, original
    refs and citations, then whole-reply validation. A reply that fails
    admission produces no provenance, no speech, and no proposal result.
    """

    def __init__(self, validator):
        self.validator = validator

    @staticmethod
    def shape_errors(request, response):
        errors = []
        if not isinstance(response, dict):
            return ["the response is not an object"]
        if tuple(sorted(response)) != tuple(sorted(RESPONSE_FIELDS)):
            errors.append("the response does not carry exactly the declared fields")
            return errors
        major = str(response.get("response_contract_version", "0")).partition(".")[0]
        if major != RESPONSE_CONTRACT_VERSION.partition(".")[0]:
            errors.append("unsupported response contract major")
        if response.get("request_id") != request.get("request_id"):
            errors.append("the response request id differs from the request")
        if canonical_bytes(response.get("refs")) != canonical_bytes(request.get("refs")):
            errors.append("the response refs differ from the request refs")
        if len(canonical_bytes(response)) > MAX_RESPONSE_BYTES:
            errors.append("the response exceeds the bounded response size")
        speech = response.get("speech_units", [])
        if not isinstance(speech, list) or len(speech) > MAX_SPEECH_UNITS:
            errors.append("the response carries too many speech units")
        else:
            for unit in speech:
                text = unit.get("text", "") if isinstance(unit, dict) else ""
                if isinstance(text, str) and len(text.encode("utf-8")) > MAX_SPEECH_TEXT_BYTES:
                    errors.append("a speech unit exceeds the bounded text size")
                for citation in (unit.get("citations", []) if isinstance(unit, dict) else []):
                    if canonical_bytes(citation.get("trial_ref")) not in [
                        canonical_bytes(ref) for ref in request.get("refs", [])
                    ]:
                        errors.append("a speech citation is outside the request refs")
        proposals = response.get("tool_proposals", [])
        if not isinstance(proposals, list) or len(proposals) > MAX_PROPOSALS:
            errors.append("the response carries too many tool proposals")
        return errors

    def admit(self, request, response, *, provenance=None, mode="live"):
        request_id = (request or {}).get("request_id", "")
        errors = self.shape_errors(request, response)
        if errors:
            return AdmissionResult(
                admitted=False,
                request_id=request_id,
                failure=TransportFailure(
                    kind="malformed_response",
                    reason_code="malformed_parameters",
                    readable="; ".join(errors),
                    request_id=request_id,
                ),
                mode=mode,
            )
        results = tuple(self.validator.validate_reply(response["tool_proposals"]))
        return AdmissionResult(
            admitted=True,
            request_id=request_id,
            results=results,
            provenance=deepcopy(provenance),
            mode=mode,
        )
