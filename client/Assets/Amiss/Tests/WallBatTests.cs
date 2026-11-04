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
