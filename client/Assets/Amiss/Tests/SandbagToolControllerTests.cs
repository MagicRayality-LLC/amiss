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
    /// The celebration wiring (`N11`): the controller owns one
    /// <see cref="SandbagTool"/> on the runtime host, polls it after the
    /// runtime steps, and the tool's measured absorption raises the approved
    /// reference dispersal. Placement stays outside the bridge; the test
    /// places the bag on a live ball's path the way the stage will.
    /// </summary>
    public class SandbagToolControllerTests
    {
        static (RoomPreparation room, CollisionPin pin, PinnedCollision pinned) AcceptedRoom(
            string trialId)
        {
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

        [Test]
        public void ThrownBagFloatArmsAndGrabbingItWithdraws()
        {
            var accepted = AcceptedRoom("sandbag-arm");
            var runtime = new DeviceSimulationRuntime(() => new CustomHardSphereWriter());
            Assert.IsTrue(runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "sandbag-arm", accepted.room.Confirmation, accepted.pin, out _)).Started);
            runtime.Step(1);

            var television = new GameObject("Television");
            television.transform.SetPositionAndRotation(new Vector3(0f, 1.35f, 1.6f),
                Quaternion.identity);
            var bagHost = new GameObject("WallClothBag");
            var controllerHost = new GameObject("SandbagToolControllerArmTest");
            var controller = controllerHost.AddComponent<SandbagToolController>();
            try
            {
                var visual = bagHost.AddComponent<WallClothBag>();
                visual.Build();
                visual.SetVisible(true);
                visual.Follow(television.transform);
                controller.Initialize(runtime, visual);

                var point = runtime.CurrentView.Balls[0].PositionM;
                visual.PlaceAt(point);
                Assert.AreEqual(ClothBagState.Floating, visual.State);
                controller.ArmFloatingBag();
                Assert.AreEqual(SandbagState.Placed, controller.State,
                    "the still float arms the hover region");
                Assert.AreEqual(point, controller.Bag.HoverCenter);
                Assert.AreEqual(1, controller.Bag.PlacementCount);

                Assert.IsTrue(visual.TryCloseGrab(visual.transform.position));
                controller.SyncVisual();
                Assert.AreEqual(SandbagState.Ready, controller.State,
                    "picking the bag back up withdraws the region");

                visual.ReleaseHold();
                Assert.AreEqual(ClothBagState.Floating, visual.State);
                controller.ArmFloatingBag();
                Assert.AreEqual(SandbagState.Placed, controller.State,
                    "the next float arms a fresh region");
                Assert.AreEqual(2, controller.Bag.PlacementCount);
            }
            finally
            {
                Object.DestroyImmediate(controllerHost);
                Object.DestroyImmediate(bagHost);
                Object.DestroyImmediate(television);
            }
        }

        [Test]
        public void MeasuredAbsorptionRaisesTheCelebration()
        {
            var accepted = AcceptedRoom("sandbag-wiring");
            var runtime = new DeviceSimulationRuntime(() => new CustomHardSphereWriter());
            Assert.IsTrue(runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "sandbag-wiring", accepted.room.Confirmation, accepted.pin, out _)).Started);

            var host = new GameObject("SandbagToolControllerTest");
            var controller = host.AddComponent<SandbagToolController>();
            var television = new GameObject("Television");
            television.transform.SetPositionAndRotation(new Vector3(0f, 1.35f, 1.6f),
                Quaternion.identity);
            var bagHost = new GameObject("WallClothBag");
            GameObject root = null;
            try
            {
                var visual = bagHost.AddComponent<WallClothBag>();
                visual.Build();
                visual.SetVisible(true);
                visual.Follow(television.transform);
                controller.Initialize(runtime, visual);
                Assert.IsNotNull(controller.Bag, "the controller owns one tool on the host");

                for (var attempt = 0; attempt < 40 && controller.AbsorptionCount == 0; attempt++)
                {
                    if (controller.State == SandbagState.Ready)
                    {
                        var anchor = runtime.CurrentView.Balls[0].PositionM;
                        Assert.IsTrue(controller.PlaceBag(anchor).WasPlaced,
                            "the bag floats on the live ball's path");
                    }
                    runtime.Step(1);
                    controller.Poll();
                }

                Assert.AreEqual(1, controller.AbsorptionCount, "one measured absorption closes once");
                Assert.AreEqual(SandbagState.Consumed, controller.State);
                Assert.Greater(controller.Bag.LastAbsorbedJ, 0.0,
                    "the celebration scales with measured joules");

                root = GameObject.Find("SandbagDispersal");
                Assert.IsNotNull(root, "absorption raises the dispersal");
                Assert.IsTrue(root.GetComponentsInChildren<Collider>(true).Length == 0,
                    "the celebration cannot touch a ball");
                Assert.IsTrue(root.GetComponentsInChildren<Rigidbody>(true).Length == 0,
                    "the celebration has no physics body");
                Assert.GreaterOrEqual(
                    root.GetComponentsInChildren<ParticleSystem>(true).Length, 3,
                    "burst plus flash plus falling glitter or confetti");
                Assert.AreEqual(ClothBagState.Docked, visual.State,
                    "the consumed bag restocks under the television");
            }
            finally
            {
                if (root != null)
                    Object.DestroyImmediate(root);
                Object.DestroyImmediate(bagHost);
                Object.DestroyImmediate(television);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void EveryFloatingBagArmsItsOwnRegion()
        {
            var accepted = AcceptedRoom("sandbag-multi-arm");
            var runtime = new DeviceSimulationRuntime(() => new CustomHardSphereWriter());
            Assert.IsTrue(runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "sandbag-multi-arm", accepted.room.Confirmation, accepted.pin,
                out _)).Started);
            runtime.Step(1);

            var television = new GameObject("Television");
            television.transform.SetPositionAndRotation(new Vector3(0f, 1.35f, 1.6f),
                Quaternion.identity);
            var supplyHost = new GameObject("ClothBagSupply");
            var controllerHost = new GameObject("SandbagToolControllerMultiArmTest");
            var controller = controllerHost.AddComponent<SandbagToolController>();
            try
            {
                var supply = supplyHost.AddComponent<ClothBagSupply>();
                supply.Build(television.transform);
                supply.SetVisible(true);
                supply.Follow(television.transform);

                var first = supply.DockedBag;
                Assert.IsNotNull(first);
                Assert.IsTrue(first.TryCloseGrab(first.transform.position));
                var second = supply.DockedBag;
                Assert.IsNotNull(second, "the grab refills the dock");
                first.PlaceAt(new Vector3(-0.4f, 1.2f, 0.4f));
                second.PlaceAt(new Vector3(0.4f, 1.2f, 0.4f));

                controller.Initialize(runtime);
                controller.ArmFloatingBag();

                Assert.AreEqual(2, controller.Bag.PlacementCount,
                    "both floating bags arm a region");
                Assert.IsTrue(controller.Bag.HasPlacement(
                    controller.Bag.OpenContactId));
                Assert.AreEqual(SandbagState.Placed, controller.State);
            }
            finally
            {
                Object.DestroyImmediate(controllerHost);
                Object.DestroyImmediate(supplyHost);
                Object.DestroyImmediate(television);
            }
        }

        [Test]
        public void NoPlacementMeansNoBagAndNoPoll()
        {
            var host = new GameObject("SandbagToolControllerIdleTest");
            var controller = host.AddComponent<SandbagToolController>();
            try
            {
                controller.Initialize(new DeviceSimulationRuntime(() => new CustomHardSphereWriter()));
                controller.Poll();
                Assert.AreEqual(SandbagState.Ready, controller.State);
                Assert.AreEqual(0, controller.AbsorptionCount);
                Assert.IsNull(GameObject.Find("SandbagDispersal"));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void OrderedAnchorFloatsOneBag()
        {
            var accepted = AcceptedRoom("sandbag-anchor");
            var anchor = new Vector3(0f, 1f, 0.2f);
            var request = TemperatureTrialSetup.CreateTrialStart(
                "sandbag-anchor", accepted.room.Confirmation, accepted.pin, out _);
            request.SandbagAnchorM = anchor;
            var runtime = new DeviceSimulationRuntime(() => new CustomHardSphereWriter());
            Assert.IsTrue(runtime.TryStartTrial(request).Started);

            var host = new GameObject("SandbagToolControllerAnchorTest");
            var controller = host.AddComponent<SandbagToolController>();
            try
            {
                controller.Initialize(runtime);
                runtime.Step(1);
                controller.Poll();
                controller.FloatOrderedBag();
                Assert.AreEqual(SandbagState.Placed, controller.State);
                Assert.AreEqual(anchor, controller.Bag.HoverCenter);
                controller.FloatOrderedBag();
                Assert.AreEqual(1, controller.Bag.PlacementCount,
                    "one Trial floats one bag, never a second");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void NonFiniteAnchorRefusesTheStart()
        {
            var accepted = AcceptedRoom("sandbag-anchor-bad");
            var request = TemperatureTrialSetup.CreateTrialStart(
                "sandbag-anchor-bad", accepted.room.Confirmation, accepted.pin, out _);
            request.SandbagAnchorM = new Vector3(float.NaN, 0f, 0f);
            var runtime = new DeviceSimulationRuntime(() => new CustomHardSphereWriter());
            var outcome = runtime.TryStartTrial(request);
            Assert.IsFalse(outcome.Started);
            Assert.AreEqual(TrialStartRefusal.RequestInvalid, outcome.Refusal);
        }

        [Test]
        public void EveryArmedBagAbsorbsItsOwnBall()
        {
            var accepted = AcceptedRoom("sandbag-multi-absorb");
            var runtime = new DeviceSimulationRuntime(() => new CustomHardSphereWriter());
            Assert.IsTrue(runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "sandbag-multi-absorb", accepted.room.Confirmation, accepted.pin,
                out _)).Started);
            runtime.Step(2);

            var controllerHost = new GameObject("SandbagToolControllerMultiAbsorbTest");
            var controller = controllerHost.AddComponent<SandbagToolController>();
            var firstHost = new GameObject("WallClothBag");
            var secondHost = new GameObject("WallClothBag");
            try
            {
                var first = firstHost.AddComponent<WallClothBag>();
                var second = secondHost.AddComponent<WallClothBag>();
                first.Build();
                second.Build();
                first.SetVisible(true);
                second.SetVisible(true);
                controller.Initialize(runtime);

                var balls = runtime.CurrentView.Balls;
                Assert.GreaterOrEqual(balls.Count, 2);
                first.PlaceAt(balls[0].PositionM);
                second.PlaceAt(balls[1].PositionM);
                controller.ArmFloatingBag();
                Assert.AreEqual(2, controller.Bag.PlacementCount,
                    "both floating bags own a placement");

                for (var attempt = 0;
                     attempt < 600 && controller.AbsorptionCount < 2;
                     attempt++)
                {
                    runtime.Step(1);
                    controller.Poll();
                }

                Assert.AreEqual(2, controller.AbsorptionCount,
                    "each armed bag absorbs on its own contact");
                Assert.AreEqual(2, controller.Bag.AbsorptionCount);
                Assert.Greater(controller.Bag.AbsorbedTotalJ, 0.0);
            }
            finally
            {
                Object.DestroyImmediate(controllerHost);
                Object.DestroyImmediate(firstHost);
                Object.DestroyImmediate(secondHost);
                var dispersals = GameObject.FindObjectsByType<GameObject>(
                    FindObjectsInactive.Exclude);
                for (var index = 0; index < dispersals.Length; index++)
                    if (dispersals[index] != null &&
                        dispersals[index].name == "SandbagDispersal")
                        Object.DestroyImmediate(dispersals[index]);
            }
        }

        [Test]
        public void ManyFloatingBagsAllArmAndAbsorb()
        {
            var accepted = AcceptedRoom("sandbag-many");
            var runtime = new DeviceSimulationRuntime(() => new CustomHardSphereWriter());
            Assert.IsTrue(runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "sandbag-many", accepted.room.Confirmation, accepted.pin, out _)).Started);
            runtime.Step(2);

            var controllerHost = new GameObject("SandbagToolControllerManyTest");
            var controller = controllerHost.AddComponent<SandbagToolController>();
            var hosts = new List<GameObject>();
            try
            {
                var balls = runtime.CurrentView.Balls;
                var count = Mathf.Min(10, balls.Count);
                for (var index = 0; index < count; index++)
                {
                    var bagHost = new GameObject("WallClothBag");
                    hosts.Add(bagHost);
                    var bag = bagHost.AddComponent<WallClothBag>();
                    bag.Build();
                    bag.SetVisible(true);
                    bag.PlaceAt(balls[index].PositionM);
                }
                controller.Initialize(runtime);
                controller.ArmFloatingBag();
                Assert.AreEqual(count, controller.Bag.PlacementCount,
                    "every floating bag arms");

                for (var attempt = 0;
                     attempt < 400 && controller.AbsorptionCount < count;
                     attempt++)
                {
                    runtime.Step(1);
                    controller.Poll();
                }

                Assert.AreEqual(count, controller.AbsorptionCount,
                    "every armed bag absorbs its own ball");
            }
            finally
            {
                Object.DestroyImmediate(controllerHost);
                for (var index = 0; index < hosts.Count; index++)
                    Object.DestroyImmediate(hosts[index]);
                var dispersals = GameObject.FindObjectsByType<GameObject>(
                    FindObjectsInactive.Exclude);
                for (var index = 0; index < dispersals.Length; index++)
                    if (dispersals[index] != null &&
                        dispersals[index].name == "SandbagDispersal")
                        Object.DestroyImmediate(dispersals[index]);
            }
        }

        [Test]
        public void OverlappingFloatingBagsSeparateAndBothStayArmed()
        {
            var accepted = AcceptedRoom("sandbag-supply-frame");
            var runtime = new DeviceSimulationRuntime(() => new CustomHardSphereWriter());
            Assert.IsTrue(runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "sandbag-supply-frame", accepted.room.Confirmation, accepted.pin,
                out _)).Started);
            runtime.Step(2);

            var television = new GameObject("Television");
            television.transform.SetPositionAndRotation(new Vector3(0f, 1.35f, 1.6f),
                Quaternion.identity);
            var supplyHost = new GameObject("ClothBagSupply");
            var controllerHost = new GameObject("SandbagToolControllerFrameTest");
            var controller = controllerHost.AddComponent<SandbagToolController>();
            try
            {
                var supply = supplyHost.AddComponent<ClothBagSupply>();
                supply.Build(television.transform);
                supply.SetVisible(true);
                supply.Follow(television.transform);

                var first = supply.DockedBag;
                Assert.IsNotNull(first);
                Assert.IsTrue(first.TryCloseGrab(first.transform.position));
                var second = supply.DockedBag;
                Assert.IsNotNull(second);
                Assert.IsTrue(second.TryCloseGrab(second.transform.position));

                var balls = runtime.CurrentView.Balls;
                first.PlaceAt(balls[0].PositionM);
                second.PlaceAt(balls[0].PositionM);
                controller.Initialize(runtime);

                for (var attempt = 0;
                     attempt < 600 && controller.AbsorptionCount < 2;
                     attempt++)
                {
                    controller.Poll();
                    controller.SyncVisual();
                    controller.FloatOrderedBag();
                    controller.ArmFloatingBag();
                    supply.SeparateFloatingBags();
                    runtime.Step(1);
                }

                Assert.AreEqual(2, controller.AbsorptionCount,
                    "both overlapping bags stay armed and each absorbs");
            }
            finally
            {
                Object.DestroyImmediate(controllerHost);
                Object.DestroyImmediate(supplyHost);
                Object.DestroyImmediate(television);
                var dispersals = GameObject.FindObjectsByType<GameObject>(
                    FindObjectsInactive.Exclude);
                for (var index = 0; index < dispersals.Length; index++)
                    if (dispersals[index] != null &&
                        dispersals[index].name == "SandbagDispersal")
                        Object.DestroyImmediate(dispersals[index]);
            }
        }
    }
}
