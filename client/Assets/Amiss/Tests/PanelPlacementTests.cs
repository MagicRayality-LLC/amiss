using System.Collections.Generic;
using System.Linq;
using Amiss.Shell;
using Amiss.Shell.Room;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;

namespace Amiss.Tests
{
    public class PanelPlacementTests
    {
        static Bounds Region() => new Bounds(new Vector3(0f, 1.25f, 0f), new Vector3(4f, 2.5f, 4f));

        static CollisionSurface Floor() => new("floor", "FLOOR", PrimaryRepresentation.Plane,
            BoundaryProvenance.Scanned, CollisionSurfaceRole.Floor, Vector3.zero,
            new Vector3(4f, 0.02f, 4f), Quaternion.identity, Vector3.up, "test plane");

        static CollisionSurface Floor(Vector3 regionSize) => new("floor", "FLOOR", PrimaryRepresentation.Plane,
            BoundaryProvenance.Scanned, CollisionSurfaceRole.Floor, Vector3.zero,
            new Vector3(regionSize.x, 0.02f, regionSize.z), Quaternion.identity, Vector3.up, "test plane");

        static CollisionSurface Wall(string id, Vector3 center, Vector3 normal, float width,
            float height, BoundaryProvenance provenance = BoundaryProvenance.Scanned) =>
            new(id, "WALL_FACE", PrimaryRepresentation.Plane, provenance, CollisionSurfaceRole.Wall,
                center, new Vector3(width, height, 0.02f), Quaternion.LookRotation(normal, Vector3.up),
                normal, "test plane");

        static RoomDraft Draft(Vector3 userForward, params CollisionSurface[] walls) =>
            new(Region(), new[] { Floor() }.Concat(walls).ToArray(), Vector3.zero, userForward, 0.9f);

        static RoomDraft StandardRoom(Vector3? userForward = null) => Draft(userForward ?? Vector3.forward,
            Wall("left", new Vector3(-2f, 1.25f, 0f), Vector3.right, 4f, 2.5f),
            Wall("right", new Vector3(2f, 1.25f, 0f), Vector3.left, 4f, 2.5f),
            Wall("back", new Vector3(0f, 1.25f, -2f), Vector3.forward, 4f, 2.5f),
            Wall("front", new Vector3(0f, 1.25f, 2f), Vector3.back, 4f, 2.5f));

        static ShellRecovery Confirmed(RoomDraft draft)
        {
            var preparation = new RoomPreparation();
            var id = preparation.Begin(1.0);
            preparation.RequestPermission(id);
            preparation.CompletePermission(id, true);
            preparation.CompleteSceneLoad(id, draft);
            Assert.IsTrue(preparation.Confirm(id), preparation.Failure.ToString());
            var recovery = new ShellRecovery();
            Assert.IsTrue(recovery.ConfirmRoom(preparation));
            return recovery;
        }

        [Test]
        public void AutomaticMountWaitsForConfirmationAndPicksTheForwardWall()
        {
            var recovery = new ShellRecovery();
            Assert.IsNull(recovery.Panel);
            var confirmed = Confirmed(StandardRoom());
            Assert.IsNotNull(confirmed.Panel);
            Assert.AreEqual(PanelMountMode.WallMounted, confirmed.Panel.MountMode);
            Assert.AreEqual(PanelPlacementSource.AutoNearestWall, confirmed.Panel.Source);
            Assert.Less(Vector3.Distance(new Vector3(0f, 1.35f, 1.89f), confirmed.Panel.Position), 0.02f);
            Assert.AreEqual("", confirmed.Panel.Reason);
        }

        [Test]
        public void TooSmallNearestWallIsSkipped()
        {
            var draft = Draft(Vector3.back,
                Wall("left", new Vector3(-2f, 1.25f, 0f), Vector3.right, 4f, 2.5f),
                Wall("right", new Vector3(2f, 1.25f, 0f), Vector3.left, 4f, 2.5f),
                Wall("back", new Vector3(0f, 1.25f, -2f), Vector3.forward, 0.8f, 2.5f),
                Wall("front", new Vector3(0f, 1.25f, 2f), Vector3.back, 4f, 2.5f));
            var recovery = Confirmed(draft);
            Assert.AreEqual(PanelMountMode.WallMounted, recovery.Panel.MountMode);
            Assert.Greater(Vector3.Distance(new Vector3(0f, 1.35f, -1.89f), recovery.Panel.Position), 0.5f);
        }

        [Test]
        public void ObstructedNearestWallIsSkipped()
        {
            var source = StandardRoom();
            var surfaces = source.Surfaces.Concat(new[]
            {
                new CollisionSurface("table", "TABLE", PrimaryRepresentation.BoundingVolume,
                    BoundaryProvenance.Scanned, CollisionSurfaceRole.Obstacle,
                    new Vector3(0f, 1.2f, 1.7f), new Vector3(2f, 1.4f, 0.6f),
                    Quaternion.identity, Vector3.up, "test obstacle")
            }).ToArray();
            var draft = new RoomDraft(Region(), surfaces, Vector3.zero, Vector3.forward, 0.9f);
            var recovery = Confirmed(draft);
            Assert.AreEqual(PanelMountMode.WallMounted, recovery.Panel.MountMode);
            Assert.Greater(Vector3.Distance(new Vector3(0f, 1.35f, 1.89f), recovery.Panel.Position), 0.5f);
        }

        [Test]
        public void NoUsableWallUsesTheLabelledForwardFallback()
        {
            var manual = RoomDraft.CreateManual(Region(), Vector3.zero, Vector3.forward);
            var recovery = Confirmed(manual);
            Assert.AreEqual(PanelMountMode.ForwardFallback, recovery.Panel.MountMode);
            Assert.AreEqual(PanelMount.WallUnavailable, recovery.Panel.Reason);
            Assert.Less(Vector3.Distance(new Vector3(0f, 1.3f, 1.5f), recovery.Panel.Position), 0.02f);
            Assert.IsFalse(recovery.ResetPanelToNearestWall());
            Assert.AreEqual(PanelMountMode.ForwardFallback, recovery.Panel.MountMode);
        }

        [Test]
        public void VirtualSupplementCarriesThePanelWithVisibleProvenance()
        {
            var supplemented = RoomDraft.CreateManual(Region(), Vector3.zero, Vector3.forward)
                .WithVirtualSupplements();
            var recovery = Confirmed(supplemented);
            Assert.AreEqual(PanelMountMode.WallMounted, recovery.Panel.MountMode);
            Assert.AreEqual("virtual supplement", recovery.Panel.WallProvenance);
        }

        [Test]
        public void ReleaseAwayFromEveryWallRevertsWithoutRevision()
        {
            var recovery = Confirmed(StandardRoom());
            var before = recovery.Panel;
            Assert.IsFalse(recovery.TryRemountPanel(new Vector3(0f, 1.35f, 0f), Quaternion.identity,
                out var reason));
            Assert.AreEqual(PanelMount.ReleaseNotOnWall, reason);
            Assert.AreEqual(0, recovery.Panel.Revision);
            Assert.AreEqual(before.Position, recovery.Panel.Position);
            Assert.AreEqual(PanelPlacementStatus.Placed, recovery.Panel.Status);
        }

        [Test]
        public void WallToWallRemountCommitsExactlyOneRevision()
        {
            var recovery = Confirmed(StandardRoom());
            Assert.IsTrue(recovery.TryRemountPanel(new Vector3(-1.7f, 1.35f, 0f), Quaternion.identity,
                out _));
            Assert.AreEqual(1, recovery.Panel.Revision);
            Assert.AreEqual(PanelPlacementSource.UserRemount, recovery.Panel.Source);
            Assert.AreEqual(PanelMountMode.WallMounted, recovery.Panel.MountMode);
            Assert.Less(Vector3.Distance(new Vector3(-1.89f, 1.35f, 0f), recovery.Panel.Position), 0.02f);
        }

        [Test]
        public void ResetReturnsToTheNearestWall()
        {
            var recovery = Confirmed(StandardRoom());
            Assert.IsTrue(recovery.TryRemountPanel(new Vector3(-1.7f, 1.35f, 0f), Quaternion.identity,
                out _));
            Assert.IsTrue(recovery.ResetPanelToNearestWall());
            Assert.AreEqual(2, recovery.Panel.Revision);
            Assert.AreEqual(PanelPlacementSource.ResetNearestWall, recovery.Panel.Source);
            Assert.Less(Vector3.Distance(new Vector3(0f, 1.35f, 1.89f), recovery.Panel.Position), 0.02f);
        }

        [Test]
        public void MovementChangesNoCollisionPinOrSetupState()
        {
            var preparation = new RoomPreparation();
            var id = preparation.Begin(1.0);
            preparation.RequestPermission(id);
            preparation.CompletePermission(id, true);
            preparation.CompleteSceneLoad(id, StandardRoom());
            preparation.Confirm(id);
            var recovery = new ShellRecovery();
            recovery.ConfirmRoom(preparation);
            var pin = recovery.AcceptTrialSetup("trial", preparation.Confirmation.CollisionChecksum);
            var checksum = pin.CollisionChecksum;
            Assert.IsTrue(recovery.TryRemountPanel(new Vector3(-1.7f, 1.35f, 0f), Quaternion.identity,
                out _));
            Assert.IsTrue(recovery.ResetPanelToNearestWall());
            Assert.AreEqual(checksum, recovery.HistoricalPins["trial"].CollisionChecksum);
            Assert.AreEqual(preparation.Confirmation.CollisionChecksum, recovery.CurrentCollision.Checksum);
            Assert.IsTrue(recovery.SetupAccepted);
        }

        [Test]
        public void FallbackPoseIsNeverProducedByAUserRelease()
        {
            var recovery = Confirmed(StandardRoom());
            Assert.IsFalse(recovery.TryRemountPanel(new Vector3(0f, 2.6f, 0f), Quaternion.identity,
                out _));
            Assert.AreEqual(PanelMountMode.WallMounted, recovery.Panel.MountMode);
            Assert.AreEqual(0, recovery.Panel.Revision);
        }

        [Test]
        public void GrabSurfaceExcludesTheScreenAndAcceptsBezelAndBracket()
        {
            var placement = new PanelPlacement(Vector3.zero, Quaternion.identity,
                PanelMount.ScreenEnvelope, 0, PanelPlacementStatus.Placed, PanelMountMode.WallMounted,
                PanelPlacementSource.AutoNearestWall, "", "scanned");
            Assert.IsFalse(PanelMount.ContainsGrabSurface(placement, new Vector3(0.4f, 0f, 0f)));
            Assert.IsFalse(PanelMount.ContainsGrabSurface(placement, new Vector3(0f, 0.2f, 0f)));
            Assert.IsTrue(PanelMount.ContainsGrabSurface(placement, new Vector3(0.76f, 0f, 0f)));
            Assert.IsTrue(PanelMount.ContainsGrabSurface(placement, new Vector3(0f, -0.50f, 0f)));
            Assert.IsFalse(PanelMount.ContainsGrabSurface(placement, new Vector3(0f, 0f, 2f)));
        }

        [Test]
        public void KnifeSwitchSitsOnTheWallToTheRightOfTheTelevision()
        {
            var facingWall = Quaternion.LookRotation(Vector3.back, Vector3.up);
            var pose = KnifeSwitchPose.Beside(Vector3.zero, facingWall, true);
            Assert.AreEqual(-KnifeSwitchPose.LateralMeters, pose.position.x, 0.0001f);
            Assert.Less(pose.position.x, -(PanelMount.ScreenWidth * 0.5f + PanelMount.Bezel));
            Assert.AreEqual(KnifeSwitchPose.CenterHeightAboveTvCenterMeters, pose.position.y, 0.0001f);
            Assert.Less(pose.position.z, -0.05f);
            Assert.Greater(Vector3.Dot(pose.rotation * Vector3.forward, Vector3.back), 0.99f);

            var floating = KnifeSwitchPose.Beside(Vector3.zero, facingWall, false);
            Assert.AreEqual(0f, floating.position.z, 0.0001f);
            Assert.AreEqual(pose.position.x, floating.position.x, 0.0001f);
            Assert.AreEqual(pose.position.y, floating.position.y, 0.0001f);
        }

        static Transform KnifeSwitchPart(WallKnifeSwitch host, string name) =>
            host.GetComponentsInChildren<Transform>(true).First(t => t.name == name);

        static WallKnifeSwitch BuiltKnifeSwitch()
        {
            var host = new GameObject("WallKnifeSwitch");
            var component = host.AddComponent<WallKnifeSwitch>();
            component.Build();
            return component;
        }

        // Renderer bounds are computed rather than serialized, so every
        // placement claim below is measured from them, in the switch's frame.
        static Bounds KnifeSwitchLocalBounds(WallKnifeSwitch host, string name)
        {
            var bounds = KnifeSwitchPart(host, name).GetComponent<Renderer>().bounds;
            var min = host.transform.InverseTransformPoint(bounds.min);
            var max = host.transform.InverseTransformPoint(bounds.max);
            return new Bounds((min + max) * 0.5f, max - min);
        }

        static void SetBladeAngle(WallKnifeSwitch host, float angle) =>
            KnifeSwitchPart(host, "BladePivot").localRotation = Quaternion.Euler(angle, 0f, 0f);

        static Vector3 KnifeSwitchVertex(Transform label, Mesh mesh, int index) =>
            label.TransformPoint(mesh.vertices[index]);

        static Vector3 Average(IEnumerable<Vector3> points)
        {
            var sum = Vector3.zero;
            var count = 0;
            foreach (var point in points)
            {
                sum += point;
                count++;
            }
            return count > 0 ? sum / count : Vector3.zero;
        }

        [Test]
        public void KnifeSwitchBladeBridgesTheOnContactWhenClosed()
        {
            var switchHost = BuiltKnifeSwitch();
            try
            {
                SetBladeAngle(switchHost, WallKnifeSwitch.AngleOn);
                var pin = KnifeSwitchLocalBounds(switchHost, "KS_Pin_L");
                var blade = KnifeSwitchLocalBounds(switchHost, "KS_Blade_L");
                var tip = KnifeSwitchLocalBounds(switchHost, "KS_BladeTip_L");
                var roomProng = KnifeSwitchLocalBounds(switchHost, "KS_JawProng_LA");
                var wallProng = KnifeSwitchLocalBounds(switchHost, "KS_JawProng_LB");
                var handle = KnifeSwitchLocalBounds(switchHost, "KS_Handle");

                // The blade starts on the hinge pin at the OFF end and reaches the
                // ON contact 0.200 m above it, on the pin's standoff from the plate.
                Assert.AreEqual(pin.center.y, blade.min.y, 0.004f, "blade near end is not on the hinge pin");
                Assert.AreEqual(pin.center.z, blade.center.z, 0.003f, "blade is off the hinge pin standoff");
                Assert.AreEqual(roomProng.center.y, blade.max.y, 0.004f, "blade does not reach the ON contact");

                // Closed means the tip is inside the jaw, between its two prongs.
                Assert.Greater(tip.min.z, roomProng.max.z + 0.001f, "blade tip crosses the room-side prong");
                Assert.Less(tip.max.z, wallProng.min.z - 0.001f, "blade tip crosses the wall-side prong");
                Assert.Greater(tip.center.y, roomProng.min.y, "blade tip is short of the ON contact");
                Assert.Less(tip.center.y, roomProng.max.y, "blade tip overshoots the ON contact");

                // Running, the handle sits at the ON end, proud of the plate.
                Assert.Greater(handle.center.y, 0.04f, "closed handle is not at the ON end");
                Assert.Less(handle.center.z, -0.05f, "closed handle is behind the plate");
            }
            finally
            {
                Object.DestroyImmediate(switchHost.gameObject);
            }
        }

        [Test]
        public void KnifeSwitchBladeWithdrawsOnTheAuthoredOpenArc()
        {
            var switchHost = BuiltKnifeSwitch();
            try
            {
                var pivot = KnifeSwitchPart(switchHost, "BladePivot").localPosition;
                SetBladeAngle(switchHost, WallKnifeSwitch.AngleOn);
                var closedHandle = KnifeSwitchLocalBounds(switchHost, "KS_Handle").center - pivot;
                SetBladeAngle(switchHost, WallKnifeSwitch.AngleOff);
                var openHandle = KnifeSwitchLocalBounds(switchHost, "KS_Handle").center - pivot;

                // The tip leaves the ON contact and the plate altogether.
                var tip = KnifeSwitchLocalBounds(switchHost, "KS_BladeTip_L");
                var jaw = KnifeSwitchLocalBounds(switchHost, "KS_JawProng_LA");
                Assert.Less(tip.max.y, jaw.min.y, "blade tip is still in the ON contact");
                Assert.Less(tip.max.z, -0.14f, "blade tip has not left the plate");

                // The handle rides one rigid radius through the authored arc.
                var closedArc = Mathf.Atan2(closedHandle.z, closedHandle.y) * Mathf.Rad2Deg;
                var openArc = Mathf.Atan2(openHandle.z, openHandle.y) * Mathf.Rad2Deg;
                Assert.AreEqual(Mathf.Abs(WallKnifeSwitch.AngleOff - WallKnifeSwitch.AngleOn),
                    Mathf.Abs(Mathf.DeltaAngle(closedArc, openArc)), 0.5f, "handle travel is not the authored arc");
                Assert.AreEqual(new Vector2(closedHandle.y, closedHandle.z).magnitude,
                    new Vector2(openHandle.y, openHandle.z).magnitude, 0.001f, "handle does not ride one radius");
            }
            finally
            {
                Object.DestroyImmediate(switchHost.gameObject);
            }
        }

        [Test]
        public void KnifeSwitchMechanismStaysInFrontOfTheBackPlate()
        {
            var switchHost = BuiltKnifeSwitch();
            try
            {
                var plate = KnifeSwitchLocalBounds(switchHost, "KS_BasePlate");
                foreach (var angle in new[] { WallKnifeSwitch.AngleOn, WallKnifeSwitch.AngleOff })
                {
                    SetBladeAngle(switchHost, angle);
                    foreach (var name in new[] { "KS_Blade_L", "KS_Blade_R", "KS_BladeTip_L",
                                 "KS_BladeTip_R", "KS_Crossbar", "KS_Handle", "KS_HandleStem" })
                    {
                        var part = KnifeSwitchLocalBounds(switchHost, name);
                        Assert.Less(part.max.z, plate.min.z, name + " reaches behind the plate at " + angle);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(switchHost.gameObject);
            }
        }

        [Test]
        public void KnifeSwitchLetteringReadsFromTheRoomOnBothLabels()
        {
            var switchHost = BuiltKnifeSwitch();
            try
            {
                var pose = KnifeSwitchPose.Beside(new Vector3(0f, 1.35f, -2f),
                    Quaternion.LookRotation(Vector3.forward, Vector3.up), true);
                switchHost.transform.SetPositionAndRotation(pose.position, pose.rotation);
                var wearerRight = pose.rotation * Vector3.right;
                var room = -(pose.rotation * Vector3.forward);

                foreach (var (name, texture) in new[]
                         {
                             ("KS_Label_OFF", "Label_OFF"), ("KS_Label_ON", "Label_ON")
                         })
                {
                    var label = KnifeSwitchPart(switchHost, name);
                    var material = label.GetComponent<MeshRenderer>().sharedMaterial;
                    Assert.AreEqual(texture, material.GetTexture("_BaseMap").name, name);
                    var mesh = label.GetComponent<MeshFilter>().sharedMesh;

                    // The printed quad is the box face nearest the room, and it
                    // faces the room so the lettering is never read through its back.
                    var roomMost = mesh.vertices.Min(v => v.z);
                    var quad = Enumerable.Range(0, mesh.vertexCount)
                        .Where(i => Mathf.Abs(mesh.vertices[i].z - roomMost) < 0.0005f &&
                                    (Mathf.Approximately(mesh.uv[i].x, 0f) || Mathf.Approximately(mesh.uv[i].x, 1f)))
                        .ToArray();
                    Assert.AreEqual(4, quad.Length, name + " printed face");
                    foreach (var index in quad)
                        Assert.Greater(
                            Vector3.Dot(label.TransformDirection(mesh.normals[index]), room), 0.99f,
                            name + " printed face does not face the room");

                    // The texture's left edge must land on the wearer's left, so
                    // the lettering advances along the wearer's right, on both
                    // labels, through the material transform the build applies.
                    var scale = material.GetTextureScale("_BaseMap");
                    var offset = material.GetTextureOffset("_BaseMap");
                    var leftEdge = quad.Where(i => Mathf.Approximately(mesh.uv[i].x * scale.x + offset.x, 0f))
                        .Select(i => KnifeSwitchVertex(label, mesh, i)).ToArray();
                    var rightEdge = quad.Where(i => Mathf.Approximately(mesh.uv[i].x * scale.x + offset.x, 1f))
                        .Select(i => KnifeSwitchVertex(label, mesh, i)).ToArray();
                    Assert.AreEqual(2, leftEdge.Length, name + " texture left edge");
                    Assert.AreEqual(2, rightEdge.Length, name + " texture right edge");
                    var reads = Average(rightEdge) - Average(leftEdge);
                    Assert.Greater(Vector3.Dot(reads, wearerRight) / reads.magnitude, 0.99f,
                        name + " reads mirrored from the room");

                    // The flip is load bearing: the raw mapping would read backwards.
                    var rawReads = Average(quad.Where(i => Mathf.Approximately(mesh.uv[i].x, 1f))
                                            .Select(i => KnifeSwitchVertex(label, mesh, i))) -
                                   Average(quad.Where(i => Mathf.Approximately(mesh.uv[i].x, 0f))
                                            .Select(i => KnifeSwitchVertex(label, mesh, i)));
                    Assert.Less(Vector3.Dot(rawReads, wearerRight) / rawReads.magnitude, -0.99f,
                        name + " flip is not load bearing");
                }
            }
            finally
            {
                Object.DestroyImmediate(switchHost.gameObject);
            }
        }

        [Test]
        public void KnifeSwitchPlateAndSurfaceFaceTheRoomOnTheTelevisionWall()
        {
            var televisionPosition = new Vector3(0f, 1.35f, -2f);
            // A wall-mounted set points into the wall, so its screen faces the room.
            var televisionRotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            var pose = KnifeSwitchPose.Beside(televisionPosition, televisionRotation, true);
            var switchHost = BuiltKnifeSwitch();
            try
            {
                switchHost.transform.SetPositionAndRotation(pose.position, pose.rotation);
                var glassNormal = -(televisionRotation * Vector3.forward);
                var plateNormal = -(pose.rotation * Vector3.forward);
                var target = KnifeSwitchPart(switchHost, "KnifeSwitchTarget");
                var surfaceNormal = target.rotation * Vector3.back;
                var plate = KnifeSwitchLocalBounds(switchHost, "KS_BasePlate");

                Assert.Greater(Vector3.Dot(glassNormal, Vector3.back), 0.999f);
                Assert.Greater(Vector3.Dot(plateNormal, glassNormal), 0.999f);
                Assert.Greater(Vector3.Dot(surfaceNormal, glassNormal), 0.999f);

                // The ray and poke plane sits on the plate's room face, 0.022 m
                // in front of the switch origin and in front of the wall.
                Assert.AreEqual(WallKnifeSwitch.PlateFrontMeters, target.localPosition.z, 0.0001f);
                Assert.AreEqual(plate.min.z, target.localPosition.z, 0.0001f);
                Assert.Greater(Vector3.Dot(target.position - switchHost.transform.position, glassNormal), 0.02f);

                // The offset is lateral only, so the switch shares the set's wall plane.
                var offset = pose.position - televisionPosition;
                Assert.AreEqual(KnifeSwitchPose.LateralMeters,
                    Vector3.Dot(offset, pose.rotation * Vector3.right), 0.0001f);
                Assert.AreEqual(KnifeSwitchPose.CenterHeightAboveTvCenterMeters,
                    Vector3.Dot(offset, pose.rotation * Vector3.up), 0.0001f);
                Assert.AreEqual(PanelMount.BracketStandoff - KnifeSwitchPose.WallClearanceMeters,
                    Vector3.Dot(offset, televisionRotation * Vector3.forward), 0.0001f);
            }
            finally
            {
                Object.DestroyImmediate(switchHost.gameObject);
            }
        }

        // The switch plate's outer edge as a lateral coordinate in the wall's
        // own right frame, measured from the wall centre, for a mounted set.
        static float SwitchOuterEdgeFromWallCentre(PanelPlacement panel, CollisionSurface surface)
        {
            var wall = WallSurface.FromSurface(surface);
            var centre = Vector3.Dot(panel.Position - wall.Center, wall.Right);
            var panelRight = Vector3.Dot(panel.Rotation * Vector3.right, wall.Right);
            return centre + panelRight *
                (KnifeSwitchPose.LateralMeters + KnifeSwitchPose.PlateHalfWidthMeters);
        }

        [Test]
        public void AReserveWideWallCarriesTheWholeSetCentredOnTheTelevision()
        {
            var front = Wall("front", new Vector3(0f, 1.25f, 2f), Vector3.back, 4f, 2.5f);
            var recovery = Confirmed(StandardRoom());
            Assert.AreEqual(PanelMountMode.WallMounted, recovery.Panel.MountMode);

            // A wide wall leaves the set where it has always sat: the
            // television centred on the wall, the switch beside it.
            Assert.AreEqual(0f, recovery.Panel.Position.x, 0.0001f, "the television is not centred");
            var switchEdge = SwitchOuterEdgeFromWallCentre(recovery.Panel, front);
            Assert.AreEqual(-(KnifeSwitchPose.LateralMeters + KnifeSwitchPose.PlateHalfWidthMeters),
                switchEdge, 0.0001f);
            Assert.LessOrEqual(Mathf.Abs(switchEdge), front.Size.x * 0.5f - PanelMount.MountMargin);
        }

        [Test]
        public void AWallThatCarriesOnlyTheTelevisionDoesNotCarryTheWholeSet()
        {
            const float narrow = 1.9f;
            // The television alone fits this wall with the declared margin...
            Assert.GreaterOrEqual(narrow,
                PanelMount.ScreenWidth + 2f * PanelMount.Bezel + 2f * PanelMount.MountMargin);
            // ...but the switch reaches past it, so no wall carries the set and
            // the shell keeps its labelled forward candidate.
            Assert.Less(narrow, PanelMount.SetWidth + 2f * PanelMount.MountMargin);

            var draft = new RoomDraft(Region(), new[]
            {
                Floor(),
                Wall("left", new Vector3(-2f, 1.25f, 0f), Vector3.right, narrow, 2.5f),
                Wall("right", new Vector3(2f, 1.25f, 0f), Vector3.left, narrow, 2.5f),
                Wall("back", new Vector3(0f, 1.25f, -2f), Vector3.forward, narrow, 2.5f)
            }, Vector3.zero, Vector3.forward, 0.9f);
            var recovery = Confirmed(draft);
            Assert.AreEqual(PanelMountMode.ForwardFallback, recovery.Panel.MountMode);
            Assert.AreEqual(PanelMount.WallUnavailable, recovery.Panel.Reason);
            Assert.Less(Vector3.Distance(new Vector3(0f, 1.3f, 1.5f), recovery.Panel.Position), 0.02f);
        }

        [Test]
        public void TheSwitchSideClampLeavesThePlateEdgeOnTheUsableWallEdge()
        {
            // The wall is wide enough for the set but off-centre, so the
            // wearer's nearest point clamps the television toward the switch
            // side of the wall.
            const float wallWidth = 2.4f;
            var front = Wall("front", new Vector3(-1f, 1.25f, 2f), Vector3.back, wallWidth, 2.5f);
            var size = new Vector3(6f, 2.5f, 4f);
            var draft = new RoomDraft(new Bounds(new Vector3(0f, 1.25f, 0f), size), new[]
            {
                Floor(size), front,
                Wall("back", new Vector3(0f, 1.25f, -2f), Vector3.forward, size.x, 2.5f),
                Wall("left", new Vector3(-3f, 1.25f, 0f), Vector3.right, size.z, 2.5f),
                Wall("right", new Vector3(3f, 1.25f, 0f), Vector3.left, size.z, 2.5f)
            }, Vector3.zero, Vector3.forward, 0.9f);
            var recovery = Confirmed(draft);
            Assert.AreEqual(PanelMountMode.WallMounted, recovery.Panel.MountMode);

            // The clamp reserves the switch's reach, so the plate's outer edge
            // stops exactly on the usable edge: the wall edge less the margin
            // every other outer edge of the set keeps.
            var switchEdge = SwitchOuterEdgeFromWallCentre(recovery.Panel, front);
            Assert.AreEqual(-(wallWidth * 0.5f - PanelMount.MountMargin), switchEdge, 0.0001f,
                "the clamped set does not keep the plate on the usable wall");
            Assert.Less(Mathf.Abs(switchEdge), wallWidth * 0.5f, "the plate bridges the wall edge");
        }

        [Test]
        public void MountReserveCoversTheKnifeSwitchReachOnItsSide()
        {
            // T11 measured the plate's outer edge 1.085 m from the set centre
            // against a reserve of 0.855 m on that side, so a set at its clamp
            // left the switch 0.23 m past the wall edge.
            Assert.AreEqual(0.985f, KnifeSwitchPose.LateralMeters, 0.0001f);
            var switchReach = KnifeSwitchPose.LateralMeters + KnifeSwitchPose.PlateHalfWidthMeters;
            Assert.AreEqual(1.085f, switchReach, 0.0001f);
            var televisionReserve = PanelMount.ScreenWidth * 0.5f + PanelMount.Bezel + PanelMount.MountMargin;
            Assert.AreEqual(0.855f, televisionReserve, 0.0001f);
            Assert.AreEqual(0.23f, switchReach - televisionReserve, 0.0001f);

            // The reserve on the switch side now carries that reach, and both
            // reserves carry the same declared margin, so the two together are
            // the set width and the mount keeps the whole set on the wall.
            Assert.AreEqual(switchReach, PanelMount.SwitchReach, 0.0001f);
            Assert.AreEqual(switchReach + PanelMount.MountMargin, PanelMount.SwitchSideReserve, 0.0001f);
            Assert.AreEqual(PanelMount.SetWidth + 2f * PanelMount.MountMargin,
                PanelMount.SwitchSideReserve + PanelMount.ScreenSideReserve, 0.0001f);
        }

        [Test]
        public void KnifeSwitchCorrectionMovesOnlyTheBladeGroup()
        {
            var prefab = Resources.Load<GameObject>("KnifeSwitch/KnifeSwitch");
            Assert.IsNotNull(prefab, "knife switch model missing");
            var raw = Object.Instantiate(prefab);
            var switchHost = BuiltKnifeSwitch();
            try
            {
                foreach (var name in new[] { "KS_BasePlate", "KS_Label_ON", "KS_Label_OFF",
                             "KS_Hinge_L", "KS_Hinge_R", "KS_Pin_L", "KS_Pin_R", "KS_JawBase_L",
                             "KS_JawBase_R", "KS_JawProng_LA", "KS_JawProng_LB", "KS_JawProng_RA",
                             "KS_JawProng_RB", "KS_Lip_Top", "KS_Lip_Bot", "KS_Rail_L", "KS_Rail_R",
                             "KS_RedBadge", "KS_LED" })
                {
                    var before = raw.GetComponentsInChildren<Transform>(true).First(t => t.name == name);
                    var after = KnifeSwitchPart(switchHost, name);
                    Assert.AreEqual(before.localPosition.x, after.localPosition.x, 0.0001f, name);
                    Assert.AreEqual(before.localPosition.y, after.localPosition.y, 0.0001f, name);
                    Assert.AreEqual(before.localPosition.z, after.localPosition.z, 0.0001f, name);
                    Assert.Greater(Quaternion.Dot(before.localRotation, after.localRotation), 0.999999f, name);
                    var beforeBounds = before.GetComponent<Renderer>().bounds.center;
                    var afterBounds = after.GetComponent<Renderer>().bounds.center;
                    Assert.AreEqual(0f, (beforeBounds - afterBounds).magnitude, 0.0002f, name);
                }

                // The blade group is the one subtree that moves, and it moves by
                // exactly the measured authoring offset.
                var rawBlade = raw.GetComponentsInChildren<Transform>(true).First(t => t.name == "KS_Blade_L");
                var blade = KnifeSwitchPart(switchHost, "KS_Blade_L");
                Assert.AreEqual(0f,
                    (rawBlade.localPosition + WallKnifeSwitch.BladeGroupCorrection - blade.localPosition).magnitude,
                    0.0001f);
            }
            finally
            {
                Object.DestroyImmediate(raw);
                Object.DestroyImmediate(switchHost.gameObject);
            }
        }

        [Test]
        public void KnifeSwitchAnswersRayAndPokeWithoutMirroringItsScale()
        {
            var prefab = Resources.Load<GameObject>("KnifeSwitch/KnifeSwitch");
            Assert.IsNotNull(prefab, "knife switch model missing");
            var instance = Object.Instantiate(prefab);
            try
            {
                Assert.AreEqual(0, instance.GetComponentsInChildren<Collider>(true).Length);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }

            var host = new GameObject("WallKnifeSwitch");
            try
            {
                host.AddComponent<WallKnifeSwitch>().Build();
                var ray = host.GetComponentsInChildren<RayInteractable>(true);
                var poke = host.GetComponentsInChildren<PokeInteractable>(true);
                Assert.AreEqual(1, ray.Length);
                Assert.AreEqual(1, poke.Length);
                Assert.IsInstanceOf<ClippedPlaneSurface>(ray[0].Surface);
                Assert.AreSame(ray[0].Surface, poke[0].SurfacePatch);
                Assert.IsInstanceOf<KnifeSwitchPointer>(ray[0].PointableElement);
                Assert.IsInstanceOf<KnifeSwitchPointer>(poke[0].PointableElement);
                foreach (var node in host.GetComponentsInChildren<Transform>(true))
                {
                    var scale = node.lossyScale;
                    Assert.Greater(scale.x, 0f, node.name);
                    Assert.Greater(scale.y, 0f, node.name);
                    Assert.Greater(scale.z, 0f, node.name);
                }
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void KnifeSwitchReachCoversThePlateAndTheHandle()
        {
            Assert.AreEqual(0.18f, WallKnifeSwitch.PinchRadiusMeters, 0.0001f);
            Assert.AreEqual(0.26f, WallKnifeSwitch.PlateRadiusMeters, 0.0001f);
            var halfHeight = WallKnifeSwitch.TargetHeightMeters * 0.5f;
            Assert.Greater(WallKnifeSwitch.PlateRadiusMeters,
                Mathf.Sqrt(KnifeSwitchPose.PlateHalfWidthMeters * KnifeSwitchPose.PlateHalfWidthMeters +
                           halfHeight * halfHeight));

            var plate = new Vector3(1f, 1f, 0f);
            var handle = plate + new Vector3(0f, 0.15f, -0.2f);
            Assert.IsTrue(WallKnifeSwitch.WithinReach(plate + Vector3.right * 0.2f, plate, handle));
            Assert.IsTrue(WallKnifeSwitch.WithinReach(handle + Vector3.right * 0.1f, plate, handle));
            Assert.IsFalse(WallKnifeSwitch.WithinReach(plate + Vector3.right * 0.5f, plate, handle));
        }
    }
}
