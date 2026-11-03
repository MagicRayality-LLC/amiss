using System.Collections.Generic;
using System.IO;
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
    /// Edit-mode tests for the device simulation runtime slice: the pinned
    /// collision representation, the one-writer slot, the sizing-derived ball
    /// count, and the fail-closed start gate. The analytic test writer stands
    /// in for a writer that needs no engine; the custom hard-sphere writer is
    /// covered by <see cref="CustomHardSphereWriterTests"/>.
    /// </summary>
    public class SimulationRuntimeTests
    {
        sealed class TestWriter : IScientificWriter
        {
            public string WriterId => "test-analytic-v1";
            public string ConfigurationId => "test-analytic-v1";
            public WriterDisposition Disposition => WriterDisposition.ProductTrial;
            public IReadOnlyList<string> ConfigurationRecord =>
                new[] { "writer_configuration_id=test-analytic-v1" };
            public BallState[] Balls;
            public int SetupCount;
            public int AdvanceCount;
            public bool TornDown;
            public SimulationTrialSetup SetupRecord;

            public void Setup(SimulationTrialSetup setup)
            {
                SetupRecord = setup;
                Balls = ((BallState[])setup.Balls);
                SetupCount++;
            }

            public void Advance(int tick, float deltaTimeSeconds)
            {
                AdvanceCount++;
                foreach (var ball in Balls)
                    ball.PositionM += ball.VelocityMPerS * deltaTimeSeconds;
            }

            public void Teardown() => TornDown = true;
        }

        static RoomPreparation ConfirmedRoom(Vector3 size, float swingRadius = 0.9f)
        {
            var manual = RoomDraft.CreateManual(
                new Bounds(new Vector3(0f, size.y * 0.5f, 0f), size), Vector3.zero, Vector3.forward);
            var surfaces = manual.Surfaces.Select(surface => new CollisionSurface(
                surface.SurfaceId, surface.SemanticLabel, surface.Primary,
                BoundaryProvenance.Scanned, surface.Role, surface.Center, surface.Size,
                surface.Rotation, surface.Normal, surface.Approximation)).ToArray();
            var draft = new RoomDraft(manual.Region, surfaces, Vector3.zero, Vector3.forward,
                swingRadius);
            var room = new RoomPreparation();
            var id = room.Begin(1.0);
            room.RequestPermission(id);
            room.CompletePermission(id, true);
            room.CompleteSceneLoad(id, draft);
            Assert.IsTrue(room.Confirm(id), room.Failure.ToString());
            return room;
        }

        static (RoomPreparation room, ShellRecovery recovery, CollisionPin pin) AcceptedRoom(
            Vector3 size, string trialId, float swingRadius = 0.9f)
        {
            var room = ConfirmedRoom(size, swingRadius);
            var recovery = new ShellRecovery();
            Assert.IsTrue(recovery.ConfirmRoom(room));
            var pin = recovery.AcceptTrialSetup(trialId, room.Confirmation.CollisionChecksum);
            Assert.IsNotNull(pin);
            return (room, recovery, pin);
        }

        [Test]
        public void BallCountFollowsTheDeclaredSizingRule()
        {
            // even(clamp(round(8 * V_eff), 36, 72)), mirroring the oracle.
            Assert.AreEqual(36, TemperatureSpaceSizing.Compute(3.0).ComputedN);
            Assert.AreEqual(36, TemperatureSpaceSizing.Compute(4.5).ComputedN);
            Assert.AreEqual(40, TemperatureSpaceSizing.Compute(5.12).ComputedN);
            Assert.AreEqual(72, TemperatureSpaceSizing.Compute(9.0).ComputedN);
            Assert.AreEqual(72, TemperatureSpaceSizing.Compute(40.0).ComputedN);
            Assert.AreEqual(40, TemperatureSpaceSizing.EvenBallCount(41));

            var below = TemperatureSpaceSizing.Compute(2.9);
            Assert.AreEqual("refused", below.Status);
            Assert.AreEqual("below_minimum_volume", below.ReasonCode);
            Assert.IsNull(below.ComputedN);

            var invalid = TemperatureSpaceSizing.Compute(double.NaN);
            Assert.AreEqual("refused", invalid.Status);
            Assert.AreEqual("invalid_volume", invalid.ReasonCode);
            Assert.IsNull(invalid.ComputedN);
        }

        [Test]
        public void ConfirmedRoomSizesTheStartedTrialWithoutForcing72()
        {
            var accepted = AcceptedRoom(new Vector3(1.6f, 2f, 1.6f), "trial-sized", 0.3f);
            var writer = new TestWriter();
            var runtime = new DeviceSimulationRuntime(() => writer);
            var request = TemperatureTrialSetup.CreateTrialStart("trial-sized",
                accepted.room.Confirmation, accepted.pin, out var sizing);

            var outcome = runtime.TryStartTrial(request);

            Assert.IsTrue(outcome.Started, outcome.ReasonCode);
            Assert.AreEqual("valid", sizing.Status);
            Assert.AreEqual(40, sizing.ComputedN);
            Assert.AreEqual(40, outcome.BallCount);
            Assert.AreNotEqual(72, outcome.BallCount);
            Assert.AreEqual(40, writer.Balls.Length);
            Assert.AreEqual(1, writer.SetupCount);
            Assert.AreEqual(0, writer.AdvanceCount, "the writer binds before the first Tick");
            Assert.AreEqual(40, runtime.TrialSummary.BallCount);
            Assert.AreEqual(1, runtime.Writers.BoundWriterCount);
            Assert.AreEqual(40, writer.SetupRecord.Balls.Count);
        }

        [Test]
        public void PinSurvivesRescanAndTheRuntimeKeepsTheFrozenRepresentation()
        {
            var accepted = AcceptedRoom(new Vector3(4f, 2.5f, 4f), "trial-pin");
            var writer = new TestWriter();
            var runtime = new DeviceSimulationRuntime(() => writer);
            var outcome = runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "trial-pin", accepted.room.Confirmation, accepted.pin, out _));
            Assert.IsTrue(outcome.Started, outcome.ReasonCode);
            var pinnedChecksum = outcome.CollisionChecksum;
            var originalSurfaceCount = accepted.pin.Representation.Surfaces.Count;

            accepted.recovery.BeginFutureRescan();
            accepted.room.Rescan();
            var nextId = accepted.room.PreparationId;
            accepted.room.RequestPermission(nextId);
            accepted.room.CompletePermission(nextId, true);
            var smaller = RoomDraft.CreateManual(
                new Bounds(new Vector3(0f, 1f, 0f), new Vector3(3f, 2f, 3f)),
                Vector3.zero, Vector3.forward);
            accepted.room.CompleteSceneLoad(nextId, smaller);
            Assert.IsTrue(accepted.room.Confirm(nextId));
            Assert.IsTrue(accepted.recovery.ConfirmRoom(accepted.room));

            Assert.AreNotEqual(pinnedChecksum, accepted.room.Collision.Checksum,
                "the rescan builds a new representation for future Trials");
            Assert.AreEqual(pinnedChecksum, accepted.recovery.HistoricalPins["trial-pin"].CollisionChecksum);
            Assert.AreEqual(pinnedChecksum, runtime.TrialSummary.CollisionChecksum,
                "the live Trial keeps its pinned representation across a rescan");
            Assert.AreEqual(originalSurfaceCount, accepted.pin.Representation.Surfaces.Count);
            Assert.IsTrue(accepted.pin.Representation.VerifyIntegrity());
            Assert.IsFalse(CollisionBridge.TryBind(accepted.room.Confirmation, accepted.pin,
                out _, out var refusal));
            Assert.AreEqual(TrialStartRefusal.CollisionPinMismatch, refusal,
                "the old pin is never silently rebound to the rescanned room");

            var step = runtime.Step(1);
            Assert.AreEqual(1, step.Executed, "the pinned Trial keeps advancing after the rescan");
        }

        [Test]
        public void RuntimeRefusesToStartWithoutAConfirmedRoomOrCollisionPin()
        {
            var runtime = new DeviceSimulationRuntime(() => new TestWriter());

            Assert.AreEqual(TrialStartRefusal.RequestInvalid, runtime.TryStartTrial(null).Refusal);

            var prepared = RoomTestFixtures.Prepared();
            Assert.IsNull(prepared.Confirmation);
            Assert.IsFalse(prepared.CanCreateTrial);
            Assert.AreEqual(TrialStartRefusal.RoomNotConfirmed, runtime.TryStartTrial(
                new TrialStartRequest
                {
                    TrialId = "trial-unconfirmed",
                    BallCount = 36,
                    EffectiveVolumeM3 = 5f
                }).Refusal);

            var first = AcceptedRoom(new Vector3(4f, 2.5f, 4f), "trial-first");
            var withoutPin = TemperatureTrialSetup.CreateTrialStart("trial-first",
                first.room.Confirmation, null, out _);
            Assert.AreEqual(TrialStartRefusal.CollisionPinMissing,
                runtime.TryStartTrial(withoutPin).Refusal);

            var second = AcceptedRoom(new Vector3(3f, 2f, 3f), "trial-second");
            var mismatched = TemperatureTrialSetup.CreateTrialStart("trial-first",
                first.room.Confirmation, second.pin, out _);
            Assert.AreEqual(TrialStartRefusal.CollisionPinMismatch,
                runtime.TryStartTrial(mismatched).Refusal);

            var wrongSizing = TemperatureTrialSetup.CreateTrialStart("trial-first",
                first.room.Confirmation, first.pin, out _);
            var correctCount = wrongSizing.BallCount;
            wrongSizing.BallCount = correctCount + 1;
            Assert.AreEqual(TrialStartRefusal.SizingInvalid,
                runtime.TryStartTrial(wrongSizing).Refusal);
            wrongSizing.BallCount = correctCount;
            wrongSizing.EffectiveVolumeM3 += 1f;
            Assert.AreEqual(TrialStartRefusal.SizingMismatch,
                runtime.TryStartTrial(wrongSizing).Refusal);

            Assert.IsFalse(runtime.HasTrial);
            Assert.AreEqual(0, runtime.Writers.BoundWriterCount);
            Assert.IsNull(runtime.TrialSummary);
        }

        [Test]
        public void ExactlyOneWriterBindsPerTrial()
        {
            var slot = new ScientificWriterSlot();
            var first = new TestWriter();
            var second = new TestWriter();
            Assert.IsTrue(slot.TryBind("trial", first, "cfg", out _));
            Assert.IsTrue(slot.TryBind("trial", first, "cfg", out _),
                "re-binding the same writer is idempotent");
            Assert.IsFalse(slot.TryBind("trial", second, "cfg", out var reason));
            Assert.AreEqual("writer_already_bound", reason);
            Assert.AreEqual(1, slot.BoundWriterCount);
            Assert.AreSame(first, slot.WriterFor("trial"));

            var accepted = AcceptedRoom(new Vector3(4f, 2.5f, 4f), "trial-one-writer");
            var writer = new TestWriter();
            var runtime = new DeviceSimulationRuntime(() => writer);
            var request = TemperatureTrialSetup.CreateTrialStart("trial-one-writer",
                accepted.room.Confirmation, accepted.pin, out _);
            var started = runtime.TryStartTrial(request);
            Assert.IsTrue(started.Started, started.ReasonCode);
            Assert.AreEqual(1, runtime.Writers.BoundWriterCount);
            Assert.AreSame(writer, runtime.WriterBindingFor("trial-one-writer").Writer);

            var again = runtime.TryStartTrial(request);
            Assert.IsFalse(again.Started);
            Assert.AreEqual(TrialStartRefusal.LiveSlotOccupied, again.Refusal);
            Assert.AreEqual(1, runtime.Writers.BoundWriterCount);
            Assert.AreSame(writer, runtime.WriterBindingFor("trial-one-writer").Writer);
            Assert.AreEqual(1, writer.SetupCount);
        }

        [Test]
        public void TicksPublishImmutableViewsAndPauseFreezesAdvance()
        {
            var accepted = AcceptedRoom(new Vector3(4f, 2.5f, 4f), "trial-ticks");
            var writer = new TestWriter();
            var runtime = new DeviceSimulationRuntime(() => writer);
            Assert.IsTrue(runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "trial-ticks", accepted.room.Confirmation, accepted.pin, out _)).Started);
            Assert.IsNull(runtime.PreviousView);
            Assert.AreEqual(0, runtime.CurrentView.Tick);

            var step = runtime.Step(10);
            Assert.AreEqual(FrozenWriterConfiguration.MaxTicksPerSchedulingSlice, step.Executed,
                "a slice never exceeds the declared whole-Tick bound");
            Assert.AreEqual(2, runtime.Tick);
            Assert.AreEqual(2, writer.AdvanceCount);

            var newer = runtime.CurrentView;
            var older = runtime.PreviousView;
            Assert.AreEqual(1, older.Tick);
            Assert.AreEqual(2, newer.Tick);
            var olderPosition = older.Balls[0].PositionM;
            runtime.Step(1);
            Assert.AreEqual(olderPosition, older.Balls[0].PositionM,
                "a published view never changes after the fact");
            Assert.AreEqual(3, runtime.CurrentView.Tick);

            runtime.RequestPause("panel_local");
            var paused = runtime.Step(2);
            Assert.AreEqual(0, paused.Executed);
            Assert.IsTrue(paused.Paused);
            Assert.AreEqual(3, runtime.Tick);
            Assert.AreEqual(3, writer.AdvanceCount);
            runtime.RequestResume();
            Assert.AreEqual(1, runtime.Step(1).Executed);
        }

        [Test]
        public void StoppingATrialClearsItsBallsAndReadoutAndAllowsANewTrial()
        {
            var accepted = AcceptedRoom(new Vector3(4f, 2.5f, 4f), "trial-stop");
            var writers = new Queue<TestWriter>(new[] { new TestWriter(), new TestWriter() });
            var runtime = new DeviceSimulationRuntime(() => writers.Dequeue());
            var request = TemperatureTrialSetup.CreateTrialStart("trial-stop",
                accepted.room.Confirmation, accepted.pin, out _);
            Assert.IsTrue(runtime.TryStartTrial(request).Started);
            runtime.Step(2);
            Assert.IsTrue(runtime.HasTrial);
            Assert.IsNotNull(runtime.CurrentView);
            Assert.IsNotNull(runtime.Readout);
            Assert.Greater(runtime.CurrentView.Balls.Count, 0);

            runtime.StopTrial("knife_switch_open");

            Assert.IsFalse(runtime.HasTrial);
            Assert.IsNull(runtime.CurrentView, "the stopped trial publishes no balls");
            Assert.IsNull(runtime.PreviousView);
            Assert.IsNull(runtime.Readout, "the stopped trial publishes no readout");
            Assert.IsNull(runtime.TrialSummary);
            Assert.AreEqual("knife_switch_open", runtime.StopReason);
            Assert.AreEqual(1, writers.Count, "exactly the stopped trial's writer had set up");
            Assert.AreEqual(0, runtime.Writers.BoundWriterCount);

            var pin = accepted.recovery.AcceptTrialSetup("trial-stop",
                accepted.room.Confirmation.CollisionChecksum);
            Assert.IsNotNull(pin, "the stopped setup is accepted again for a new trial");
            Assert.IsTrue(runtime.TryStartTrial(request).Started,
                "the same confirmed room can start the next trial");
            Assert.IsTrue(runtime.HasTrial);
            Assert.IsNotNull(runtime.Readout);
        }

        [Test]
        public void WriterConfigurationRecordNamesEveryDesignField()
        {
            var configuration = new FrozenWriterConfiguration("collision-0001", "checksum",
                CollisionRepresentation.CurrentFormatVersion, 42,
                FrozenWriterConfiguration.DefaultSeedStreamId);
            var lines = configuration.Record();
            var keys = lines.Select(line => line.Split('=')[0]).ToArray();

            foreach (var key in new[]
                     {
                         "fixed_delta_time_id", "fixed_delta_time_s", "solver_iterations",
                         "solver_velocity_iterations", "bounce_threshold_m_per_s",
                         "contact_offset_m", "collision_detection_mode", "interpolation",
                         "enhanced_determinism", "material_dynamic_friction",
                         "material_static_friction", "material_bounciness",
                         "material_friction_combine", "material_bounce_combine",
                         "ball_mass_kg", "ball_radius_m", "gravity_m_per_s2",
                         "linear_damping", "angular_damping", "sleep_enabled",
                         "seed_expansion_id", "stream_layout_version", "seed_stream_id",
                         "root_seed", "collision_representation_id", "collision_checksum",
                         "collision_format_version"
                     })
                CollectionAssert.Contains(keys, key);

            Assert.IsTrue(lines.Any(line => line.Contains(
                FrozenWriterConfiguration.Uncalibrated)));
            Assert.IsTrue(lines.Any(line => line.Contains(
                FrozenWriterConfiguration.SolverGateEvidence)));
            Assert.IsTrue(lines.Contains("collision_detection_mode=" +
                                         CollisionDetectionMode.ContinuousSpeculative));
        }

        [Test]
        public void DeclaredEnhancedDeterminismMatchesTheProjectSetting()
        {
            var settings = File.ReadAllText("ProjectSettings/DynamicsManager.asset");
            StringAssert.Contains("m_EnableEnhancedDeterminism: 1", settings);
            Assert.AreEqual("project_setting_enabled",
                FrozenWriterConfiguration.EnhancedDeterminism);
        }

        [Test]
        public void NamedStreamsMirrorTheHeadlessOracle()
        {
            // Expected values computed by simulation/runtime.py NamedRandomStreams(42).
            var streams = new NamedRandomStreams(42);
            var draws = streams.Draw(FrozenWriterConfiguration.DefaultSeedStreamId, 6);
            var expected = new[]
            {
                0.8423811574224903, 0.06010100991117628, 0.7795074618837577,
                0.33529191762485056, 0.18706071751693798, 0.5791495981611136
            };
            for (var index = 0; index < expected.Length; index++)
                Assert.AreEqual(expected[index], draws[index], 1e-15,
                    "draw " + index + " must match the oracle");
            Assert.AreEqual(6, streams.DrawCount("initial-setup"));
            Assert.AreEqual(0, streams.DrawCount("presentation-hue"));
        }
    }
}
