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
            public readonly HashSet<string> OpenContacts = new();
            public readonly HashSet<string> HitContacts = new();
            public readonly HashSet<string> Cleared = new();
            public readonly Dictionary<string, SweptClosure> CloseResults = new();
            public Vector3 ArmedCenter;
            public float ArmedRadius;
            public string LastClosedReason = "";
            public SweptClosure CloseResult;

            public bool HasTrial => HasTrialValue;
            public bool SupportsSandbagContacts => SupportsValue;
            public int CurrentTick => TickValue;
            public TemperatureReadoutSnapshot Readout => null;

            public bool HasOpenContact(string contactId) =>
                OpenContacts.Contains(contactId);

            public bool OpenContactHasHits(string contactId) =>
                HitContacts.Contains(contactId);

            public void OpenSandbagContact(string contactId, int tick)
            {
                if (!OpenContacts.Add(contactId))
                    throw new InvalidOperationException("contact_already_open");
            }

            public void SubmitHoverAbsorber(string contactId, Vector3 centerM, float radiusM)
            {
                Assert.IsTrue(OpenContacts.Contains(contactId));
                ArmedCenter = centerM;
                ArmedRadius = radiusM;
            }

            public SweptClosure CloseSandbagContact(string contactId, string reason)
            {
                Assert.IsTrue(OpenContacts.Contains(contactId));
                LastClosedReason = reason;
                OpenContacts.Remove(contactId);
                HitContacts.Remove(contactId);
                if (CloseResults.TryGetValue(contactId, out var closure))
                    return closure;
                return CloseResult;
            }

            public void ClearHoverAbsorber(string contactId) { Cleared.Add(contactId); }
        }

        static SweptClosure ContactedClosure(string contactId, double workJ) =>
            new(contactId, SandbagTool.PropId, 0, 1, workJ, "absorbed",
                new EnergyContactEntry(contactId, SandbagTool.PropId, "ball-0001",
                    0, 1, workJ, "absorbed"),
                true);

        // ---- writer: first valid impact damps once, then the bag is gone ----

        [Test]
        public void OneBagSlowsTheBallButNeverStopsIt()
        {
            var accepted = AcceptedPinned("sandbag-row");
            var ball = Ball("ball-0001", new Vector3(-0.9f, 1f, 0f),
                new Vector3(1f, 0f, 0f));
            var writer = WriterWith(new[] { ball }, accepted.pinned);
            var bagX = new[] { -0.6f, -0.3f, 0f, 0.3f, 0.6f };
            for (var index = 0; index < bagX.Length; index++)
                writer.SetHoverAbsorber("bag-" + index,
                    new Vector3(bagX[index], 1f, 0f),
                    SandbagTool.DeclaredAbsorberRadiusM,
                    SandbagTool.DeclaredEnergyDeltaJ,
                    SandbagTool.DeclaredSpeedFloorFactor);
            var triggered = new HashSet<string>();
            for (var tick = 1; tick <= 400; tick++)
            {
                writer.Advance(tick, FrozenWriterConfiguration.FixedDeltaTimeSeconds);
                foreach (var hit in writer.LastAppliedDampings)
                {
                    triggered.Add(hit.InteractionId);
                    Assert.Greater(ball.VelocityMPerS.magnitude, 0f,
                        "a slowed ball keeps flying after each bag");
                    Assert.Less(hit.EnergyDeltaJ, 0.0,
                        "each bag reports a measured absorption");
                    Assert.Greater(hit.EnergyDeltaJ, -SandbagTool.DeclaredEnergyDeltaJ - 1e-9,
                        "a bag never takes more than its declared cap");
                }
            }
            Assert.AreEqual(bagX.Length, triggered.Count,
                "every bag in the ball's path triggers");
            Assert.AreEqual("free", ball.Membership);
        }

        [Test]
        public void HoverAbsorberDampsTheFirstBallOnce()
        {
            var accepted = AcceptedPinned("sandbag-once");
            var fast = Ball("ball-0001", new Vector3(0f, 1f, 0f), new Vector3(2f, 0f, 0f));
            var far = Ball("ball-0002", new Vector3(0.6f, 1f, 0.6f), new Vector3(0f, 0f, 1f));
            var writer = WriterWith(new[] { fast, far }, accepted.pinned);
            var before = fast.VelocityMPerS;

            writer.SetHoverAbsorber("sandbag-contact-00000001-0001", fast.PositionM,
                SandbagTool.DeclaredAbsorberRadiusM, SandbagTool.DeclaredEnergyDeltaJ,
                SandbagTool.DeclaredSpeedFloorFactor);
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
        public void TwoArmedAbsorbersDampTwoBallsInTheSameTick()
        {
            var accepted = AcceptedPinned("sandbag-two-bags");
            var first = Ball("ball-0001", new Vector3(-0.3f, 1f, 0f),
                new Vector3(2f, 0f, 0f));
            var second = Ball("ball-0002", new Vector3(0.3f, 1f, 0f),
                new Vector3(2f, 0f, 0f));
            var writer = WriterWith(new[] { first, second }, accepted.pinned);
            var firstBefore = first.VelocityMPerS;
            var secondBefore = second.VelocityMPerS;

            writer.SetHoverAbsorber("sandbag-contact-00000001-0001", first.PositionM,
                SandbagTool.DeclaredAbsorberRadiusM, SandbagTool.DeclaredEnergyDeltaJ,
                SandbagTool.DeclaredSpeedFloorFactor);
            writer.SetHoverAbsorber("sandbag-contact-00000001-0002", second.PositionM,
                SandbagTool.DeclaredAbsorberRadiusM, SandbagTool.DeclaredEnergyDeltaJ,
                SandbagTool.DeclaredSpeedFloorFactor);
            writer.Advance(1, FrozenWriterConfiguration.FixedDeltaTimeSeconds);

            var hits = writer.LastAppliedDampings;
            Assert.AreEqual(2, hits.Count,
                "both armed bags trigger; neither replaces the other");
            var firstHit = hits[0];
            var secondHit = hits[1];
            Assert.AreEqual("ball-0001", firstHit.EntityId);
            Assert.AreEqual("ball-0002", secondHit.EntityId);
            Assert.AreEqual(-SandbagTool.DeclaredEnergyDeltaJ, firstHit.EnergyDeltaJ, 1e-9);
            Assert.AreEqual(-SandbagTool.DeclaredEnergyDeltaJ, secondHit.EnergyDeltaJ, 1e-9);
            Assert.Less(first.VelocityMPerS.magnitude, firstBefore.magnitude);
            Assert.Less(second.VelocityMPerS.magnitude, secondBefore.magnitude);

            writer.Advance(2, FrozenWriterConfiguration.FixedDeltaTimeSeconds);
            Assert.AreEqual(0, writer.LastAppliedDampings.Count,
                "each bag spends its one absorption exactly once");
        }

        [Test]
        public void RestingBallDoesNotSpendTheBag()
        {
            var accepted = AcceptedPinned("sandbag-rest");
            var rest = Ball("ball-0001", new Vector3(0f, 1f, 0f), Vector3.zero);
            var writer = WriterWith(new[] { rest }, accepted.pinned);

            writer.SetHoverAbsorber("sandbag-contact-00000001-0001", rest.PositionM,
                SandbagTool.DeclaredAbsorberRadiusM, SandbagTool.DeclaredEnergyDeltaJ,
                SandbagTool.DeclaredSpeedFloorFactor);
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
                SandbagTool.DeclaredAbsorberRadiusM, SandbagTool.DeclaredEnergyDeltaJ,
                SandbagTool.DeclaredSpeedFloorFactor);
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
                "id", Vector3.zero, 0.07f, 0.5, 0.5f));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "", Vector3.zero, 0.07f, 0.5, 0.5f));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", Vector3.zero, 0f, 0.5, 0.5f));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", Vector3.zero, 0.07f, -1.0, 0.5f));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", Vector3.zero, 0.07f, 0.0, 0.5f));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", Vector3.zero, 0.07f, double.NaN, 0.5f));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", new Vector3(float.NaN, 0f, 0f), 0.07f, 0.5, 0.5f));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", Vector3.zero, 0.07f, 0.5, -0.1f));
            Assert.Throws<ArgumentException>(() => writer.SetHoverAbsorber(
                "id", Vector3.zero, 0.07f, 0.5, 1.5f));
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
            Assert.IsTrue(runtime.HasAnyOpenContactHits);

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

        [Test]
        public void TwoOpenContactsCloseIntoTwoLedgerEntries()
        {
            var balls = new List<PublishedBall>
            {
                Published("ball-0001", new Vector3(2f, 0f, 0f)),
                Published("ball-0002", new Vector3(0f, 0f, 1f))
            };
            var runtime = LedgerRuntime("ledger-two-bags", balls, 1);
            runtime.OpenContact("sandbag-contact-00000000-0001",
                SandbagTool.PropId, 0);
            runtime.OpenContact("sandbag-contact-00000001-0002",
                SandbagTool.PropId, 1);

            runtime.RecordAppliedHits("sandbag-contact-00000000-0001",
                new[] { new AppliedSweepHit("sandbag-contact-00000000-0001",
                    "ball-0001", -1.0) });
            runtime.RecordAppliedHits("sandbag-contact-00000001-0002",
                new[] { new AppliedSweepHit("sandbag-contact-00000001-0002",
                    "ball-0002", -0.5) });

            Assert.AreEqual(2, runtime.OpenContactIds.Count,
                "two bags hold two simultaneous contacts");
            Assert.IsTrue(runtime.HasOpenContactHits("sandbag-contact-00000000-0001"));
            Assert.IsTrue(runtime.HasOpenContactHits("sandbag-contact-00000001-0002"));

            runtime.CloseContact("sandbag-contact-00000000-0001", "absorbed");
            runtime.CloseContact("sandbag-contact-00000001-0002", "absorbed");

            Assert.AreEqual(2, runtime.Ledger.Contacts.Count,
                "each bag closes its own ledger entry");
            Assert.AreEqual(1.5, runtime.Ledger.ESandbagJ, 1e-12);
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
            Assert.AreEqual(0, noFace.OpenContacts.Count,
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
            var absorbedContact = "";
            bag.Absorbed += (contactId, center, joules) =>
            {
                absorbedContact = contactId;
                absorbedAt = center;
                absorbedJ = joules;
            };
            host.HitContacts.Add(placed.ContactId);
            host.CloseResults[placed.ContactId] =
                ContactedClosure(placed.ContactId, -0.375);
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
            host.HitContacts.Add(first.ContactId);
            host.CloseResults[first.ContactId] =
                ContactedClosure(first.ContactId, -0.2);
            Assert.AreEqual(SandbagAbsorbOutcome.Absorbed, bag.PumpAbsorption().Outcome);

            host.HitContacts.Clear();
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

            host.OpenContacts.Clear();
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
            Assert.IsTrue(host.Cleared.Contains(placed.ContactId),
                "the hover region is disarmed");
            Assert.IsFalse(host.HasOpenContact(placed.ContactId));
            Assert.AreEqual(0, bag.AbsorptionCount);
            Assert.AreEqual(0.0, bag.AbsorbedTotalJ);
            Assert.IsFalse(bag.Withdraw("bag_moved"),
                "there is nothing left to withdraw");

            host.Cleared.Clear();
            host.HitContacts.Clear();
            Assert.IsTrue(bag.Place(new Vector3(0.1f, 1.2f, 0f), 4).WasPlaced,
                "the next float arms a fresh region");
            Assert.AreEqual(2, bag.PlacementCount);
        }

        [Test]
        public void TwoPlacementsAbsorbIndependently()
        {
            var host = new ScriptedSandbagHost();
            var bag = new SandbagTool(host);
            var first = bag.Place(new Vector3(-0.3f, 1.2f, 0f), 0);
            var second = bag.Place(new Vector3(0.3f, 1.2f, 0f), 1);
            Assert.IsTrue(first.WasPlaced);
            Assert.IsTrue(second.WasPlaced, "a second bag does not replace the first");
            Assert.AreEqual(2, bag.PlacementCount);
            Assert.IsTrue(bag.HasPlacement(first.ContactId));
            Assert.IsTrue(bag.HasPlacement(second.ContactId));

            var absorbed = new List<string>();
            bag.Absorbed += (contactId, center, joules) => absorbed.Add(contactId);
            host.HitContacts.Add(first.ContactId);
            host.HitContacts.Add(second.ContactId);
            host.CloseResults[first.ContactId] =
                ContactedClosure(first.ContactId, -0.3);
            host.CloseResults[second.ContactId] =
                ContactedClosure(second.ContactId, -0.2);

            var result = bag.PumpAbsorption();

            Assert.AreEqual(SandbagAbsorbOutcome.Absorbed, result.Outcome);
            Assert.AreEqual(2, bag.AbsorptionCount, "both bags trigger in one poll");
            CollectionAssert.AreEquivalent(
                new[] { first.ContactId, second.ContactId }, absorbed);
            Assert.AreEqual(0.5, bag.AbsorbedTotalJ, 1e-12);
            Assert.AreEqual(SandbagState.Consumed, bag.State);
            Assert.IsFalse(bag.HasPlacement(first.ContactId));
            Assert.IsFalse(bag.HasPlacement(second.ContactId));
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
