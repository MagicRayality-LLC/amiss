from copy import deepcopy

from schema.validate import SchemaValidator, semantic_errors


class StageLoadError(ValueError):
    def __init__(self, errors):
        super().__init__("; ".join(errors))
        self.errors = tuple(errors)


class GuideRunner:
    def __init__(self, kernel, stage_data):
        self.kernel = kernel
        self.stage_data = self.load(stage_data)
        self.nodes = self.stage_data["stage_graph"]["nodes"]
        self.node_by_id = {node["stage_id"]: node for node in self.nodes}
        self.definitions = {
            item["fact_kind"]: item
            for item in self.stage_data["evidence_flag_definitions"]
        }
        self.stage_id = self.stage_data["stage_graph"]["initial_stage_id"]
        self.satisfied_evidence_fact_ids = []
        self.stage_facts = []
        self.candidates = []
        self.token_signature = None
        self.token_set = None
        self.token_generation = 0
        kernel.set_guide(self)

    @staticmethod
    def load(stage_data):
        value = deepcopy(stage_data)
        validator = SchemaValidator()
        errors = validator.validate(value, "stage-data.schema.json")
        if not errors:
            errors = semantic_errors(value, "stage-data.schema.json", "")
        if value.get("integrity", {}).get("status") != "verified":
            errors.append("$.integrity.status: stage data is not verified")
        if errors:
            raise StageLoadError(errors)
        return value

    @property
    def policy_version(self):
        return self.stage_data["policy_version"]

    @property
    def current_node(self):
        return self.node_by_id[self.stage_id]

    def _signature(self, refs):
        return (
            self.stage_id,
            self.policy_version,
            tuple((item["trial_id"], item["trial_revision"]) for item in refs),
            tuple(item["prediction_id"] for item in self.kernel.current.pending_predictions),
            tuple(
                (item["candidate_id"], tuple((ref["trial_id"], ref["trial_revision"]) for ref in item["refs"]))
                for item in self.candidates
            ),
        )

    def _tokens(self, refs):
        signature = self._signature(refs)
        if signature == self.token_signature:
            return deepcopy(self.token_set)
        self.token_generation += 1
        tokens = [
            {"kind": "stage", "value_id": self.stage_id},
            {"kind": "policy_version", "value_id": self.policy_version},
        ]
        tokens.extend(
            {"kind": "allowed_action", "value_id": action_id}
            for action_id in self.stage_data["allowed_action_set"]["action_ids"]
        )
        tokens.extend(
            {"kind": "setup_family", "value_id": family_id}
            for family_id in self.kernel.setup_family_ids
        )
        for pending in self.kernel.current.pending_predictions:
            tokens.append(
                {
                    "kind": "solicitation",
                    "value_id": pending["prediction_id"],
                    "solicitation_kind": "prediction",
                }
            )
        for candidate in self.candidates:
            if candidate["refs"] == refs:
                tokens.append(
                    {"kind": "proactive_candidate", "value_id": candidate["candidate_id"]}
                )
        for index, token in enumerate(tokens, 1):
            token["token_id"] = f"guide-token-{self.token_generation}-{index}"
        self.token_signature = signature
        self.token_set = {
            "episode_id": self.kernel.episode_id,
            "stage_id": self.stage_id,
            "policy_version": self.policy_version,
            "refs": deepcopy(refs),
            "tokens": tokens,
        }
        return deepcopy(self.token_set)

    def projection(self, trial, refs):
        return {
            "guide": {
                "stage_id": self.stage_id,
                "satisfied_evidence_fact_ids": list(self.satisfied_evidence_fact_ids),
                "policy_version": self.policy_version,
                "trial_ref": trial.ref.to_dict(),
            },
            "policy_tokens": self._tokens(refs),
        }

    def authorize_token(self, token_id):
        return any(
            token["token_id"] == token_id
            for token in self._tokens(self.kernel.current_refs())["tokens"]
        )

    def record_fact(self, fact_id, fact_kind, trial_ref):
        self.kernel.assert_current_refs([trial_ref])
        if fact_kind not in self.definitions:
            raise ValueError("unknown evidence fact kind")
        if any(item["fact_id"] == fact_id for item in self.stage_facts):
            raise ValueError("duplicate evidence fact id")
        fact = {"fact_id": fact_id, "fact_kind": fact_kind, "trial_ref": deepcopy(trial_ref)}
        self.stage_facts.append(fact)
        self.kernel.append_guide_entry(trial_ref, "evidence_fact", fact_id=fact_id)
        if self.current_node["terminal"]:
            return False
        kinds = {item["fact_kind"] for item in self.stage_facts}
        for rule in self.current_node["evidence_pair_rules"]:
            first = rule["first_fact_kind"]
            second = rule["second_fact_kind"]
            if first not in kinds or second not in kinds:
                continue
            first_fact = next(item for item in self.stage_facts if item["fact_kind"] == first)
            second_fact = next(item for item in self.stage_facts if item["fact_kind"] == second)
            pair = [first_fact["fact_id"], second_fact["fact_id"]]
            self.satisfied_evidence_fact_ids = pair
            self.kernel.append_guide_entry(
                trial_ref, "evidence_pair_satisfied", evidence=pair
            )
            index = self.nodes.index(self.current_node)
            if index + 1 < len(self.nodes):
                self.stage_id = self.nodes[index + 1]["stage_id"]
                self.stage_facts = []
                if not self.current_node["terminal"]:
                    self.satisfied_evidence_fact_ids = []
            return True
        return False

    def _require_control(self, control):
        if control not in self.current_node["user_controls"]:
            raise ValueError(f"{control} is not available in this stage")

    def skip(self):
        self._require_control("skip")
        index = self.nodes.index(self.current_node)
        self.kernel.append_guide_entry(self.kernel.current.ref.to_dict(), "user_skip")
        if index + 1 < len(self.nodes):
            self.stage_id = self.nodes[index + 1]["stage_id"]
        self.stage_facts = []
        self.satisfied_evidence_fact_ids = []

    def replay(self):
        self._require_control("replay")
        self.kernel.append_guide_entry(self.kernel.current.ref.to_dict(), "user_replay")
        self.stage_facts = []
        self.satisfied_evidence_fact_ids = []

    def free_exploration(self):
        self._require_control("free_exploration")
        self.kernel.append_guide_entry(
            self.kernel.current.ref.to_dict(), "user_free_exploration"
        )
        self.kernel.set_session_mode("free_exploration")

    def issue_proactive_candidate(self, candidate_id):
        candidate = {"candidate_id": candidate_id, "refs": self.kernel.current_refs()}
        self.candidates.append(candidate)
        issued = next(
            item
            for item in self._tokens(self.kernel.current_refs())["tokens"]
            if item["kind"] == "proactive_candidate" and item["value_id"] == candidate_id
        )
        return issued["token_id"]

    def default_path(self):
        trial = self.kernel.current
        if trial.pending_predictions:
            condition = "unresolved_prediction"
            action = "predict_or_skip"
        elif self.current_node["terminal"]:
            condition = "complete"
            action = "await_user"
        elif trial.state["phase"] == "setup" and trial.state["acceptance"] != "accepted":
            condition = "no_accepted_setup"
            action = "show_setup_card"
        elif trial.state["phase"] == "setup":
            condition = "accepted_setup"
            action = "await_explicit_start"
        elif trial.state["phase"] in {"baseline", "settling"}:
            condition = "evidence_incomplete"
            action = "continue_actual_measurement"
        elif trial.state["phase"] == "intervention":
            condition = "intervention_active"
            action = "await_contact_close"
        else:
            condition = "evidence_incomplete"
            action = "expose_allowed_controls"
        return {
            "default_path_id": self.current_node["default_path_id"],
            "condition": condition,
            "action": action,
            "dynamic_wording": False,
        }
