using System;
using System.Collections.Generic;
using System.Linq;
using Amiss.Shell;
using Amiss.Shell.Episodes;
using Amiss.Shell.Input;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;
using Amiss.Shell.Simulation;
using Amiss.Shell.UI;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// Test-only fixtures for the bat-contact and readout slice.
    ///
    /// <see cref="TestOnlyProfile"/> is synthetic: it exists so the semantics of
    /// an accepted swing can be exercised edit-mode, and it is never installed
    /// in a build. The production install site uses
    /// <see cref="InputLimitProfile.CandidateUnmeasured"/>, one declared and
    /// explicitly uncalibrated candidate, so valid device samples reach the
    /// bat; <see cref="InputLimitProfile.Unmeasured"/> stays fail-closed.
    /// </summary>
    public static class BatContactFixtures
    {
        public const string TestOnlyProfileVersion =
            "test_only_synthetic_profile_not_measured";

        /// <summary>Synthetic swing limits; nothing here is a device measurement.</summary>
        public static InputLimitProfile TestOnlyProfile() => new(
            TestOnlyProfileVersion, true,
            maxSampleAgeTicks: 2, maxSamplesPerTick: 4, minimumConfidence: 0.5f,
            maximumLinearSpeed: 40f, maximumAngularSpeed: 20f,
            swingEnterSpeed: 3f, swingExitSpeed: 1f, enterTicks: 1, exitTicks: 1);

        public static RawInputSample Sample(InputModality modality = InputModality.Hand,
            long sequence = 1, int sourceTick = 1, int targetTick = 1,
            Vector3? position = null, Vector3? velocity = null, float confidence = 1f,
            bool systemGesture = false) => new(
            modality, InputSide.Right, sequence, sourceTick, targetTick,
            new Pose(position ?? new Vector3(0f, 1f, 0.2f), Quaternion.identity),
            velocity ?? Vector3.zero, Vector3.zero, confidence, systemGesture);

        public static InputNormalizationResult Accepted(Vector3 position, Vector3 velocity,
            int targetTick) => new(true, InvalidInputReason.None,
            new NormalizedToolCommand(targetTick, InputSide.Right,
                new Pose(position, Quaternion.identity), velocity, Vector3.zero));

        public static InputNormalizationResult Rejected(InvalidInputReason reason) =>
            new(false, reason);

        public static (RoomPreparation room, ShellRecovery recovery, CollisionPin pin)
            AcceptedRoom(string trialId, float sizeM = 1.6f, float swingRadiusM = 0.3f)
        {
            // The manual draft is built explicitly so the declared clear swing
            // volume fits the small fixture room; CreateManual's 0.9 m default
            // needs a room at least 1.8 m tall.
            var region = new Bounds(new Vector3(0f, sizeM * 0.5f, 0f),
                new Vector3(sizeM, sizeM, sizeM));
            var manualBase = RoomDraft.CreateManual(region, Vector3.zero, Vector3.forward);
            var manual = new RoomDraft(region, manualBase.Surfaces, Vector3.zero,
                Vector3.forward, swingRadiusM);
            var room = new RoomPreparation();
            var id = room.Begin(1.0);
            room.RequestPermission(id);
            room.CompletePermission(id, true);
            room.CompleteSceneLoad(id, manual);
            Assert.IsTrue(room.Confirm(id), room.Failure.ToString());
            var recovery = new ShellRecovery();
            Assert.IsTrue(recovery.ConfirmRoom(room));
            var pin = recovery.AcceptTrialSetup(trialId, room.Confirmation.CollisionChecksum);
            Assert.IsNotNull(pin);
            return (room, recovery, pin);
        }

        public static DeviceSimulationRuntime StartedTrial(string trialId,
            out TestSweepWriter writer)
        {
            var accepted = AcceptedRoom(trialId);
            var created = new TestSweepWriter();
            writer = created;
            var runtime = new DeviceSimulationRuntime(() => created);
            var outcome = runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                trialId, accepted.room.Confirmation, accepted.pin, out _));
            Assert.IsTrue(outcome.Started, outcome.ReasonCode);
            return runtime;
        }

        public static DeviceSimulationRuntime StartedProductTrial(string trialId)
        {
            var accepted = AcceptedRoom(trialId);
            var runtime = new DeviceSimulationRuntime(() => new CustomHardSphereWriter());
            var outcome = runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                trialId, accepted.room.Confirmation, accepted.pin, out _));
            Assert.IsTrue(outcome.Started, outcome.ReasonCode);
            return runtime;
        }
    }

    /// <summary>
    /// A test-only writer whose queued sweeps are applied by arithmetic this
    /// test controls: every reached ball receives the queued impulse and the
    /// measured energy is the literal pre/post difference. It is never bound by
    /// the product composition root.
    /// </summary>
    public sealed class TestSweepWriter : IScientificWriter, IContactFactWriter,
        ISweepEffectWriter
    {
        readonly List<AppliedSweepHit> applied = new();

        public string WriterId => "test-sweep-v1";
        public string ConfigurationId => "test-sweep-v1";
        public WriterDisposition Disposition => WriterDisposition.ProductTrial;
        public IReadOnlyList<string> ConfigurationRecord =>
            new[] { "writer_configuration_id=test-sweep-v1" };
        public BallState[] Balls;
        public int AdvanceCount;
        public ContactStepFacts LastContactFacts { get; private set; }

        /// <summary>
        /// Moves every ball far outside any fixture sweep so a test controls
        /// exactly which balls a swing reaches.
        /// </summary>
        public void IsolateBalls()
        {
            for (var index = 0; index < Balls.Length; index++)
            {
                Balls[index].PositionM = new Vector3(100f + index * 2f, 100f, 100f);
                Balls[index].VelocityMPerS = Vector3.zero;
            }
        }

        /// <summary>Places one ball, so a test can put it inside a sweep.</summary>
        public void PlaceBall(string entityId, Vector3 position, Vector3 velocity)
        {
            for (var index = 0; index < Balls.Length; index++)
            {
                if (Balls[index].EntityId != entityId)
                    continue;
                Balls[index].PositionM = position;
                Balls[index].VelocityMPerS = velocity;
                return;
            }
        }

        public void Setup(SimulationTrialSetup setup)
        {
            Balls = (BallState[])setup.Balls;
            LastContactFacts = new ContactStepFacts(0, 0, false);
        }

        public void Advance(int tick, float deltaTimeSeconds)
        {
            AdvanceCount++;
            for (var index = 0; index < Balls.Length; index++)
                Balls[index].PositionM += Balls[index].VelocityMPerS * deltaTimeSeconds;
            LastContactFacts = new ContactStepFacts(0, 0, false);
        }

        public void QueueSweep(string interactionId, Vector3 origin, Vector3 velocityMPerS,
            float sweptRadiusM, double impulseMagnitude)
        {
            applied.Clear();
            var sweep = velocityMPerS * FrozenWriterConfiguration.FixedDeltaTimeSeconds;
            var lengthSquared = Mathf.Max(1e-12f, sweep.sqrMagnitude);
            for (var index = 0; index < Balls.Length; index++)
            {
                var ball = Balls[index];
                if (ball.Membership != "free")
                    continue;
                var parameter = Mathf.Clamp01(
                    Vector3.Dot(ball.PositionM - origin, sweep) / lengthSquared);
                var closest = origin + sweep * parameter;
                var direction = ball.PositionM - closest;
                if (direction.magnitude > sweptRadiusM + ball.RadiusM)
                    continue;
                direction = direction.sqrMagnitude < 1e-12f
                    ? Vector3.right
                    : direction.normalized;
                var before = 0.5 * (double)ball.MassKg * ball.VelocityMPerS.sqrMagnitude;
                ball.VelocityMPerS += direction * (float)impulseMagnitude;
                var after = 0.5 * (double)ball.MassKg * ball.VelocityMPerS.sqrMagnitude;
                applied.Add(new AppliedSweepHit(interactionId, ball.EntityId, after - before));
            }
        }

        public IReadOnlyList<AppliedSweepHit> LastAppliedSweeps => applied;

        public void Teardown() => Balls = null;
    }

    public class BatToolContactTests
    {
        static (DeviceSimulationRuntime runtime, TestSweepWriter writer, BatTool bat)
            StartedTrial(string trialId)
        {
            var runtime = BatContactFixtures.StartedTrial(trialId, out var writer);
            return (runtime, writer, new BatTool(runtime, BatContactFixtures.TestOnlyProfile()));
        }

        [Test]
        public void UnmeasuredProfileStaysDeclaredAndFailClosed()
        {
            var profile = InputLimitProfile.Unmeasured();
            var bat = new BatTool(new NoTrialHost(), profile);
            Assert.AreEqual("declared-unmeasured", profile.ProfileVersion);
            Assert.IsFalse(profile.Locked);
            Assert.AreEqual(0f, profile.SwingEnterSpeed,
                "no numeric swing limit is invented for the production profile");

            var runtime = BatContactFixtures.StartedProductTrial("trial-unmeasured");
            var production = new BatTool(runtime, InputLimitProfile.Unmeasured());
            var sample = new InputNormalizer(InputLimitProfile.Unmeasured()).Normalize(
                BatContactFixtures.Sample(velocity: new Vector3(0f, 0f, 10f)));
            var result = production.Consume(sample, 1);

            Assert.AreEqual(BatSwingOutcome.NotAccepted, result.Outcome);
            Assert.AreEqual(InvalidInputReason.ProfileNotLocked.ToString(), result.ReasonCode);
            Assert.AreEqual(0, production.ContactCount);
            Assert.AreEqual(0, production.EmptySwingCount, "a rejected sample is not a swing");
            Assert.AreEqual("", production.OpenContactId);
            Assert.AreEqual(0, runtime.Readout.Ledger.Contacts.Count);
            Assert.AreEqual(0.0, runtime.Readout.Ledger.WorkEventsCumulativeKeJ);
            Assert.IsFalse(bat.Profile.Locked);
        }

        [Test]
        public void OneAcceptedContactIsOneSignedWorkEntryAndOneSemanticCommit()
        {
            var (runtime, writer, bat) = StartedTrial("trial-bat");
            var ball = runtime.CurrentView.Balls[0];
            writer.IsolateBalls();
            writer.PlaceBall(ball.EntityId, Vector3.zero, Vector3.zero);
            var revisionBefore = bat.Readout.ContactCommits;

            var swing = bat.Consume(BatContactFixtures.Accepted(
                Vector3.zero, new Vector3(4f, 0f, 0f), 1), 1);
            Assert.AreEqual(BatSwingOutcome.Submitted, swing.Outcome);
            runtime.Step(1);

            var closed = bat.Close();
            Assert.AreEqual(BatSwingOutcome.Contacted, closed.Outcome);
            Assert.IsTrue(closed.ProducedWork);

            var ledger = bat.Readout.Ledger;
            Assert.AreEqual(1, ledger.Contacts.Count, "exactly one contact record");
            var entry = ledger.Contacts[0];
            Assert.AreEqual(BatTool.PropId, entry.PropId);
            Assert.AreEqual(ball.EntityId, entry.TargetBallId);
            Assert.Greater(entry.WorkJ, 0.0);
            Assert.AreEqual(entry.WorkJ, ledger.WorkEventsCumulativeKeJ, 1e-12);
            Assert.AreEqual(revisionBefore + 1, bat.Readout.ContactCommits,
                "one contacted closure closes one semantic commit");
            Assert.IsTrue(closed.Closure.SemanticCommit);
            Assert.AreEqual(1, bat.ContactCount);
            Assert.AreEqual(0, bat.EmptySwingCount);
        }

        [Test]
        public void ANegativeStrikeIsRecordedAsNegativeWork()
        {
            var (runtime, writer, bat) = StartedTrial("trial-bat-cooling");
            var ball = runtime.CurrentView.Balls[0];
            // The ball arrives against the impulse direction, so the measured
            // strike removes energy and the ledger keeps the sign.
            writer.IsolateBalls();
            writer.PlaceBall(ball.EntityId, Vector3.zero, new Vector3(-4f, 0f, 0f));

            bat.Consume(BatContactFixtures.Accepted(
                Vector3.zero, new Vector3(4f, 0f, 0f), 1), 1);
            runtime.Step(1);
            var closed = bat.Close();

            Assert.AreEqual(BatSwingOutcome.Contacted, closed.Outcome);
            Assert.Less(closed.Closure.WorkJ, 0.0);
            Assert.Less(bat.Readout.Ledger.WorkEventsCumulativeKeJ, 0.0,
                "the signed algebraic sum keeps a cooling strike negative");
        }

        [Test]
        public void EmptySwingProducesNoWorkNoReleaseAndNoStageEvidence()
        {
            var (runtime, _, bat) = StartedTrial("trial-bat-empty");
            var contactsBefore = bat.Readout.Ledger.Contacts.Count;

            bat.Consume(BatContactFixtures.Accepted(
                new Vector3(40f, 40f, 40f), new Vector3(4f, 0f, 0f), 1), 1);
            runtime.Step(1);
            var closed = bat.Close();

            Assert.AreEqual(BatSwingOutcome.NoContact, closed.Outcome);
            Assert.IsFalse(closed.ProducedWork);
            Assert.AreEqual(0.0, closed.Closure.WorkJ);
            Assert.IsNull(closed.Closure.Entry);
            Assert.AreEqual(contactsBefore, bat.Readout.Ledger.Contacts.Count);
            Assert.AreEqual(0.0, bat.Readout.Ledger.WorkEventsCumulativeKeJ);
            Assert.AreEqual(1, bat.EmptySwingCount);
            Assert.AreEqual(0, bat.ContactCount);
        }

        [Test]
        public void TrackingLossAbortsWithoutContactOrSyntheticRelease()
        {
            var (runtime, writer, bat) = StartedTrial("trial-bat-loss");
            var ball = runtime.CurrentView.Balls[0];
            writer.IsolateBalls();
            writer.PlaceBall(ball.EntityId, Vector3.zero, Vector3.zero);

            bat.Consume(BatContactFixtures.Accepted(
                Vector3.zero, new Vector3(4f, 0f, 0f), 1), 1);
            var invalid = bat.Consume(
                BatContactFixtures.Rejected(InvalidInputReason.LowConfidence), 2);
            runtime.Step(1);

            Assert.AreEqual(BatSwingOutcome.NotAccepted, invalid.Outcome);
            Assert.AreEqual(InvalidInputReason.LowConfidence.ToString(), invalid.ReasonCode);
            Assert.AreEqual(0, bat.ContactCount);
            Assert.AreEqual(1, bat.AbortedContactCount);
            Assert.AreEqual(0, bat.Readout.Ledger.Contacts.Count,
                "a tracking-loss abort adds no ledger entry");
            Assert.AreEqual(0.0, bat.Readout.Ledger.WorkEventsCumulativeKeJ);
            Assert.AreEqual(1, bat.InvalidCount);
            Assert.AreEqual(1, bat.Diagnostics.Count(InvalidInputReason.LowConfidence));
        }

        [Test]
        public void EveryContactClosureIsOneSemanticCommit()
        {
            // 02 section 4.1 and the oracle's close_interaction: the closure is
            // the semantic event, so an empty swing is one commit with no work,
            // and a contacted closure is one commit with one ledger entry.
            var (runtime, writer, bat) = StartedTrial("trial-bat-commits");
            var revisionBefore = bat.Readout.ContactCommits;
            var effectiveEvents = new List<string>();
            runtime.SessionEventCommitted = (kind, trialId, tick, revision) =>
            {
                if (kind == "effective_action")
                    effectiveEvents.Add(trialId + ":" + tick + ":" + revision);
            };

            bat.Consume(BatContactFixtures.Accepted(
                new Vector3(40f, 40f, 40f), new Vector3(4f, 0f, 0f), 1), 1);
            runtime.Step(1);
            bat.Close();
            Assert.AreEqual(revisionBefore + 1, bat.Readout.ContactCommits,
                "the empty swing closes as one semantic commit");
            Assert.AreEqual(0, bat.Readout.Ledger.Contacts.Count);
            Assert.IsEmpty(effectiveEvents, "an empty swing is not an effective action");

            var ball = runtime.CurrentView.Balls[0];
            writer.IsolateBalls();
            writer.PlaceBall(ball.EntityId, Vector3.zero, Vector3.zero);
            bat.Consume(BatContactFixtures.Accepted(
                Vector3.zero, new Vector3(4f, 0f, 0f), 2), 2);
            runtime.Step(1);
            bat.Close();
            Assert.AreEqual(revisionBefore + 2, bat.Readout.ContactCommits,
                "the contacted swing closes as one more commit, not one per sample");
            Assert.AreEqual(1, bat.Readout.Ledger.Contacts.Count);
            Assert.AreEqual(1, effectiveEvents.Count);
            StringAssert.EndsWith(":" + bat.Readout.ContactCommits, effectiveEvents[0]);
        }

        [Test]
        public void InvalidSampleNeverOpensAContact()
        {
            var runtime = BatContactFixtures.StartedTrial("trial-bat-invalid", out _);
            var bat = new BatTool(runtime, BatContactFixtures.TestOnlyProfile());
            foreach (var reason in new[]
                     {
                         InvalidInputReason.ProfileNotLocked, InvalidInputReason.Stale,
                         InvalidInputReason.NonFinite, InvalidInputReason.OutOfOrder,
                         InvalidInputReason.OverRate, InvalidInputReason.OverSpeed,
                         InvalidInputReason.LowConfidence, InvalidInputReason.SystemGesture
                     })
            {
                var result = bat.Consume(BatContactFixtures.Rejected(reason), 1);
                Assert.AreEqual(BatSwingOutcome.NotAccepted, result.Outcome, reason.ToString());
                Assert.AreEqual("", result.ContactId, reason.ToString());
                Assert.AreEqual(reason.ToString(), result.ReasonCode);
            }
            Assert.AreEqual("", bat.OpenContactId);
            Assert.AreEqual(0, runtime.Readout.Ledger.Contacts.Count);
            Assert.AreEqual(0.0, runtime.Readout.Ledger.WorkEventsCumulativeKeJ);
            Assert.AreEqual(0, bat.ContactCount);
        }

        [Test]
        public void HandAndControllerProduceTheSameContactSemantics()
        {
            var results = new List<(int contacts, int empty, double work, int revision)>();
            foreach (var modality in new[] { InputModality.Hand, InputModality.Controller })
            {
                var (runtime, writer, bat) = StartedTrial("trial-bat-" + modality);
                var ball = runtime.CurrentView.Balls[0];
                writer.IsolateBalls();
            writer.PlaceBall(ball.EntityId, Vector3.zero, Vector3.zero);
                var normalizer = new InputNormalizer(BatContactFixtures.TestOnlyProfile());
                var sample = normalizer.Normalize(BatContactFixtures.Sample(
                    modality: modality, position: Vector3.zero,
                    velocity: new Vector3(4f, 0f, 0f)));
                Assert.IsTrue(sample.Accepted, modality + " " + sample.InvalidReason);
                bat.Consume(sample, 1);
                runtime.Step(1);
                bat.Close();
                results.Add((bat.ContactCount, bat.EmptySwingCount,
                    bat.Readout.Ledger.WorkEventsCumulativeKeJ, bat.Readout.ContactCommits));
            }

            Assert.AreEqual(1, results[0].contacts);
            Assert.AreEqual(results[0].contacts, results[1].contacts);
            Assert.AreEqual(results[0].empty, results[1].empty);
            Assert.AreEqual(results[0].revision, results[1].revision);
            Assert.AreEqual(results[0].work, results[1].work, 1e-12,
                "equivalent normalized commands produce the same measured work");
        }

        static PublishedBall FarthestFreeBall(IReadOnlyList<PublishedBall> balls)
        {
            PublishedBall best = null;
            var bestGap = float.NegativeInfinity;
            foreach (var ball in balls)
            {
                if (ball.Membership != "free")
                    continue;
                var nearest = float.PositiveInfinity;
                foreach (var other in balls)
                {
                    if (other == ball)
                        continue;
                    nearest = Mathf.Min(nearest,
                        Vector3.Distance(ball.PositionM, other.PositionM));
                }
                if (nearest <= bestGap)
                    continue;
                bestGap = nearest;
                best = ball;
            }
            return best;
        }

        [Test]
        public void AnAcceptedContactRaisesMeasuredTemperatureAgainstTheSameTrialWithoutAHit()
        {
            const string trialId = "trial-bat-temperature";
            var control = BatContactFixtures.StartedProductTrial(trialId);
            var hit = BatContactFixtures.StartedProductTrial(trialId);
            var profile = InputLimitProfile.CandidateUnmeasured();

            var ball = FarthestFreeBall(hit.CurrentView.Balls);
            Assert.IsNotNull(ball);
            var direction = ball.VelocityMPerS.sqrMagnitude > 1e-6f
                ? ball.VelocityMPerS.normalized
                : Vector3.right;
            // The sweep runs across the ball's own motion direction and starts
            // just behind it, so the measured impulse pushes the ball along
            // that direction: the contact does positive work.
            var sweep = Vector3.Cross(direction,
                    Mathf.Abs(direction.z) < 0.9f ? Vector3.forward : Vector3.right)
                .normalized * 3f;
            var sample = new InputNormalizer(profile).Normalize(
                BatContactFixtures.Sample(position: ball.PositionM - direction * 0.1f,
                    velocity: sweep));
            Assert.IsTrue(sample.Accepted, sample.InvalidReason.ToString());

            var bat = new BatTool(hit, profile);
            Assert.AreEqual(BatSwingOutcome.Submitted, bat.Consume(sample, 1).Outcome);
            Assert.AreEqual(1, hit.Step(1).Executed);
            var closed = bat.Close();
            Assert.AreEqual(BatSwingOutcome.Contacted, closed.Outcome, closed.ReasonCode);
            Assert.Greater(closed.Closure.WorkJ, 0.0,
                "the accepted contact does positive work");

            Assert.AreEqual(1, control.Step(1).Executed);
            var withoutHit = control.Readout.Measurement;
            var withHit = hit.Readout.Measurement;
            Assert.AreEqual(TemperatureMeasurement.ValidStatus, withoutHit.Status);
            Assert.AreEqual(TemperatureMeasurement.ValidStatus, withHit.Status);
            Assert.Greater(withHit.TK.Value, withoutHit.TK.Value,
                "positive bat work raises the measured T_kin");
        }

        [Test]
        public void ATrialWithoutTheToolEffectFaceRefusesToProduceWork()
        {
            var accepted = BatContactFixtures.AcceptedRoom("trial-no-tool");
            var runtime = new DeviceSimulationRuntime(() => new NoToolWriter());
            Assert.IsTrue(runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "trial-no-tool", accepted.room.Confirmation, accepted.pin, out _)).Started);
            var bat = new BatTool(runtime, BatContactFixtures.TestOnlyProfile());
            var result = bat.Consume(BatContactFixtures.Accepted(
                Vector3.zero, new Vector3(4f, 0f, 0f), 1), 1);
            Assert.AreEqual(BatSwingOutcome.NotAccepted, result.Outcome);
            Assert.AreEqual("writer_has_no_tool_effect_face", result.ReasonCode);
            Assert.AreEqual(0, bat.ContactCount);
            Assert.AreEqual(0, runtime.Readout.Ledger.Contacts.Count);
        }

        sealed class NoToolWriter : IScientificWriter
        {
            BallState[] balls;
            public string WriterId => "test-no-tool-v1";
            public string ConfigurationId => "test-no-tool-v1";
            public WriterDisposition Disposition => WriterDisposition.ProductTrial;
            public IReadOnlyList<string> ConfigurationRecord =>
                new[] { "writer_configuration_id=test-no-tool-v1" };
            public void Setup(SimulationTrialSetup setup) => balls = (BallState[])setup.Balls;
            public void Advance(int tick, float deltaTimeSeconds)
            {
                foreach (var ball in balls)
                    ball.PositionM += ball.VelocityMPerS * deltaTimeSeconds;
            }
            public void Teardown() => balls = null;
        }

        sealed class NoTrialHost : IBatContactHost
        {
            public bool HasTrial => false;
            public bool SupportsToolContacts => false;
            public int CurrentTick => 0;
            public int ContactCommits => 0;
            public TemperatureReadoutSnapshot Readout => null;
            public void OpenSweptContact(string contactId, string propId, int tick) { }
            public void SubmitSweep(string contactId, Vector3 origin, Vector3 velocityMPerS,
                float sweptRadiusM) { }
            public SweptClosure CloseSweptContact(string contactId, string reason) => null;
        }
    }

    public class TemperatureReadoutProjectionTests
    {
        static MeasurementWindow Window() => new("window-test", 0, 10, 0.5f, 11);

        static IReadOnlyList<PublishedBall> Balls(int count, float speed)
        {
            var balls = new List<PublishedBall>();
            for (var index = 0; index < count; index++)
            {
                var angle = index + 1.0;
                balls.Add(new PublishedBall("ball-" + index.ToString("D3"), Vector3.zero,
                    new Vector3((float)Math.Sin(angle) * speed,
                        (float)Math.Cos(angle) * speed, speed), 1f, 0.05f, "free"));
            }
            return balls;
        }

        [Test]
        public void PanelRendersOneTupleFromOneMeasurement()
        {
            var measurement = TemperatureMeasurementFace.Measure("measurement-test",
                Balls(12, 1f), Window(), initialTK: 300.0);
            Assert.AreEqual(TemperatureMeasurement.ValidStatus, measurement.Status);
            var tuple = measurement.Display;
            var text = TemperatureReadoutProjection.Compose(new TemperatureReadoutSnapshot(
                "trial-readout", 10, "view-1", measurement,
                new TemperatureEnergyLedger(10.0, 10.0, 8.0, 2.0), 0));

            StringAssert.Contains(tuple.Kelvin + " K", text);
            StringAssert.Contains(tuple.Celsius + " °C", text);
            StringAssert.Contains(tuple.Fahrenheit + " °F", text);
            StringAssert.Contains(TemperatureReadoutProjection.ScaleLabel, text);
            StringAssert.Contains("K_thermal 8.00 J", text);
            StringAssert.Contains("K_drift 2.00 J", text);

            // Every printed unit comes from the one tuple: recomputing it from
            // the same T_kin reproduces the rendered text exactly.
            var recomputed = TemperatureDisplayTuple.FromKelvin(measurement.TK.Value,
                measurement.InitialTK.Value);
            Assert.AreEqual(tuple.Kelvin, recomputed.Kelvin);
            Assert.AreEqual(tuple.Celsius, recomputed.Celsius);
            Assert.AreEqual(tuple.Fahrenheit, recomputed.Fahrenheit);
            StringAssert.Contains(recomputed.Celsius + " °C", text);
            StringAssert.Contains(recomputed.KelvinRatioBaseline.ToString("0.0") +
                                 "× initial", text);
        }

        [Test]
        public void NotApplicableMeasurementShowsNoNumber()
        {
            var measurement = TemperatureMeasurementFace.Measure("measurement-na",
                Balls(1, 1f), Window(), null);
            Assert.AreEqual(TemperatureMeasurement.NotApplicableStatus, measurement.Status);
            Assert.IsNull(measurement.Display);
            Assert.IsNull(measurement.TK);

            var text = TemperatureReadoutProjection.Compose(new TemperatureReadoutSnapshot(
                "trial-na", 10, "view-na", measurement,
                new TemperatureEnergyLedger(1.0, 1.0, 1.0, 0.0), 0));

            StringAssert.Contains("not applicable", text);
            StringAssert.Contains("no temperature value is shown", text);
            Assert.IsFalse(text.Contains("°C"), "an N/A readout prints no celsius value");
            Assert.IsFalse(text.Contains("°F"), "an N/A readout prints no fahrenheit value");
            Assert.IsFalse(text.Contains("× initial"), "no ratio without a baseline");
            Assert.IsFalse(text.Contains(" K "), "an N/A readout prints no kelvin value");
        }

        [Test]
        public void NoProducerKeepsTheHonestUnavailableState()
        {
            Assert.AreEqual(TemperatureReadoutProjection.NoProducer,
                TemperatureReadoutProjection.Compose(null));
            var runtime = new UnboundSimulationRuntime();
            Assert.IsNull(runtime.Readout);
            Assert.AreEqual(TemperatureReadoutProjection.NoProducer,
                TemperatureReadoutProjection.Compose(runtime.Readout));
        }

        [Test]
        public void InputLineLabelsTheInstalledCandidateProfileAsUncalibrated()
        {
            var measurement = TemperatureMeasurementFace.Measure("measurement-profile",
                Balls(12, 1f), Window(), initialTK: 300.0);
            var text = TemperatureReadoutProjection.Compose(new TemperatureReadoutSnapshot(
                "trial-profile", 10, "view-profile", measurement,
                new TemperatureEnergyLedger(1.0, 1.0, 1.0, 0.0), 0));

            var lines = text.Split('\n');
            var inputLine = lines[lines.Length - 1];
            StringAssert.Contains(InputLimitProfile.CandidateUnmeasured().ProfileVersion, inputLine);
            StringAssert.Contains("declared candidate", inputLine);
            StringAssert.Contains("uncalibrated", inputLine);
            Assert.IsFalse(text.Contains("declared-unmeasured"),
                "the installed candidate is not described as unmeasured");
        }

        [Test]
        public void UnmeasuredProfileKeepsTheRefusalWording()
        {
            var candidate = InputLimitProfile.CandidateUnmeasured();
            Assert.AreEqual(TemperatureReadoutProjection.ProfileCandidate,
                TemperatureReadoutProjection.ProfileLine(candidate));
            Assert.AreEqual(TemperatureReadoutProjection.ProfileUnmeasured,
                TemperatureReadoutProjection.ProfileLine(InputLimitProfile.Unmeasured()));
            StringAssert.Contains(candidate.ProfileVersion,
                TemperatureReadoutProjection.ProfileCandidate);

            var measurement = TemperatureMeasurementFace.Measure("measurement-refused",
                Balls(12, 1f), Window(), initialTK: 300.0);
            var refused = TemperatureReadoutProjection.Compose(new TemperatureReadoutSnapshot(
                "trial-refused", 10, "view-refused", measurement,
                new TemperatureEnergyLedger(1.0, 1.0, 1.0, 0.0), 0),
                InputLimitProfile.Unmeasured());

            StringAssert.Contains("swings refused", refused);
            Assert.IsFalse(refused.Contains("swings admitted"),
                "an unmeasured profile still refuses every swing");
        }

        [Test]
        public void ZeroAndOneFreeBallNeverProduceKelvin()
        {
            foreach (var count in new[] { 0, 1 })
            {
                var measurement = TemperatureMeasurementFace.Measure("na-" + count,
                    Balls(count, 2f), Window(), null);
                Assert.AreEqual(TemperatureMeasurement.NotApplicableStatus,
                    measurement.Status, "free=" + count);
                Assert.IsNull(measurement.Display);
                Assert.IsNull(measurement.TK);
                Assert.IsFalse(measurement.EquilibriumSufficient);
            }
        }

        [Test]
        public void DisplayTupleDerivesFromTheSingleUnroundedKineticTemperature()
        {
            var measurement = TemperatureMeasurementFace.Measure("measurement-conv",
                Balls(12, 1.3f), Window(), initialTK: 400.0);
            Assert.IsTrue(measurement.Applicable);
            var kelvin = measurement.TK.Value;
            Assert.AreEqual((int)Math.Round(kelvin), measurement.Display.Kelvin);
            Assert.AreEqual((int)Math.Round(kelvin - 273.15), measurement.Display.Celsius);
            Assert.AreEqual((int)Math.Round((kelvin - 273.15) * 9.0 / 5.0 + 32.0),
                measurement.Display.Fahrenheit);
            Assert.AreEqual(Math.Round(kelvin / 400.0, 1),
                measurement.Display.KelvinRatioBaseline);
            Assert.AreEqual(measurement.KTotalJ,
                measurement.KThermalJ + measurement.KDriftJ, 1e-9);
        }

        [Test]
        public void ScalePointMeasuresThreeHundredKelvinAtOnePointOneMetresPerSecond()
        {
            var declared = FrozenWriterConfiguration.BallMassKg * 1.1 * 1.1 / (3.0 * 300.0);
            Assert.AreEqual(declared,
                TemperatureMeasurementParameters.Declared.EffectiveKB, 1e-15,
                "declared effective k_B holds the 07 section 3.2 scale point");

            var atScale = TemperatureMeasurementFace.Measure("measurement-scale",
                ScalePointBalls(1.1f), Window(), initialTK: 300.0);
            Assert.AreEqual(1.1, atScale.VRmsMPerS, 1e-6, "v_rms at the scale point");
            Assert.AreEqual(300.0, atScale.TK.Value, 1e-3, "300 K at v_rms 1.1 m/s");

            var doubled = TemperatureMeasurementFace.Measure("measurement-scale-double",
                ScalePointBalls(2.2f), Window(), initialTK: 300.0);
            Assert.AreEqual(2.2, doubled.VRmsMPerS, 1e-6, "doubled v_rms");
            Assert.AreEqual(1200.0, doubled.TK.Value, 1e-3, "double v_rms quadruples T");
        }

        static IReadOnlyList<PublishedBall> ScalePointBalls(float speed)
        {
            // Equal masses in opposite velocity pairs: the center of mass is
            // zero, so every speed is thermal and v_rms is the requested speed.
            var directions = new[]
            {
                new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
                new Vector3(0f, 0f, 1f)
            };
            var balls = new List<PublishedBall>();
            for (var index = 0; index < 12; index++)
            {
                var sign = index % 2 == 0 ? 1f : -1f;
                balls.Add(new PublishedBall("ball-scale-" + index.ToString("D2"),
                    Vector3.zero, directions[(index / 2) % 3] * (sign * speed),
                    1f, 0.05f, "free"));
            }
            return balls;
        }

        [Test]
        public void LedgerEnergyValuesComeFromTheSameProducerReadout()
        {
            var runtime = BatContactFixtures.StartedTrial("trial-readout-live",
                out var writer);
            var ball = runtime.CurrentView.Balls[0];
            writer.IsolateBalls();
            writer.PlaceBall(ball.EntityId, Vector3.zero, Vector3.zero);

            var bat = new BatTool(runtime, BatContactFixtures.TestOnlyProfile());
            bat.Consume(BatContactFixtures.Accepted(
                Vector3.zero, new Vector3(4f, 0f, 0f), 1), 1);
            runtime.Step(1);
            var closed = bat.Close();

            var readout = runtime.Readout;
            Assert.AreEqual(1, readout.Ledger.Contacts.Count);
            var text = TemperatureReadoutProjection.Compose(readout);
            StringAssert.Contains(
                "net work +" + closed.Closure.WorkJ.ToString("0.00"), text);
            StringAssert.Contains("1 contacts", text);
        }
    }
}
