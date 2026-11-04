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
        public void MeasuredAbsorptionRaisesTheCelebration()
        {
            var accepted = AcceptedRoom("sandbag-wiring");
            var runtime = new DeviceSimulationRuntime(() => new CustomHardSphereWriter());
            Assert.IsTrue(runtime.TryStartTrial(TemperatureTrialSetup.CreateTrialStart(
                "sandbag-wiring", accepted.room.Confirmation, accepted.pin, out _)).Started);

            var host = new GameObject("SandbagToolControllerTest");
            var controller = host.AddComponent<SandbagToolController>();
            GameObject root = null;
            try
            {
                controller.Initialize(runtime);
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
            }
            finally
            {
                if (root != null)
                    Object.DestroyImmediate(root);
                Object.DestroyImmediate(host);
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
    }
}
