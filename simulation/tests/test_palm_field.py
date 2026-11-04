"""Palm cooling field: hourglass optical-trap damping between two palms."""
import math
import unittest

from simulation.runtime import (
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
    def test_ball_on_axis_slows_by_exp_beta_dt(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(writer)
        writer.set_palm_field(
            [1.0, 2.0, 2.0], [3.0, 2.0, 2.0],
            waist_radius_m=0.02, mouth_radius_m=0.07, beta_per_s=2.0,
        )
        delta = 0.05
        facts = writer.advance(state, delta, collision, streams, [], 4096)
        ball = state["balls"]["ball-0001"]
        expected = 1.0 * math.exp(-2.0 * delta)
        self.assertAlmostEqual(ball["velocity_m_per_s"][0], expected, places=12)
        self.assertEqual(facts.get("palm_cooled_count"), 1)
        cooling = [e for e in facts["applied_effects"] if e["effect_kind"] == "palm_cooling"]
        self.assertEqual(len(cooling), 1)
        self.assertLess(cooling[0]["energy_delta_j"], 0.0)

    def test_ball_outside_tube_unaffected(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(
            writer, position=[2.0, 3.5, 2.0], velocity=[1.0, 0.0, 0.0])
        writer.set_palm_field(
            [1.0, 2.0, 2.0], [3.0, 2.0, 2.0],
            waist_radius_m=0.02, mouth_radius_m=0.07, beta_per_s=2.0,
        )
        facts = writer.advance(state, 0.05, collision, streams, [], 4096)
        ball = state["balls"]["ball-0001"]
        # Only wall-free drift: x moves, velocity unchanged by cooling.
        self.assertAlmostEqual(ball["velocity_m_per_s"][0], 1.0, places=12)
        self.assertEqual(facts.get("palm_cooled_count", 0), 0)

    def test_clear_disables_cooling(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(writer)
        writer.set_palm_field(
            [1.0, 2.0, 2.0], [3.0, 2.0, 2.0],
            waist_radius_m=0.02, mouth_radius_m=0.07, beta_per_s=2.0,
        )
        writer.clear_palm_field()
        facts = writer.advance(state, 0.05, collision, streams, [], 4096)
        ball = state["balls"]["ball-0001"]
        self.assertAlmostEqual(ball["velocity_m_per_s"][0], 1.0, places=12)
        self.assertEqual(facts.get("palm_cooled_count", 0), 0)

    def test_repeated_ticks_cool_geometrically(self):
        writer = HeadlessBallWriter()
        state, collision, streams = make_state(writer)
        writer.set_palm_field(
            [0.5, 2.0, 2.0], [3.5, 2.0, 2.0],
            waist_radius_m=0.30, mouth_radius_m=0.30, beta_per_s=1.0,
        )
        # Wide tube so the ball stays inside while drifting; freeze position
        # effect by zeroing velocity direction change: only magnitude matters.
        speeds = []
        for _ in range(4):
            # Re-centre so the ball never leaves the wide tube.
            ball = state["balls"]["ball-0001"]
            ball["position_m"] = [2.0, 2.0, 2.0]
            writer.advance(state, 0.05, collision, streams, [], 4096)
            speeds.append(math.sqrt(sum(v * v for v in ball["velocity_m_per_s"])))
        for earlier, later in zip(speeds, speeds[1:]):
            self.assertLess(later, earlier)
        self.assertAlmostEqual(speeds[-1], 1.0 * math.exp(-1.0 * 0.05 * 4), places=9)

    def test_invalid_field_rejected(self):
        from simulation.runtime import RuntimeContractError
        writer = HeadlessBallWriter()
        with self.assertRaises(RuntimeContractError):
            writer.set_palm_field(
                [0.0, 0.0, 0.0], [0.0, 0.0, 0.0],
                waist_radius_m=0.02, mouth_radius_m=0.07, beta_per_s=1.0,
            )
        with self.assertRaises(RuntimeContractError):
            writer.set_palm_field(
                [0.0, 0.0, 0.0], [1.0, 0.0, 0.0],
                waist_radius_m=-0.01, mouth_radius_m=0.07, beta_per_s=1.0,
            )


if __name__ == "__main__":
    unittest.main()
