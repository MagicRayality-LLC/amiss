from copy import deepcopy
from dataclasses import dataclass, field
import math


CONTRACT_VERSIONS = {
    "framework_api_version": "1.0.0",
    "snapshot_envelope_version": "1.0.0",
    "tool_schema_version": "1.0.0",
    "save_format_version": "1.0.0",
}
CONTINUOUS_PHASES = frozenset({"baseline", "intervention", "settling"})
TERMINAL_PHASES = frozenset({"archived", "abandoned"})


class KernelError(ValueError):
    def __init__(self, reason_code, message):
        super().__init__(message)
        self.reason_code = reason_code


@dataclass(frozen=True)
class TrialRef:
    trial_id: str
    trial_revision: int

    def to_dict(self):
        return {"trial_id": self.trial_id, "trial_revision": self.trial_revision}

    @classmethod
    def from_dict(cls, value):
        return cls(value["trial_id"], value["trial_revision"])


@dataclass
class Trial:
    identity: dict
    configuration: dict
    state: dict
    episode_extension: dict
    measurements: list = field(default_factory=list)
    predictions: list = field(default_factory=list)
    pending_predictions: list = field(default_factory=list)
    ledger: list = field(default_factory=list)
    invalidated_measurement_ids: set = field(default_factory=set)
    display_requirements: dict = field(default_factory=dict)
    setup_immutable: bool = False
    prediction_counter: int = 0
    event_counter: int = 0

    @property
    def ref(self):
        return TrialRef(self.identity["trial_id"], self.state["trial_revision"])


def ref_key(value):
    return value["trial_id"], value["trial_revision"]


def canonical_refs(refs):
    result = []
    seen = set()
    for value in refs:
        key = ref_key(value)
        if key not in seen:
            seen.add(key)
            result.append(deepcopy(value))
    return result


class TrialKernel:
    def __init__(
        self,
        episode_id,
        extension_namespace,
        *,
        session_id="headless-session",
        app_build_version="headless-build",
        episode_model_version="1.0.0",
        episode_module_version="1.0.0",
        continuous_phases=CONTINUOUS_PHASES,
        prediction_attachments=(),
        setup_family_ids=(),
    ):
        unknown = set(continuous_phases) - CONTINUOUS_PHASES
        if unknown:
            raise ValueError(f"unknown continuous phases: {sorted(unknown)}")
        if not set(prediction_attachments) <= {"pre_run", "pre_reveal"}:
            raise ValueError("unknown prediction attachment")
        self.episode_id = episode_id
        self.extension_namespace = extension_namespace
        self.session_id = session_id
        self.app_build_version = app_build_version
        self.episode_model_version = episode_model_version
        self.episode_module_version = episode_module_version
        self.continuous_phases = frozenset(continuous_phases)
        self.prediction_attachments = frozenset(prediction_attachments)
        self.setup_family_ids = tuple(setup_family_ids)
        self.trials = {}
        self.comparison_trial_ids = []
        self.current_trial_id = None
        self.global_revision = 0
        self.session_mode = "guided"
        self.guide = None
        self.rejected_proposal_id = None
        self.rejected_reason_code = None
        self.preferences = {
            "mute": False,
            "tips_enabled": True,
            "guidance_preference_id": "default",
        }

    @property
    def discrete(self):
        return not self.continuous_phases

    @property
    def current(self):
        if self.current_trial_id is None:
            raise KernelError("no_current_trial", "no current Trial exists")
        return self.trials[self.current_trial_id]

    def set_guide(self, guide):
        self.guide = guide

    def current_refs(self):
        return [self.trials[item].ref.to_dict() for item in self.comparison_trial_ids]

    def assert_current_refs(self, refs):
        canonical = canonical_refs(refs)
        if not canonical or len(canonical) > 4:
            raise KernelError("invalid_comparison_set", "refs must contain one to four unique refs")
        for value in canonical:
            trial = self.trials.get(value["trial_id"])
            if trial is None or trial.state["trial_revision"] != value["trial_revision"]:
                raise KernelError("stale_trial_ref", "a cited TrialRef is stale")
        return canonical

    def _append_ledger(
        self,
        trial,
        entry_kind,
        source,
        *,
        fact_id=None,
        reason_code=None,
        evidence=(),
    ):
        entry = {
            "sequence": len(trial.ledger) + 1,
            "tick": trial.state["tick"],
            "trial_ref": trial.ref.to_dict(),
            "source": source,
            "schema_version": "1.0.0",
            "entry_kind": entry_kind,
            "evidence_fact_ids": list(evidence),
        }
        if fact_id is not None:
            entry["fact_id"] = fact_id
        if reason_code is not None:
            entry["reason_code"] = reason_code
        trial.ledger.append(entry)
        return entry

    def append_guide_entry(self, trial_ref, entry_kind, fact_id=None, evidence=()):
        refs = self.assert_current_refs([trial_ref])
        trial = self.trials[refs[0]["trial_id"]]
        return self._append_ledger(
            trial, entry_kind, "user_panel", fact_id=fact_id, evidence=evidence
        )

    def _commit(
        self, trial, entry_kind, source, *, fact_id=None, reason_code=None, evidence=()
    ):
        trial.state["trial_revision"] += 1
        return self._append_ledger(
            trial,
            entry_kind,
            source,
            fact_id=fact_id,
            reason_code=reason_code,
            evidence=evidence,
        )

    def _reject(self, trial, reason_code):
        self.rejected_reason_code = reason_code
        self._append_ledger(trial, "rejection", "safety", reason_code=reason_code)
        raise KernelError(reason_code, reason_code)

    def create_trial(
        self,
        trial_id,
        setup_family_id,
        target_parameters,
        *,
        seed=0,
        proposed=False,
        source_trial_ref=None,
        source_input_delta=None,
        declared_downstream_effect_ids=(),
        actual_initial_invariant_ids=("finite-state",),
        collision_representation_id="synthetic-collision",
        collision_representation_version=1,
        dimensions_m=None,
        effective_volume_m3=1.0,
    ):
        if trial_id in self.trials:
            raise KernelError("duplicate_trial_id", "Trial id already exists")
        if len(self.comparison_trial_ids) >= 4:
            raise KernelError("comparison_set_full", "the comparison set is full")
        source = None
        if source_trial_ref is not None:
            source = self.assert_current_refs([source_trial_ref])[0]
        identity = {
            "trial_id": trial_id,
            "episode_id": self.episode_id,
            "episode_model_version": self.episode_model_version,
            "episode_module_version": self.episode_module_version,
            "app_build_version": self.app_build_version,
            "contract_versions": deepcopy(CONTRACT_VERSIONS),
            "source_trial_ref": source,
        }
        configuration = {
            "setup_family_id": setup_family_id,
            "target_parameters": deepcopy(target_parameters),
            "seed": seed,
            "rng_expansion_id": "deterministic-v1",
            "actual_initial_invariant_ids": list(actual_initial_invariant_ids),
            "collision_representation_id": collision_representation_id,
            "collision_representation_version": collision_representation_version,
            "source_input_delta": deepcopy(source_input_delta),
            "declared_downstream_effect_ids": list(declared_downstream_effect_ids),
        }
        trial = Trial(
            identity=identity,
            configuration=configuration,
            state={
                "phase": "setup",
                "acceptance": "proposed" if proposed else "accepted",
                "activity": "running",
                "terminal_reason": None,
                "trial_revision": 1,
                "tick": 0,
            },
            episode_extension={
                "namespace": self.extension_namespace,
                "schema_version": "1.0.0",
                "facts": {},
            },
        )
        dimensions = dimensions_m or {"x": 1.0, "y": 1.0, "z": 1.0}
        trial.space = {
            "scene_valid": True,
            "collision_representation_id": collision_representation_id,
            "collision_representation_version": collision_representation_version,
            "dimensions_m": deepcopy(dimensions),
            "effective_volume_m3": effective_volume_m3,
            "virtual_boundary": True,
        }
        self.trials[trial_id] = trial
        self.comparison_trial_ids.append(trial_id)
        if self.current_trial_id is None:
            self.current_trial_id = trial_id
        self._append_ledger(trial, "trial_created", "accepted_proposal" if proposed else "user_panel")
        return trial.ref.to_dict()

    def derive_trial(
        self,
        trial_id,
        source_trial_ref,
        parameter_id,
        to_value,
        *,
        unit=None,
        downstream_effect_ids=(),
    ):
        source_ref = self.assert_current_refs([source_trial_ref])[0]
        source = self.trials[source_ref["trial_id"]]
        if parameter_id not in source.configuration["target_parameters"]:
            raise KernelError("unknown_input_parameter", "the source has no such input parameter")
        targets = deepcopy(source.configuration["target_parameters"])
        from_value = targets[parameter_id]
        targets[parameter_id] = to_value
        delta = {
            "parameter_id": parameter_id,
            "from_value": from_value,
            "to_value": to_value,
            "downstream_effect_ids": list(downstream_effect_ids),
        }
        if unit is not None:
            delta["unit"] = unit
        return self.create_trial(
            trial_id,
            source.configuration["setup_family_id"],
            targets,
            seed=source.configuration["seed"],
            proposed=True,
            source_trial_ref=source_ref,
            source_input_delta=delta,
            declared_downstream_effect_ids=downstream_effect_ids,
            actual_initial_invariant_ids=source.configuration["actual_initial_invariant_ids"],
            collision_representation_id=source.configuration["collision_representation_id"],
            collision_representation_version=source.configuration[
                "collision_representation_version"
            ],
            dimensions_m=source.space["dimensions_m"],
            effective_volume_m3=source.space["effective_volume_m3"],
        )

    def accept_trial(self, trial_id=None):
        trial = self.trials[trial_id or self.current_trial_id]
        if trial.state["phase"] != "setup" or trial.state["acceptance"] != "proposed":
            self._reject(trial, "acceptance_not_proposed")
        trial.state["acceptance"] = "accepted"
        self._commit(trial, "proposal_accepted", "user_panel")
        self.current_trial_id = trial.identity["trial_id"]
        return trial.ref.to_dict()

    def reject_trial(self, trial_id=None):
        trial = self.trials[trial_id or self.current_trial_id]
        if trial.state["phase"] != "setup" or trial.state["acceptance"] != "proposed":
            self._reject(trial, "proposal_not_pending")
        trial.state["phase"] = "abandoned"
        trial.state["terminal_reason"] = "proposal_rejected"
        self._commit(trial, "trial_abandoned", "user_panel", reason_code="proposal_rejected")
        return trial.ref.to_dict()

    def return_to_source(self, trial_id=None):
        trial = self.trials[trial_id or self.current_trial_id]
        source = trial.identity["source_trial_ref"]
        if source is None or trial.state["acceptance"] != "proposed":
            self._reject(trial, "source_return_unavailable")
        trial.state["phase"] = "abandoned"
        trial.state["terminal_reason"] = "returned_to_source"
        self._commit(trial, "trial_abandoned", "user_panel", reason_code="returned_to_source")
        self.current_trial_id = source["trial_id"]
        return deepcopy(source)

    def _require_running(self, trial):
        if trial.state["activity"] != "running" or trial.state["phase"] in TERMINAL_PHASES:
            self._reject(trial, "trial_not_running")

    def _has_resolved_gate(self, trial, attachment):
        return any(item["_attachment"] == attachment for item in trial.predictions)

    def _validate_measurement(self, trial, measurement):
        if any(
            item["measurement_id"] == measurement["measurement_id"]
            for candidate in self.trials.values()
            for item in candidate.measurements
        ):
            self._reject(trial, "duplicate_measurement_id")
        numeric = measurement.get("value")
        if isinstance(numeric, float) and not math.isfinite(numeric):
            self._reject(trial, "non_finite_measurement")
        if measurement["applicability"] == "valid" and numeric is None:
            self._reject(trial, "missing_measurement_value")

    def _store_measurement(self, trial, measurement):
        value = deepcopy(measurement)
        required_components = value.pop("required_display_components", ())
        value["trial_ref"] = trial.ref.to_dict()
        if value["applicability"] != "valid":
            value["value"] = None
            value.pop("display_tuple", None)
        trial.measurements.append(value)
        trial.display_requirements[value["measurement_id"]] = tuple(required_components)
        return value

    def _start_discrete(self, trial, measurement, source):
        if measurement is None:
            self._reject(trial, "measurement_required")
        self._validate_measurement(trial, measurement)
        trial.state["phase"] = "measured"
        trial.setup_immutable = True
        self._commit(trial, "discrete_measurement_committed", source)
        return self._store_measurement(trial, measurement)

    def start_trial(self, measurement=None):
        trial = self.current
        self._require_running(trial)
        if trial.state["phase"] != "setup" or trial.state["acceptance"] != "accepted":
            self._reject(trial, "setup_not_accepted")
        if "pre_run" in self.prediction_attachments and not self._has_resolved_gate(
            trial, "pre_run"
        ):
            self._reject(trial, "prediction_required")
        if self.discrete:
            return self._start_discrete(trial, measurement, "user_panel")
        trial.setup_immutable = True
        if "baseline" in self.continuous_phases:
            trial.state["phase"] = "baseline"
        elif "settling" in self.continuous_phases:
            trial.state["phase"] = "settling"
        else:
            self._reject(trial, "no_applicable_start_phase")
        self._commit(trial, "setup_confirmed", "user_panel")
        return trial.ref.to_dict()

    def continue_trial(self, measurement=None):
        trial = self.current
        self._require_running(trial)
        if trial.state["phase"] not in {"predicted", "skipped"}:
            self._reject(trial, "prediction_gate_not_resolved")
        attachment = trial.predictions[-1]["_attachment"]
        if self.discrete:
            return self._start_discrete(trial, measurement, "user_panel")
        target = "baseline" if attachment == "pre_run" else "settling"
        if target not in self.continuous_phases:
            self._reject(trial, "phase_not_applicable")
        trial.state["phase"] = target
        self._commit(trial, "prediction_gate_continued", "user_panel")
        return trial.ref.to_dict()

    def begin_intervention(self):
        trial = self.current
        self._require_running(trial)
        if trial.state["phase"] != "baseline" or "intervention" not in self.continuous_phases:
            self._reject(trial, "intervention_not_applicable")
        trial.state["phase"] = "intervention"
        self._commit(trial, "intervention_started", "user_interaction")
        return trial.ref.to_dict()

    def finish_intervention(self):
        trial = self.current
        self._require_running(trial)
        if trial.state["phase"] != "intervention" or "settling" not in self.continuous_phases:
            self._reject(trial, "settling_not_applicable")
        trial.state["phase"] = "settling"
        self._commit(trial, "intervention_finished", "user_interaction")
        return trial.ref.to_dict()

    def close_interaction(self, fact_kind="interaction_closed"):
        trial = self.current
        self._require_running(trial)
        trial.event_counter += 1
        event_id = f"event-{trial.identity['trial_id']}-{trial.event_counter}"
        self._commit(trial, "interaction_closed", "user_interaction")
        event = {
            "event_id": event_id,
            "trial_ref": trial.ref.to_dict(),
            "tick": trial.state["tick"],
            "fact_category": fact_kind,
            "fact_payload": {},
        }
        trial.ledger[-1]["event"] = deepcopy(event)
        return event

    def commit_measurement(self, measurement):
        trial = self.current
        self._require_running(trial)
        self._validate_measurement(trial, measurement)
        self._commit(trial, "measurement_committed", "user_interaction")
        return self._store_measurement(trial, measurement)

    def invalidate_measurement(self, measurement_id):
        trial = self.current
        if not any(item["measurement_id"] == measurement_id for item in trial.measurements):
            self._reject(trial, "unknown_measurement")
        self._commit(trial, "measurement_invalidated", "user_interaction")
        trial.invalidated_measurement_ids.add(measurement_id)
        return trial.ref.to_dict()

    def advance_free_ticks(self, count=1):
        if not isinstance(count, int) or isinstance(count, bool) or count < 0:
            raise ValueError("Tick count must be a non-negative integer")
        trial = self.current
        self._require_running(trial)
        trial.state["tick"] += count
        return trial.ref.to_dict()

    def sample_held_pose(self):
        trial = self.current
        self._require_running(trial)
        return {"trial_ref": trial.ref.to_dict(), "tick": trial.state["tick"]}

    def pause(self):
        trial = self.current
        if trial.state["activity"] != "running" or trial.state["phase"] in TERMINAL_PHASES:
            self._reject(trial, "pause_not_available")
        trial.state["activity"] = "paused"
        self._commit(trial, "trial_paused", "user_panel")
        return trial.ref.to_dict()

    def resume(self):
        trial = self.current
        if trial.state["activity"] != "paused" or trial.state["phase"] in TERMINAL_PHASES:
            self._reject(trial, "resume_not_available")
        trial.state["activity"] = "running"
        self._commit(trial, "trial_resumed", "user_panel")
        return trial.ref.to_dict()

    def archive(self, *, generation_committed=False):
        trial = self.current
        if trial.state["phase"] != "measured" or not generation_committed:
            self._reject(trial, "complete_generation_required")
        trial.state["phase"] = "archived"
        self._commit(trial, "trial_archived", "user_panel")
        return trial.ref.to_dict()

    def abandon(self, reason_code="user_abandoned"):
        trial = self.current
        if trial.state["phase"] in TERMINAL_PHASES:
            self._reject(trial, "trial_terminal")
        trial.state["phase"] = "abandoned"
        trial.state["terminal_reason"] = reason_code
        self._commit(trial, "trial_abandoned", "user_panel", reason_code=reason_code)
        return trial.ref.to_dict()

    def select_trial(self, trial_id):
        if trial_id not in self.comparison_trial_ids:
            raise KernelError("trial_not_in_comparison_set", "Trial is not in the comparison set")
        self.current_trial_id = trial_id
        return self.current.ref.to_dict()

    def set_session_mode(self, mode):
        if mode not in {"guided", "free_exploration"}:
            raise ValueError("unknown session mode")
        self.session_mode = mode

    def open_prediction(
        self,
        gate_id,
        attachment,
        choice_set_id,
        choice_ids,
        target_trial_ref,
        target_measurement_ids,
    ):
        trial = self.current
        self._require_running(trial)
        if attachment not in self.prediction_attachments:
            self._reject(trial, "prediction_attachment_not_declared")
        expected_phase = "setup" if attachment == "pre_run" else "baseline"
        if trial.state["phase"] != expected_phase:
            self._reject(trial, "prediction_attachment_phase")
        if trial.pending_predictions:
            self._reject(trial, "prediction_already_open")
        target = self.assert_current_refs([target_trial_ref])[0]
        target_trial = self.trials[target["trial_id"]]
        available = {item["measurement_id"] for item in target_trial.measurements}
        if not target_measurement_ids or not set(target_measurement_ids) <= available:
            self._reject(trial, "prediction_target_missing")
        if not choice_ids:
            self._reject(trial, "prediction_choices_missing")
        trial.prediction_counter += 1
        prediction_id = f"prediction-{trial.identity['trial_id']}-{trial.prediction_counter}"
        trial.pending_predictions.append(
            {
                "prediction_id": prediction_id,
                "gate_id": gate_id,
                "subject_trial_ref": trial.ref.to_dict(),
                "target_trial_ref": target,
                "choice_id": None,
                "skipped": False,
                "readout_masked": True,
                "measurement_id": None,
                "_attachment": attachment,
                "_choice_set_id": choice_set_id,
                "_choice_ids": tuple(choice_ids),
                "_target_measurement_ids": tuple(target_measurement_ids),
            }
        )
        return prediction_id

    def resolve_prediction(self, prediction_id, subject_trial_ref, *, choice_id=None, skip=False):
        trial = self.current
        subject = self.assert_current_refs([subject_trial_ref])[0]
        if subject["trial_id"] != trial.identity["trial_id"]:
            self._reject(trial, "prediction_subject_mismatch")
        pending = next(
            (item for item in trial.pending_predictions if item["prediction_id"] == prediction_id),
            None,
        )
        if pending is None or pending["subject_trial_ref"] != subject:
            self._reject(trial, "stale_prediction")
        if bool(skip) == (choice_id is not None):
            self._reject(trial, "prediction_choice_invalid")
        if choice_id is not None and choice_id not in pending["_choice_ids"]:
            self._reject(trial, "prediction_choice_unknown")
        trial.pending_predictions.remove(pending)
        pending["choice_id"] = choice_id
        pending["skipped"] = bool(skip)
        pending["readout_masked"] = False
        pending["measurement_id"] = pending["_target_measurement_ids"][0]
        trial.state["phase"] = "skipped" if skip else "predicted"
        trial.predictions.append(pending)
        self._commit(trial, "prediction_resolved", "user_panel", fact_id=prediction_id)
        return deepcopy(pending)

    def _project_measurement(self, trial, measurement):
        value = deepcopy(measurement)
        masked = any(
            pending["target_trial_ref"]["trial_id"] == measurement["trial_ref"]["trial_id"]
            and measurement["measurement_id"] in pending["_target_measurement_ids"]
            for candidate in self.trials.values()
            for pending in candidate.pending_predictions
        )
        if masked or measurement["measurement_id"] in trial.invalidated_measurement_ids:
            value["value"] = None
            value["applicability"] = (
                "masked_until_prediction" if masked else "not_applicable"
            )
            value.pop("display_tuple", None)
        return value

    def _default_guide_projection(self, trial, refs):
        return {
            "guide": {
                "stage_id": "unguided",
                "satisfied_evidence_fact_ids": [],
                "policy_version": "none",
                "trial_ref": trial.ref.to_dict(),
            },
            "policy_tokens": {
                "episode_id": self.episode_id,
                "stage_id": "unguided",
                "policy_version": "none",
                "refs": deepcopy(refs),
                "tokens": [
                    {"token_id": "unguided-stage", "kind": "stage", "value_id": "unguided"},
                    {"token_id": "unguided-policy", "kind": "policy_version", "value_id": "none"},
                ],
            },
        }

    def _make_snapshot(self, trial, global_revision):
        refs = self.current_refs()
        measurements = []
        for trial_id in self.comparison_trial_ids:
            candidate = self.trials[trial_id]
            for measurement in candidate.measurements:
                projected = self._project_measurement(candidate, measurement)
                measurements.append(projected)
                if projected["trial_ref"] not in refs and len(refs) < 4:
                    refs.append(deepcopy(projected["trial_ref"]))
        guide = (
            self.guide.projection(trial, self.current_refs())
            if self.guide is not None
            else self._default_guide_projection(trial, self.current_refs())
        )
        predictions = []
        for item in trial.predictions:
            for field in ("subject_trial_ref", "target_trial_ref"):
                if item[field] not in refs and len(refs) < 4:
                    refs.append(deepcopy(item[field]))
            predictions.append(
                {key: deepcopy(value) for key, value in item.items() if not key.startswith("_")}
            )
        return {
            "identity": {
                "snapshot_envelope_version": "1.0.0",
                "app_build_version": self.app_build_version,
                "session_id": self.session_id,
                "episode_id": self.episode_id,
                "episode_module_version": self.episode_module_version,
                "trial_identity": deepcopy(trial.identity),
                "refs": refs,
                "global_revision": global_revision,
                "current_trial_ref": trial.ref.to_dict(),
                "tick": trial.state["tick"],
                "diagnostic_wall_timestamp": "2000-01-01T00:00:00Z",
            },
            "space": deepcopy(trial.space),
            "configuration": deepcopy(trial.configuration),
            "measurements": measurements,
            "progress": {
                "guide": guide["guide"],
                "trial_state": deepcopy(trial.state),
                "preferences": deepcopy(self.preferences),
                "last_proactive_speech_trial_ref": None,
            },
            "behaviour_summary": {
                "attempt_count": 0,
                "effective_action_count": 0,
                "ticks_since_last_effective_action": 0,
                "panel_interaction_count": 0,
                "head_orientation_category": "unknown",
            },
            "session_comfort": {
                "session_duration_seconds": 0.0,
                "seated": True,
                "safety_warning_count": 0,
                "tracking_quality": "unknown",
                "paused": trial.state["activity"] == "paused",
            },
            "predictions": predictions,
            "policy_tokens": guide["policy_tokens"],
            "diagnostics": {
                "numerical_state": "ok",
                "tracking_state": "unknown",
                "scene_state": "valid",
                "network_state": "not_used",
                "voice_state": "unavailable",
                "rejected_proposal_id": self.rejected_proposal_id,
                "rejected_reason_code": self.rejected_reason_code,
            },
            "episode_extension": deepcopy(trial.episode_extension),
        }

    def snapshot(self):
        self.global_revision += 1
        return self._make_snapshot(self.current, self.global_revision)

    def build_context_payload(self, utterance_id, text):
        self.global_revision += 1
        refs = self.current_refs()
        package = [
            self._make_snapshot(self.trials[trial_id], self.global_revision)
            for trial_id in self.comparison_trial_ids
        ]
        token_set = package[0]["policy_tokens"] if package[0]["identity"]["current_trial_ref"] == refs[0] else self._default_guide_projection(self.current, refs)["policy_tokens"]
        if self.guide is not None:
            token_set = self.guide.projection(self.current, refs)["policy_tokens"]
        return {
            "context_payload_version": "1.0.0",
            "comparison_package": package,
            "refs": refs,
            "policy_tokens": token_set,
            "compressed_session_context": {
                "session_duration_seconds": 0.0,
                "preferences": deepcopy(self.preferences),
                "minimal_profile": None,
            },
            "current_user_utterance": {"utterance_id": utterance_id, "text": text},
        }

    def admit_speech(self, refs, citations=()):
        self.assert_current_refs(refs)
        for citation in citations:
            matches = [
                (trial, measurement)
                for trial in self.trials.values()
                for measurement in trial.measurements
                if measurement["measurement_id"] == citation["measurement_id"]
            ]
            if len(matches) != 1:
                raise KernelError("unknown_measurement_citation", "measurement citation is unknown")
            trial, measurement = matches[0]
            if measurement["trial_ref"] != citation["trial_ref"]:
                raise KernelError("stale_measurement_citation", "measurement citation is stale")
            projected = self._project_measurement(trial, measurement)
            if projected["applicability"] != "valid":
                raise KernelError("measurement_not_citable", "measurement is not currently citable")
            required = set(trial.display_requirements.get(measurement["measurement_id"], ()))
            if required:
                tuple_value = projected.get("display_tuple")
                if tuple_value is None or citation.get("display_tuple_id") != tuple_value["display_tuple_id"]:
                    raise KernelError("display_tuple_required", "the complete display tuple is required")
                components = {item["component_id"] for item in tuple_value["components"]}
                if not required <= components:
                    raise KernelError("display_tuple_incomplete", "the display tuple is incomplete")
        return True
