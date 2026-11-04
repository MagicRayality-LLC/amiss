"""Palm field: one-unit pass debit and slow-ball capture between two palms."""
import math
import unittest

from simulation.runtime import (
    PALM_PASS_ENERGY_DELTA_J,
    HeadlessBallWriter,
    NamedRandomStreams,
    box_representation,
)


def make_state(writer, ball_id="ball-0001", position=None, velocity=None):
    collision = box_representation("room-1", {"x": 4.0, "y": 4.0, "z": 4.0})
    streams = NamedRandomStreams(7)
    state = writer.setup(
        streams,
        {"ball_count": 1, "ball_radius_m": 0.05, "initial_speed_scale": 0.0},
        collision,
    )
    ball = state["balls"][ball_id]
    ball["position_m"] = list(position or [2.0, 2.0, 2.0])
    ball["velocity_m_per_s"] = list(velocity or [1.0, 0.0, 0.0])
    return state, collision, streams


class PalmFieldTestCase(unittest.TestCase):
    def test_fast_ball_loses_one_unit_on_a_pass(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(writer)
        writer.set_palm_field(
            [1.0, 2.0, 2.0], [3.0, 2.0, 2.0],
            waist_radius_m=0.30, mouth_radius_m=0.30,
            pass_energy_delta_j=PALM_PASS_ENERGY_DELTA_J,
        )
        facts = writer.advance(state, 0.05, collision, streams, [], 4096)
        ball = state["balls"]["ball-0001"]
        expected = math.sqrt(1.0 - 2.0 * PALM_PASS_ENERGY_DELTA_J)
        self.assertAlmostEqual(ball["velocity_m_per_s"][0], expected, places=12)
        self.assertEqual(facts.get("palm_cooled_count"), 1)
        cooling = [e for e in facts["applied_effects"]
                   if e["effect_kind"] == "palm_cooling"]
        self.assertEqual(len(cooling), 1)
        self.assertAlmostEqual(
            cooling[0]["energy_delta_j"], -PALM_PASS_ENERGY_DELTA_J, places=12
        )

    def test_ball_outside_tube_unaffected(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(
            writer, position=[2.0, 3.5, 2.0], velocity=[1.0, 0.0, 0.0])
        writer.set_palm_field(
            [1.0, 2.0, 2.0], [3.0, 2.0, 2.0],
            waist_radius_m=0.02, mouth_radius_m=0.07,
        )
        facts = writer.advance(state, 0.05, collision, streams, [], 4096)
        ball = state["balls"]["ball-0001"]
        # Only wall-free drift: x moves, velocity unchanged by the field.
        self.assertAlmostEqual(ball["velocity_m_per_s"][0], 1.0, places=12)
        self.assertEqual(facts.get("palm_cooled_count", 0), 0)

    def test_clear_disables_the_field(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(writer)
        writer.set_palm_field(
            [1.0, 2.0, 2.0], [3.0, 2.0, 2.0],
            waist_radius_m=0.30, mouth_radius_m=0.30,
        )
        writer.clear_palm_field()
        facts = writer.advance(state, 0.05, collision, streams, [], 4096)
        ball = state["balls"]["ball-0001"]
        self.assertAlmostEqual(ball["velocity_m_per_s"][0], 1.0, places=12)
        self.assertEqual(facts.get("palm_cooled_count", 0), 0)

    def test_one_pass_removes_one_unit_even_when_staying_inside(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(writer)
        writer.set_palm_field(
            [0.5, 2.0, 2.0], [3.5, 2.0, 2.0],
            waist_radius_m=0.50, mouth_radius_m=0.50,
        )
        ball = state["balls"]["ball-0001"]
        writer.advance(state, 0.05, collision, streams, [], 4096)
        after_first = math.sqrt(sum(v * v for v in ball["velocity_m_per_s"]))
        self.assertAlmostEqual(
            after_first,
            math.sqrt(1.0 - 2.0 * PALM_PASS_ENERGY_DELTA_J),
            places=12,
        )
        for _ in range(3):
            writer.advance(state, 0.05, collision, streams, [], 4096)
            self.assertAlmostEqual(
                math.sqrt(sum(v * v for v in ball["velocity_m_per_s"])),
                after_first,
                places=12,
            )

    def test_a_new_pass_removes_another_unit(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(writer)
        writer.set_palm_field(
            [1.0, 2.0, 2.0], [3.0, 2.0, 2.0],
            waist_radius_m=0.30, mouth_radius_m=0.30,
        )
        ball = state["balls"]["ball-0001"]
        writer.advance(state, 0.05, collision, streams, [], 4096)
        first = math.sqrt(sum(v * v for v in ball["velocity_m_per_s"]))

        ball["position_m"] = [2.0, 3.5, 2.0]
        ball["velocity_m_per_s"] = [0.0, 0.0, 0.0]
        writer.advance(state, 0.05, collision, streams, [], 4096)
        ball["position_m"] = [2.0, 2.0, 2.0]
        ball["velocity_m_per_s"] = [1.0, 0.0, 0.0]
        writer.advance(state, 0.05, collision, streams, [], 4096)
        second = math.sqrt(sum(v * v for v in ball["velocity_m_per_s"]))
        self.assertAlmostEqual(second, first, places=12,
                               msg="a new pass loses the same one unit")

    def test_invalid_field_rejected(self):
        from simulation.runtime import RuntimeContractError
        writer = HeadlessBallWriter()
        with self.assertRaises(RuntimeContractError):
            writer.set_palm_field(
                [0.0, 0.0, 0.0], [0.0, 0.0, 0.0],
                waist_radius_m=0.02, mouth_radius_m=0.07,
            )
        with self.assertRaises(RuntimeContractError):
            writer.set_palm_field(
                [0.0, 0.0, 0.0], [1.0, 0.0, 0.0],
                waist_radius_m=-0.01, mouth_radius_m=0.07,
            )
        with self.assertRaises(RuntimeContractError):
            writer.set_palm_field(
                [0.0, 0.0, 0.0], [1.0, 0.0, 0.0],
                waist_radius_m=0.02, mouth_radius_m=0.07,
                pass_energy_delta_j=0.0,
            )
        with self.assertRaises(RuntimeContractError):
            writer.set_palm_field(
                [0.0, 0.0, 0.0], [1.0, 0.0, 0.0],
                waist_radius_m=0.02, mouth_radius_m=0.07,
                capture_speed_m_per_s=-0.1,
            )

    def test_slow_ball_is_trapped_at_the_field_centre(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(
            writer, velocity=[0.4, 0.0, 0.0])
        writer.set_palm_field(
            [1.0, 2.0, 2.0], [3.0, 2.0, 2.0],
            waist_radius_m=0.30, mouth_radius_m=0.30,
            capture_speed_m_per_s=0.5,
        )
        facts = writer.advance(state, 0.05, collision, streams, [], 4096)
        ball = state["balls"]["ball-0001"]
        self.assertEqual(ball["membership"], "held")
        self.assertEqual(ball["position_m"], [2.0, 2.0, 2.0])
        self.assertEqual(ball["velocity_m_per_s"], [0.4, 0.0, 0.0])
        self.assertEqual(facts.get("palm_trapped_count"), 1)
        self.assertEqual(facts.get("palm_cooled_count", 0), 0)

    def test_fast_ball_passes_through_instead_of_being_trapped(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(writer)
        writer.set_palm_field(
            [1.0, 2.0, 2.0], [3.0, 2.0, 2.0],
            waist_radius_m=0.30, mouth_radius_m=0.30,
            capture_speed_m_per_s=0.5,
        )
        facts = writer.advance(state, 0.05, collision, streams, [], 4096)
        ball = state["balls"]["ball-0001"]
        self.assertEqual(ball["membership"], "free")
        self.assertEqual(facts.get("palm_cooled_count"), 1)
        self.assertEqual(facts.get("palm_trapped_count", 0), 0)

    def test_clearing_the_field_releases_the_trapped_ball(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(
            writer, velocity=[0.4, 0.0, 0.0])
        writer.set_palm_field(
            [1.0, 2.0, 2.0], [3.0, 2.0, 2.0],
            waist_radius_m=0.30, mouth_radius_m=0.30,
            capture_speed_m_per_s=0.5,
        )
        writer.advance(state, 0.05, collision, streams, [], 4096)
        ball = state["balls"]["ball-0001"]
        self.assertEqual(ball["membership"], "held")

        writer.clear_palm_field()
        writer.advance(state, 0.05, collision, streams, [], 4096)
        self.assertEqual(ball["membership"], "free")
        self.assertAlmostEqual(
            math.sqrt(sum(v * v for v in ball["velocity_m_per_s"])),
            0.4,
            places=12,
        )


if __name__ == "__main__":
    unittest.main()
