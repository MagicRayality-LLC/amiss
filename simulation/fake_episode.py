from copy import deepcopy

from .guide import GuideRunner
from .kernel import TrialKernel


BEHAVIOUR_FACES = (
    "Lifecycle",
    "SimulationFactory",
    "InitialSetup",
    "Advance",
    "Interaction",
    "MeasurementFace",
    "EventFace",
    "SnapshotExtension",
    "PresentationRoot",
    "ToolDeclaration",
    "DefaultPath",
)


class FakeDiscreteEpisode:
    def __init__(self):
        self.declaration = {
            "episode_id": "fake-discrete",
            "episode_module_version": "1.0.0",
            "scene_form": "compact_bench",
            "minimum_volume_m3": 1.0,
            "availability": "bundled",
            "non_product": True,
            "asset_ids": ["fake-discrete-asset"],
            "required_capabilities": [],
            "behaviour_faces": BEHAVIOUR_FACES,
            "continuous_phases": [],
        }
        self.loaded = False

    def load_gate(self, available_asset_ids):
        reasons = []
        if self.declaration["minimum_volume_m3"] <= 0:
            reasons.append("minimum_volume_missing")
        if set(self.declaration["behaviour_faces"]) != set(BEHAVIOUR_FACES):
            reasons.append("behaviour_face_missing")
        if not set(self.declaration["asset_ids"]) <= set(available_asset_ids):
            reasons.append("critical_asset_missing")
        verdict = {"status": "not_runnable" if reasons else "runnable", "reasons": reasons}
        self.loaded = not reasons
        return verdict

    def create_kernel(self):
        if not self.loaded:
            raise RuntimeError("load gate has not passed")
        return TrialKernel(
            "fake-discrete",
            "fake_discrete",
            continuous_phases=(),
            setup_family_ids=("discrete-count",),
        )

    @staticmethod
    def measurement(measurement_id, count):
        return {
            "measurement_id": measurement_id,
            "value": count,
            "unit_id": "count",
            "coordinate_frame_id": "discrete-state",
            "quantity_convention_id": "element-count",
            "window": {
                "window_id": f"window-{measurement_id}",
                "start_tick": 0,
                "end_tick": 0,
                "duration_seconds": 0.0,
            },
            "sample_count": count,
            "uncertainty": None,
            "sufficient": True,
            "applicability": "valid",
            "calculation_version": "1.0.0",
        }

    @staticmethod
    def stage_data():
        return {
            "stage_data_format_version": "1.0.0",
            "policy_version": "fake-contract-v1",
            "episode_id": "fake-discrete",
            "integrity": {"algorithm": "sha256", "digest": "0" * 64, "status": "verified"},
            "stage_graph": {
                "initial_stage_id": "observe",
                "nodes": [
                    {
                        "stage_id": "observe",
                        "default_path_id": "offline-observe",
                        "evidence_pair_rules": [
                            {
                                "rule_id": "observed-discrete-pair",
                                "first_fact_kind": "discrete-action",
                                "second_fact_kind": "discrete-state",
                            }
                        ],
                        "user_controls": ["skip", "replay", "free_exploration"],
                        "terminal": False,
                    },
                    {
                        "stage_id": "complete",
                        "default_path_id": "offline-complete",
                        "evidence_pair_rules": [],
                        "user_controls": ["replay", "free_exploration"],
                        "terminal": True,
                    },
                ],
            },
            "evidence_flag_definitions": [
                {
                    "fact_kind": "discrete-action",
                    "fact_class": "action",
                    "measurement_path_id": "discrete-input",
                },
                {
                    "fact_kind": "discrete-state",
                    "fact_class": "state",
                    "measurement_path_id": "discrete-measurement",
                },
            ],
            "allowed_action_set": {"action_ids": ["discrete-step"]},
        }

    def attach_guide(self, kernel):
        return GuideRunner(kernel, self.stage_data())

    def unload(self):
        self.loaded = False
        return []


def fake_stage_data():
    return deepcopy(FakeDiscreteEpisode.stage_data())
