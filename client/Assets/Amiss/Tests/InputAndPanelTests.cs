using System.Linq;
using Amiss.Shell;
using Amiss.Shell.Input;
using Amiss.Shell.UI;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    public class NormalizedInputTests
    {
        static InputLimitProfile FixtureProfile() => new(
            "quest-fixture-unmeasured", true, 2, 2, 0.75f, 20f, 10f,
            4f, 1f, 2, 2);

        public static RawInputSample Sample(
            InputModality modality = InputModality.Hand,
            long sequence = 1,
            int sourceTick = 1,
            int targetTick = 1,
            float speed = 0f,
            float confidence = 1f,
            bool systemGesture = false) => new(
            modality, InputSide.Right, sequence, sourceTick, targetTick,
            new Pose(new Vector3(0f, 1f, 0.2f), Quaternion.identity),
            new Vector3(0f, 0f, speed), Vector3.zero, confidence, systemGesture);

        [Test]
        public void UnmeasuredProfileFailsPhysicalInputClosed()
        {
            var result = new InputNormalizer(InputLimitProfile.Unmeasured()).Normalize(Sample());
            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(InvalidInputReason.ProfileNotLocked, result.InvalidReason);
        }

        [Test]
        public void UnmeasuredProfileStillRejectsTheCandidateAcceptedSample()
        {
            var sample = Sample(sourceTick: 1, targetTick: 3, speed: 3f);
            Assert.IsTrue(new InputNormalizer(InputLimitProfile.CandidateUnmeasured())
                .Normalize(sample).Accepted, "the candidate accepts this sample");
            var unmeasured = new InputNormalizer(InputLimitProfile.Unmeasured())
                .Normalize(sample);
            Assert.IsFalse(unmeasured.Accepted);
            Assert.AreEqual(InvalidInputReason.ProfileNotLocked, unmeasured.InvalidReason);
        }

        [Test]
        public void CandidateProfileIsTheDeclaredUncalibratedHackathonProfile()
        {
            var profile = InputLimitProfile.CandidateUnmeasured();
            Assert.AreEqual("quest-pro-candidate-unmeasured", profile.ProfileVersion);
            Assert.IsTrue(profile.Locked, "the candidate is locked so samples are admissible");
            Assert.AreEqual(2, profile.MaxSampleAgeTicks);
            Assert.AreEqual(4, profile.MaxSamplesPerTick);
            Assert.AreEqual(0.5f, profile.MinimumConfidence);
            Assert.AreEqual(40f, profile.MaximumLinearSpeed);
            Assert.AreEqual(20f, profile.MaximumAngularSpeed);
            Assert.AreEqual(3f, profile.SwingEnterSpeed);
            Assert.AreEqual(1f, profile.SwingExitSpeed);
            Assert.AreEqual(1, profile.EnterTicks);
            Assert.AreEqual(1, profile.ExitTicks);
        }

        [Test]
        public void InstalledInputProfileIsTheDeclaredCandidateBeforeAndAfterStart()
        {
            var host = new GameObject("CandidateInputAdapter");
            try
            {
                var adapter = host.AddComponent<OvrToolInputAdapter>();
                Assert.AreEqual("quest-pro-candidate-unmeasured",
                    adapter.Profile.ProfileVersion,
                    "the composition root's Awake sees the candidate");

                typeof(OvrToolInputAdapter)
                    .GetMethod("Start",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic)
                    .Invoke(adapter, null);
                Assert.AreEqual("quest-pro-candidate-unmeasured",
                    adapter.Profile.ProfileVersion,
                    "Start installs the same declared candidate");
                Assert.IsTrue(adapter.Profile.Locked);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void CandidateProfileAcceptsAnInBoundsFiniteSample()
        {
            var result = new InputNormalizer(InputLimitProfile.CandidateUnmeasured())
                .Normalize(Sample(sourceTick: 1, targetTick: 3, speed: 3f));
            Assert.IsTrue(result.Accepted, result.InvalidReason.ToString());
            Assert.AreEqual(InvalidInputReason.None, result.InvalidReason);
            Assert.AreEqual(3, result.Command.TargetTick);
            Assert.AreEqual(InputSide.Right, result.Command.Side);
            Assert.AreEqual(new Vector3(0f, 0f, 3f), result.Command.LinearVelocity);
        }

        [Test]
        public void CandidateProfileRejectsValuesJustOutsideTheAdoptedSet()
        {
            var profile = InputLimitProfile.CandidateUnmeasured();
            var sample = Sample(sourceTick: 1, targetTick: 1, speed: 0f);
            var nonFinite = new RawInputSample(sample.Modality, sample.Side, sample.Sequence,
                sample.SourceTick, sample.TargetTick, sample.Pose,
                new Vector3(float.NaN, 0f, 0f), Vector3.zero, 1f);

            Assert.AreEqual(InvalidInputReason.NonFinite,
                new InputNormalizer(profile).Normalize(nonFinite).InvalidReason,
                "NaN velocity");

            Assert.AreEqual(InvalidInputReason.Stale,
                new InputNormalizer(profile)
                    .Normalize(Sample(sourceTick: 0, targetTick: 3)).InvalidReason,
                "sample age 3 ticks > adopted 2");

            Assert.AreEqual(InvalidInputReason.OverSpeed,
                new InputNormalizer(profile).Normalize(Sample(speed: 40.5f)).InvalidReason,
                "linear speed 40.5 m/s > adopted 40");

            Assert.AreEqual(InvalidInputReason.LowConfidence,
                new InputNormalizer(profile).Normalize(Sample(confidence: 0.49f)).InvalidReason,
                "confidence 0.49 < adopted 0.5");

            var overRate = new InputNormalizer(profile);
            for (var sequence = 1; sequence <= 4; sequence++)
                Assert.IsTrue(overRate.Normalize(Sample(
                    sequence: sequence, sourceTick: 1, targetTick: 1)).Accepted,
                    "adopted rate admits " + sequence + " samples at one source tick");
            Assert.AreEqual(InvalidInputReason.OverRate,
                overRate.Normalize(Sample(sequence: 5, sourceTick: 1, targetTick: 1))
                    .InvalidReason,
                "a fifth sample at one source tick > adopted 4");
        }

        [Test]
        public void InvalidSamplesCarryStableReasonsAndNoCommand()
        {
            var nonFinite = Sample();
            nonFinite = new RawInputSample(nonFinite.Modality, nonFinite.Side, nonFinite.Sequence,
                nonFinite.SourceTick, nonFinite.TargetTick, nonFinite.Pose,
                new Vector3(float.NaN, 0f, 0f), Vector3.zero, 1f);
            var cases = new[]
            {
                (Sample(sourceTick: 0, targetTick: 4), InvalidInputReason.Stale),
                (nonFinite, InvalidInputReason.NonFinite),
                (Sample(confidence: 0.25f), InvalidInputReason.LowConfidence),
                (Sample(systemGesture: true), InvalidInputReason.SystemGesture),
                (Sample(speed: 30f), InvalidInputReason.OverSpeed)
            };
            foreach (var (sample, reason) in cases)
            {
                var result = new InputNormalizer(FixtureProfile()).Normalize(sample);
                Assert.IsFalse(result.Accepted, reason.ToString());
                Assert.AreEqual(reason, result.InvalidReason);
            }
        }

        [Test]
        public void DuplicateOutOfOrderAndOverRateAreRejected()
        {
            var normalizer = new InputNormalizer(FixtureProfile());
            Assert.IsTrue(normalizer.Normalize(Sample(sequence: 2)).Accepted);
            Assert.AreEqual(InvalidInputReason.Duplicate,
                normalizer.Normalize(Sample(sequence: 2)).InvalidReason);
            Assert.AreEqual(InvalidInputReason.OutOfOrder,
                normalizer.Normalize(Sample(sequence: 1)).InvalidReason);
            Assert.IsTrue(normalizer.Normalize(Sample(sequence: 3)).Accepted);
            Assert.AreEqual(InvalidInputReason.OverRate,
                normalizer.Normalize(Sample(sequence: 4)).InvalidReason);
        }

        [Test]
        public void HandAndControllerProduceIdenticalSemanticReplay()
        {
            var samples = Enumerable.Range(1, 6).Select(tick => Sample(
                sequence: tick, sourceTick: tick, targetTick: tick, speed: tick < 4 ? 5f : 0f));
            var hand = NormalizedInputReplay.Run(samples, FixtureProfile());
            var controller = NormalizedInputReplay.Run(samples.Select(sample =>
                new RawInputSample(InputModality.Controller, sample.Side, sample.Sequence,
                    sample.SourceTick, sample.TargetTick, sample.Pose, sample.LinearVelocity,
                    sample.AngularVelocity, sample.TrackingConfidence)), FixtureProfile());
            Assert.AreEqual(hand.Commands.Count, controller.Commands.Count);
            for (var i = 0; i < hand.Commands.Count; i++)
            {
                Assert.AreEqual(hand.Commands[i].TargetTick, controller.Commands[i].TargetTick);
                Assert.AreEqual(hand.Commands[i].Side, controller.Commands[i].Side);
                Assert.AreEqual(hand.Commands[i].Pose, controller.Commands[i].Pose);
                Assert.AreEqual(hand.Commands[i].LinearVelocity, controller.Commands[i].LinearVelocity);
                Assert.AreEqual(hand.Commands[i].AngularVelocity, controller.Commands[i].AngularVelocity);
            }
            Assert.AreEqual(hand.Transitions.Select(t => t.Current),
                controller.Transitions.Select(t => t.Current));
            Assert.AreEqual(0, hand.Diagnostics.TotalInvalid);
            Assert.AreEqual(0, controller.Diagnostics.TotalInvalid);
        }

        [Test]
        public void SwingEntryAndExitRequireDeclaredConsecutiveTicks()
        {
            var replay = NormalizedInputReplay.Run(Enumerable.Range(1, 5).Select(tick =>
                Sample(sequence: tick, sourceTick: tick, targetTick: tick,
                    speed: tick <= 2 ? 5f : 0f)), FixtureProfile());
            Assert.AreEqual(SwingActivity.Candidate, replay.Transitions[0].Current);
            Assert.AreEqual(SwingActivity.Active, replay.Transitions[1].Current);
            Assert.AreEqual(SwingActivity.Exiting, replay.Transitions[2].Current);
            Assert.AreEqual(SwingActivity.Inactive, replay.Transitions[3].Current);
            Assert.IsFalse(replay.Transitions.Any(t => t.InvalidClosure));
        }

        [Test]
        public void PhysicalSourcesSelectOneBestInputPerSide()
        {
            var sources = new[]
            {
                (Id: 1, Side: InputSide.Left, Quality: 0),
                (Id: 2, Side: InputSide.Left, Quality: 3),
                (Id: 3, Side: InputSide.Left, Quality: 1),
                (Id: 4, Side: InputSide.Right, Quality: 2),
                (Id: 5, Side: InputSide.Unknown, Quality: 3)
            };
            var selected = InputSourceSelector.SelectBest(sources,
                source => source.Side, source => source.Quality);
            Assert.AreEqual(2, selected.Count);
            Assert.AreEqual(2, selected[InputSide.Left].Id);
            Assert.AreEqual(4, selected[InputSide.Right].Id);
        }

        [Test]
        public void TrackingLossAbortsWithoutWorkOrSyntheticRelease()
        {
            var valid = Sample(sequence: 1, sourceTick: 1, targetTick: 1, speed: 5f);
            var invalid = Sample(sequence: 2, sourceTick: 2, targetTick: 2, confidence: 0f);
            var replay = NormalizedInputReplay.Run(new[] { valid, invalid }, FixtureProfile());
            Assert.AreEqual(1, replay.Commands.Count);
            Assert.AreEqual(1, replay.Diagnostics.Count(InvalidInputReason.LowConfidence));
            Assert.IsTrue(replay.Transitions[1].InvalidClosure);
            Assert.AreEqual(SwingActivity.Inactive, replay.Transitions[1].Current);
        }
    }

    public class InputEvidenceTests
    {
        [Test]
        public void EvidenceContainsBoundedAggregatesWithoutRawTracking()
        {
            var accumulator = new InputEvidenceAccumulator();
            accumulator.Begin(10f);
            var sample = NormalizedInputTests.Sample();
            accumulator.Observe(sample, new InputNormalizationResult(true, InvalidInputReason.None), 11f);
            accumulator.Observe(sample, new InputNormalizationResult(false, InvalidInputReason.LowConfidence), 12f);
            var entry = accumulator.Complete("tracking\nloss", "observed", "quest_diagnostic",
                InputLimitProfile.Unmeasured(), 15f);
            var manifest = InputEvidenceManifest.Create();
            manifest.entries.Add(entry);
            var json = manifest.ToJson();

            Assert.AreEqual("tracking loss", entry.scenario_id);
            Assert.AreEqual(2, entry.channels[0].observed_samples);
            Assert.AreEqual(1, entry.channels[0].accepted_samples);
            Assert.AreEqual(1, entry.invalid_reasons[0].count);
            Assert.AreEqual(5f, entry.elapsed_seconds);
            Assert.AreEqual(1f, entry.seconds_to_first_accepted);
            Assert.IsFalse(json.Contains("position"));
            Assert.IsFalse(json.Contains("rotation"));
            Assert.IsFalse(json.Contains("sequence"));
            Assert.IsFalse(json.Contains("device_id"));
            Assert.IsFalse(json.Contains("trajectory"));
        }

        [Test]
        public void EvidenceTracksAgeRateSpeedAndFirstAcceptedTime()
        {
            var accumulator = new InputEvidenceAccumulator();
            accumulator.Begin(10f);
            var first = NormalizedInputTests.Sample(sequence: 1, sourceTick: 7, targetTick: 9, speed: 3f);
            var second = NormalizedInputTests.Sample(sequence: 2, sourceTick: 7, targetTick: 9, speed: 5f);
            accumulator.Observe(first, new InputNormalizationResult(false, InvalidInputReason.Stale), 11f);
            accumulator.Observe(second, new InputNormalizationResult(true, InvalidInputReason.None), 12.5f);
            var entry = accumulator.Complete("rates", "observed", "quest_diagnostic",
                InputLimitProfile.Unmeasured(), 15f);
            var channel = entry.channels[0];

            Assert.AreEqual(2, channel.maximum_sample_age_ticks);
            Assert.AreEqual(2, channel.maximum_samples_per_source_tick);
            Assert.AreEqual(4f, channel.mean_linear_speed);
            Assert.AreEqual(5f, channel.maximum_linear_speed);
            Assert.AreEqual(2.5f, entry.seconds_to_first_accepted);
        }
    }

    public class CorePanelControllerTests
    {
        static CorePanelController Build(out UnboundSimulationRuntime runtime)
        {
            runtime = new UnboundSimulationRuntime();
            return new CorePanelController(runtime, new UnboundGuideRunner(), null);
        }

        [Test]
        public void ChartSelectionIsClosedToFourFixedSlots()
        {
            var panel = Build(out _);
            CollectionAssert.AreEquivalent(new[] { "chart.1", "chart.2", "chart.3", "chart.4" },
                CorePanelController.ChartSlots);
            Assert.IsFalse(panel.Apply(new PanelCommand(PanelCommandKind.SelectChart, "chart.5")));
            Assert.AreEqual("chart.1", panel.ActiveChartSlot);
            Assert.IsTrue(panel.Apply(new PanelCommand(PanelCommandKind.SelectChart, "chart.4")));
            Assert.AreEqual("chart.4", panel.ActiveChartSlot);
        }

        [Test]
        public void AudioTipsAndSafetyControlsAreIndependent()
        {
            var panel = Build(out _);
            Assert.IsTrue(panel.Apply(new PanelCommand(PanelCommandKind.ToggleAudio)));
            Assert.IsTrue(panel.AudioMuted);
            Assert.IsFalse(panel.TipsOff);
            Assert.IsTrue(panel.Apply(new PanelCommand(PanelCommandKind.ToggleTips)));
            Assert.IsTrue(panel.TipsOff);
            Assert.IsTrue(panel.AudioMuted);
        }

        [Test]
        public void UnboundMicAndGuideControlsFailClosed()
        {
            var panel = Build(out _);
            Assert.IsFalse(panel.MicrophoneAvailable);
            Assert.IsFalse(panel.Apply(new PanelCommand(PanelCommandKind.ToggleMicrophone)));
            Assert.IsFalse(panel.Apply(new PanelCommand(PanelCommandKind.Skip)));
            Assert.IsFalse(panel.Apply(new PanelCommand(PanelCommandKind.Replay)));
            Assert.IsFalse(panel.Apply(new PanelCommand(PanelCommandKind.FreeExplore)));
        }

        [Test]
        public void PauseAndResumeUseOnlyTheRuntimePort()
        {
            var panel = Build(out var runtime);
            Assert.IsTrue(panel.Apply(new PanelCommand(PanelCommandKind.Pause)));
            Assert.IsTrue(runtime.PauseRequested);
            Assert.AreEqual("panel_local", runtime.PauseReason);
            Assert.IsTrue(panel.Apply(new PanelCommand(PanelCommandKind.Resume)));
            Assert.IsFalse(runtime.PauseRequested);
        }
    }
}
