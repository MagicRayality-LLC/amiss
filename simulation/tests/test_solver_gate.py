import math
import unittest

from simulation.solver_gate import (
    GATE_ENERGY_RELATIVE_TOLERANCE,
    GateClassification,
    classify_gate,
    energy_relative_envelope,
    kinetic_energy_relative_change,
    momentum_delta,
    momentum_scale,
    normal_restitution,
    restitution_residual,
)


def energy_series(e_rel, *, initial=100.0, samples=1201, worst_index=None):
    """A flat series whose worst deviation is exactly ``e_rel``."""

    series = [initial] * samples
    index = samples - 1 if worst_index is None else worst_index
    series[index] = initial * (1.0 + e_rel)
    return series


def elasticity_case(residuals=(1e-7, 1e-7, 1e-7), contact=True):
    momentum, energy, restitution = residuals
    return {
        "contact_observed": contact,
        "momentum_residual_relative": momentum,
        "kinetic_energy_residual_relative": energy,
        "restitution_residual": restitution,
    }


def record(
    seed_id="seed-a",
    *,
    e_rel=2e-4,
    ticks_per_frame=1,
    ball_ball=180,
    ball_boundary=420,
    executed_ticks=1200,
    model_seconds=60.0,
    interrupted=False,
    energy_tolerance=GATE_ENERGY_RELATIVE_TOLERANCE,
    elasticity_tolerances=(1e-6, 1e-6, 1e-6),
    elasticity_residuals=(1e-7, 1e-7, 1e-7),
    elasticity_contact=True,
    elasticity=None,
    ball_count=72,
    effective_volume=9.0,
    reported_e_rel=None,
    worst_tick=None,
    series=None,
):
    series = energy_series(e_rel) if series is None else series
    computed, computed_worst = energy_relative_envelope(series)
    tolerance_keys = ("momentum_relative", "kinetic_energy_relative", "restitution_residual")
    declared = all(value is not None for value in elasticity_tolerances)
    tolerances = {
        "energy_relative": energy_tolerance,
        "elasticity_tolerance_declared": declared,
    }
    for key, value in zip(tolerance_keys, elasticity_tolerances):
        tolerances[f"elasticity_{key}"] = -1.0 if value is None else value
    return {
        "record_version": "1.0.0",
        "run_id": f"{seed_id}-{ticks_per_frame}tpf",
        "build": {
            "application_version": "0.1.0",
            "unity_version": "6000.6.5f1",
            "development_build": True,
        },
        "writer_configuration_id": "unity-rigidbody-frozen-v1",
        "writer_configuration": [f"key_{index}=value" for index in range(35)],
        "fixture": {
            "label": "design_table_E-07-01_9m3_72_ball_case",
            "effective_volume_m3": effective_volume,
            "ball_count": ball_count,
            "sizing_computed_n": ball_count,
        },
        "seed": {"seed_id": seed_id, "trial_id": f"solver-gate-{seed_id}"},
        "schedule": {
            "fixed_delta_time_s": 0.05,
            "ticks_per_frame": ticks_per_frame,
            "executed_ticks": executed_ticks,
            "model_seconds": model_seconds,
            "interrupted": interrupted,
        },
        "energy": {
            "e_rel": computed if reported_e_rel is None else reported_e_rel,
            "worst_tick": computed_worst if worst_tick is None else worst_tick,
            "series_kt_j": series,
        },
        "collisions": {
            "ball_ball_samples": ball_ball,
            "ball_boundary_samples": ball_boundary,
        },
        "elasticity": elasticity
        if elasticity is not None
        else {
            "two_ball_head_on": elasticity_case(elasticity_residuals, elasticity_contact),
            "two_ball_oblique": elasticity_case(elasticity_residuals, elasticity_contact),
            "static_boundary_rebound": elasticity_case(
                elasticity_residuals, elasticity_contact
            ),
        },
        "tolerances": tolerances,
    }


def three_seed_records(**overrides):
    return [
        record("seed-a", **overrides),
        record("seed-b", **overrides),
        record("seed-c", **overrides),
        record("seed-a", ticks_per_frame=2, **overrides),
    ]


class SolverGateMetricTests(unittest.TestCase):
    def test_energy_envelope_is_the_worst_relative_deviation(self):
        series = [200.0, 200.0, 199.9, 200.2, 200.0]
        worst, index = energy_relative_envelope(series)
        self.assertAlmostEqual(worst, 0.001, places=12)
        self.assertEqual(index, 3)

    def test_energy_envelope_refuses_a_degenerate_series(self):
        with self.assertRaises(ValueError):
            energy_relative_envelope([])
        with self.assertRaises(ValueError):
            energy_relative_envelope([0.0, 1.0])
        with self.assertRaises(ValueError):
            energy_relative_envelope([1.0, float("nan")])

    def test_momentum_delta_is_zero_for_a_head_on_swap(self):
        before = [(1.0, (1.0, 0.0, 0.0)), (1.0, (-1.0, 0.0, 0.0))]
        after = [(1.0, (-1.0, 0.0, 0.0)), (1.0, (1.0, 0.0, 0.0))]
        self.assertEqual(momentum_delta(before, after), 0.0)
        self.assertAlmostEqual(momentum_scale(before), 2.0, places=12)

    def test_momentum_delta_reports_a_lost_component(self):
        before = [(1.0, (1.0, 0.0, 0.0)), (1.0, (-1.0, 0.0, 0.0))]
        after = [(1.0, (-0.4, 0.0, 0.0)), (1.0, (-0.4, 0.0, 0.0))]
        self.assertAlmostEqual(momentum_delta(before, after), 0.8, places=12)

    def test_kinetic_energy_change_and_restitution_residual(self):
        before = [(1.0, (1.0, 0.0, 0.0)), (1.0, (-1.0, 0.0, 0.0))]
        after = [(1.0, (-1.0, 0.0, 0.0)), (1.0, (1.0, 0.0, 0.0))]
        self.assertEqual(kinetic_energy_relative_change(before, after), 0.0)
        self.assertEqual(normal_restitution(-2.0, 2.0), 1.0)
        self.assertEqual(restitution_residual(1.0), 0.0)
        self.assertAlmostEqual(restitution_residual(0.95), 0.05, places=12)
        with self.assertRaises(ValueError):
            normal_restitution(0.0, 1.0)


class SolverGateClassificationTests(unittest.TestCase):
    def test_three_seeds_and_two_cadences_pass(self):
        classification = classify_gate(three_seed_records())
        self.assertIsInstance(classification, GateClassification)
        self.assertEqual(classification.status, "pass")
        self.assertTrue(classification.passed)
        self.assertEqual(classification.failures, ())
        self.assertEqual(classification.incomplete, ())
        self.assertEqual(classification.seeds, ("seed-a", "seed-b", "seed-c"))
        self.assertEqual(classification.render_rate_delta, 0.0)
        self.assertEqual(len(classification.energy_relative), 4)

    def test_energy_envelope_over_the_locked_tolerance_fails(self):
        classification = classify_gate(three_seed_records(e_rel=4e-3))
        self.assertEqual(classification.status, "fail")
        self.assertTrue(any("energy_envelope_exceeded" in reason
                            for reason in classification.failures))

    def test_energy_envelope_at_the_tolerance_passes(self):
        classification = classify_gate(three_seed_records(e_rel=1e-3))
        self.assertEqual(classification.status, "pass")

    def test_missing_ball_ball_samples_is_incomplete_never_a_pass(self):
        classification = classify_gate(three_seed_records(ball_ball=0))
        self.assertEqual(classification.status, "incomplete")
        self.assertFalse(classification.passed)
        self.assertTrue(any("missing_collision_samples:ball_ball" in reason
                            for reason in classification.incomplete))

    def test_missing_ball_boundary_samples_is_incomplete(self):
        classification = classify_gate(three_seed_records(ball_boundary=0))
        self.assertEqual(classification.status, "incomplete")
        self.assertTrue(any("missing_collision_samples:ball_boundary" in reason
                            for reason in classification.incomplete))

    def test_undeclared_elasticity_tolerance_is_incomplete(self):
        classification = classify_gate(three_seed_records(
            elasticity_tolerances=(None, None, None)))
        self.assertEqual(classification.status, "incomplete")
        self.assertTrue(any("elasticity_tolerance_undeclared" in reason
                            for reason in classification.incomplete))

    def test_declared_elasticity_tolerance_exceeded_fails(self):
        classification = classify_gate(three_seed_records(
            elasticity_residuals=(0.2, 0.0, 0.3)))
        self.assertEqual(classification.status, "fail")
        self.assertTrue(any("elasticity_exceeded:two_ball_head_on:momentum_relative"
                            in reason for reason in classification.failures))

    def test_elasticity_without_a_contact_is_incomplete(self):
        classification = classify_gate(three_seed_records(elasticity_contact=False))
        self.assertEqual(classification.status, "incomplete")
        self.assertTrue(any("elasticity_no_contact" in reason
                            for reason in classification.incomplete))

    def test_weakened_energy_tolerance_never_passes(self):
        classification = classify_gate(three_seed_records(energy_tolerance=0.01))
        self.assertEqual(classification.status, "incomplete")
        self.assertTrue(any("energy_tolerance_weakened" in reason
                            for reason in classification.incomplete))

    def test_fewer_than_three_seeds_is_incomplete(self):
        records = [record("seed-a"), record("seed-b")]
        classification = classify_gate(records)
        self.assertEqual(classification.status, "incomplete")
        self.assertTrue(any("fewer_than_3_seeds" in reason
                            for reason in classification.incomplete))

    def test_short_or_interrupted_run_is_incomplete(self):
        classification = classify_gate(three_seed_records(
            executed_ticks=600, model_seconds=30.0, interrupted=True))
        self.assertEqual(classification.status, "incomplete")
        self.assertTrue(any("run_did_not_complete_1200_ticks" in reason
                            for reason in classification.incomplete))
        self.assertTrue(any("run_interrupted" in reason
                            for reason in classification.incomplete))

    def test_missing_render_cadence_pair_is_incomplete(self):
        records = [record("seed-a"), record("seed-b"), record("seed-c")]
        classification = classify_gate(records)
        self.assertEqual(classification.status, "incomplete")
        self.assertIn("render_rate_comparison_missing", classification.incomplete)

    def test_render_cadence_changing_the_outcome_fails(self):
        records = three_seed_records()
        records[3] = record("seed-a", ticks_per_frame=2, e_rel=5e-4)
        classification = classify_gate(records)
        self.assertEqual(classification.status, "fail")
        self.assertTrue(any("render_rate_changed_outcome" in reason
                            for reason in classification.failures))

    def test_failure_outranks_missing_evidence(self):
        records = three_seed_records(e_rel=4e-3, ball_ball=0)
        classification = classify_gate(records)
        self.assertEqual(classification.status, "fail")
        self.assertTrue(classification.incomplete)

    def test_tampered_energy_report_is_incomplete(self):
        classification = classify_gate(three_seed_records(reported_e_rel=1e-5))
        self.assertEqual(classification.status, "incomplete")
        self.assertTrue(any("reported_energy_envelope_mismatch" in reason
                            for reason in classification.incomplete))

    def test_a_non_design_fixture_is_incomplete(self):
        classification = classify_gate(three_seed_records(ball_count=40))
        self.assertEqual(classification.status, "incomplete")
        self.assertTrue(any("fixture_not_the_72_ball_design_case" in reason
                            for reason in classification.incomplete))

    def test_no_records_is_incomplete(self):
        classification = classify_gate([])
        self.assertEqual(classification.status, "incomplete")
        self.assertIn("no_gate_records", classification.incomplete)

    def test_classification_is_json_ready(self):
        payload = classify_gate(three_seed_records()).as_dict()
        self.assertEqual(payload["status"], "pass")
        self.assertEqual(payload["seeds"], ["seed-a", "seed-b", "seed-c"])
        self.assertTrue(math.isclose(payload["energy_relative"][0]["e_rel"], 2e-4))


if __name__ == "__main__":
    unittest.main()
