using Amiss.Shell;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// The bag supply: every grab refills the dock, a consumed bag is removed,
    /// and the room never holds more than <see cref="ClothBagSupply.MaxBagsInRoom"/>
    /// bags. Presentation only; the sandbag tool still owns absorption.
    /// </summary>
    public class ClothBagSupplyTests
    {
        static ClothBagSupply BuildSupply(out GameObject supplyHost,
            out GameObject television)
        {
            television = new GameObject("Television");
            television.transform.SetPositionAndRotation(new Vector3(0f, 1.35f, 2f),
                Quaternion.identity);
            supplyHost = new GameObject("ClothBagSupply");
            var supply = supplyHost.AddComponent<ClothBagSupply>();
            supply.Build(television.transform);
            supply.SetVisible(true);
            supply.Follow(television.transform);
            return supply;
        }

        static void Destroy(params GameObject[] hosts)
        {
            for (var index = 0; index < hosts.Length; index++)
                if (hosts[index] != null)
                    Object.DestroyImmediate(hosts[index]);
        }

        [Test]
        public void GrabbingTheDockedBagRespawnsAnotherAtTheDock()
        {
            var supply = BuildSupply(out var supplyHost, out var television);
            try
            {
                var first = supply.DockedBag;
                Assert.IsNotNull(first, "the supply starts with a docked bag");

                Assert.IsTrue(first.TryCloseGrab(first.transform.position));
                Assert.AreEqual(ClothBagState.Held, first.State);

                Assert.AreEqual(2, supply.Count, "the grab respawns one bag");
                var second = supply.DockedBag;
                Assert.IsNotNull(second, "the fresh bag waits at the dock");
                Assert.AreNotSame(first, second);
                Assert.AreEqual(ClothBagState.Docked, second.State);
            }
            finally
            {
                Destroy(supplyHost, television);
            }
        }

        [Test]
        public void TheRoomHoldsAtMostOneHundredBags()
        {
            var supply = BuildSupply(out var supplyHost, out var television);
            try
            {
                for (var grab = 1; grab < ClothBagSupply.MaxBagsInRoom; grab++)
                {
                    var bag = supply.DockedBag;
                    Assert.IsNotNull(bag, "the dock refills until the cap");
                    Assert.IsTrue(bag.TryCloseGrab(bag.transform.position));
                }
                Assert.AreEqual(ClothBagSupply.MaxBagsInRoom, supply.Count);

                var last = supply.DockedBag;
                Assert.IsNotNull(last);
                Assert.IsTrue(last.TryCloseGrab(last.transform.position));
                Assert.AreEqual(ClothBagSupply.MaxBagsInRoom, supply.Count,
                    "the cap refuses a replacement");
                Assert.IsNull(supply.DockedBag, "no bag is left at the dock");
            }
            finally
            {
                Destroy(supplyHost, television);
            }
        }

        [Test]
        public void ConsumingABagRemovesItAndRefillsTheDock()
        {
            var supply = BuildSupply(out var supplyHost, out var television);
            try
            {
                var docked = supply.DockedBag;
                docked.PlaceAt(new Vector3(0f, 1.2f, 0.4f));
                Assert.AreEqual(ClothBagState.Floating, docked.State);

                Assert.IsTrue(supply.Consume(docked));
                Assert.AreEqual(1, supply.Count, "the consumed bag leaves the room");
                var replacement = supply.DockedBag;
                Assert.IsNotNull(replacement);
                Assert.AreEqual(ClothBagState.Docked, replacement.State);
            }
            finally
            {
                Destroy(supplyHost, television);
            }
        }

        [Test]
        public void HidingTheWallSetClearsTheSupplyAndShowsOneBagAgain()
        {
            var supply = BuildSupply(out var supplyHost, out var television);
            try
            {
                var first = supply.DockedBag;
                Assert.IsTrue(first.TryCloseGrab(first.transform.position));
                Assert.AreEqual(2, supply.Count);

                supply.SetVisible(false);
                Assert.AreEqual(0, supply.Count, "a hidden set holds no bags");

                supply.SetVisible(true);
                Assert.AreEqual(1, supply.Count);
                Assert.AreEqual(ClothBagState.Docked, supply.DockedBag.State);
            }
            finally
            {
                Destroy(supplyHost, television);
            }
        }
    }
}
