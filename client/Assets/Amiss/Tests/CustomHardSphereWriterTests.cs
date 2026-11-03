using System.Collections.Generic;
using System.Linq;
using Amiss.Shell.Episodes;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;
using Amiss.Shell.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// Parity of the custom hard-sphere writer with the approved headless
    /// oracle (<c>simulation/runtime.py</c>, <c>HeadlessBallWriter.advance</c>).
    ///
    /// The expected values were produced by running that oracle on the exact
    /// fixtures below (room <c>Bounds(center (0,1,0), size (2,2,2))</c>, radius
    /// 0.05 m, mass 1 kg, fixed Tick 0.05 s, contact bound 4096); the mirrored
    /// oracle case is asserted in <c>simulation/tests/test_runtime.py</c>
    /// (<c>HardSphereOracleParityTestCase</c>).
    ///
    /// Tolerances follow the existing Python tests: ball positions are compared
    /// at <c>assertAlmostEqual</c>'s default seven decimal places
    /// (<c>simulation/tests/test_runtime.py</c>, held-target position checks),
    /// translated to an absolute 1e-7 because the device runtime's ball-state
    /// copy stores <see cref="Vector3"/> (float32) components. The writer
    /// computes in double and reads back once per Tick, so a short run differs
    /// from the oracle by a single float32 rounding; the 60-Tick oblique case
    /// widens position and velocity tolerances to 1e-5 because float32 inputs,
    /// float32 boundary mirrors, and the second oblique contact accumulate
    /// about 1e-6 of drift, while the symmetric 60-Tick head-on case and every
    /// short run stay within 1e-7. Contact outcomes are compared exactly.
    /// </summary>
    public class CustomHardSphereWriterTests
    {
        static (RoomPreparation room, CollisionPin pin, PinnedCollision pinned) AcceptedRoom(
            Vector3 size, string trialId)
        {
            var manual = RoomDraft.CreateManual(
                new Bounds(new Vector3(0f, size.y * 0.5f, 0f), size), Vector3.zero,
                Vector3.forward);
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
            Assert.IsTrue(CollisionBridge.TryBind(room.Confirmation, pin, out var pinned,
                out _));
            return (room, pin, pinned);
        }

        static CustomHardSphereWriter WriterWith(BallState[] balls, PinnedCollision collision)
        {
            var writer = new CustomHardSphereWriter();
            writer.Setup(new SimulationTrialSetup("parity-" + balls.Length, balls, collision,
                new FrozenWriterConfiguration(collision.RepresentationId,
                    collision.Checksum, collision.FormatVersion, 7, "initial-setup")));
            return writer;
        }

        static BallState Ball(int index, Vector3 position, Vector3 velocity) =>
            new("ball-" + index.ToString("D4"), position, velocity,
                FrozenWriterConfiguration.BallMassKg, FrozenWriterConfiguration.BallRadiusM);

        sealed class Step
        {
            public int Tick;
            public int PairContacts;
            public int WallContacts;
        }

        /// <summary>Runs the writer and records every Tick that made a contact.</summary>
        static (List<Step> contacts, Dictionary<string, BallState> final) Run(
            BallState[] balls, PinnedCollision collision, int ticks)
        {
            var writer = WriterWith(balls, collision);
            var contacts = new List<Step>();
            for (var tick = 1; tick <= ticks; tick++)
            {
                writer.Advance(tick, FrozenWriterConfiguration.FixedDeltaTimeSeconds);
                var facts = writer.LastContactFacts;
                if (facts.PairContacts > 0 || facts.WallContacts > 0)
                    contacts.Add(new Step
                    {
                        Tick = tick,
                        PairContacts = facts.PairContacts,
                        WallContacts = facts.WallContacts
                    });
                if (facts.IterationBoundExhausted)
                    Assert.Fail("iteration bound exhausted at tick " + tick);
            }
            var final = balls.ToDictionary(ball => ball.EntityId, ball => ball);
            return (contacts, final);
        }

        static void AssertMatchesOracle(Dictionary<string, BallState> balls,
            (double x, double y, double z) position, (double x, double y, double z) velocity,
            string entityId, double positionTolerance = 1e-7, double velocityTolerance = 1e-7)
        {
            var ball = balls[entityId];
            Assert.AreEqual(position.x, ball.PositionM.x, positionTolerance, entityId + " position x");
            Assert.AreEqual(position.y, ball.PositionM.y, positionTolerance, entityId + " position y");
            Assert.AreEqual(position.z, ball.PositionM.z, positionTolerance, entityId + " position z");
            Assert.AreEqual(velocity.x, ball.VelocityMPerS.x, velocityTolerance, entityId + " velocity x");
            Assert.AreEqual(velocity.y, ball.VelocityMPerS.y, velocityTolerance, entityId + " velocity y");
            Assert.AreEqual(velocity.z, ball.VelocityMPerS.z, velocityTolerance, entityId + " velocity z");
        }

        static void AssertContactTicks(List<Step> contacts, (int tick, int pairs, int walls)[] expected)
        {
            Assert.AreEqual(expected.Length, contacts.Count, "contact Tick count");
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.AreEqual(expected[index].tick, contacts[index].Tick, "contact Tick");
                Assert.AreEqual(expected[index].pairs, contacts[index].PairContacts,
                    "pair contacts");
                Assert.AreEqual(expected[index].walls, contacts[index].WallContacts,
                    "wall contacts");
            }
        }

        [Test]
        public void HeadOnTwoBallCollisionMatchesTheOracle()
        {
            var accepted = AcceptedRoom(new Vector3(2f, 2f, 2f), "parity-head-on");
            var balls = new[]
            {
                Ball(1, new Vector3(-0.28f, 1f, 0f), new Vector3(1f, 0f, 0f)),
                Ball(2, new Vector3(0.28f, 1f, 0f), new Vector3(-1f, 0f, 0f))
            };
            var (contacts, final) = Run(balls, accepted.pinned, 60);
            AssertContactTicks(contacts, new[]
            {
                (5, 1, 0), (24, 0, 2), (42, 1, 0)
            });
            // The Tick-5 contact applies the full elastic exchange: the balls
            // swap velocities exactly, keep |v| = 1, rebound off both walls at
            // Tick 24, and exchange again when the symmetric paths re-meet at
            // Tick 42 - the perfectly elastic pair contact of 07 section 3.1
            // and 02 section 3.1, matching the corrected oracle.
            AssertMatchesOracle(final, (-0.92, 1.0, 0.0), (-1.0, 0.0, 0.0),
                "ball-0001");
            AssertMatchesOracle(final, (0.92, 1.0, 0.0), (1.0, 0.0, 0.0),
                "ball-0002");
        }

        [Test]
        public void ObliqueTwoBallContactMatchesTheOracle()
        {
            var accepted = AcceptedRoom(new Vector3(2f, 2f, 2f), "parity-oblique");
            var balls = new[]
            {
                Ball(1, new Vector3(-0.28f, 1f, -0.03f), new Vector3(1f, 0f, 0f)),
                Ball(2, new Vector3(0.28f, 1f, 0.03f), new Vector3(-1f, 0f, 0f))
            };
            var (contacts, final) = Run(balls, accepted.pinned, 60);
            // A genuine off-axis pair contact at Tick 5: momentum stays zero on
            // every axis, each speed stays 1, and the deflection is symmetric.
            AssertContactTicks(contacts, new[]
            {
                (5, 1, 0), (24, 0, 2), (42, 1, 0)
            });
            AssertMatchesOracle(final, (-0.8607692308, 1.0, 0.3261538462),
                (-0.9230769231, 0.0, 0.3846153846), "ball-0001", 1e-5, 1e-5);
            AssertMatchesOracle(final, (0.8607692308, 1.0, -0.3261538462),
                (0.9230769231, 0.0, -0.3846153846), "ball-0002", 1e-5, 1e-5);
        }

        [Test]
        public void StationaryBoundaryReboundMatchesTheOracle()
        {
            var accepted = AcceptedRoom(new Vector3(2f, 2f, 2f), "parity-boundary");
            var balls = new[] { Ball(1, new Vector3(-0.28f, 1f, 0f), new Vector3(-1f, 0f, 0f)) };
            var (contacts, final) = Run(balls, accepted.pinned, 30);
            AssertContactTicks(contacts, new[] { (14, 0, 1) });
            AssertMatchesOracle(final, (-0.12, 1.0, 0.0), (1.0, 0.0, 0.0), "ball-0001");
        }

        [Test]
        public void ShortMultiBallRunMatchesTheOracle()
        {
            var accepted = AcceptedRoom(new Vector3(2f, 2f, 2f), "parity-multi");
            var balls = new[]
            {
                Ball(1, new Vector3(-0.28f, 1.00f, 0.00f), new Vector3(1.0f, 0.0f, 0.0f)),
                Ball(2, new Vector3(0.28f, 1.00f, 0.00f), new Vector3(-1.0f, 0.0f, 0.0f)),
                Ball(3, new Vector3(-0.60f, 0.40f, -0.92f), new Vector3(0.5f, 0.5f, -1.0f)),
                Ball(4, new Vector3(0.60f, 0.40f, 0.60f), new Vector3(-0.5f, 0.5f, 0.0f)),
                Ball(5, new Vector3(0.00f, 1.50f, 0.30f), new Vector3(0.0f, 0.0f, 0.0f)),
                Ball(6, new Vector3(-0.10f, 0.70f, 0.35f), new Vector3(0.4f, 0.0f, -0.4f))
            };
            var (contacts, final) = Run(balls, accepted.pinned, 8);
            AssertContactTicks(contacts, new[] { (1, 0, 1), (5, 1, 0) });
            // Tick 5 exchanges the pair velocities exactly; Tick 1 is ball 3's
            // z-wall rebound with the normal velocity sign flipped.
            AssertMatchesOracle(final, (-0.18, 1.0, 0.0), (-1.0, 0.0, 0.0), "ball-0001");
            AssertMatchesOracle(final, (0.18, 1.0, 0.0), (1.0, 0.0, 0.0), "ball-0002");
            AssertMatchesOracle(final, (-0.4, 0.6, -0.58), (0.5, 0.5, 1.0), "ball-0003");
            AssertMatchesOracle(final, (0.4, 0.6, 0.6), (-0.5, 0.5, 0.0), "ball-0004");
            AssertMatchesOracle(final, (0.0, 1.5, 0.3), (0.0, 0.0, 0.0), "ball-0005");
            AssertMatchesOracle(final, (0.06, 0.7, 0.19), (0.4, 0.0, -0.4), "ball-0006");
        }

        [Test]
        public void ContactIterationBoundBecomesANumericalHoldNotATunnel()
        {
            var accepted = AcceptedRoom(new Vector3(2f, 2f, 2f), "parity-bound-exhausted");
            var balls = new[]
            {
                Ball(1, new Vector3(-0.28f, 1f, 0f), new Vector3(1f, 0f, 0f)),
                Ball(2, new Vector3(0.28f, 1f, 0f), new Vector3(-1f, 0f, 0f))
            };
            // A zero-resolution bound leaves the Tick-5 overlap unresolved; the
            // step reports exhaustion, the impulse is skipped (velocities keep
            // their pre-contact values at that Tick), and no contact is ever
            // applied past the bound. Contact facts are per-step, so the
            // exhaustion is observed on the step that produced it.
            var writer = new CustomHardSphereWriter(0);
            writer.Setup(new SimulationTrialSetup("parity-bound", balls, accepted.pinned,
                new FrozenWriterConfiguration(accepted.pinned.RepresentationId,
                    accepted.pinned.Checksum, accepted.pinned.FormatVersion, 7,
                    "initial-setup")));
            var exhaustedTick = 0;
            for (var tick = 1; tick <= 8; tick++)
            {
                writer.Advance(tick, FrozenWriterConfiguration.FixedDeltaTimeSeconds);
                if (!writer.LastContactFacts.IterationBoundExhausted)
                    continue;
                exhaustedTick = tick;
                Assert.AreEqual(0, writer.LastContactFacts.PairContacts,
                    "no impulse is applied once the bound is exhausted");
                Assert.AreEqual(1.0, balls[0].VelocityMPerS.x, 1e-7,
                    "the skipped pair impulse leaves the velocity untouched");
                Assert.AreEqual(-1.0, balls[1].VelocityMPerS.x, 1e-7,
                    "the skipped pair impulse leaves the velocity untouched");
                break;
            }
            Assert.AreEqual(5, exhaustedTick,
                "the Tick-5 overlap must exhaust the zero-resolution bound");
        }

        [Test]
        public void ProductRuntimeBindsTheCustomWriterAndRefusesTheArchivedOne()
        {
            var accepted = AcceptedRoom(new Vector3(2f, 2f, 2f), "parity-binding");
            var runtime = new DeviceSimulationRuntime();
            Assert.IsInstanceOf<CustomHardSphereWriter>(runtime.CreateWriter(),
                "the product factory binds the custom hard-sphere writer");

            var slot = new ScientificWriterSlot();
            Assert.IsFalse(slot.TryBind("trial", new UnityRigidbodyWriter(), "cfg", out var reason));
            Assert.AreEqual("writer_configuration_archived_only", reason);
            Assert.AreEqual(0, slot.BoundWriterCount,
                "the rejected Unity configuration cannot bind to any Trial");

            var withUnity = new DeviceSimulationRuntime(() => new UnityRigidbodyWriter());
            var outcome = withUnity.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "parity-binding", accepted.room.Confirmation, accepted.pin, out _));
            Assert.IsFalse(outcome.Started);
            Assert.AreEqual(TrialStartRefusal.WriterConfigurationRejected, outcome.Refusal);
            Assert.AreEqual("writer_configuration_rejected:writer_configuration_archived_only",
                outcome.ReasonCode);
            Assert.IsFalse(withUnity.HasTrial);
        }

        [Test]
        public void RuntimeHoldsNumericallyWhenTheContactBoundIsExhausted()
        {
            var accepted = AcceptedRoom(new Vector3(2f, 2f, 2f), "parity-hold");
            var runtime = new DeviceSimulationRuntime(() => new CustomHardSphereWriter(0));
            Assert.IsTrue(runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "parity-hold", accepted.room.Confirmation, accepted.pin, out _)).Started);
            for (var slice = 0; slice < 100 && string.IsNullOrEmpty(runtime.NumericalHold); slice++)
                runtime.Step(2);
            Assert.AreEqual("contact_iteration_bound_exhausted", runtime.NumericalHold,
                "a zero-resolution bound must hold the Trial, not tunnel through a contact");
            var tickAtHold = runtime.Tick;
            var viewAtHold = runtime.CurrentView;
            Assert.GreaterOrEqual(tickAtHold, 1);

            runtime.RequestResume();
            var stillHeld = runtime.Step(2);
            Assert.AreEqual(0, stillHeld.Executed,
                "a numerical hold is sticky; only a new Trial recovers");
            Assert.AreEqual(tickAtHold, runtime.Tick);
            Assert.AreSame(viewAtHold, runtime.CurrentView,
                "a step that exhausted the bound is never published");
        }

        [Test]
        public void ConfigurationRecordNamesEveryWriterField()
        {
            var accepted = AcceptedRoom(new Vector3(2f, 2f, 2f), "parity-record");
            var writer = new CustomHardSphereWriter();
            writer.Setup(new SimulationTrialSetup("parity-record",
                new[] { Ball(1, Vector3.zero, Vector3.zero) }, accepted.pinned,
                new FrozenWriterConfiguration(accepted.pinned.RepresentationId,
                    accepted.pinned.Checksum, accepted.pinned.FormatVersion, 42,
                    "initial-setup")));
            var lines = writer.ConfigurationRecord;
            Assert.GreaterOrEqual(lines.Count, 20);
            Assert.IsTrue(lines.Contains("writer_configuration_id=" +
                                         CustomHardSphereWriter.ConfigurationIdValue));
            Assert.IsTrue(lines.Contains("physx_ball_state_write=none"));
            Assert.IsTrue(lines.Contains("fixed_delta_time_s=" +
                                         FrozenWriterConfiguration.FixedDeltaTimeSeconds
                                             .ToString("0.######")));
            Assert.IsTrue(lines.Contains("contact_iteration_bound=" +
                                         FrozenWriterConfiguration.ContactIterationBound));
            Assert.IsTrue(lines.Contains("ball_mass_radius_evidence=" +
                                         FrozenWriterConfiguration.Uncalibrated));
            Assert.IsTrue(lines.Any(line => line.StartsWith("root_seed=42")));
            Assert.IsFalse(lines.Any(line => line.Contains("unity")),
                "the replacement record carries no engine field");
        }

        [Test]
        public void TicksAtTwoRenderCadencesGiveIdenticalMeasurements()
        {
            var accepted = AcceptedRoom(new Vector3(2f, 2f, 2f), "parity-cadence");
            var onePerFrame = new DeviceSimulationRuntime();
            var twoPerFrame = new DeviceSimulationRuntime();
            var trialId = "parity-cadence";
            Assert.IsTrue(onePerFrame.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                trialId, accepted.room.Confirmation, accepted.pin, out _)).Started);
            Assert.IsTrue(twoPerFrame.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                trialId, accepted.room.Confirmation, accepted.pin, out _)).Started);

            for (var frame = 0; frame < 100; frame++)
            {
                for (var slice = 0; slice < 2; slice++)
                    Assert.AreEqual(1, onePerFrame.Step(1).Executed);
                Assert.AreEqual(2, twoPerFrame.Step(2).Executed);
                Assert.AreEqual(onePerFrame.CurrentView.Tick, twoPerFrame.CurrentView.Tick);
                Assert.AreEqual(onePerFrame.CurrentView.Balls.Count,
                    twoPerFrame.CurrentView.Balls.Count);
                for (var index = 0; index < onePerFrame.CurrentView.Balls.Count; index++)
                {
                    var slow = onePerFrame.CurrentView.Balls[index];
                    var fast = twoPerFrame.CurrentView.Balls[index];
                    Assert.AreEqual(slow.PositionM.x, fast.PositionM.x, "ball " + index + " x");
                    Assert.AreEqual(slow.PositionM.y, fast.PositionM.y, "ball " + index + " y");
                    Assert.AreEqual(slow.PositionM.z, fast.PositionM.z, "ball " + index + " z");
                    Assert.AreEqual(slow.VelocityMPerS.x, fast.VelocityMPerS.x,
                        "ball " + index + " vx");
                    Assert.AreEqual(slow.VelocityMPerS.y, fast.VelocityMPerS.y,
                        "ball " + index + " vy");
                    Assert.AreEqual(slow.VelocityMPerS.z, fast.VelocityMPerS.z,
                        "ball " + index + " vz");
                }
            }
            Assert.AreEqual(200, onePerFrame.CurrentView.Tick);
            Assert.AreEqual(onePerFrame.CurrentView.ViewId, twoPerFrame.CurrentView.ViewId);
        }
    }
}
