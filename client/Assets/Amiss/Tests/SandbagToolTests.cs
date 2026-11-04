using System;
using System.Collections.Generic;
using Amiss.Shell;
using Amiss.Shell.Episodes;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;
using Amiss.Shell.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// The P0 sandbag slice: one floating bag absorbs the first ball that
    /// reaches it, exactly once, into the ledger's sandbag account, then
    /// celebrates with the reference firework. 07 section 4.2 and SRD TEMP-TOOL
    /// own the mechanic; these tests pin the device mirror of the headless
    /// oracle's <c>register_prop</c> / <c>prop_trigger</c> / <c>record_facts</c>
    /// one-shot lifecycle.
    /// </summary>
    public class SandbagToolTests
    {
        static (RoomPreparation room, CollisionPin pin, PinnedCollision pinned) AcceptedPinned(
            string trialId)
        {
            // A 2 m room: the 1.8 m swing cube around the standing user must fit
            // inside the region or Confirm refuses with UnsafeRegion. A smaller
            // fixture room can never confirm, so it cannot host writer tests.
            var manual = RoomDraft.CreateManual(
                new Bounds(new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f)),
                Vector3.zero, Vector3.forward);
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

        static BallState Ball(string entityId, Vector3 position, Vector3 velocity) =>
            new(entityId, position, velocity,
                FrozenWriterConfiguration.BallMassKg, FrozenWriterConfiguration.BallRadiusM);

        static CustomHardSphereWriter WriterWith(BallState[] balls, PinnedCollision collision)
        {
            var writer = new CustomHardSphereWriter();
            writer.Setup(new SimulationTrialSetup("sandbag-" + balls.Length, balls, collision,
                new FrozenWriterConfiguration(collision.RepresentationId,
                    collision.Checksum, collision.FormatVersion, 7, "initial-setup")));
            return writer;
        }

        /// <summary>Scripted tool host: the test, not a Trial, drives contacts.</summary>
        sealed class ScriptedSandbagHost : ISandbagContactHost
        {
            public bool HasTrialValue = true;
            public bool SupportsValue = true;
            public int TickValue;
            public bool HasOpenContactValue;
            public string OpenContactIdValue = "";
            public bool HasHitsValue;
            public Vector3 ArmedCenter;
            public float ArmedRadius;
            public string LastClosedReason = "";
            public SweptClosure CloseResult;

            public bool HasTrial => HasTrialValue;
            public bool SupportsSandbagContacts => SupportsValue;
            public int CurrentTick => TickValue;
            public bool HasOpenContact => HasOpenContactValue;
            public string OpenContactId => OpenContactIdValue;
            public bool OpenContactHasHits => HasHitsValue;
            public TemperatureReadoutSnapshot Readout => null;

            public void OpenSandbagContact(string contactId, int tick)
            {
                if (HasOpenContactValue)
                    throw new InvalidOperationException("contact_already_open");
                HasOpenContactValue = true;
                OpenContactIdValue = contactId;
            }

            public void SubmitHoverAbsorber(string contactId, Vector3 centerM, float radiusM)
            {
                Assert.AreEqual(OpenContactIdValue, contactId);
                ArmedCenter = centerM;
                ArmedRadius = radiusM;
            }

            public SweptClosure CloseSandbagContact(string contactId, string reason)
            {
                Assert.AreEqual(OpenContactIdValue, contactId);
                LastClosedReason = reason;
                HasOpenContactValue = false;
                OpenContactIdValue = "";
                return CloseResult;
            }

            public bool Cleared;
            public void ClearHoverAbsorber() { Cleared = true; }
        }

        static SweptClosure ContactedClosure(string contactId, double workJ) =>
            new(contactId, SandbagTool.PropId, 0, 1, workJ, "absorbed",
                new EnergyContactEntry(contactId, SandbagTool.PropId, "ball-0001",
                    0, 1, workJ, "absorbed"),
                true);

        // ---- writer: first valid impact damps once, then the bag is gone ----

        [Test]
        public void HoverAbsorberDampsTheFirstBallOnce()
        {
            var accepted = AcceptedPinned("sandbag-once");
            var fast = Ball("ball-0001", new Vector3(0f, 1f, 0f), new Vector3(2f, 0f, 0f));
            var far = Ball("ball-0002", new Vector3(0.6f, 1f, 0.6f), new Vector3(0f, 0f, 1f));
            var writer = WriterWith(new[] { fast, far }, accepted.pinned);
            var before = fast.VelocityMPerS;

            writer.SetHoverAbsorber("sandbag-contact-00000001-0001", fast.PositionM,
                SandbagTool.DeclaredAbsorberRadiusM, SandbagTool.DeclaredEnergyDeltaJ);
            writer.Advance(1, FrozenWriterConfiguration.FixedDeltaTimeSeconds);

            var hits = writer.LastAppliedDampings;
            Assert.AreEqual(1, hits.Count, "exactly the first valid impact is reported");
            Assert.AreEqual("ball-0001", hits[0].EntityId);
            var beforeEnergy = 0.5 * before.sqrMagnitude;
            var expectedAfter = before * (float)Math.Sqrt(
                1.0 - SandbagTool.DeclaredEnergyDeltaJ / beforeEnergy);
            Assert.AreEqual(expectedAfter.x, fast.VelocityMPerS.x, 1e-6f);
            Assert.AreEqual(0.5 * 1.0 * (expectedAfter.sqrMagnitude - before.sqrMagnitude),
                hits[0].EnergyDeltaJ, 1e-9,
                "the measured delta is the absorbed pre/post ball-energy change");
            Assert.AreEqual(-SandbagTool.DeclaredEnergyDeltaJ, hits[0].EnergyDeltaJ, 1e-9,
                "the bag removes exactly one declared 10-unit absorption");
            Assert.Less(hits[0].EnergyDeltaJ, 0.0, "slowing the ball absorbs energy");
            Assert.AreEqual("free", fast.Membership, "the ball keeps flying free");

            writer.Advance(2, FrozenWriterConfiguration.FixedDeltaTimeSeconds);
            Assert.AreEqual(0, writer.LastAppliedDampings.Count,
                "the consumed bag absorbs no second ball");
        }

        [Test]
        public void RestingBallDoesNotSpendTheBag()
        {
            var accepted = AcceptedPinned("sandbag-rest");
            var rest = Ball("ball-0001", new Vector3(0f, 1f, 0f), Vector3.zero);
            var writer = WriterWith(new[] { rest }, accepted.pinned);

            writer.SetHoverAbsorber("sandbag-contact-00000001-0001", rest.PositionM,
                SandbagTool.DeclaredAbsorberRadiusM, SandbagTool.DeclaredEnergyDeltaJ);
            writer.Advance(1, FrozenWriterConfiguration.FixedDeltaTimeSeconds);
            writer.Advance(2, FrozenWriterConfiguration.FixedDeltaTimeSeconds);

            Assert.AreEqual(0, writer.LastAppliedDampings.Count,
                "a numerically resting ball is not a valid impact; the bag keeps hovering");
        }

        [Test]
        public void HeldBallNeverTriggersAbsorption()
        {
            var accepted = AcceptedPinned("sandbag-held");
            var held = Ball("ball-0001", new Vector3(0f, 1f, 0f), new Vector3(3f, 0f, 0f));
            held.Membership = "held";
            var writer = WriterWith(new[] { held }, accepted.pinned);

            writer.SetHoverAbsorber("sandbag-contact-00000001-0001", held.PositionM,
                SandbagTool.DeclaredAbsorberRadiusM, SandbagTool.DeclaredEnergyDeltaJ);
            writer.Advance(1, FrozenWriterConfiguration.FixedDeltaTimeSeconds);

            Assert.AreEqual(0, writer.LastAppliedDampings.Count);
            Assert.AreEqual(new Vector3(3f, 0f, 0f), held.VelocityMPerS);
        }

        [Test]
        public void AbsorberArgumentsAreValidated()
        {
            var accepted = AcceptedPinned("sandbag-args");
            var writer = WriterWith(
                new[] { Ball("ball-0001", Vector3.zero, Vector3.zero) }, accepted.pinned);
            var unconfigured = new CustomHardSphereWriter();

            Assert.Throws<InvalidOperationException>(() => unconfigured.SetHoverAbsorber(
                "id", Vector3.zero, 0.07f, 0.5));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "", Vector3.zero, 0.07f, 0.5));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", Vector3.zero, 0f, 0.5));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", Vector3.zero, 0.07f, -1.0));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", Vector3.zero, 0.07f, 0.0));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", Vector3.zero, 0.07f, double.NaN));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", new Vector3(float.NaN, 0f, 0f), 0.07f, 0.5));
        }

        // ---- ledger: absorption joins the sandbag account, never free energy ----

        static TemperatureTrialRuntime LedgerRuntime(string trialId,
            IReadOnlyList<PublishedBall> balls, int tick)
        {
            return new TemperatureTrialRuntime(() => balls, () => tick, () => trialId);
        }

        static PublishedBall Published(string entityId, Vector3 velocity) =>
            new(entityId, new Vector3(0f, 1f, 0f), velocity,
                FrozenWriterConfiguration.BallMassKg, FrozenWriterConfiguration.BallRadiusM,
                "free");

        [Test]
        public void AbsorptionSettlesOneLedgerEntryAndSandbagAccount()
        {
            var balls = new List<PublishedBall>
            {
                Published("ball-0001", new Vector3(2f, 0f, 0f)),
                Published("ball-0002", new Vector3(0f, 0f, 1f))
            };
            var runtime = LedgerRuntime("ledger-sandbag", balls, 0);
            runtime.OpenContact("sandbag-contact-00000001-0001", SandbagTool.PropId, 0);
            runtime.RecordAppliedHits("sandbag-contact-00000001-0001",
                new[] { new AppliedSweepHit("sandbag-contact-00000001-0001", "ball-0001", -1.5) });
            Assert.IsTrue(runtime.HasOpenContactHits);

            var closure = runtime.CloseContact("sandbag-contact-00000001-0001", "absorbed");

            Assert.IsTrue(closure.Contacted);
            Assert.AreEqual(SandbagTool.PropId, closure.PropId);
            Assert.AreEqual("ball-0001", closure.Entry.TargetBallId);
            var ledger = runtime.Ledger;
            Assert.AreEqual(1, ledger.Contacts.Count);
            Assert.AreEqual(-1.5, ledger.WorkEventsCumulativeKeJ, 1e-12);
            Assert.AreEqual(1.5, ledger.ESandbagJ, 1e-12,
                "the measured negative work joins the separate sandbag account");
            Assert.Throws<ArgumentException>(() => ledger.WithSandbagAbsorption(-0.1));
        }

        // ---- tool: placement, polling, one-shot, abort ----

        [Test]
        public void PlaceIsRefusedWithoutTrialOrWriterFace()
        {
            var noTrial = new ScriptedSandbagHost { HasTrialValue = false };
            var refusedTrial = new SandbagTool(noTrial).Place(new Vector3(0f, 1.2f, 0f), 0);
            Assert.AreEqual(SandbagPlaceOutcome.NotAccepted, refusedTrial.Outcome);
            Assert.AreEqual("trial_not_prepared", refusedTrial.ReasonCode);

            var noFace = new ScriptedSandbagHost { SupportsValue = false };
            var refusedFace = new SandbagTool(noFace).Place(new Vector3(0f, 1.2f, 0f), 0);
            Assert.AreEqual(SandbagPlaceOutcome.NotAccepted, refusedFace.Outcome);
            Assert.AreEqual("writer_has_no_tool_effect_face", refusedFace.ReasonCode);
            Assert.IsFalse(noFace.HasOpenContactValue,
                "a refused placement arms no region the ledger cannot see");
        }

        [Test]
        public void PlaceArmsHoverAndPumpClosesExactlyOnce()
        {
            var host = new ScriptedSandbagHost();
            var bag = new SandbagTool(host);
            var anchor = new Vector3(0f, 1.2f, 0.4f);

            var placed = bag.Place(anchor, 3);
            Assert.IsTrue(placed.WasPlaced);
            Assert.AreEqual(SandbagState.Placed, bag.State);
            Assert.AreEqual(anchor, host.ArmedCenter);
            Assert.AreEqual(SandbagTool.DeclaredAbsorberRadiusM, host.ArmedRadius);
            Assert.AreEqual(1, bag.PlacementCount);

            var quiet = bag.PumpAbsorption();
            Assert.AreEqual(SandbagAbsorbOutcome.NoAbsorption, quiet.Outcome);
            Assert.AreEqual(SandbagState.Placed, bag.State,
                "the bag keeps floating until a ball reaches it");

            var absorbedJ = 0.0;
            var absorbedAt = new Vector3();
            bag.Absorbed += (center, joules) => { absorbedAt = center; absorbedJ = joules; };
            host.HasHitsValue = true;
            host.CloseResult = ContactedClosure(placed.ContactId, -0.375);
            var absorbed = bag.PumpAbsorption();

            Assert.AreEqual(SandbagAbsorbOutcome.Absorbed, absorbed.Outcome);
            Assert.AreEqual("absorbed", host.LastClosedReason);
            Assert.AreEqual("ball-0001", absorbed.TargetBallId);
            Assert.AreEqual(0.375, absorbed.AbsorbedJ, 1e-12);
            Assert.AreEqual(0.375, bag.LastAbsorbedJ, 1e-12);
            Assert.AreEqual(0.375, bag.AbsorbedTotalJ, 1e-12);
            Assert.AreEqual(anchor, absorbedAt);
            Assert.AreEqual(0.375, absorbedJ, 1e-12);
            Assert.AreEqual(SandbagState.Consumed, bag.State);
            Assert.AreEqual(1, bag.AbsorptionCount);

            var again = bag.PumpAbsorption();
            Assert.AreEqual(SandbagAbsorbOutcome.NotPlaced, again.Outcome,
                "the consumed contact closes exactly once");
        }

        [Test]
        public void PlacingAgainAfterConsumptionFloatsAFreshBag()
        {
            var host = new ScriptedSandbagHost();
            var bag = new SandbagTool(host);
            var first = bag.Place(new Vector3(0f, 1.2f, 0f), 0);
            host.HasHitsValue = true;
            host.CloseResult = ContactedClosure(first.ContactId, -0.2);
            Assert.AreEqual(SandbagAbsorbOutcome.Absorbed, bag.PumpAbsorption().Outcome);

            host.HasHitsValue = false;
            var second = bag.Place(new Vector3(0.2f, 1.1f, 0f), 5);
            Assert.IsTrue(second.WasPlaced);
            Assert.AreNotEqual(first.ContactId, second.ContactId);
            Assert.AreEqual(2, bag.PlacementCount);
            Assert.AreEqual(SandbagAbsorbOutcome.NoAbsorption, bag.PumpAbsorption().Outcome);
        }

        [Test]
        public void LostContactAbortsWithoutAnEntry()
        {
            var host = new ScriptedSandbagHost();
            var bag = new SandbagTool(host);
            bag.Place(new Vector3(0f, 1.2f, 0f), 0);

            host.HasOpenContactValue = false;
            host.OpenContactIdValue = "";
            var aborted = bag.PumpAbsorption();

            Assert.AreEqual(SandbagAbsorbOutcome.Aborted, aborted.Outcome);
            Assert.AreEqual(SandbagState.Ready, bag.State);
            Assert.AreEqual(0, bag.AbsorptionCount);
            Assert.AreEqual(0.0, bag.AbsorbedTotalJ);
        }

        [Test]
        public void WithdrawDisarmsWithoutAnEntry()
        {
            var host = new ScriptedSandbagHost();
            var bag = new SandbagTool(host);
            var placed = bag.Place(new Vector3(0f, 1.2f, 0f), 0);
            Assert.IsTrue(placed.WasPlaced);

            Assert.IsTrue(bag.Withdraw("bag_moved"));
            Assert.AreEqual(SandbagState.Ready, bag.State);
            Assert.AreEqual("bag_moved", host.LastClosedReason,
                "the moved bag closes its contact instead of absorbing");
            Assert.IsTrue(host.Cleared, "the hover region is disarmed");
            Assert.IsFalse(host.HasOpenContactValue);
            Assert.AreEqual(0, bag.AbsorptionCount);
            Assert.AreEqual(0.0, bag.AbsorbedTotalJ);
            Assert.IsFalse(bag.Withdraw("bag_moved"),
                "there is nothing left to withdraw");

            host.Cleared = false;
            host.HasHitsValue = false;
            Assert.IsTrue(bag.Place(new Vector3(0.1f, 1.2f, 0f), 4).WasPlaced,
                "the next float arms a fresh region");
            Assert.AreEqual(2, bag.PlacementCount);
        }

        // ---- presentation: the celebratory reference firework ----

        [Test]
        public void DispersalCarriesNoPhysicsAndStaysInBudget()
        {
            var root = SandbagModel.TriggerDispersal(new Vector3(0f, 1.2f, 0.4f),
                SandbagModel.ReferenceAbsorptionJ);
            try
            {
                Assert.IsNotNull(root, "dispersal builds a transient root");
                Assert.IsTrue(root.GetComponentsInChildren<Collider>(true).Length == 0,
                    "dispersal debris has no collider and cannot touch a ball");
                Assert.IsTrue(root.GetComponentsInChildren<Rigidbody>(true).Length == 0,
                    "dispersal debris has no physics body");
                Assert.IsTrue(root.GetComponentsInChildren<Light>(true).Length == 0,
                    "dispersal uses no hot light");
                var systems = root.GetComponentsInChildren<ParticleSystem>(true);
                Assert.GreaterOrEqual(systems.Length, 3,
                    "burst plus flash plus falling glitter or confetti");
                var budget = 0;
                foreach (var system in systems)
                {
                    budget += system.main.maxParticles;
                    var renderMode = system.GetComponent<ParticleSystemRenderer>().renderMode;
                    Assert.IsTrue(
                        renderMode == ParticleSystemRenderMode.Billboard ||
                        renderMode == ParticleSystemRenderMode.Stretch,
                        "every firework system is a sprite system, never a mesh");
                }
                Assert.LessOrEqual(budget, SandbagModel.DispersalParticleBudget);
            }
            finally
            {
                if (root != null)
                    UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void MountFailsClosed()
        {
            Assert.IsFalse(SandbagModel.TryMount(null, out var view));
            Assert.IsNull(view);
        }
    }
}
