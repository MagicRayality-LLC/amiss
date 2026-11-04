"""Teacher distribution control: count, speed, drift, and mass as scalars.

Founder decision 2026-11-04 (option A, distribution-preserving): the teacher
sets group-level distribution parameters and local deterministic sampling
produces the balls. No per-ball list exists anywhere on this path.
"""

from copy import deepcopy
import hashlib
import unittest

from simulation.fake_episode import fake_stage_data
from simulation.guide import GuideRunner
from simulation.kernel import TrialKernel
from simulation.runtime import (
    HeadlessBallWriter,
    NamedRandomStreams,
    RuntimeContractError,
    SimulationRuntime,
    box_representation,
)
from simulation.temperature import (
    TEACHER_GROUP_CONTROLLABLE_INPUTS,
    TEACHER_GROUP_DEFAULT_PARAMETERS,
    TEACHER_GROUP_FAMILY_ID,
    teacher_group_declarations,
)
from simulation.validator import (
    InMemoryIdempotencyStore,
    LocalGates,
    ProposalValidator,
)


def make_collision():
    return box_representation("room-1", {"x": 2.0, "y": 1.5, "z": 2.5})


def setup_balls(parameters, seed=11):
    writer = HeadlessBallWriter()
    streams = NamedRandomStreams(seed)
    return writer.setup(streams, parameters, make_collision())["balls"]


def reason_code(testcase, callable_value, *args, **kwargs):
    with testcase.assertRaises(RuntimeContractError) as caught:
        callable_value(*args, **kwargs)
    return caught.exception.reason_code


class TeacherKnobTestCase(unittest.TestCase):
    def test_zero_drift_reproduces_legacy_state(self):
        legacy = setup_balls({"ball_count": 6, "initial_speed_scale": 1.0})
        knobs = setup_balls({
            "ball_count": 6,
            "initial_speed_scale": 1.0,
            "ball_mass_kg": 1.0,
            "drift_vx_m_per_s": 0.0,
            "drift_vy_m_per_s": 0.0,
            "drift_vz_m_per_s": 0.0,
        })
        self.assertEqual(legacy, knobs)

    def test_count_and_mass_are_honored(self):
        balls = setup_balls({"ball_count": 12, "ball_mass_kg": 2.5})
        self.assertEqual(12, len(balls))
        for ball in balls.values():
            self.assertEqual(2.5, ball["mass_kg"])
            self.assertEqual("free", ball["membership"])

    def test_speed_scale_zero_gives_still_balls_without_drift(self):
        balls = setup_balls({"ball_count": 4, "initial_speed_scale": 0.0})
        for ball in balls.values():
            self.assertEqual([0.0, 0.0, 0.0], ball["velocity_m_per_s"])

    def test_drift_shifts_every_velocity_exactly(self):
        drift = [1.5, -2.0, 0.25]
        still = setup_balls({"ball_count": 5, "initial_speed_scale": 1.0})
        moved = setup_balls({
            "ball_count": 5,
            "initial_speed_scale": 1.0,
            "drift_vx_m_per_s": drift[0],
            "drift_vy_m_per_s": drift[1],
            "drift_vz_m_per_s": drift[2],
        })
        self.assertEqual(
            [ball["position_m"] for ball in still.values()],
            [ball["position_m"] for ball in moved.values()],
        )
        for plain, pushed in zip(still.values(), moved.values()):
            for axis in range(3):
                self.assertEqual(
                    plain["velocity_m_per_s"][axis] + drift[axis],
                    pushed["velocity_m_per_s"][axis],
                )

    def test_single_ball_takes_an_exact_velocity(self):
        balls = setup_balls({
            "ball_count": 1,
            "initial_speed_scale": 0.0,
            "drift_vx_m_per_s": 1.0,
            "drift_vy_m_per_s": 2.0,
            "drift_vz_m_per_s": -0.5,
        })
        self.assertEqual(1, len(balls))
        only = next(iter(balls.values()))
        self.assertEqual([1.0, 2.0, -0.5], only["velocity_m_per_s"])

    def test_invalid_setup_parameters_are_refused(self):
        writer = HeadlessBallWriter()
        streams = lambda: NamedRandomStreams(3)
        collision = make_collision()
        self.assertEqual(
            "invalid_ball_count",
            reason_code(self, writer.setup, streams(), {"ball_count": -1}, collision),
        )
        self.assertEqual(
            "invalid_ball_mass",
            reason_code(self, writer.setup, streams(), {"ball_mass_kg": 0.0}, collision),
        )
        self.assertEqual(
            "invalid_ball_mass",
            reason_code(self, writer.setup, streams(), {"ball_mass_kg": -2.0}, collision),
        )
        self.assertEqual(
            "invalid_ball_radius",
            reason_code(self, writer.setup, streams(), {"ball_radius_m": 0.0}, collision),
        )
        self.assertEqual(
            "invalid_speed_scale",
            reason_code(
                self, writer.setup, streams(), {"initial_speed_scale": -1.0}, collision
            ),
        )
        self.assertEqual(
            "non_finite_input",
            reason_code(
                self,
                writer.setup,
                streams(),
                {"drift_vx_m_per_s": float("nan")},
                collision,
            ),
        )
        self.assertEqual(
            "non_finite_input",
            reason_code(
                self,
                writer.setup,
                streams(),
                {"drift_vz_m_per_s": float("inf")},
                collision,
            ),
        )


def make_teacher_environment():
    kernel = TrialKernel(
        "what_is_temperature",
        "temperature_test",
        continuous_phases=("baseline", "intervention", "settling"),
        setup_family_ids=(TEACHER_GROUP_FAMILY_ID,),
    )
    declared = teacher_group_declarations()
    stage = fake_stage_data()
    stage["allowed_action_set"]["action_ids"] = [
        item.action_id for item in declared
    ]
    guide = GuideRunner(kernel, stage)
    validator = ProposalValidator(
        kernel, declared, store=InMemoryIdempotencyStore(), gates=LocalGates()
    )
    return kernel, guide, validator


def setup_token(guide):
    # No trial exists yet: seed one so the guide can project a token set.
    kernel = guide.kernel
    kernel.create_trial(
        "trial-seed", TEACHER_GROUP_FAMILY_ID, dict(TEACHER_GROUP_DEFAULT_PARAMETERS)
    )
    return family_token(guide)


def family_token(guide):
    kernel = guide.kernel
    token_set = guide.projection(kernel.current, kernel.current_refs())["policy_tokens"]
    return next(
        item["token_id"]
        for item in token_set["tokens"]
        if item["kind"] == "setup_family" and item["value_id"] == TEACHER_GROUP_FAMILY_ID
    )


def propose_create(token_id, targets, parameter_id, to_value, call_id):
    delta = {
        "parameter_id": parameter_id,
        "to_value": to_value,
        "downstream_effect_ids": ["initial-conditions"],
    }
    return {
        "tool_schema_version": "1.0.0",
        "call_id": call_id,
        "origin": "model",
        "tool": "trial.propose",
        "target": {
            "kind": "new_trial",
            "episode_id": "what_is_temperature",
            "source_trial_ref": None,
        },
        "basis_trial_ref": None,
        "refs": [],
        "parameters": {
            "operation": "create",
            "setup_family_token_id": token_id,
            "target_parameters": targets,
            "input_delta": delta,
            "expected_invariant_ids": ["finite-state"],
        },
    }


def trial_id_for(call_id):
    return "trial-" + hashlib.sha256(call_id.encode("ascii")).hexdigest()[:24]


class TeacherProposalTestCase(unittest.TestCase):
    def test_create_with_teacher_scalars_executes_and_spawns(self):
        kernel, guide, validator = make_teacher_environment()
        token_id = setup_token(guide)
        targets = dict(TEACHER_GROUP_DEFAULT_PARAMETERS, ball_count=12)
        result = validator.validate(
            propose_create(token_id, targets, "ball_count", 12, "call-teacher-count")
        )
        self.assertEqual("executed", result["outcome"])
        trial_id = trial_id_for("call-teacher-count")
        runtime = SimulationRuntime(kernel, collision=make_collision())
        runtime.prepare_trial(trial_id)
        balls = runtime.physical[trial_id].mechanics.state["balls"]
        self.assertEqual(12, len(balls))
        self.assertEqual(
            {"ball_count", "initial_speed_scale", "ball_mass_kg",
             "drift_vx_m_per_s", "drift_vy_m_per_s", "drift_vz_m_per_s"},
            set(TEACHER_GROUP_CONTROLLABLE_INPUTS),
        )

    def test_derive_changing_drift_keeps_count_and_shifts_mean(self):
        kernel, guide, validator = make_teacher_environment()
        token_id = setup_token(guide)
        first = validator.validate(
            propose_create(
                token_id,
                dict(TEACHER_GROUP_DEFAULT_PARAMETERS, ball_count=12),
                "ball_count",
                12,
                "call-teacher-first",
            )
        )
        self.assertEqual("executed", first["outcome"])
        source_id = trial_id_for("call-teacher-first")
        kernel.accept_trial(source_id)
        source_ref = kernel.trials[source_id].ref.to_dict()
        targets = dict(TEACHER_GROUP_DEFAULT_PARAMETERS, ball_count=12,
                       drift_vx_m_per_s=2.0)
        token_id = family_token(guide)
        proposal = {
            "tool_schema_version": "1.0.0",
            "call_id": "call-teacher-drift",
            "origin": "model",
            "tool": "trial.propose",
            "target": {
                "kind": "new_trial",
                "episode_id": "what_is_temperature",
                "source_trial_ref": deepcopy(source_ref),
            },
            "basis_trial_ref": deepcopy(source_ref),
            "refs": [deepcopy(source_ref)],
            "parameters": {
                "operation": "derive",
                "setup_family_token_id": token_id,
                "target_parameters": targets,
                "input_delta": {
                    "parameter_id": "drift_vx_m_per_s",
                    "from_value": 0.0,
                    "to_value": 2.0,
                    "downstream_effect_ids": ["initial-conditions"],
                },
                "expected_invariant_ids": ["finite-state"],
            },
        }
        result = validator.validate(proposal)
        self.assertEqual("executed", result["outcome"])
        runtime = SimulationRuntime(kernel, collision=make_collision())
        runtime.prepare_trial(source_id)
        derived_id = trial_id_for("call-teacher-drift")
        runtime.prepare_trial(derived_id)
        plain = runtime.physical[source_id].mechanics.state["balls"]
        moved = runtime.physical[derived_id].mechanics.state["balls"]
        self.assertEqual(len(plain), len(moved))
        for first_ball, second_ball in zip(plain.values(), moved.values()):
            self.assertAlmostEqual(
                first_ball["velocity_m_per_s"][0] + 2.0,
                second_ball["velocity_m_per_s"][0],
            )

    def test_out_of_range_count_is_rejected_at_step_6(self):
        kernel, guide, validator = make_teacher_environment()
        token_id = setup_token(guide)
        targets = dict(TEACHER_GROUP_DEFAULT_PARAMETERS, ball_count=100)
        result = validator.validate(
            propose_create(token_id, targets, "ball_count", 100, "call-teacher-far")
        )
        self.assertEqual("rejected", result["outcome"])
        self.assertEqual("numeric_invariant_failed", result["reason_code"])
        self.assertEqual("step_6", result["rejection_stage"])

    def test_per_ball_list_is_rejected_at_step_1(self):
        kernel, guide, validator = make_teacher_environment()
        token_id = setup_token(guide)
        proposal = propose_create(
            token_id,
            {"ball_count": 2, "velocities": [[1.0, 0.0, 0.0]]},
            "ball_count",
            2,
            "call-teacher-vector",
        )
        result = validator.validate(proposal)
        self.assertEqual("rejected", result["outcome"])
        self.assertEqual("malformed_parameters", result["reason_code"])
        self.assertEqual("step_1", result["rejection_stage"])


if __name__ == "__main__":
    unittest.main()
