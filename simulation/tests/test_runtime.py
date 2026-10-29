from copy import deepcopy
import math
import unittest

from simulation.kernel import TrialKernel
from simulation.runtime import (
    DEFAULT_UNIT_CONVENTION,
    CollisionContractError,
    CollisionRepresentation,
    HeadlessBallWriter,
    ModelDeterminismDeclaration,
    RuntimeContractError,
    ScientificWriterSlot,
    SimulationRuntime,
    box_representation,
    failure_report,
    kinetic_energies,
    kinetic_temperature,
    round_trip_units,
)


class PoisonStateWriter(HeadlessBallWriter):
    def advance(self, state, *args, **kwargs):
        facts = super().advance(state, *args, **kwargs)
        first_id = sorted(state["balls"])[0]
        state["balls"][first_id]["velocity_m_per_s"][0] = float("nan")
        return facts


def make_pair(collision=None, **runtime_kwargs):
    kernel = TrialKernel("runtime-headless", "runtime_test")
    collision = collision or box_representation(
        "room-1", {"x": 2.0, "y": 1.5, "z": 2.5}
    )
    runtime = SimulationRuntime(kernel, collision=collision, **runtime_kwargs)
    return kernel, runtime


def start_trial(kernel, runtime, trial_id="trial-a", *, seed=11, ball_count=8, speed=1.0):
    kernel.create_trial(
        trial_id,
        "count-family",
        {"ball_count": ball_count, "initial_speed_scale": speed},
        seed=seed,
    )
    runtime.prepare_trial(trial_id)
    kernel.start_trial()
    runtime.activate_live(trial_id)
    return trial_id


def measurement_series(runtime, trial_id):
    physical = runtime.physical[trial_id]
    return [
        (sample["tick"], sample["kinetic"]["k_total_j"], sample["kinetic"]["k_thermal_j"])
        for sample in physical.tick_measurements
    ]


def reason_code(testcase, callable_value, *args, **kwargs):
    with testcase.assertRaises(RuntimeContractError) as caught:
        callable_value(*args, **kwargs)
    return caught.exception.reason_code


class FixedTickTestCase(unittest.TestCase):
    def test_identical_results_across_request_patterns(self):
        baseline = None
        for pattern in ([1] * 8, [2, 2, 2, 2], [2, 1, 2, 1, 1, 1]):
            kernel, runtime = make_pair()
            start_trial(kernel, runtime, seed=17)
            for demand in pattern:
                runtime.request_slice("trial-a", demand)
            physical = runtime.physical["trial-a"]
            captured = {
                "series": measurement_series(runtime, "trial-a"),
                "log": deepcopy(physical.command_log),
                "draws": physical.streams.scientific_counts(),
                "tick": physical.completed_tick,
                "views": [view["tick"] for view in [runtime.state_view("trial-a")]],
            }
            if baseline is None:
                baseline = captured
            else:
                self.assertEqual(captured, baseline)
        self.assertEqual(baseline["tick"], 8)

    def test_slow_motion_changes_only_request_rate(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, seed=17)
        for _ in range(4):
            runtime.request_slice("trial-a", 1)
        reference = measurement_series(runtime, "trial-a")
        kernel2, runtime2 = make_pair()
        start_trial(kernel2, runtime2, seed=17)
        runtime2.request_slice("trial-a", 2)
        runtime2.request_slice("trial-a", 1)
        runtime2.request_slice("trial-a", 1)
        self.assertEqual(measurement_series(runtime2, "trial-a"), reference)
        clock = runtime2.clock("trial-a")
        self.assertEqual(clock["delta_time_s"], runtime2.determinism.delta_time_s)
        self.assertEqual(
            clock["fixed_delta_time_id"],
            runtime2.determinism.fixed_delta_time_id,
        )


class PauseOverloadTestCase(unittest.TestCase):
    def test_pause_resume_and_no_catch_up(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime)
        runtime.request_slice("trial-a", 2)
        kernel.pause()
        paused = runtime.request_slice("trial-a", 3)
        self.assertTrue(paused["paused"])
        self.assertEqual(paused["executed"], 0)
        self.assertEqual(paused["tick"], 2)
        kernel.resume()
        resumed = runtime.request_slice("trial-a", 1)
        self.assertEqual(resumed["tick"], 3)

    def test_overload_sheds_and_holds_without_tick_debt(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime)
        runtime.request_slice("trial-a", 2)
        overloaded = runtime.request_slice("trial-a", 10)
        self.assertLessEqual(overloaded["executed"], 2)
        self.assertEqual(overloaded["shed_ticks"], 8)
        steps = [event["step"] for event in overloaded["degradation"]]
        self.assertEqual(steps[0], "replay")
        self.assertLess(steps.index("batch"), steps.index("presentation"))
        next_slice = runtime.request_slice("trial-a", 1)
        self.assertEqual(next_slice["executed"], 1)
        self.assertEqual(next_slice["tick"], overloaded["tick"] + 1)
        for _ in range(3):
            runtime.request_slice("trial-a", 10)
        physical = runtime.physical["trial-a"]
        self.assertEqual(physical.hold["kind"], "performance")
        held = runtime.request_slice("trial-a", 1)
        self.assertEqual(held["executed"], 0)
        self.assertIn("hold", held)
        report = runtime.failure_reports[-1]
        self.assertEqual(report["layer"], "performance_thermal")
        self.assertEqual(report["reason"], "sustained_tick_pressure")
        recovered = runtime.recover_hold("trial-a", "clear_performance_hold")
        self.assertTrue(recovered["recovered"])
        self.assertTrue(
            any(item["reason"] == "sustained_tick_pressure" for item in runtime.failure_reports)
        )
        after = runtime.request_slice("trial-a", 1)
        self.assertEqual(after["executed"], 1)


class AdmissionOrderingTestCase(unittest.TestCase):
    def test_log_before_effect_order_and_sources(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime)
        user = runtime.admit(
            "trial-a",
            "ball_impulse",
            {"entity_id": "ball-0001", "delta_v_m_per_s": [0.5, 0.0, 0.0]},
        )
        proposal = runtime.admit(
            "trial-a",
            "ball_impulse",
            {"entity_id": "ball-0002", "delta_v_m_per_s": [0.0, 0.5, 0.0]},
            source="accepted_proposal",
            command_id="command-1",
            basis_trial_ref=kernel.current.ref.to_dict(),
        )
        self.assertTrue(user["admitted"])
        self.assertTrue(proposal["admitted"])
        log = runtime.physical["trial-a"].command_log
        commands = [entry for entry in log if entry["kind"] == "command"]
        self.assertEqual([entry["admission_sequence"] for entry in commands], [1, 2])
        self.assertEqual(commands[0]["source"], "user_input")
        self.assertEqual(commands[1]["source"], "accepted_proposal")
        self.assertTrue(all(not entry["executed"] for entry in commands))
        runtime.request_slice("trial-a", 1)
        self.assertTrue(all(entry["executed"] for entry in commands))
        velocities = {
            entity["entity_id"]: entity["velocity_m_per_s"]
            for entity in runtime.state_view("trial-a")["entities"]
        }
        self.assertNotEqual(velocities["ball-0001"][0], 0.0)
        self.assertNotEqual(velocities["ball-0002"][1], 0.0)

    def test_stale_proposal_late_input_and_duplicates(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime)
        runtime.request_slice("trial-a", 2)
        stale = runtime.admit(
            "trial-a",
            "ball_impulse",
            {"entity_id": "ball-0001", "delta_v_m_per_s": [0.5, 0.0, 0.0]},
            source="accepted_proposal",
            command_id="command-stale",
            basis_trial_ref={"trial_id": "trial-a", "trial_revision": 1},
        )
        self.assertFalse(stale["admitted"])
        self.assertEqual(stale["reason_code"], "stale_trial_ref")
        self.assertTrue(
            any(
                entry["kind"] == "invalid_input_fact"
                and entry["reason_code"] == "stale_trial_ref"
                for entry in runtime.physical["trial-a"].command_log
            )
        )
        late = runtime.admit(
            "trial-a",
            "ball_impulse",
            {"entity_id": "ball-0001", "delta_v_m_per_s": [0.25, 0.0, 0.0]},
            intended_tick=0,
        )
        self.assertTrue(late["admitted"])
        self.assertEqual(late["target_tick"], 3)
        self.assertEqual(
            late["late_assignment_fact"]["assigned_to_next_unstarted_tick"], 3
        )
        fresh = runtime.admit(
            "trial-a",
            "ball_impulse",
            {"entity_id": "ball-0001", "delta_v_m_per_s": [0.5, 0.0, 0.0]},
            source="accepted_proposal",
            command_id="command-1",
            basis_trial_ref=kernel.current.ref.to_dict(),
        )
        self.assertTrue(fresh["admitted"])
        duplicate = runtime.admit(
            "trial-a",
            "ball_impulse",
            {"entity_id": "ball-0001", "delta_v_m_per_s": [0.5, 0.0, 0.0]},
            source="accepted_proposal",
            command_id="command-1",
            basis_trial_ref=kernel.current.ref.to_dict(),
        )
        self.assertFalse(duplicate["admitted"])
        self.assertTrue(duplicate["replay"])
        runtime.request_slice("trial-a", 1)
        entries = [
            entry
            for entry in runtime.physical["trial-a"].command_log
            if entry["kind"] == "command" and entry.get("command_id") == "command-1"
        ]
        self.assertEqual(len(entries), 1)
        invalid = runtime.admit(
            "trial-a",
            "ball_impulse",
            {"entity_id": "ball-0001", "delta_v_m_per_s": [float("inf"), 0.0, 0.0]},
        )
        self.assertFalse(invalid["admitted"])
        self.assertEqual(invalid["reason_code"], "non_finite_input")


class RngIdentityTestCase(unittest.TestCase):
    def test_presentation_draws_do_not_shift_scientific_streams(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, seed=23)
        for _ in range(3):
            runtime.request_slice("trial-a", 2)
        reference = {
            "series": measurement_series(runtime, "trial-a"),
            "draws": runtime.physical["trial-a"].streams.scientific_counts(),
        }
        kernel2, runtime2 = make_pair()
        start_trial(kernel2, runtime2, seed=23)
        for _ in range(3):
            runtime2.physical["trial-a"].streams.draw("presentation-particles", 3)
            runtime2.request_slice("trial-a", 2)
        self.assertEqual(
            runtime2.physical["trial-a"].streams.scientific_counts(),
            reference["draws"],
        )
        self.assertEqual(measurement_series(runtime2, "trial-a"), reference["series"])

    def test_version_change_is_not_silent_compatibility(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, seed=23)
        runtime.request_slice("trial-a", 2)
        changed = ModelDeterminismDeclaration(rng_algorithm_id="xorshift-v9")
        result = runtime.recompute_trial("trial-a", determinism=changed)
        self.assertEqual(result["status"], "unsupported")
        self.assertEqual(result["reason_code"], "incompatible_model_version")
        self.assertFalse(runtime.physical["trial-a"].recompute_supported)


class CollisionContractTestCase(unittest.TestCase):
    def test_each_primary_representation_choice_is_accepted(self):
        box = box_representation("room-1", {"x": 2.0, "y": 1.5, "z": 2.5})
        self.assertEqual(box.validate()["status"], "valid")
        volume = CollisionRepresentation(
            "volume-1",
            "1",
            "room-frame-v1",
            [
                {
                    "surface_id": "room-volume",
                    "primary_representation": "bounding_volume",
                    "provenance": "scanned_confirmed",
                    "bounding_volume": {
                        "center": [1.0, 0.75, 1.25],
                        "half_extent": 1.0,
                    },
                }
            ],
        )
        self.assertEqual(volume.validate()["status"], "valid")
        mesh = CollisionRepresentation(
            "mesh-1",
            "1",
            "room-frame-v1",
            [
                {
                    "surface_id": "room-mesh",
                    "primary_representation": "global_mesh",
                    "provenance": "scanned_confirmed",
                    "global_mesh": {
                        "vertices": [[0, 0, 0], [2, 0, 0], [0, 1, 0], [0, 0, 2]]
                    },
                }
            ],
        )
        self.assertEqual(mesh.validate()["status"], "valid")

    def test_duplicate_and_invalid_representations_are_refused(self):
        duplicate = CollisionRepresentation(
            "dup-1",
            "1",
            "room-frame-v1",
            [
                {
                    "surface_id": "s1",
                    "primary_representation": "plane",
                    "provenance": "scanned_confirmed",
                    "plane": {"normal": [1.0, 0.0, 0.0], "offset": 2.0},
                    "bounding_volume": {"center": [0, 0, 0], "half_extent": 1.0},
                }
            ],
        )
        with self.assertRaises(CollisionContractError) as caught:
            duplicate.validate()
        self.assertEqual(caught.exception.reason_code, "duplicate_representation")
        non_finite = CollisionRepresentation(
            "bad-1",
            "1",
            "room-frame-v1",
            [
                {
                    "surface_id": "s1",
                    "primary_representation": "plane",
                    "provenance": "scanned_confirmed",
                    "plane": {"normal": [1.0, 0.0, 0.0], "offset": float("inf")},
                }
            ],
        )
        with self.assertRaises(CollisionContractError) as caught:
            non_finite.validate()
        self.assertEqual(caught.exception.reason_code, "non_finite_geometry")
        kernel, runtime = make_pair()
        start_trial(kernel, runtime)
        kernel.create_trial("trial-b", "count-family", {"ball_count": 4}, seed=2)
        runtime.collision_supply = duplicate
        self.assertEqual(
            reason_code(self, runtime.prepare_trial, "trial-b"),
            "duplicate_representation",
        )
        self.assertTrue(
            any(
                report["layer"] == "scene_permission"
                and report["reason"] == "duplicate_representation"
                for report in runtime.failure_reports
            )
        )

    def test_virtual_boundary_labelling(self):
        box = box_representation("room-1", {"x": 2.0, "y": 1.5, "z": 2.5}, virtual=True)
        for surface in box.surfaces:
            self.assertEqual(surface["provenance"], "virtual_supplement")
        scanned = box_representation(
            "room-2", {"x": 2.0, "y": 1.5, "z": 2.5}, virtual=False
        )
        for surface in scanned.surfaces:
            self.assertEqual(surface["provenance"], "scanned_confirmed")

    def test_pin_survives_rescan_and_scene_events(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime)
        original_pin = deepcopy(runtime.physical["trial-a"].setup["collision_pin"])
        result = runtime.rescan_collision(
            box_representation("room-2", {"x": 1.0, "y": 1.0, "z": 1.0})
        )
        self.assertEqual(
            result["existing_pins"]["trial-a"]["content_checksum"],
            original_pin["content_checksum"],
        )
        for event in ("furniture_moved", "tracking_relocalization"):
            pins = runtime.notify_scene_event(event)["pins"]
            self.assertEqual(
                pins["trial-a"]["content_checksum"], original_pin["content_checksum"]
            )
        kernel.create_trial(
            "trial-b", "count-family", {"ball_count": 4}, seed=2
        )
        runtime.prepare_trial("trial-b")
        self.assertEqual(
            runtime.physical["trial-b"].setup["collision_pin"]["representation_id"],
            "room-2",
        )
        runtime.request_slice("trial-a", 1)
        self.assertEqual(
            runtime.state_view("trial-a")["collision_pin"]["content_checksum"],
            original_pin["content_checksum"],
        )

    def test_safety_boundary_loss_pauses_explainably(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime)
        result = runtime.notify_scene_event("safety_boundary_lost")
        self.assertEqual(result["paused_trial_id"], "trial-a")
        held = runtime.request_slice("trial-a", 1)
        self.assertEqual(held["executed"], 0)
        self.assertEqual(held["hold"]["kind"], "scene")
        self.assertTrue(
            any(
                report["layer"] == "scene_permission"
                and report["reason"] == "safety_boundary_lost"
                for report in runtime.failure_reports
            )
        )


class ContactClosureTestCase(unittest.TestCase):
    def test_swept_contact_records_work_once(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=4, speed=0.0)
        position = runtime.state_view("trial-a")["entities"][0]["position_m"]
        interaction = runtime.open_contact(
            "trial-a", "swept", ["ball-0001"], {"impulse_magnitude": 2.0}
        )
        admission = runtime.submit_sweep_sample(
            "trial-a",
            interaction,
            {
                "origin_m": position,
                "velocity_m_per_s": [0.0, 0.0, 0.0],
                "radius_m": 0.1,
                "tracking_valid": True,
            },
        )
        self.assertTrue(admission["admitted"])
        revision_before = kernel.trials["trial-a"].state["trial_revision"]
        runtime.request_slice("trial-a", 1)
        velocity = runtime.state_view("trial-a")["entities"][0]["velocity_m_per_s"]
        self.assertNotEqual(velocity, [0.0, 0.0, 0.0])
        closure = runtime.close_contact("trial-a", interaction)
        self.assertGreater(closure["work_j"], 0.0)
        self.assertEqual(
            kernel.trials["trial-a"].state["trial_revision"],
            revision_before + 1,
        )
        self.assertEqual(
            reason_code(self, runtime.close_contact, "trial-a", interaction),
            "contact_already_closed",
        )

    def test_empty_swing_and_tracking_loss(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=4, speed=0.0)
        before = deepcopy(runtime.state_view("trial-a")["entities"])
        interaction = runtime.open_contact(
            "trial-a", "swept", ["ball-0001"], {"impulse_magnitude": 2.0}
        )
        runtime.submit_sweep_sample(
            "trial-a",
            interaction,
            {
                "origin_m": [1.0, 10.0, 1.0],
                "velocity_m_per_s": [0.0, 0.0, 0.0],
                "radius_m": 0.05,
                "tracking_valid": True,
            },
        )
        runtime.request_slice("trial-a", 1)
        closure = runtime.close_contact("trial-a", interaction)
        self.assertEqual(closure["work_j"], 0.0)
        self.assertEqual(runtime.state_view("trial-a")["entities"], before)
        lost = runtime.open_contact(
            "trial-a", "swept", ["ball-0002"], {"impulse_magnitude": 2.0}
        )
        result = runtime.submit_sweep_sample(
            "trial-a",
            lost,
            {
                "origin_m": [0.0, 0.0, 0.0],
                "velocity_m_per_s": [1.0, 0.0, 0.0],
                "radius_m": 0.1,
                "tracking_valid": False,
            },
        )
        self.assertFalse(result["accepted"])
        self.assertTrue(result["aborted"])
        contact = runtime.physical["trial-a"].mechanics.contacts[lost]
        self.assertEqual(contact["closure"]["closure_reason"], "tracking_lost_aborted")
        self.assertEqual(runtime.state_view("trial-a")["entities"], before)

    def test_one_shot_prop_consumed_once(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=4, speed=1.0)
        runtime.register_prop("trial-a", "prop-absorber", damping_factor=0.5)
        admission = runtime.admit(
            "trial-a",
            "prop_trigger",
            {"prop_id": "prop-absorber", "entity_id": "ball-0002"},
        )
        self.assertTrue(admission["admitted"])
        runtime.request_slice("trial-a", 1)
        props = runtime.prop_states("trial-a")
        self.assertEqual(props["prop-absorber"]["state"], "consumed")
        self.assertGreater(props["prop-absorber"]["absorbed_energy_j"], 0.0)
        replay = runtime.admit(
            "trial-a",
            "prop_trigger",
            {"prop_id": "prop-absorber", "entity_id": "ball-0002"},
        )
        self.assertFalse(replay["admitted"])
        self.assertEqual(replay["reason_code"], "prop_consumed")


class ReadOnlyIndependenceTestCase(unittest.TestCase):
    def test_presentation_activity_cannot_change_science(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, seed=31)
        for _ in range(6):
            runtime.request_slice("trial-a", 1)
        reference = {
            "series": measurement_series(runtime, "trial-a"),
            "draws": runtime.physical["trial-a"].streams.scientific_counts(),
            "log": deepcopy(runtime.physical["trial-a"].command_log),
        }
        kernel2, runtime2 = make_pair()
        start_trial(kernel2, runtime2, seed=31)
        render_objects = []
        for _ in range(6):
            for _ in range(3):
                view = runtime2.state_view("trial-a")
                render_objects.extend(deepcopy(view["entities"]))
            render_objects.reverse()
            runtime2.physical["trial-a"].streams.draw("presentation-particles", 2)
            runtime2.request_slice("trial-a", 1)
        self.assertEqual(measurement_series(runtime2, "trial-a"), reference["series"])
        self.assertEqual(
            runtime2.physical["trial-a"].streams.scientific_counts(),
            reference["draws"],
        )
        self.assertEqual(runtime2.physical["trial-a"].command_log, reference["log"])
        self.assertGreater(len(render_objects), 0)
        entity_ids = [
            entity["entity_id"]
            for entity in runtime2.state_view("trial-a")["entities"]
        ]
        self.assertEqual(entity_ids, sorted(entity_ids))

    def test_views_are_immutable_by_value(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime)
        runtime.request_slice("trial-a", 1)
        view = runtime.state_view("trial-a")
        original = deepcopy(view["entities"][0]["position_m"])
        view["entities"][0]["position_m"][0] = 999.0
        view["energy"]["k_total_j"] = -1.0
        fresh = runtime.state_view("trial-a")
        self.assertEqual(fresh["entities"][0]["position_m"], original)
        self.assertNotEqual(fresh["energy"]["k_total_j"], -1.0)
        draws_before = runtime.physical["trial-a"].streams.counts()
        for _ in range(5):
            runtime.state_view("trial-a")
        self.assertEqual(runtime.physical["trial-a"].streams.counts(), draws_before)


class UnitsHighDriftTestCase(unittest.TestCase):
    def test_unit_round_trips(self):
        for axis_id, value in (("length", 2.5), ("energy", 3.25), ("mass", 1.5)):
            record = round_trip_units(value, axis_id, DEFAULT_UNIT_CONVENTION)
            self.assertAlmostEqual(record["restored_internal_value"], value)
        self.assertEqual(
            reason_code(
                self, round_trip_units, 1e308, "energy", DEFAULT_UNIT_CONVENTION
            ),
            "conversion_out_of_tolerance",
        )

    def test_high_drift_matches_double_precision_direct_sum(self):
        balls = {}
        for index in range(20):
            jitter = 0.02 if index % 2 else -0.02
            balls[f"ball-{index + 1:04d}"] = {
                "entity_id": f"ball-{index + 1:04d}",
                "position_m": [0.0, 0.0, float(index)],
                "velocity_m_per_s": [10.0 + jitter, jitter, 0.0],
                "mass_kg": 1.0,
                "radius_m": 0.05,
                "membership": "free",
            }
        energies = kinetic_energies(balls)
        self.assertGreaterEqual(energies["k_drift_j"] / energies["k_total_j"], 0.99)
        temperature = kinetic_temperature(energies, DEFAULT_UNIT_CONVENTION, 10)
        expected_thermal = math.fsum(
            0.5
            * ball["mass_kg"]
            * math.fsum(
                (ball["velocity_m_per_s"][axis] - energies["v_cm_m_per_s"][axis]) ** 2
                for axis in range(3)
            )
            for ball in balls.values()
        )
        expected = 2.0 * expected_thermal / (20 * 3 * 1.0)
        self.assertAlmostEqual(temperature["value"], expected, delta=1e-9 * max(1.0, expected))

    def test_small_samples_are_not_applicable(self):
        balls = {}
        for index in range(3):
            balls[f"ball-{index + 1:04d}"] = {
                "entity_id": f"ball-{index + 1:04d}",
                "position_m": [0.0, 0.0, float(index)],
                "velocity_m_per_s": [1.0, 2.0, 3.0],
                "mass_kg": 1.0,
                "radius_m": 0.05,
                "membership": "free",
            }
        temperature = kinetic_temperature(
            kinetic_energies(balls), DEFAULT_UNIT_CONVENTION, 10
        )
        self.assertEqual(temperature["applicability"], "not_applicable")
        self.assertIsNone(temperature["value"])
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=4)
        runtime.request_slice("trial-a", 1)
        thermal = runtime.thermal_measurement("trial-a")
        self.assertEqual(thermal["applicability"], "not_applicable")
        self.assertIsNone(thermal["value"])

    def test_non_finite_state_withholds_publication(self):
        kernel, runtime = make_pair(writer_factory=PoisonStateWriter)
        start_trial(kernel, runtime, ball_count=4)
        with self.assertRaises(RuntimeContractError):
            runtime.request_slice("trial-a", 1)
        physical = runtime.physical["trial-a"]
        self.assertEqual(physical.hold["kind"], "numerical")
        self.assertEqual(physical.publications, [])
        self.assertEqual(physical.tick_measurements, [])
        self.assertTrue(
            any(
                report["layer"] == "numerical_scientific"
                and report["reason"] == "non_finite_state"
                for report in runtime.failure_reports
            )
        )


class HeldObjectTestCase(unittest.TestCase):
    def test_grab_targets_release_accounting(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=4, speed=0.2)
        revision_before = kernel.trials["trial-a"].state["trial_revision"]
        grab = runtime.admit("trial-a", "grab", {"entity_id": "ball-0001"})
        self.assertTrue(grab["admitted"])
        runtime.request_slice("trial-a", 1)
        view = runtime.state_view("trial-a")
        memberships = {
            entity["entity_id"]: entity["membership"] for entity in view["entities"]
        }
        self.assertEqual(memberships["ball-0001"], "held")
        self.assertEqual(view["energy"]["free_count"], 3)
        self.assertEqual(view["energy"]["held_count"], 1)
        self.assertEqual(
            kernel.trials["trial-a"].state["trial_revision"], revision_before
        )
        target = runtime.admit(
            "trial-a",
            "held_target",
            {
                "entity_id": "ball-0001",
                "target_position_m": [0.5, 0.5, 0.5],
                "target_velocity_m_per_s": [1.0, 0.0, 0.0],
                "tracking_valid": True,
            },
        )
        self.assertTrue(target["admitted"])
        runtime.request_slice("trial-a", 1)
        position = runtime.state_view("trial-a")["entities"][0]["position_m"]
        self.assertAlmostEqual(position[0], 0.5)
        self.assertAlmostEqual(position[1], 0.5)
        over_limit = runtime.admit(
            "trial-a",
            "held_target",
            {
                "entity_id": "ball-0001",
                "target_position_m": [0.5, 0.5, 0.5],
                "target_velocity_m_per_s": [100.0, 0.0, 0.0],
                "tracking_valid": True,
            },
        )
        self.assertTrue(over_limit["admitted"])
        runtime.request_slice("trial-a", 1)
        velocity = runtime.state_view("trial-a")["entities"][0]["velocity_m_per_s"]
        self.assertEqual(velocity, [6.0, 0.0, 0.0])
        invalid = runtime.admit(
            "trial-a",
            "held_target",
            {
                "entity_id": "ball-0001",
                "target_position_m": [float("nan"), 0.5, 0.5],
                "target_velocity_m_per_s": [1.0, 0.0, 0.0],
                "tracking_valid": True,
            },
        )
        self.assertFalse(invalid["admitted"])
        lost = runtime.admit(
            "trial-a",
            "held_target",
            {
                "entity_id": "ball-0001",
                "target_position_m": [0.5, 0.5, 0.5],
                "target_velocity_m_per_s": [1.0, 0.0, 0.0],
                "tracking_valid": False,
            },
        )
        self.assertTrue(lost["admitted"])
        runtime.request_slice("trial-a", 1)
        refused = runtime.admit("trial-a", "release", {"entity_id": "ball-0001"})
        self.assertFalse(refused["admitted"])
        self.assertEqual(refused["reason_code"], "tracking_lost_release_refused")
        restored = runtime.admit(
            "trial-a",
            "held_target",
            {
                "entity_id": "ball-0001",
                "target_position_m": [0.6, 0.5, 0.5],
                "target_velocity_m_per_s": [2.0, 0.0, 0.0],
                "tracking_valid": True,
            },
        )
        self.assertTrue(restored["admitted"])
        runtime.request_slice("trial-a", 1)
        release = runtime.admit("trial-a", "release", {"entity_id": "ball-0001"})
        self.assertTrue(release["admitted"])
        runtime.request_slice("trial-a", 1)
        view = runtime.state_view("trial-a")
        memberships = {
            entity["entity_id"]: entity["membership"] for entity in view["entities"]
        }
        self.assertEqual(memberships["ball-0001"], "free")
        self.assertEqual(view["energy"]["free_count"], 4)
        release_velocity = view["entities"][0]["velocity_m_per_s"]
        self.assertLessEqual(abs(release_velocity[0]), 6.0)
        self.assertEqual(
            kernel.trials["trial-a"].state["trial_revision"],
            revision_before + 1,
        )
        closure_entry = [
            entry
            for entry in runtime.physical["trial-a"].command_log
            if entry.get("outcome", {}).get("semantic_closure")
        ]
        self.assertEqual(len(closure_entry), 1)


class IsolationLiveSlotTestCase(unittest.TestCase):
    def test_live_slot_matrix(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=6, seed=5)
        runtime.request_slice("trial-a", 2)
        source_ref = kernel.trials["trial-a"].ref.to_dict()
        kernel.derive_trial(
            "trial-c", source_ref, "ball_count", 12,
            downstream_effect_ids=["collision_cross_section"],
        )
        runtime.prepare_trial("trial-c")
        self.assertTrue(runtime.physical["trial-c"].candidate)
        self.assertEqual(
            reason_code(self, runtime.request_slice, "trial-c", 1),
            "trial_not_live",
        )
        runtime.request_slice("trial-a", 1)
        kernel.return_to_source("trial-c")
        runtime.abandon_candidate("trial-c")
        runtime.request_slice("trial-a", 1)
        self.assertEqual(runtime.physical["trial-a"].completed_tick, 4)
        kernel.derive_trial(
            "trial-d", source_ref, "ball_count", 12,
            downstream_effect_ids=["collision_cross_section"],
        )
        runtime.prepare_trial("trial-d")
        kernel.accept_trial("trial-d")
        kernel.start_trial()
        runtime.activate_live("trial-d")
        self.assertIn("trial-a", runtime.frozen_trial_ids)
        self.assertEqual(
            reason_code(self, runtime.request_slice, "trial-a", 1),
            "trial_not_live",
        )
        state_a = {
            "tick": runtime.physical["trial-a"].completed_tick,
            "draws": runtime.physical["trial-a"].streams.counts(),
            "log_length": len(runtime.physical["trial-a"].command_log),
        }
        runtime.request_slice("trial-d", 2)
        self.assertEqual(runtime.physical["trial-a"].completed_tick, state_a["tick"])
        self.assertEqual(
            runtime.physical["trial-a"].streams.counts(), state_a["draws"]
        )
        self.assertEqual(
            len(runtime.physical["trial-a"].command_log), state_a["log_length"]
        )
        self.assertIsNot(
            runtime.slot.writer_for("trial-a"), runtime.slot.writer_for("trial-d")
        )
        returned = runtime.return_from_started("trial-d")
        self.assertEqual(returned["disposition"], "paused")
        self.assertEqual(returned["source_disposition"], "resumed_live")
        self.assertEqual(runtime.live_trial_id, "trial-a")
        runtime.request_slice("trial-a", 1)
        self.assertEqual(runtime.physical["trial-a"].completed_tick, 5)

    def test_measured_source_returns_read_only(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=6, seed=5)
        source_ref = kernel.trials["trial-a"].ref.to_dict()
        kernel.create_trial(
            "trial-f", "count-family", {"ball_count": 6}, seed=8
        )
        runtime.prepare_trial("trial-f")
        kernel.select_trial("trial-f")
        kernel.start_trial()
        kernel.trials["trial-f"].state["phase"] = "measured"
        measured_ref = kernel.trials["trial-f"].ref.to_dict()
        kernel.derive_trial(
            "trial-g", measured_ref, "ball_count", 12,
            downstream_effect_ids=["collision_cross_section"],
        )
        runtime.prepare_trial("trial-g")
        kernel.accept_trial("trial-g")
        kernel.start_trial()
        runtime.activate_live("trial-g")
        runtime.request_slice("trial-g", 1)
        returned = runtime.return_from_started("trial-g")
        self.assertEqual(returned["source_disposition"], "read_only")
        self.assertIsNone(runtime.live_trial_id)
        self.assertEqual(
            reason_code(self, runtime.request_slice, "trial-f", 1),
            "trial_not_live",
        )
        self.assertEqual(
            reason_code(self, runtime.request_slice, "trial-g", 1),
            "trial_not_live",
        )


class RecomputeTestCase(unittest.TestCase):
    def test_recompute_from_initial_state_and_checkpoint(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=6, seed=9)
        for _ in range(2):
            runtime.request_slice("trial-a", 2)
        runtime.admit(
            "trial-a",
            "ball_impulse",
            {"entity_id": "ball-0002", "delta_v_m_per_s": [1.0, 0.0, 0.0]},
        )
        for _ in range(2):
            runtime.request_slice("trial-a", 2)
        self.assertEqual(runtime.physical["trial-a"].completed_tick, 8)
        runtime.capture_checkpoint("trial-a", verified=True)
        result = runtime.recompute_trial("trial-a")
        self.assertEqual(result["status"], "recomputed")
        self.assertTrue(result["measurements_match"])
        from_checkpoint = runtime.recompute_trial(
            "trial-a", from_checkpoint_tick=8
        )
        self.assertTrue(from_checkpoint["measurements_match"])
        self.assertEqual(from_checkpoint["from_checkpoint_tick"], 8)
        runtime.capture_checkpoint("trial-a", verified=False)
        recipe = runtime.build_replay_recipe("trial-a")
        refused = SimulationRuntime.recompute(
            recipe, from_checkpoint_tick=runtime.physical["trial-a"].completed_tick
        )
        self.assertEqual(refused["reason_code"], "checkpoint_unverified")

    def test_partial_trailing_command_is_rejected(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=6, seed=9)
        runtime.request_slice("trial-a", 2)
        recipe = runtime.build_replay_recipe("trial-a")
        recipe["commands"].append(
            {
                "kind": "command",
                "command_id": "late",
                "source": "user_input",
                "operation": "ball_impulse",
                "parameters": {
                    "entity_id": "ball-0001",
                    "delta_v_m_per_s": [1.0, 0.0, 0.0],
                },
                "target_tick": recipe["to_tick"] + 1,
                "admission_sequence": 99,
                "executed": True,
            }
        )
        result = SimulationRuntime.recompute(recipe)
        self.assertEqual(result["status"], "refused")
        self.assertEqual(result["reason_code"], "partial_trailing_command")

    def test_rescan_does_not_change_old_recipes(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=6, seed=9)
        runtime.request_slice("trial-a", 3)
        runtime.rescan_collision(
            box_representation("room-9", {"x": 1.0, "y": 1.0, "z": 1.0})
        )
        result = runtime.recompute_trial("trial-a")
        self.assertTrue(result["measurements_match"])

    def test_missing_geometry_and_checksum_mismatch(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=6, seed=9)
        runtime.request_slice("trial-a", 2)
        missing = runtime.build_replay_recipe("trial-a")
        del missing["collision"]
        result = SimulationRuntime.recompute(missing)
        self.assertEqual(result["reason_code"], "collision_data_missing")
        tampered = runtime.build_replay_recipe("trial-a")
        tampered["collision"]["surfaces"][0]["plane"]["offset"] += 0.5
        result = SimulationRuntime.recompute(tampered)
        self.assertEqual(result["reason_code"], "collision_checksum_mismatch")

    def test_state_playback_is_not_recompute(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=6, seed=9)
        runtime.request_slice("trial-a", 2)
        playback = runtime.mark_state_playback("trial-a")
        self.assertEqual(playback["capability"], "state_playback")
        result = SimulationRuntime.recompute(playback)
        self.assertEqual(result["status"], "refused")
        self.assertEqual(result["reason_code"], "state_playback_not_recompute")


class BatchAtomicityTestCase(unittest.TestCase):
    def declaration(self):
        return {
            "batch_id": "batch-1",
            "sample_count": 3,
            "input_delta": {
                "parameter_id": "initial_speed_scale",
                "from_value": 1.0,
                "to_value": 2.0,
                "downstream_effect_ids": ["mean_collision_rate"],
            },
            "result_granularity": "per_sample_summary",
            "ticks_per_sample": 5,
            "aggregate_statistic_ids": ["mean_final_k_thermal"],
        }

    def test_batch_completion_commits_once(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=6, seed=3)
        runtime.begin_batch("trial-a", self.declaration())
        self.assertEqual(
            reason_code(
                self,
                runtime.admit,
                "trial-a",
                "ball_impulse",
                {"entity_id": "ball-0001", "delta_v_m_per_s": [1.0, 0.0, 0.0]},
            ),
            "batch_occupies_command_stream",
        )
        revision_before = kernel.trials["trial-a"].state["trial_revision"]
        first = runtime.step_batch("trial-a")
        self.assertEqual(first["sample_index"], 0)
        self.assertEqual(
            kernel.trials["trial-a"].state["trial_revision"], revision_before
        )
        runtime.step_batch("trial-a")
        final = runtime.step_batch("trial-a")
        self.assertEqual(final["status"], "complete")
        self.assertEqual(
            kernel.trials["trial-a"].state["trial_revision"],
            revision_before + 1,
        )
        self.assertTrue(
            any(
                item["measurement_id"] == "batch-batch-1-aggregate"
                for item in kernel.trials["trial-a"].measurements
            )
        )
        self.assertTrue(
            runtime.batch_evidence_status("trial-a")[
                "satisfies_complete_measurement_rule"
            ]
        )

    def test_batch_fixed_step_equivalence(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=6, seed=3)
        declaration = self.declaration()
        runtime.begin_batch("trial-a", declaration)
        first = runtime.step_batch("trial-a")
        sample_seed = runtime._batch_sample_seed(
            runtime.physical["trial-a"], declaration, 0
        )
        kernel.create_trial(
            "trial-equivalent",
            "count-family",
            {"ball_count": 6, "initial_speed_scale": 2.0},
            seed=sample_seed,
        )
        runtime.prepare_trial("trial-equivalent")
        kernel.select_trial("trial-equivalent")
        kernel.start_trial()
        runtime.activate_live("trial-equivalent")
        for _ in range(5):
            runtime.request_slice("trial-equivalent", 1)
        equivalent = [
            (
                sample["kinetic"]["k_total_j"],
                sample["kinetic"]["k_thermal_j"],
            )
            for sample in runtime.physical["trial-equivalent"].tick_measurements
        ]
        produced = [
            (item["k_total_j"], item["k_thermal_j"])
            for item in first["result"]["series"]
        ]
        self.assertEqual(produced, equivalent)

    def test_batch_determinism_and_cancellation(self):
        results = []
        for _ in range(2):
            kernel, runtime = make_pair()
            start_trial(kernel, runtime, ball_count=6, seed=3)
            runtime.begin_batch("trial-a", self.declaration())
            for _ in range(3):
                runtime.step_batch("trial-a")
            results.append(runtime.physical["trial-a"].batch["aggregate"])
        self.assertEqual(results[0], results[1])
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=6, seed=3)
        runtime.begin_batch("trial-a", self.declaration())
        runtime.step_batch("trial-a")
        runtime.step_batch("trial-a")
        cancelled = runtime.cancel_batch("trial-a", "user_cancelled")
        self.assertEqual(cancelled["status"], "incomplete")
        self.assertEqual(cancelled["completed_samples"], 2)
        self.assertEqual(cancelled["reason_code"], "user_cancelled")
        status = runtime.batch_evidence_status("trial-a")
        self.assertFalse(status["satisfies_complete_measurement_rule"])
        self.assertEqual(len(cancelled["results"]), 2)

    def test_batch_failure_retains_completed_samples(self):
        counter = {"count": 0}

        def flaky_factory():
            counter["count"] += 1
            if counter["count"] == 3:
                raise RuntimeError("injected sample failure")
            return HeadlessBallWriter()

        kernel, runtime = make_pair(writer_factory=flaky_factory)
        start_trial(kernel, runtime, ball_count=6, seed=3)
        runtime.begin_batch("trial-a", self.declaration())
        runtime.step_batch("trial-a")
        failed = runtime.step_batch("trial-a")
        self.assertEqual(failed["status"], "incomplete")
        self.assertEqual(failed["reason_code"], "sample_execution_failed")
        self.assertEqual(failed["completed_samples"], 1)


class ControlledComparisonTestCase(unittest.TestCase):
    def test_one_input_delta_record_projects_identically(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=12, seed=4)
        source_ref = kernel.trials["trial-a"].ref.to_dict()
        kernel.derive_trial(
            "trial-c",
            source_ref,
            "ball_count",
            24,
            downstream_effect_ids=["collision_cross_section", "mixing_rate"],
        )
        runtime.prepare_trial("trial-c")
        kernel.accept_trial("trial-c")
        kernel.start_trial()
        runtime.activate_live("trial-c")
        for _ in range(5):
            runtime.request_slice("trial-c", 2)
        record = runtime.controlled_comparison("trial-c", window_ticks=10)
        self.assertEqual(
            record["input_delta"],
            kernel.trials["trial-c"].configuration["source_input_delta"],
        )
        self.assertEqual(record["runtime_evidence"]["applicability"], "valid")
        downstream = record["runtime_evidence"]["downstream_effect_measurements"]
        self.assertIn("collision_cross_section", downstream)
        self.assertIn("mixing_rate", downstream)
        self.assertIsNotNone(downstream["collision_cross_section"]["value"])
        for projection in (
            "trial_card",
            "snapshot",
            "validation_evidence",
            "explanation_citation",
        ):
            projected = SimulationRuntime.project_comparison(record, projection)
            self.assertEqual(projected["content"], record)
        self.assertEqual(
            reason_code(
                self, SimulationRuntime.project_comparison, record, "unknown_place"
            ),
            "unknown_comparison_projection",
        )

    def test_insufficient_window_yields_uncertainty(self):
        kernel, runtime = make_pair()
        start_trial(kernel, runtime, ball_count=12, seed=4)
        source_ref = kernel.trials["trial-a"].ref.to_dict()
        kernel.derive_trial(
            "trial-c",
            source_ref,
            "ball_count",
            24,
            downstream_effect_ids=["collision_cross_section"],
        )
        runtime.prepare_trial("trial-c")
        kernel.accept_trial("trial-c")
        kernel.start_trial()
        runtime.activate_live("trial-c")
        runtime.request_slice("trial-c", 2)
        record = runtime.controlled_comparison("trial-c", window_ticks=10)
        self.assertEqual(record["runtime_evidence"]["applicability"], "insufficient")
        self.assertEqual(
            record["runtime_evidence"]["actual_sample_count"], 2
        )
        unsettled = runtime.controlled_comparison("trial-c", window_ticks=20)
        self.assertEqual(unsettled["runtime_evidence"]["applicability"], "insufficient")
        self.assertIsNotNone(unsettled["runtime_evidence"]["uncertainty"])
        self.assertEqual(unsettled["no_conclusion_reason"], "insufficient_window_or_sample")


class RecordsTestCase(unittest.TestCase):
    def test_failure_report_contract(self):
        with self.assertRaises(ValueError):
            failure_report("mystery_layer", "x")
        report = failure_report(
            "numerical_scientific",
            "non_finite_state",
            affected_operation="tick",
            trial_ref={"trial_id": "t", "trial_revision": 1},
            tick=3,
            containment_result="hold",
            permitted_recovery_actions=["restart_trial"],
            diagnostic_sequence=1,
        )
        self.assertEqual(
            set(report),
            {
                "layer",
                "reason",
                "affected_operation",
                "trial_ref",
                "tick",
                "diagnostic_sequence",
                "containment_result",
                "permitted_recovery_actions",
            },
        )

    def test_writer_slot_binds_one_writer(self):
        slot = ScientificWriterSlot()
        first = HeadlessBallWriter()
        second = HeadlessBallWriter()
        slot.bind("trial-a", first, "config-v1")
        self.assertEqual(
            reason_code(self, slot.bind, "trial-a", second, "config-v1"),
            "writer_already_bound",
        )
        slot.bind("trial-a", first, "config-v1")
        self.assertIs(slot.writer_for("trial-a"), first)
        kernel, runtime = make_pair()
        start_trial(kernel, runtime)
        self.assertEqual(
            reason_code(self, runtime.prepare_trial, "trial-a"),
            "trial_already_prepared",
        )


if __name__ == "__main__":
    unittest.main()
