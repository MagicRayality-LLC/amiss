using System;
using System.Collections.Generic;
using System.Linq;
using Amiss.Shell.Room;
using Amiss.Shell.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    internal static class RoomTestFixtures
    {
        internal static RoomDraft Valid(BoundaryProvenance provenance = BoundaryProvenance.Scanned)
        {
            var manual = RoomDraft.CreateManual(new Bounds(new Vector3(0f, 1.25f, 0f),
                new Vector3(4f, 2.5f, 4f)), Vector3.zero, Vector3.forward);
            if (provenance == BoundaryProvenance.ManualVirtual)
                return manual;
            var surfaces = manual.Surfaces.Select(s => new CollisionSurface(s.SurfaceId, s.SemanticLabel,
                s.Primary, provenance, s.Role, s.Center, s.Size, s.Rotation, s.Normal, s.Approximation)).ToArray();
            return new RoomDraft(manual.Region, surfaces, Vector3.zero, Vector3.forward, 0.9f);
        }

        internal static RoomDraft WithObstacle(Vector3 center, Vector3 size)
        {
            var source = Valid();
            var surfaces = source.Surfaces.ToList();
            surfaces.Add(new CollisionSurface("table", "TABLE", PrimaryRepresentation.BoundingVolume,
                BoundaryProvenance.Scanned, CollisionSurfaceRole.Obstacle, center, size,
                Quaternion.identity, Vector3.up, "test volume"));
            return new RoomDraft(source.Region, surfaces, Vector3.zero, Vector3.forward, 0.9f);
        }

        internal static RoomPreparation Prepared(RoomDraft draft = null)
        {
            var preparation = new RoomPreparation();
            var id = preparation.Begin(1.0);
            preparation.RequestPermission(id);
            preparation.CompletePermission(id, true);
            preparation.CompleteSceneLoad(id, draft ?? Valid());
            return preparation;
        }

        internal static RoomPreparation Confirmed(RoomDraft draft = null)
        {
            var preparation = Prepared(draft);
            Assert.IsTrue(preparation.Confirm(preparation.PreparationId));
            return preparation;
        }
    }

    public class RoomPreparationTests
    {
        [Test]
        public void PermissionDenialFailsClosed()
        {
            var room = new RoomPreparation();
            var id = room.Begin(1.0);
            room.RequestPermission(id);
            room.CompletePermission(id, false);
            Assert.AreEqual(RoomPreparationStatus.Restricted, room.Status);
            Assert.AreEqual(RoomFailureReason.ScenePermission, room.Failure);
            Assert.IsFalse(room.RoomConfirmed);
            Assert.IsFalse(room.CanCreateTrial);
            Assert.IsNull(room.Collision);
        }

        [Test]
        public void ExistingCurrentRoomSkipsSpaceSetup()
        {
            var room = new RoomPreparation();
            var id = room.Begin(1.0);
            room.RequestPermission(id);
            room.CompletePermission(id, true);
            Assert.AreEqual(RoomPreparationStatus.SceneLoading, room.Status);
            room.CompleteSceneLoad(id, RoomTestFixtures.Valid());
            Assert.AreEqual(RoomPreparationStatus.AwaitingConfirmation, room.Status);
            Assert.IsFalse(room.RequestSpaceSetup(id));
            Assert.IsNotNull(room.Draft);
        }

        [Test]
        public void MissingSceneOffersExplicitSpaceSetup()
        {
            var room = new RoomPreparation();
            var id = room.Begin(1.0);
            room.RequestPermission(id);
            room.CompletePermission(id, true);
            room.CompleteSceneLoad(id, null, RoomFailureReason.NoScene);
            Assert.AreEqual(RoomPreparationStatus.SpaceSetupReady, room.Status);
            Assert.AreEqual(RoomFailureReason.NoScene, room.Failure);
            Assert.IsFalse(room.RoomConfirmed);
            Assert.IsTrue(room.RequestSpaceSetup(id));
        }

        [Test]
        public void OtherLocationRoomOffersScanWithoutFallback()
        {
            var room = new RoomPreparation();
            var id = room.Begin(1.0);
            room.RequestPermission(id);
            room.CompletePermission(id, true);
            room.CompleteSceneLoad(id, null, RoomFailureReason.NoCurrentRoom);
            Assert.AreEqual(RoomPreparationStatus.SpaceSetupReady, room.Status);
            Assert.AreEqual(RoomFailureReason.NoCurrentRoom, room.Failure);
            Assert.IsNull(room.Draft);
            Assert.IsTrue(room.ChooseManualBoundary(id, Vector3.zero, Vector3.forward));
            Assert.AreEqual("virtual boundaries — not the scanned room", room.Draft.BoundaryLabel);
        }

        [Test]
        public void SpaceSetupCancelAndFailureAreRestricted()
        {
            foreach (var cancelled in new[] { true, false })
            {
                var room = new RoomPreparation();
                var id = room.Begin(1.0);
                room.RequestPermission(id);
                room.CompletePermission(id, true);
                room.CompleteSceneLoad(id, null, RoomFailureReason.NoScene);
                room.RequestSpaceSetup(id);
                room.CompleteSpaceSetup(id, false, cancelled);
                Assert.AreEqual(RoomPreparationStatus.Restricted, room.Status);
                Assert.AreEqual(cancelled ? RoomFailureReason.SpaceSetupCancelled :
                    RoomFailureReason.SpaceSetupFailed, room.Failure);
            }
        }

        [Test]
        public void MissingSceneAfterSetupFailsClosedWithoutFallback()
        {
            var room = new RoomPreparation();
            var id = room.Begin(1.0);
            room.RequestPermission(id);
            room.CompletePermission(id, true);
            room.CompleteSceneLoad(id, null, RoomFailureReason.NoScene);
            room.RequestSpaceSetup(id);
            room.CompleteSpaceSetup(id, true);
            room.CompleteSceneLoad(id, null, RoomFailureReason.NoCurrentRoom);
            Assert.AreEqual(RoomPreparationStatus.Restricted, room.Status);
            Assert.AreEqual(RoomFailureReason.NoCurrentRoom, room.Failure);
            Assert.IsNull(room.Draft);
            Assert.IsNull(room.Collision);
        }

        [Test]
        public void SpaceSetupReturnReloadsCurrentRoom()
        {
            var room = new RoomPreparation();
            var id = room.Begin(1.0);
            room.RequestPermission(id);
            room.CompletePermission(id, true);
            room.CompleteSceneLoad(id, null, RoomFailureReason.NoScene);
            room.RequestSpaceSetup(id);
            room.CompleteSpaceSetup(id, true);
            Assert.AreEqual(RoomPreparationStatus.SceneLoading, room.Status);
            room.CompleteSceneLoad(id, RoomTestFixtures.Valid());
            Assert.AreEqual(RoomPreparationStatus.AwaitingConfirmation, room.Status);
            Assert.IsTrue(room.Confirm(id));
        }

        [Test]
        public void InitialLoadFailureDoesNotOfferSpaceSetup()
        {
            var room = new RoomPreparation();
            var id = room.Begin(1.0);
            room.RequestPermission(id);
            room.CompletePermission(id, true);
            room.CompleteSceneLoad(id, null, RoomFailureReason.SceneLoadFailed);
            Assert.AreEqual(RoomPreparationStatus.Restricted, room.Status);
            Assert.IsFalse(room.RequestSpaceSetup(id));
        }

        [Test]
        public void StaleCallbacksCannotMutateANewPreparation()
        {
            var room = new RoomPreparation();
            var stale = room.Begin(1.0);
            room.RequestPermission(stale);
            room.Rescan();
            Assert.IsFalse(room.CompletePermission(stale, true));
            Assert.AreEqual(RoomPreparationStatus.Explanation, room.Status);
            Assert.AreNotEqual(stale, room.PreparationId);
            var sceneStale = room.PreparationId;
            room.RequestPermission(sceneStale);
            room.CompletePermission(sceneStale, true);
            room.Rescan();
            Assert.IsFalse(room.CompleteSceneLoad(sceneStale, RoomTestFixtures.Valid()));
            Assert.AreEqual(RoomPreparationStatus.Explanation, room.Status);
            Assert.IsNull(room.Draft);
        }

        [Test]
        public void IncompleteScanNeedsExplicitSupplementsBeforeConfirmation()
        {
            var draft = RoomTestFixtures.Valid();
            var incomplete = new RoomDraft(draft.Region,
                draft.Surfaces.Where(s => s.Role != CollisionSurfaceRole.Wall).ToArray(),
                Vector3.zero, Vector3.forward, 0.9f);
            var room = RoomTestFixtures.Prepared(incomplete);
            Assert.AreEqual(RoomFailureReason.DisconnectedRegion, room.Failure);
            Assert.IsFalse(room.Confirm(room.PreparationId));
            room.AddVirtualSupplements(room.PreparationId);
            Assert.IsTrue(room.Confirm(room.PreparationId));
            StringAssert.Contains("virtual supplements", room.Confirmation.BoundaryLabel);
        }

        [Test]
        public void ScannedConfirmationProducesVerifiedChecksumHandoff()
        {
            var room = RoomTestFixtures.Confirmed();
            Assert.IsTrue(room.RoomConfirmed);
            Assert.AreEqual(64, room.Collision.Checksum.Length);
            Assert.IsTrue(room.Collision.VerifyIntegrity());
            Assert.AreEqual("confirmed scanned room", room.Confirmation.BoundaryLabel);
            Assert.AreEqual(room.Collision.Checksum, room.Confirmation.CollisionChecksum);
            Assert.IsFalse(room.CanCreateTrial);
        }

        [Test]
        public void ManualPathIsAlwaysLabelledVirtual()
        {
            var room = RoomTestFixtures.Confirmed(RoomTestFixtures.Valid(BoundaryProvenance.ManualVirtual));
            Assert.AreEqual("virtual boundaries — not the scanned room", room.Confirmation.BoundaryLabel);
            Assert.IsTrue(room.Collision.Surfaces.All(s => s.Provenance == BoundaryProvenance.ManualVirtual));
        }

        [Test]
        public void ManualChoiceUsesTheFloorLevelUserFrame()
        {
            var room = new RoomPreparation();
            var id = room.Begin(1.0);
            room.RequestPermission(id);
            room.CompletePermission(id, false);
            Assert.IsTrue(room.ChooseManualBoundary(id, Vector3.zero, Vector3.forward));
            Assert.AreEqual(RoomFailureReason.None, room.Failure);
            Assert.IsTrue(room.Confirm(id));
            Assert.AreEqual(0f, room.Confirmation.Region.min.y, 0.0001f);
        }

        [Test]
        public void EffectiveVolumeSubtractsOnlyObstacleIntersection()
        {
            var room = RoomTestFixtures.Confirmed(RoomTestFixtures.WithObstacle(
                new Vector3(1.5f, 0.5f, 0f), Vector3.one));
            Assert.AreEqual(39f, room.Collision.EffectiveVolumeM3, 0.0001f);
            StringAssert.Contains("minus", room.Collision.DeductionMethod);
        }

        [Test]
        public void SteppingClearOfAnObstacleLetsTheSameScanConfirm()
        {
            var source = RoomTestFixtures.Valid();
            var surfaces = source.Surfaces.ToList();
            surfaces.Add(new CollisionSurface("table", "TABLE", PrimaryRepresentation.BoundingVolume,
                BoundaryProvenance.Scanned, CollisionSurfaceRole.Obstacle, Vector3.zero, new Vector3(0.6f, 1f, 0.6f),
                Quaternion.identity, Vector3.up, "test volume"));
            var region = new Bounds(new Vector3(0f, 1.25f, 0f), new Vector3(8f, 2.5f, 8f));
            var blocked = new RoomDraft(region, surfaces, Vector3.zero, Vector3.forward, 0.9f);
            var room = RoomTestFixtures.Prepared(blocked);

            Assert.AreEqual(RoomFailureReason.UnsafeRegion, room.Failure);
            Assert.IsFalse(room.Confirm(room.PreparationId));
            Assert.IsTrue(room.RetargetUser(new Vector3(2f, 0f, 0f), Vector3.forward));
            Assert.AreEqual(RoomFailureReason.None, room.Failure);
            Assert.IsTrue(room.Confirm(room.PreparationId));
        }

        [Test]
        public void SafetyOffConfirmsAnUnsafeRegion()
        {
            var source = RoomTestFixtures.Valid();
            var surfaces = source.Surfaces.ToList();
            surfaces.Add(new CollisionSurface("table", "TABLE", PrimaryRepresentation.BoundingVolume,
                BoundaryProvenance.Scanned, CollisionSurfaceRole.Obstacle, Vector3.zero, new Vector3(0.6f, 1f, 0.6f),
                Quaternion.identity, Vector3.up, "test volume"));
            var blocked = new RoomDraft(new Bounds(new Vector3(0f, 1.25f, 0f), new Vector3(8f, 2.5f, 8f)),
                surfaces, Vector3.zero, Vector3.forward, 0.9f);
            var room = RoomTestFixtures.Prepared(blocked);

            Assert.AreEqual(RoomFailureReason.UnsafeRegion, room.Failure);
            Assert.IsFalse(room.Confirm(room.PreparationId));
            Assert.IsTrue(room.Confirm(room.PreparationId, true));
            Assert.AreEqual(RoomPreparationStatus.Confirmed, room.Status);
        }

        [Test]
        public void ALoadedRoomCanOfferAReplacementScan()
        {
            var room = RoomTestFixtures.Prepared();
            Assert.IsTrue(room.OfferReplacementScan(room.PreparationId));
            Assert.AreEqual(RoomPreparationStatus.SpaceSetupReady, room.Status);
            Assert.IsFalse(room.OfferReplacementScan(room.PreparationId));
        }

        [Test]
        public void BelowMinimumAndUnsafeRegionsCannotConfirm()
        {
            var small = RoomDraft.CreateManual(new Bounds(new Vector3(0f, 0.5f, 0f),
                new Vector3(0.5f, 1f, 0.5f)), Vector3.zero, Vector3.forward);
            Assert.AreEqual(RoomFailureReason.BelowMinimum, small.Validate(1.0));
            var unsafeDraft = RoomTestFixtures.WithObstacle(Vector3.up * 0.5f, Vector3.one);
            Assert.AreEqual(RoomFailureReason.UnsafeRegion, unsafeDraft.Validate(1.0));
        }

        [Test]
        public void NonFiniteGeometryIsRejected()
        {
            var draft = RoomTestFixtures.Valid();
            var invalid = new RoomDraft(new Bounds(new Vector3(float.NaN, 1f, 0f), Vector3.one * 3f),
                draft.Surfaces, Vector3.zero, Vector3.forward, 0.9f);
            Assert.AreEqual(RoomFailureReason.InvalidGeometry, invalid.Validate(1.0));
        }

        [Test]
        public void ChecksumIsOrderIndependentAndGeometrySensitive()
        {
            var draft = RoomTestFixtures.Valid();
            Assert.IsTrue(CollisionRepresentation.TryCreate("0001", draft, 1.0, out var first, out _));
            var reversed = new RoomDraft(draft.Region, draft.Surfaces.Reverse().ToArray(),
                Vector3.zero, Vector3.forward, 0.9f);
            Assert.IsTrue(CollisionRepresentation.TryCreate("0001", reversed, 1.0, out var second, out _));
            Assert.AreEqual(first.Checksum, second.Checksum);
            var changed = RoomTestFixtures.WithObstacle(new Vector3(1.5f, 0.5f, 0f), Vector3.one);
            Assert.IsTrue(CollisionRepresentation.TryCreate("0001", changed, 1.0, out var third, out _));
            Assert.AreNotEqual(first.Checksum, third.Checksum);
            Assert.IsTrue(first.Surfaces.All(s => s.Primary == PrimaryRepresentation.Plane));
            Assert.IsTrue(third.Surfaces.Any(s => s.Primary == PrimaryRepresentation.BoundingVolume));
        }

        [Test]
        public void RotatedBoundsIncludeEveryTransformedCorner()
        {
            var bounds = RoomGeometry.RotatedBounds(Vector3.zero, new Vector3(2f, 1f, 1f),
                Quaternion.Euler(0f, 90f, 0f));
            Assert.AreEqual(1f, bounds.size.x, 0.0001f);
            Assert.AreEqual(2f, bounds.size.z, 0.0001f);
        }

        [Test]
        public void CollisionSurfaceCollectionIsReadOnly()
        {
            var room = RoomTestFixtures.Confirmed();
            var list = room.Collision.Surfaces as IList<CollisionSurface>;
            Assert.IsNotNull(list);
            Assert.IsTrue(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
    }

    public class ShellRecoveryTests
    {
        [Test]
        public void SetupAcceptanceIsSeparateAndChecksumBound()
        {
            var room = RoomTestFixtures.Confirmed();
            var recovery = new ShellRecovery();
            Assert.IsTrue(recovery.ConfirmRoom(room));
            Assert.IsFalse(recovery.SetupAccepted);
            Assert.IsFalse(recovery.CanCreateTrial("trial"));
            Assert.IsNull(recovery.AcceptTrialSetup("trial", "wrong"));
            var pin = recovery.AcceptTrialSetup("trial", room.Collision.Checksum);
            Assert.IsNotNull(pin);
            Assert.IsTrue(recovery.CanCreateTrial("trial"));
            Assert.AreSame(pin, recovery.AcceptTrialSetup("trial", room.Collision.Checksum));
        }

        [Test]
        public void RescanPreservesHistoricalPinAndCreatesFutureRepresentation()
        {
            var first = RoomTestFixtures.Confirmed();
            var recovery = new ShellRecovery();
            recovery.ConfirmRoom(first);
            var pin = recovery.AcceptTrialSetup("historical", first.Collision.Checksum);
            recovery.BeginFutureRescan();
            first.Rescan();
            var id = first.PreparationId;
            first.RequestPermission(id);
            first.CompletePermission(id, true);
            first.CompleteSceneLoad(id, RoomTestFixtures.Valid());
            Assert.IsTrue(first.Confirm(id));
            var next = first;
            recovery.ConfirmRoom(next);
            Assert.AreEqual(pin.CollisionChecksum, recovery.HistoricalPins["historical"].CollisionChecksum);
            Assert.AreNotEqual(pin.CollisionChecksum, next.Collision.Checksum);
            Assert.AreEqual(next.Collision.Checksum, recovery.CurrentCollision.Checksum);
            Assert.IsTrue(next.Collision.VerifyIntegrity());
        }

        [Test]
        public void SceneUpdateAndRelocalizationNeverRewriteHistoricalPin()
        {
            var room = RoomTestFixtures.Confirmed();
            var recovery = new ShellRecovery();
            recovery.ConfirmRoom(room);
            var pin = recovery.AcceptTrialSetup("trial", room.Collision.Checksum);
            recovery.SceneUpdated();
            recovery.RelocalizationChanged(false);
            Assert.IsTrue(recovery.SceneUpdateAvailable);
            Assert.AreEqual(PanelPlacementStatus.RecoveryNeeded, recovery.Panel.Status);
            Assert.AreEqual(pin.CollisionChecksum, recovery.HistoricalPins["trial"].CollisionChecksum);
        }

        [Test]
        public void FocusReturnDoesNotAutomaticallyResume()
        {
            var recovery = new ShellRecovery();
            recovery.FocusLost();
            Assert.IsTrue(recovery.PauseRequested);
            recovery.FocusReturned();
            Assert.IsTrue(recovery.PauseRequested);
            Assert.IsTrue(recovery.ResumeRequired);
            Assert.IsTrue(recovery.RequestResume());
            Assert.IsFalse(recovery.PauseRequested);
        }

        [Test]
        public void SafetyHoldNeedsReconfirmationThenExplicitResume()
        {
            var room = RoomTestFixtures.Confirmed();
            var recovery = new ShellRecovery();
            recovery.ConfirmRoom(room);
            recovery.InvalidateSafety();
            Assert.IsFalse(recovery.RequestResume());
            Assert.IsFalse(recovery.CanCreateTrial("trial"));
            Assert.IsTrue(recovery.ReconfirmSafety(room.Collision.Checksum));
            Assert.IsTrue(recovery.PauseRequested);
            Assert.IsTrue(recovery.RequestResume());
            Assert.IsFalse(recovery.SafetyHold);
        }

        [Test]
        public void InitialPanelIsOutsideSwingVolumeAndUsesFrozenEnvelope()
        {
            var room = RoomTestFixtures.Confirmed();
            var recovery = new ShellRecovery();
            recovery.ConfirmRoom(room);
            Assert.AreEqual(PanelMount.ScreenEnvelope, recovery.Panel.PanelEnvelope);
            Assert.AreEqual(PanelPlacementStatus.Placed, recovery.Panel.Status);
            Assert.AreEqual(PanelMountMode.WallMounted, recovery.Panel.MountMode);
            Assert.AreEqual(PanelPlacementSource.AutoNearestWall, recovery.Panel.Source);
            Assert.IsTrue(ShellRecovery.ValidatePanel(recovery.Panel, room.Confirmation));
            var directionToUser = room.Confirmation.ConfirmedUserPosition - recovery.Panel.Position;
            directionToUser.y = 0f;
            directionToUser.Normalize();
            Assert.Greater(Vector3.Dot(-(recovery.Panel.Rotation * Vector3.forward), directionToUser), 0.99f);
        }

        [Test]
        public void PanelCandidatesRejectSwingAndObstacleOverlap()
        {
            var room = RoomTestFixtures.Confirmed(RoomTestFixtures.WithObstacle(
                new Vector3(1.5f, 0.5f, 0f), Vector3.one));
            var swingCandidate = new PanelPlacement(Vector3.up, Quaternion.identity, new Vector2(1.2f, 0.8f), 1);
            Assert.IsFalse(ShellRecovery.ValidatePanel(swingCandidate, room.Confirmation));
            var obstacleCandidate = new PanelPlacement(new Vector3(1.4f, 1.3f, 0f), Quaternion.identity,
                new Vector2(1.2f, 0.8f), 1);
            Assert.IsFalse(ShellRecovery.ValidatePanel(obstacleCandidate, room.Confirmation));
        }

        [Test]
        public void ExplicitRelocationChangesOnlyPresentation()
        {
            var room = RoomTestFixtures.Confirmed();
            var recovery = new ShellRecovery();
            recovery.ConfirmRoom(room);
            var pin = recovery.AcceptTrialSetup("trial", room.Collision.Checksum);
            var checksum = pin.CollisionChecksum;
            Assert.IsTrue(recovery.RelocatePanel(new Vector3(0f, 1.3f, 2f),
                Quaternion.LookRotation(Vector3.back)));
            Assert.AreEqual(1, recovery.Panel.Revision);
            Assert.AreEqual(checksum, recovery.HistoricalPins["trial"].CollisionChecksum);
        }

        [Test]
        public void UnboundRuntimeReceivesPauseAndExplicitResume()
        {
            var runtime = new Amiss.Shell.UnboundSimulationRuntime();
            runtime.RequestPause("application_focus");
            Assert.IsTrue(runtime.PauseRequested);
            Assert.AreEqual("application_focus", runtime.PauseReason);
            runtime.RequestResume();
            Assert.IsFalse(runtime.PauseRequested);
        }
    }

    public class EvidenceManifestTests
    {
        [Test]
        public void SimulationEvidenceCapturesTheProducerReadoutVerbatim()
        {
            var measurement = TemperatureMeasurementFace.Measure("measurement-evidence",
                new[]
                {
                    new PublishedBall("ball-0001", Vector3.zero,
                        new Vector3(1f, 0.5f, 0.25f), 1f, 0.05f, "free")
                },
                new MeasurementWindow("window-evidence", 0, 4, 0.2f, 5), null);
            Assert.AreEqual(TemperatureMeasurement.NotApplicableStatus, measurement.Status);
            var manifest = SimulationEvidenceManifest.Create("what_is_temperature");
            manifest.CaptureReadout(new TemperatureReadoutSnapshot("trial-evidence", 4,
                "view-evidence", measurement,
                new TemperatureEnergyLedger(1.0, 1.0, 1.0, 0.0), 0));

            Assert.AreEqual("not_applicable", manifest.readout_status);
            Assert.AreEqual("", manifest.readout_kelvin,
                "a not-applicable measurement writes no kelvin number");
            Assert.AreEqual("", manifest.readout_celsius);
            Assert.AreEqual("", manifest.readout_fahrenheit);
            Assert.AreEqual("", manifest.readout_kelvin_ratio_baseline);
            Assert.AreEqual("K", manifest.readout_display_primary_unit);
            Assert.IsTrue(manifest.readout_panel_text.Contains("not applicable"));
        }

        [Test]
        public void SimulationEvidenceCapturesAValidTupleAndTheLedger()
        {
            var balls = new List<PublishedBall>();
            for (var index = 0; index < 12; index++)
            {
                var angle = index + 1.0;
                balls.Add(new PublishedBall("ball-" + index.ToString("D4"), Vector3.zero,
                    new Vector3((float)System.Math.Sin(angle),
                        (float)System.Math.Cos(angle), 0.5f), 1f, 0.05f, "free"));
            }
            var measurement = TemperatureMeasurementFace.Measure("measurement-evidence",
                balls, new MeasurementWindow("window-evidence", 0, 4, 0.2f, 5), 300.0);
            var ledger = new TemperatureEnergyLedger(10.0, 10.0, 8.0, 2.0)
                .WithContact(new EnergyContactEntry("contact-1", "bat", "ball-0001", 0, 4,
                    1.5, "separated"));
            var manifest = SimulationEvidenceManifest.Create("what_is_temperature");
            manifest.CaptureReadout(new TemperatureReadoutSnapshot("trial-evidence", 4,
                "view-evidence", measurement, ledger, 1));

            Assert.AreEqual("valid", manifest.readout_status);
            Assert.AreEqual(measurement.Display.Kelvin.ToString(),
                manifest.readout_kelvin);
            Assert.AreEqual(measurement.Display.Celsius.ToString(),
                manifest.readout_celsius);
            Assert.AreEqual(measurement.Display.Fahrenheit.ToString(),
                manifest.readout_fahrenheit);
            Assert.AreEqual(1.5, manifest.readout_ledger_work_events_cumulative_ke_j);
            Assert.AreEqual(1, manifest.readout_ledger_contact_count);
            Assert.AreEqual(1, manifest.readout_contact_commits);
            StringAssert.Contains("net work +1.50", manifest.readout_panel_text);
        }

        [Test]
        public void ManifestContainsOnlyDerivedSanitizedFields()
        {
            var room = RoomTestFixtures.Confirmed();
            var manifest = EvidenceManifest.Create();
            manifest.Record("room_confirm", "confirmed", "edit_mode_fixture", room);
            var json = manifest.ToJson();
            StringAssert.Contains(room.Collision.Checksum, json);
            StringAssert.Contains("effective_volume_m3", json);
            StringAssert.DoesNotContain("device", json.ToLowerInvariant());
            StringAssert.DoesNotContain("account", json.ToLowerInvariant());
            StringAssert.DoesNotContain("mesh", json.ToLowerInvariant());
            StringAssert.DoesNotContain("trajectory", json.ToLowerInvariant());
            StringAssert.DoesNotContain("uuid", json.ToLowerInvariant());
        }
    }
}
