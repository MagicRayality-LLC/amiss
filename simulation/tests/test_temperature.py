from dataclasses import fields, replace
import math
import unittest

from simulation.runtime import (
    DEFAULT_UNIT_CONVENTION,
    ModelDeterminismDeclaration,
    kinetic_energies,
)
from simulation.temperature import (
    UNCALIBRATED,
    EnergyContactEntry,
    MeasurementWindow,
    TemperatureEnergyLedger,
    TemperatureModelDeclaration,
    compute_space_sizing,
    measure_temperature,
)


def make_balls(count, *, masses=None, drift=(0.0, 0.0, 0.0), held=0):
    balls = {}
    for index in range(count):
        angle = index + 1.0
        balls[f"ball-{index:03d}"] = {
            "entity_id": f"ball-{index:03d}",
            "position_m": [float(index), 0.0, 0.0],
            "velocity_m_per_s": [
                drift[0] + math.sin(angle),
                drift[1] + math.cos(angle * 1.3),
                drift[2] + math.sin(angle * 0.7),
            ],
            "mass_kg": masses[index] if masses else 1.0,
            "radius_m": 0.05,
            "membership": "free",
        }
    for index in range(held):
        balls[f"held-{index:03d}"] = {
            "entity_id": f"held-{index:03d}",
            "position_m": [0.0, 0.0, 0.0],
            "velocity_m_per_s": [0.5, 0.0, 0.0],
            "mass_kg": 1.0,
            "radius_m": 0.05,
            "membership": "held",
        }
    return balls


class SpaceSizingTest(unittest.TestCase):
    def test_e_07_01_space_count_table(self):
        expected = {3.0: 36, 4.5: 36, 6.0: 48, 9.0: 72, 15.0: 72}
        for volume, count in expected.items():
            with self.subTest(volume=volume):
                sizing = compute_space_sizing(volume)
                self.assertEqual("valid", sizing.status)
                self.assertEqual(count, sizing.computed_n)
                self.assertEqual(volume, sizing.v_eff_m3)

    def test_below_minimum_and_invalid_volume_refuse(self):
        for volume in (0.0, 1.0, 2.999, -1.0):
            with self.subTest(volume=volume):
                sizing = compute_space_sizing(volume)
                self.assertEqual("refused", sizing.status)
                self.assertEqual("below_minimum_volume", sizing.reason_code)
                self.assertIsNone(sizing.computed_n)
                self.assertIsNone(sizing.counted_volume_m3)
                self.assertIsNone(sizing.actual_number_density_per_m3)
        invalid = compute_space_sizing(float("nan"))
        self.assertEqual("refused", invalid.status)
        self.assertEqual("invalid_volume", invalid.reason_code)
        self.assertEqual("valid", compute_space_sizing(3.0).status)

    def test_odd_rounded_count_moves_down_and_density_is_reported(self):
        # 8 * 5.375 = 43, an odd rounded result, so even() moves it down.
        sizing = compute_space_sizing(5.375)
        self.assertEqual(42, sizing.computed_n)
        self.assertAlmostEqual(42.0 / 5.375, sizing.actual_number_density_per_m3)
        self.assertNotAlmostEqual(
            8.0, sizing.actual_number_density_per_m3, places=3
        )

    def test_large_room_warning_and_virtual_subspace_option(self):
        warning = compute_space_sizing(15.0)
        self.assertTrue(warning.large_room_warning)
        self.assertFalse(warning.large_room_subspace_applied)
        self.assertEqual(15.0, warning.counted_volume_m3)
        self.assertEqual(72, warning.computed_n)

        reduced = compute_space_sizing(15.0, apply_large_room_subspace=True)
        self.assertTrue(reduced.large_room_subspace_applied)
        self.assertEqual(15.0, reduced.v_eff_m3)
        self.assertEqual(9.0, reduced.counted_volume_m3)
        self.assertEqual(72, reduced.computed_n)

        self.assertFalse(compute_space_sizing(11.99).large_room_warning)

    def test_opening_override_keeps_its_declared_small_count(self):
        sizing = compute_space_sizing(
            3.0, is_opening_override_n=True, opening_ball_count=1
        )
        self.assertEqual("valid", sizing.status)
        self.assertEqual(1, sizing.computed_n)
        self.assertTrue(sizing.is_opening_override_n)
        with self.assertRaises(ValueError):
            compute_space_sizing(3.0, is_opening_override_n=True)

    def test_model_parameters_are_declared_uncalibrated(self):
        model = TemperatureModelDeclaration()
        self.assertEqual(UNCALIBRATED, model.evidence_status)
        self.assertEqual("3N_free", model.dof_formula)
        self.assertEqual(3.0, model.v_min_m3)
        self.assertEqual(8.0, model.target_number_density_per_m3)
        self.assertEqual((36, 72), (model.minimum_ball_count, model.maximum_ball_count))
        self.assertEqual(12.0, model.large_room_warning_m3)


class TemperatureMeasurementTest(unittest.TestCase):
    def setUp(self):
        self.minimum = ModelDeterminismDeclaration().statistical_minimum_free_count
        self.window = MeasurementWindow(
            "window-1", start_tick=0, end_tick=10, duration_seconds=0.5, sample_count=11
        )

    def measure(self, balls, **kwargs):
        return measure_temperature(
            balls,
            measurement_id="measurement-1",
            window=kwargs.pop("window", self.window),
            statistical_minimum_free_count=self.minimum,
            **kwargs,
        )

    def test_centered_direct_sum_agrees_with_k_total(self):
        balls = make_balls(
            12, masses=[1.0] * 6 + [2.0] * 6, drift=(0.7, -0.2, 0.4)
        )
        energies = kinetic_energies(balls)
        measurement = self.measure(balls, initial_t_k=300.0)

        self.assertEqual("valid", measurement.status)
        self.assertGreater(energies["k_drift_j"], 0.0)
        self.assertAlmostEqual(
            energies["k_total_j"],
            energies["k_thermal_j"] + energies["k_drift_j"],
            places=12,
        )
        self.assertAlmostEqual(measurement.k_total_j, energies["k_total_j"], places=12)
        self.assertAlmostEqual(
            measurement.k_thermal_j, energies["k_thermal_j"], places=12
        )
        k_b = DEFAULT_UNIT_CONVENTION["scale_constants"]["effective_kB"]
        expected_t_k = 2.0 * measurement.k_thermal_j / (
            3.0 * measurement.n_free * k_b
        )
        self.assertAlmostEqual(expected_t_k, measurement.t_k, places=12)

    def test_zero_and_one_free_ball_are_not_applicable(self):
        for count in (0, 1):
            with self.subTest(free=count):
                measurement = self.measure(make_balls(count), initial_t_k=300.0)
                self.assertEqual("not_applicable", measurement.status)
                self.assertIsNone(measurement.t_k)
                self.assertIsNone(measurement.display_tuple)
                self.assertFalse(measurement.equilibrium_sufficient)

        held_only = self.measure(make_balls(0, held=1))
        self.assertEqual("not_applicable", held_only.status)
        self.assertEqual(0, held_only.n_free)
        self.assertEqual(1, held_only.n_held)
        self.assertIsNone(held_only.t_k)

    def test_display_tuple_derives_from_the_one_measurement(self):
        balls = make_balls(12)
        first = self.measure(balls, initial_t_k=300.0)
        self.assertEqual("valid", first.status)

        same = self.measure(balls, initial_t_k=first.t_k)
        baseline = first.t_k
        expected = {
            "kelvin": round(baseline),
            "celsius": round(baseline - 273.15),
            "fahrenheit": round((baseline - 273.15) * 9.0 / 5.0 + 32.0),
            "kelvin_ratio_baseline": round(baseline / baseline, 1),
        }
        self.assertEqual(
            {field.name for field in fields(same.display_tuple)},
            set(expected),
        )
        self.assertEqual(expected["kelvin"], same.display_tuple.kelvin)
        self.assertEqual(expected["celsius"], same.display_tuple.celsius)
        self.assertEqual(expected["fahrenheit"], same.display_tuple.fahrenheit)
        self.assertEqual(1.0, same.display_tuple.kelvin_ratio_baseline)

        hotter = self.measure(balls, initial_t_k=baseline / 2.0)
        self.assertEqual(2.0, hotter.display_tuple.kelvin_ratio_baseline)

    @staticmethod
    def scale_point_balls(speed):
        # Equal masses in opposite velocity pairs: the center of mass is zero,
        # so every speed is thermal and v_rms is the declared scale-point speed.
        directions = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))
        balls = {}
        for index in range(12):
            sign = 1.0 if index % 2 == 0 else -1.0
            direction = directions[(index // 2) % 3]
            balls[f"ball-scale-{index:02d}"] = {
                "entity_id": f"ball-scale-{index:02d}",
                "position_m": [0.0, 0.0, 0.0],
                "velocity_m_per_s": [sign * speed * axis for axis in direction],
                "mass_kg": 1.0,
                "radius_m": 0.05,
                "membership": "free",
            }
        return balls

    def test_scale_point_measures_300_k_at_1_1_m_per_s(self):
        # 07 section 3.2 prototype scale point: an equal-mass free set at
        # v_rms = 1.1 m/s measures 300 K. Doubling v_rms quadruples T.
        for speed, expected in ((1.1, 300.0), (2.2, 1200.0)):
            with self.subTest(v_rms=speed):
                measurement = self.measure(
                    self.scale_point_balls(speed), initial_t_k=300.0
                )
                self.assertEqual("valid", measurement.status)
                self.assertAlmostEqual(speed, measurement.v_rms_m_per_s, places=12)
                self.assertAlmostEqual(expected, measurement.t_k, places=9)

    def test_a_single_frame_cannot_claim_equilibrium(self):
        single = MeasurementWindow(
            "window-single", start_tick=4, end_tick=4, duration_seconds=0.05, sample_count=1
        )
        measurement = self.measure(
            make_balls(12),
            window=single,
            initial_t_k=300.0,
            equilibrium_sufficient=True,
        )
        self.assertFalse(measurement.equilibrium_sufficient)

    def test_a_valid_measurement_requires_its_initial_baseline(self):
        with self.assertRaises(ValueError):
            self.measure(make_balls(12))


class EnergyLedgerTest(unittest.TestCase):
    def setUp(self):
        self.ledger = TemperatureEnergyLedger(
            initial_total_ke_j=10.0,
            free_total_ke_j=9.0,
            k_thermal_j=8.0,
            k_drift_j=1.0,
            k_held_j=0.25,
            e_sandbag_j=0.5,
        )

    def test_signed_bat_work_entries_and_accounts(self):
        heating = EnergyContactEntry(
            "contact-1", "bat", "ball-001", 0, 2, 2.5, "separated"
        )
        cooling = EnergyContactEntry(
            "contact-2", "bat", "ball-002", 3, 4, -0.75, "separated"
        )
        updated = self.ledger.with_contact(heating).with_contact(cooling)

        self.assertEqual(1.75, updated.work_events_cumulative_ke_j)
        self.assertEqual(0.0, self.ledger.work_events_cumulative_ke_j)
        self.assertEqual(9.0, updated.k_total_j)
        self.assertEqual(9.75, updated.e_accounted_j)
        self.assertEqual(0.0, updated.closure_residual_j)

    def test_sandbag_account_and_setup_difference_are_not_work_events(self):
        derived = replace(self.ledger, setup_energy_diff_from_source_j=1.25)
        self.assertEqual(0.0, derived.work_events_cumulative_ke_j)
        self.assertEqual(0.5, derived.e_sandbag_j)
        self.assertNotIn(
            0.5, [entry.work_j for entry in derived.contacts]
        )
        self.assertNotIn(1.25, [entry.work_j for entry in derived.contacts])


if __name__ == "__main__":
    unittest.main()
