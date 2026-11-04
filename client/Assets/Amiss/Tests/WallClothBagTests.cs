using Amiss.Shell;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// The grabbable cloth bag docked under the television: hand grab and far
    /// fetch like the bat, a throw that stops itself and floats still, and a
    /// restock under the set after the one absorption. Presentation only — the
    /// sandbag tool still owns the hover region and the measured absorption.
    /// </summary>
    public class WallClothBagTests
    {
        static WallClothBag BuildShownBag(out GameObject host, out GameObject television)
        {
            television = new GameObject("Television");
            television.transform.SetPositionAndRotation(new Vector3(1f, 1.35f, 2f),
                Quaternion.LookRotation(Vector3.forward, Vector3.up));
            host = new GameObject("WallClothBag");
            var bag = host.AddComponent<WallClothBag>();
            bag.Build();
            bag.SetVisible(true);
            bag.Follow(television.transform);
            return bag;
        }

        [Test]
        public void BagDocksUnderTheTelevisionUntilGrabbed()
        {
            var bag = BuildShownBag(out var host, out var television);
            try
            {
                Assert.AreEqual(ClothBagState.Docked, bag.State);
                var expected = ClothBagPose.DockPose(new Pose(television.transform.position,
                    television.transform.rotation));
                Assert.Less(Vector3.Distance(expected.position, bag.transform.position),
                    0.0001f);
                var local = Quaternion.Inverse(television.transform.rotation) *
                            (bag.transform.position - television.transform.position);
                Assert.Less(local.z, 0f, "in front of the set, on the room side");
                Assert.IsTrue(bag.IsDocked);

                Assert.IsTrue(bag.TryCloseGrab(bag.transform.position + Vector3.up * 0.02f),
                    "pinch at the bag");
                Assert.AreEqual(ClothBagState.Held, bag.State);
                Assert.IsFalse(bag.IsDocked);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(television);
            }
        }

        [Test]
        public void ThrownBagStopsItselfAndFloatsStill()
        {
            var bag = BuildShownBag(out var host, out var television);
            try
            {
                Assert.IsTrue(bag.TryCloseGrab(bag.transform.position));
                var anchor = bag.transform.position;
                for (var frame = 0; frame < 8; frame++)
                {
                    anchor += Vector3.up * 0.06f;
                    bag.HoldTick(anchor, 1f / 72f);
                }
                var release = bag.transform.position;
                bag.ReleaseHold();
                Assert.AreEqual(ClothBagState.Thrown, bag.State);

                for (var frame = 0; frame < 300 && bag.State == ClothBagState.Thrown;
                     frame++)
                    bag.ThrowTick(1f / 72f);

                Assert.AreEqual(ClothBagState.Floating, bag.State);
                Assert.Greater(bag.FloatingPosition.y, release.y + 0.1f,
                    "the bag rises before it stops itself");
                Assert.AreEqual(1, bag.FloatSerial);

                var still = bag.FloatingPosition;
                for (var frame = 0; frame < 120; frame++)
                {
                    // A floating bag has nothing to advance; it must stay put.
                }
                Assert.AreEqual(still, bag.FloatingPosition);
                Assert.AreEqual(ClothBagState.Floating, bag.State);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(television);
            }
        }

        [Test]
        public void SlowReleaseFloatsWhereItWasLetGo()
        {
            var bag = BuildShownBag(out var host, out var television);
            try
            {
                var point = bag.transform.position + new Vector3(0f, 0.2f, -0.1f);
                Assert.IsTrue(bag.TryCloseGrab(bag.transform.position));
                bag.HoldTick(point, 1f);
                bag.HoldTick(point, 1f);
                bag.ReleaseHold();

                Assert.AreEqual(ClothBagState.Floating, bag.State);
                Assert.Less(Vector3.Distance(point, bag.FloatingPosition), 0.0001f);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(television);
            }
        }

        [Test]
        public void FarPinchFetchesTheBagToTheHand()
        {
            var bag = BuildShownBag(out var host, out var television);
            try
            {
                var target = bag.transform.position + new Vector3(0f, -0.3f, -1.8f);
                var forward = (bag.transform.position - target).normalized;
                Assert.IsTrue(bag.TryFetch(target, forward, 1), "aim plus pinch");
                Assert.AreEqual(ClothBagState.Fetching, bag.State);

                for (var frame = 0; frame < 30 && bag.State == ClothBagState.Fetching;
                     frame++)
                    bag.FetchTickTo(target, 0.02f);

                Assert.AreEqual(ClothBagState.Held, bag.State);
                Assert.AreEqual(1, bag.HeldSide,
                    "the fetched bag is owned by the hand that pulled it in");
                Assert.Less(Vector3.Distance(target, bag.transform.position), 0.0001f);

                bag.ReleaseHold();
                Assert.AreEqual(ClothBagState.Floating, bag.State,
                    "a fetched bag can be let go and floats where it arrived");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(television);
            }
        }

        [Test]
        public void ConsumedBagRespawnsUnderTheTelevision()
        {
            var bag = BuildShownBag(out var host, out var television);
            try
            {
                bag.PlaceAt(new Vector3(0.2f, 1.4f, 1.2f));
                Assert.AreEqual(ClothBagState.Floating, bag.State);
                bag.RespawnToDock();

                Assert.AreEqual(ClothBagState.Docked, bag.State);
                var expected = ClothBagPose.DockPose(new Pose(television.transform.position,
                    television.transform.rotation));
                Assert.Less(Vector3.Distance(expected.position, bag.transform.position),
                    0.0001f);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(television);
            }
        }

        [Test]
        public void SeparationIsSymmetricAndLeavesClearPairsAlone()
        {
            Assert.IsFalse(ClothBagPose.TrySeparate(Vector3.zero,
                new Vector3(0.3f, 0f, 0f), ClothBagPose.MinBagSeparationMeters,
                out _, out _), "bags already apart do not move");

            Assert.IsTrue(ClothBagPose.TrySeparate(Vector3.zero, Vector3.zero,
                ClothBagPose.MinBagSeparationMeters, out var first, out var second),
                "coincident bags still separate deterministically");
            Assert.AreEqual(ClothBagPose.MinBagSeparationMeters * 0.5f, second.x, 1e-5f);
            Assert.AreEqual(-first.x, second.x, 1e-5f);
            Assert.AreEqual(0f, first.y + second.y, 1e-5f);
        }

        [Test]
        public void ThrowVelocityIsClampedAndTheRuleShowsTheApexStop()
        {
            var clamped = ClothBagPose.ClampThrowVelocity(new Vector3(0f, 100f, 0f));
            Assert.AreEqual(ClothBagPose.MaxThrowSpeedMeters, clamped.magnitude, 0.0001f);

            var start = Vector3.up;
            Assert.IsFalse(ClothBagPose.ShouldStopThrow(start, Vector3.up, start, 0.2f),
                "still rising");
            Assert.IsTrue(ClothBagPose.ShouldStopThrow(start, Vector3.down, start, 0.2f),
                "apex reached");
            Assert.IsTrue(ClothBagPose.ShouldStopThrow(start, Vector3.up, start,
                ClothBagPose.MaxThrowSeconds + 0.01f), "longest flight");
        }
    }
}
