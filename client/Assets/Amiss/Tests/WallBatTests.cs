using System.IO;
using Amiss.Shell;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    public class WallBatTests
    {
        const string ArtFolder = "Assets/Amiss/Art/BaseballBat/Resources/BaseballBat";

        [Test]
        public void BatArtShipsWithTheModelAndMaps()
        {
            Assert.IsTrue(File.Exists(Path.Combine(ArtFolder, "BaseballBat.fbx")), "bat fbx");
            Assert.IsTrue(File.Exists(Path.Combine(ArtFolder, "Wood_Albedo.png")), "wood albedo");
            Assert.IsTrue(File.Exists(Path.Combine(ArtFolder, "Wood_Normal.png")), "wood normal");
            Assert.IsTrue(File.Exists(Path.Combine(ArtFolder, "Grip_Albedo.png")), "grip albedo");
            Assert.IsTrue(File.Exists(Path.Combine(ArtFolder, "Grip_Normal.png")), "grip normal");
            Assert.IsTrue(File.Exists(Path.Combine(ArtFolder, "TealAccent_Albedo.png")), "teal albedo");
        }

        [Test]
        public void BatModelMountsWithGripTipAndLitMaterials()
        {
            var host = new GameObject("WallBatModel");
            try
            {
                Assert.IsTrue(WallBatModel.TryMount(host.transform, out var grip, out var tip),
                    "mount " + WallBatModel.PrefabResource);
                Assert.IsNotNull(grip, "GripPoint");
                Assert.IsNotNull(tip, "TipPoint");
                Assert.Greater(Vector3.Distance(grip.position, tip.position), 0.5f, "bat length");
                var renderers = host.GetComponentsInChildren<MeshRenderer>(true);
                Assert.GreaterOrEqual(renderers.Length, 3, "body, grip, ring");
                foreach (var renderer in renderers)
                    Assert.IsNotNull(renderer.sharedMaterial, renderer.name + " material");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void DockPoseSitsBelowTheTelevision()
        {
            var television = new Pose(new Vector3(1f, 1.35f, 2f),
                Quaternion.LookRotation(Vector3.forward, Vector3.up));
            var dock = WallBatPose.DockPose(television);
            var local = Quaternion.Inverse(television.rotation) * (dock.position - television.position);
            Assert.AreEqual(WallBatPose.DockLocalPosition.x, local.x, 0.0001f);
            Assert.AreEqual(WallBatPose.DockLocalPosition.y, local.y, 0.0001f);
            Assert.AreEqual(WallBatPose.DockLocalPosition.z, local.z, 0.0001f);
            Assert.Less(dock.position.y, television.position.y - 0.4f, "below the glass");
            Assert.Greater(dock.position.y, 0.3f, "above the floor");
        }

        [Test]
        public void HoldPoseKeepsTheGripAtTheHand()
        {
            var pointer = new Pose(new Vector3(0.2f, 1.2f, -0.4f),
                Quaternion.LookRotation(Vector3.forward, Vector3.up));
            var gripLocal = new Vector3(0f, 0.05f, 0f);
            var hold = WallBatPose.HoldPose(pointer, gripLocal);
            var anchor = pointer.position + pointer.rotation * WallBatPose.HeldPositionOffset;
            var gripWorld = hold.position + hold.rotation * gripLocal;
            Assert.Less(Vector3.Distance(anchor, gripWorld), 0.0001f);
        }

        [Test]
        public void JointHoldPosePutsTheGripOnThePinchAlongTheHand()
        {
            var pinchMid = new Vector3(0.2f, 1.2f, -0.4f);
            var barrelDir = new Vector3(0f, 1f, 0.2f);
            var pinchAxis = Vector3.right;
            var gripLocal = new Vector3(0f, 0.05f, 0f);
            var hold = WallBatPose.JointHoldPose(pinchMid, barrelDir, pinchAxis,
                gripLocal);
            var gripWorld = hold.position + hold.rotation * gripLocal;
            Assert.Less(Vector3.Distance(pinchMid, gripWorld), 0.0001f);
            var barrelWorld = hold.rotation * Vector3.up;
            Assert.Greater(Vector3.Dot(barrelWorld, barrelDir.normalized), 0.9999f,
                "the barrel runs along the hand's long axis");
            var acrossWorld = hold.rotation * Vector3.right;
            Assert.Greater(Vector3.Dot(acrossWorld, pinchAxis), 0.0f,
                "the roll follows the pinch axis, so both hands mirror");
        }

        [Test]
        public void JointHoldPoseSurvivesADegeneratePinchAxis()
        {
            var pinchMid = new Vector3(0.2f, 1.2f, -0.4f);
            var hold = WallBatPose.JointHoldPose(pinchMid, Vector3.up,
                Vector3.zero, Vector3.zero);
            Assert.Less(Vector3.Distance(pinchMid, hold.position), 0.0001f);
            var barrelWorld = hold.rotation * Vector3.up;
            Assert.Greater(Vector3.Dot(barrelWorld, Vector3.up), 0.9999f);
        }

        [Test]
        public void PinchGraceHoldsAShortDropoutAndReleasesAfter()
        {
            Assert.IsFalse(WallBatPose.ShouldRelease(1.0f, 1.0f), "still pinching");
            Assert.IsFalse(WallBatPose.ShouldRelease(1.0f + WallBatPose.PinchGraceSeconds * 0.5f, 1.0f),
                "a one-frame dropout keeps the hold");
            Assert.IsTrue(WallBatPose.ShouldRelease(1.0f + WallBatPose.PinchGraceSeconds + 0.05f, 1.0f),
                "an opened hand releases");
        }

        [Test]
        public void GripAnchorPrefersThePinchAndFallsBackToTheFist()
        {
            var pinch = new Vector3(0.2f, 1.2f, -0.4f);
            var fist = new Vector3(0.25f, 1.1f, -0.4f);
            Assert.AreEqual(pinch, WallBatPose.GripAnchor(true, pinch, fist));
            Assert.AreEqual(fist, WallBatPose.GripAnchor(false, pinch, fist));
        }

        [Test]
        public void BarrelAxisRunsAcrossTheKnucklesWithoutPalmTilt()
        {
            var wrist = Vector3.zero;
            var middle = Vector3.up * 0.08f;
            var rightIndex = new Vector3(0.04f, 0.09f, 0f);
            var rightPinky = new Vector3(-0.04f, 0.07f, 0f);
            var expected = new Vector3(0.08f,
                0.02f * (1f - WallBatPose.GripAxisProjection), 0f).normalized;
            Assert.Less(Vector3.Distance(expected, WallBatPose.BarrelAxis(
                wrist, middle, rightIndex, rightPinky)), 0.0001f,
                "the declared share of the palm tilt is removed");
            var mirrored = -expected;
            Assert.Less(Vector3.Distance(mirrored, WallBatPose.BarrelAxis(
                wrist, middle, rightPinky, rightIndex)), 0.0001f,
                "the mirrored hand points the other way");
            Assert.Less(Vector3.Distance(Vector3.up, WallBatPose.BarrelAxis(
                wrist, middle, rightIndex, rightIndex)), 0.0001f,
                "a collapsed knuckle span falls back");
        }

        [Test]
        public void ForwardTiltTurnsTheBarrelTowardTheHandDirection()
        {
            Assert.AreEqual(25f, WallBatPose.ForwardTiltDegrees);
            var tilted = WallBatPose.TiltToward(Vector3.up, Vector3.forward,
                WallBatPose.ForwardTiltDegrees);
            Assert.AreEqual(25f, Vector3.Angle(Vector3.up, tilted), 0.001f);
            Assert.AreEqual(65f, Vector3.Angle(tilted, Vector3.forward), 0.001f);
            Assert.AreEqual(Vector3.up,
                WallBatPose.TiltToward(Vector3.up, Vector3.up, 15f),
                "an axis already pointing forward has no turn direction");
        }

        [Test]
        public void ClosedFistNeedsNoThumbPinchSignal()
        {
            var palm = new Vector3(0.2f, 1.2f, -0.4f);
            var curled = new[]
            {
                palm + new Vector3(0.04f, 0f, 0f),
                palm + new Vector3(0.045f, 0.01f, 0f),
                palm + new Vector3(0.04f, 0.02f, 0f),
                palm + new Vector3(0.035f, 0.025f, 0f)
            };
            Assert.IsTrue(WallBatPose.IsClosedFist(palm, curled[0], curled[1],
                curled[2], curled[3], 1f), "four curled fingers form a fist");

            var extended = palm + Vector3.up * 0.14f;
            Assert.IsTrue(WallBatPose.IsClosedFist(palm, extended, curled[1],
                curled[2], curled[3], 1f), "three curled fingers are enough");
            Assert.IsFalse(WallBatPose.IsClosedFist(palm, extended, extended,
                curled[2], curled[3], 1f), "two curled fingers are not a fist");
            Assert.IsFalse(WallBatPose.IsClosedFist(palm, extended, extended,
                extended, extended, 1f), "an open hand does not grab");
        }

        [Test]
        public void JointHoldTickLandsTheModelGripOnThePinch()
        {
            var bat = BuildShownBat(out var host, out var television);
            try
            {
                var pinchMid = host.transform.position + new Vector3(0.05f, 0.1f, 0f);
                bat.JointHoldTick(pinchMid, Vector3.up, Vector3.right);
                var grip = FindGripWorld(host.transform);
                Assert.Less(Vector3.Distance(pinchMid, grip), 0.01f,
                    "the model grip sits on the fingers, not beside them");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(television);
            }
        }

        static Vector3 FindGripWorld(Transform host)
        {
            foreach (var child in host.GetComponentsInChildren<Transform>(true))
                if (child.name == "GripPoint")
                    return child.position;
            return host.position;
        }

        [Test]
        public void AimAcceptsOnlyPointingHandsInRange()
        {
            var target = new Vector3(0f, 0.9f, 2f);
            var hand = new Vector3(0f, 1.2f, 0f);
            var forward = (target - hand).normalized;
            Assert.IsTrue(WallBatPose.AimsAt(hand, forward, target), "pointing at the bat");
            Assert.IsFalse(WallBatPose.AimsAt(hand, -forward, target), "pointing away");
            Assert.IsFalse(WallBatPose.AimsAt(hand, forward, hand + forward * 0.1f), "too close");
            Assert.IsFalse(WallBatPose.AimsAt(hand, forward,
                hand + forward * (WallBatPose.FetchRangeMeters + 1f)), "too far");
            var offAxis = Quaternion.Euler(0f, 2f * WallBatPose.FetchHalfAngleDegrees, 0f) * forward;
            Assert.IsFalse(WallBatPose.AimsAt(hand, offAxis, target), "outside the cone");
        }

        [Test]
        public void SegmentDistanceFindsTheBarrel()
        {
            var a = Vector3.zero;
            var b = new Vector3(0f, 0.8f, 0f);
            Assert.AreEqual(0.1f, WallBatPose.DistanceToSegment(new Vector3(0.1f, 0.4f, 0f), a, b), 0.0001f);
            Assert.AreEqual(0.2f, WallBatPose.DistanceToSegment(new Vector3(0f, 1f, 0f), a, b), 0.0001f);
        }

        [Test]
        public void DriftDampingFreezesASlowBat()
        {
            var velocity = new Vector3(2f, 0f, 0f);
            for (var i = 0; i < 240; i++)
                velocity = WallBatPose.DampDrift(velocity, 1f / 72f);
            Assert.Less(velocity.magnitude, WallBatPose.ReleaseStopSpeedMeters);
        }

        WallBat BuildShownBat(out GameObject host, out GameObject television)
        {
            television = new GameObject("Television");
            television.transform.SetPositionAndRotation(new Vector3(1f, 1.35f, 2f),
                Quaternion.LookRotation(Vector3.forward, Vector3.up));
            host = new GameObject("WallBat");
            var bat = host.AddComponent<WallBat>();
            bat.Build();
            bat.SetVisible(true);
            return bat;
        }

        [Test]
        public void BatDocksUnderTheTelevisionUntilGrabbed()
        {
            var bat = BuildShownBat(out var host, out var television);
            try
            {
                Assert.AreEqual(WallBatState.Docked, bat.State);
                bat.Follow(television.transform);
                var expected = WallBatPose.DockPose(
                    new Pose(television.transform.position, television.transform.rotation));
                Assert.Less(Vector3.Distance(expected.position, host.transform.position), 0.0001f);
                Assert.IsTrue(bat.IsDocked);

                Assert.IsTrue(bat.TryCloseGrab(host.transform.position + Vector3.up * 0.02f),
                    "pinch at the grip");
                Assert.AreEqual(WallBatState.Held, bat.State);
                Assert.IsFalse(bat.IsDocked);

                bat.ReleaseHold();
                Assert.AreEqual(WallBatState.Resting, bat.State);
                var dropped = host.transform.position;
                bat.Follow(television.transform);
                Assert.AreEqual(dropped, host.transform.position, "a moved bat stays put");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(television);
            }
        }

        [Test]
        public void FarPinchFetchesTheBatToTheHand()
        {
            var bat = BuildShownBat(out var host, out var television);
            try
            {
                bat.Follow(television.transform);
                var grip = host.transform.position;
                var hand = grip + new Vector3(0f, 0.3f, -2f);
                var forward = (grip - hand).normalized;
                Assert.IsTrue(bat.TryFetch(hand, forward), "aim plus pinch");
                Assert.AreEqual(WallBatState.Flying, bat.State);

                var pointer = new Pose(hand, Quaternion.LookRotation(forward, Vector3.up));
                for (var i = 0; i < 30 && bat.State == WallBatState.Flying; i++)
                    bat.FlyTick(pointer, 0.02f);
                Assert.AreEqual(WallBatState.Held, bat.State);

                bat.ReleaseHold();
                bat.SetVisible(false);
                Assert.AreEqual(WallBatState.Hidden, bat.State);
                bat.SetVisible(true);
                Assert.AreEqual(WallBatState.Docked, bat.State, "hiding re-docks the bat");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(television);
            }
        }
    }
}
