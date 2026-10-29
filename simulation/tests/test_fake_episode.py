from copy import deepcopy
import unittest

from simulation.fake_episode import BEHAVIOUR_FACES, FakeDiscreteEpisode


class FakeDiscreteEpisodeTest(unittest.TestCase):
    def test_non_product_discrete_contract_walk_and_derivation(self):
        episode = FakeDiscreteEpisode()
        self.assertEqual(
            {"status": "runnable", "reasons": []},
            episode.load_gate(["fake-discrete-asset"]),
        )
        self.assertEqual(11, len(BEHAVIOUR_FACES))
        kernel = episode.create_kernel()
        guide = episode.attach_guide(kernel)
        kernel.create_trial("fake-a", "discrete-count", {"count": 3})
        kernel.start_trial(episode.measurement("fake-measurement-a", 3))
        self.assertEqual("measured", kernel.current.state["phase"])
        self.assertEqual(2, kernel.current.state["trial_revision"])
        source_ref = kernel.current.ref.to_dict()
        ref = kernel.current.ref.to_dict()
        guide.record_fact("fake-action", "discrete-action", ref)
        guide.record_fact("fake-state", "discrete-state", ref)
        self.assertEqual("complete", guide.stage_id)
        source_ledger = deepcopy(kernel.current.ledger)
        kernel.derive_trial("fake-b", source_ref, "count", 4)
        kernel.accept_trial("fake-b")
        kernel.start_trial(episode.measurement("fake-measurement-b", 4))
        snapshot = kernel.snapshot()
        self.assertEqual(4, snapshot["measurements"][-1]["value"])
        self.assertEqual(source_ledger, kernel.trials["fake-a"].ledger)
        self.assertEqual("fake_discrete", snapshot["episode_extension"]["namespace"])
        self.assertNotIn("count", snapshot["configuration"])
        self.assertEqual([], episode.unload())

    def test_load_gate_fails_closed(self):
        episode = FakeDiscreteEpisode()
        verdict = episode.load_gate([])
        self.assertEqual("not_runnable", verdict["status"])
        self.assertIn("critical_asset_missing", verdict["reasons"])
        with self.assertRaises(RuntimeError):
            episode.create_kernel()


if __name__ == "__main__":
    unittest.main()
