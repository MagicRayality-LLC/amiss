using Amiss.Shell;
using Amiss.Shell.Room;
using NUnit.Framework;

namespace Amiss.Tests
{
    public class EpisodeLaunchGateTests
    {
        [Test]
        public void AFinishedScanConfirmsFirstAndStartsOnALaterPress()
        {
            var waiting = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation);
            var confirmed = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomConfirmed,
                RoomPreparationStatus.Confirmed);

            Assert.IsTrue(waiting.CanLaunchEpisodeOne);
            Assert.IsTrue(waiting.ButtonEnabled);
            Assert.IsTrue(waiting.ScanReady);
            Assert.AreEqual("CONFIRM ROOM", waiting.Label);
            Assert.AreEqual(EpisodeHubAction.Launch, waiting.Action);
            StringAssert.Contains("CONFIRM ROOM", waiting.Status);

            Assert.IsTrue(confirmed.CanLaunchEpisodeOne);
            Assert.IsTrue(confirmed.ButtonEnabled);
            Assert.AreEqual("START TRIAL", confirmed.Label);
            Assert.AreEqual(EpisodeHubAction.StartTrial, confirmed.Action);
        }

        [Test]
        public void LaunchFromScanConfirmsTheRoomAndStartsNoTrial()
        {
            // Tonight's direction: the press that follows a loaded scan
            // confirms the room. It ends at RoomConfirmed, so no trial runs,
            // the wall set is visible, and the hub stays up.
            var confirmPress = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation);
            Assert.AreEqual(EpisodeHubAction.Launch, confirmPress.Action);

            var afterConfirm = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomConfirmed,
                RoomPreparationStatus.Confirmed);
            Assert.IsFalse(EpisodeLaunchGate.TrialRunning(ShellSessionState.RoomConfirmed));
            Assert.IsFalse(EpisodeLaunchGate.HubClosesAfterEpisodeStart(ShellSessionState.RoomConfirmed));
            Assert.IsTrue(EpisodeLaunchGate.WallSetVisible(ShellSessionState.RoomConfirmed));
            Assert.IsTrue(EpisodeLaunchGate.KnifeSwitchStartsTrial(
                ShellSessionState.RoomConfirmed, closed: false));

            // The start is a different action, so a confirm press cannot be
            // read as a start press.
            Assert.AreNotEqual(confirmPress.Action, afterConfirm.Action);
            Assert.AreEqual(EpisodeHubAction.StartTrial, afterConfirm.Action);
            Assert.AreEqual("START TRIAL", afterConfirm.Label);
        }

        [Test]
        public void AClosedSwitchStartsExactlyOnce()
        {
            var start = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomConfirmed,
                RoomPreparationStatus.Confirmed, RoomFailureReason.None, "", "", true);
            Assert.AreEqual(EpisodeHubAction.StartTrial, start.Action);

            // Once the setup is accepted the same action is no longer offered,
            // and a second switch close changes nothing.
            var running = EpisodeLaunchGate.Evaluate(true, ShellSessionState.SetupAccepted,
                RoomPreparationStatus.Confirmed);
            Assert.IsFalse(running.ButtonEnabled);
            Assert.AreEqual("EPISODE ONE", running.Label);
            Assert.IsFalse(EpisodeLaunchGate.KnifeSwitchStartsTrial(
                ShellSessionState.SetupAccepted, closed: false));
        }

        [Test]
        public void ARefusedStartKeepsTheRoomConfirmedAndTheStartAvailable()
        {
            foreach (var startBlock in new[] { "could_not_lock", "safety" })
            {
                var refused = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomConfirmed,
                    RoomPreparationStatus.Confirmed, RoomFailureReason.None, "", startBlock);
                Assert.IsTrue(refused.ButtonEnabled, startBlock);
                Assert.IsTrue(refused.CanLaunchEpisodeOne, startBlock);
                Assert.AreEqual("START TRIAL", refused.Label, startBlock);
                Assert.AreEqual(EpisodeHubAction.StartTrial, refused.Action, startBlock);
                Assert.IsTrue(refused.EmphasizeStatus, startBlock);
                StringAssert.Contains("START TRIAL", refused.Status, startBlock);
                Assert.IsFalse(EpisodeLaunchGate.HubClosesAfterEpisodeStart(
                    ShellSessionState.RoomConfirmed), startBlock);
                Assert.IsTrue(EpisodeLaunchGate.KnifeSwitchStartsTrial(
                    ShellSessionState.RoomConfirmed, closed: false), startBlock);
            }
        }

        [Test]
        public void LaunchGuidanceNamesTheSwitchAndTheFallbackAsTheStart()
        {
            var library = EpisodeLaunchGate.Evaluate(true, ShellSessionState.Library,
                RoomPreparationStatus.Idle);
            StringAssert.Contains("confirms it", library.Status);
            StringAssert.Contains("knife switch", library.Status);
            StringAssert.Contains("START TRIAL", library.Status);
            StringAssert.DoesNotContain("starts episode one", library.Status);

            var clear = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation, RoomFailureReason.None,
                "confirmed scanned room");
            Assert.AreEqual("CONFIRM ROOM", clear.Label);
            StringAssert.Contains("Press CONFIRM ROOM to confirm this room", clear.Status);
            // The wall set, switch included, appears only after confirmation,
            // so the confirmation press must not describe the switch.
            StringAssert.DoesNotContain("knife switch", clear.Status);
            StringAssert.DoesNotContain("START TRIAL", clear.Status);
        }

        [Test]
        public void AnUnscannedRoomKeepsEpisodeOneFromLaunching()
        {
            var library = EpisodeLaunchGate.Evaluate(true, ShellSessionState.Library,
                RoomPreparationStatus.Idle);
            var permission = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.Explanation);
            var setup = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.SpaceSetupReady);
            var loading = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.SceneLoading);
            var failed = EpisodeLaunchGate.Evaluate(true, ShellSessionState.Restricted,
                RoomPreparationStatus.Restricted);

            Assert.IsFalse(library.CanLaunchEpisodeOne);
            Assert.IsTrue(library.ButtonEnabled);
            Assert.AreEqual("LAUNCH", library.Label);

            Assert.IsFalse(permission.CanLaunchEpisodeOne);
            Assert.AreEqual("ALLOW SCENE ACCESS", permission.Label);

            Assert.IsFalse(setup.CanLaunchEpisodeOne);
            Assert.AreEqual("START SPACE SETUP", setup.Label);

            Assert.IsFalse(loading.CanLaunchEpisodeOne);
            Assert.IsFalse(loading.ButtonEnabled);
            Assert.AreEqual("LOADING ROOM", loading.Label);

            Assert.IsFalse(failed.CanLaunchEpisodeOne);
            Assert.IsTrue(failed.ButtonEnabled);
            Assert.IsFalse(failed.ScanReady);
            Assert.AreEqual("SCAN AGAIN", failed.Label);
            StringAssert.Contains("cannot be used", failed.Status);
            Assert.AreEqual("", failed.SecondaryLabel);
        }

        [Test]
        public void EveryGateStateNamesTheSingleNextPressBeforeTheTrial()
        {
            var library = EpisodeLaunchGate.Evaluate(true, ShellSessionState.Library,
                RoomPreparationStatus.Idle);
            var permission = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.Explanation);
            var waiting = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.PermissionRequested);
            var loading = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.SceneLoading);
            var setup = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.SpaceSetupReady);
            var confirming = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation, RoomFailureReason.None,
                "confirmed scanned room");
            var unsafeConfirm = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation, RoomFailureReason.UnsafeRegion);
            var confirmed = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomConfirmed,
                RoomPreparationStatus.Confirmed);
            var running = EpisodeLaunchGate.Evaluate(true, ShellSessionState.SetupAccepted,
                RoomPreparationStatus.Confirmed);

            Assert.AreEqual("LAUNCH", library.Label);
            Assert.AreEqual("ALLOW SCENE ACCESS", permission.Label);
            Assert.AreEqual("WAITING FOR PERMISSION", waiting.Label);
            Assert.AreEqual("LOADING ROOM", loading.Label);
            Assert.AreEqual("START SPACE SETUP", setup.Label);
            Assert.AreEqual("CONFIRM ROOM", confirming.Label);
            Assert.AreEqual("CONFIRM ROOM", unsafeConfirm.Label);
            Assert.AreEqual("START TRIAL", confirmed.Label);
            Assert.AreEqual("EPISODE ONE", running.Label);

            // The confirmation press has no wall set to act on yet, so its
            // guidance names only that press.
            StringAssert.Contains("CONFIRM ROOM", confirming.Status);
            StringAssert.Contains("CONFIRM ROOM", unsafeConfirm.Status);
            StringAssert.DoesNotContain("knife switch", confirming.Status);
            StringAssert.DoesNotContain("START TRIAL", confirming.Status);
            StringAssert.DoesNotContain("knife switch", unsafeConfirm.Status);
        }

        [Test]
        public void AFailedRoomCheckNamesTheActionThatResolvesIt()
        {
            var missing = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation, RoomFailureReason.DisconnectedRegion);
            var small = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation, RoomFailureReason.BelowMinimum);
            var blocked = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation, RoomFailureReason.UnsafeRegion);
            var denied = EpisodeLaunchGate.Evaluate(true, ShellSessionState.Restricted,
                RoomPreparationStatus.Restricted, RoomFailureReason.ScenePermission);

            Assert.IsFalse(missing.CanLaunchEpisodeOne);
            Assert.AreEqual("ADD MISSING WALLS", missing.Label);
            Assert.AreEqual(EpisodeHubAction.AddMissingWalls, missing.Action);
            StringAssert.Contains("virtual", missing.Status);
            StringAssert.Contains("CONFIRM ROOM", missing.Status);
            Assert.AreEqual("", missing.SecondaryLabel);

            Assert.AreEqual("REDRAW THE ROOM", small.Label);
            Assert.AreEqual(EpisodeHubAction.RedrawRoom, small.Action);
            StringAssert.Contains("3", small.Status);

            Assert.IsTrue(blocked.CanLaunchEpisodeOne);
            Assert.AreEqual("CONFIRM ROOM", blocked.Label);
            Assert.AreEqual(EpisodeHubAction.Launch, blocked.Action);
            StringAssert.Contains("Safety hold", blocked.Status);
            Assert.IsTrue(blocked.EmphasizeStatus);
            Assert.AreEqual("", blocked.SecondaryLabel);

            var waived = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation, RoomFailureReason.UnsafeRegion,
                "", "", false);
            Assert.IsTrue(waived.CanLaunchEpisodeOne);
            Assert.AreEqual("CONFIRM ROOM", waived.Label);
            StringAssert.Contains("Safety check is off", waived.Status);
            Assert.IsFalse(waived.EmphasizeStatus);

            Assert.AreEqual("ALLOW SCENE ACCESS", denied.Label);
            Assert.AreEqual(EpisodeHubAction.RetrySetup, denied.Action);
            StringAssert.Contains("denied", denied.Status);
        }

        [Test]
        public void AClearScanDoesNotOfferAVirtualRoom()
        {
            var ready = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation, RoomFailureReason.None,
                "confirmed scanned room");

            Assert.AreEqual("CONFIRM ROOM", ready.Label);
            Assert.AreEqual("", ready.SecondaryLabel);
            StringAssert.Contains("CONFIRM ROOM", ready.Status);
        }

        [Test]
        public void AVirtualBoundarySaysItIsNotTheScannedRoom()
        {
            var ready = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation, RoomFailureReason.None,
                "virtual boundaries — not the scanned room");

            Assert.AreEqual("CONFIRM ROOM", ready.Label);
            StringAssert.Contains("not the scanned room", ready.Status);
        }

        [Test]
        public void ALockedCardStaysLockedAfterTheScan()
        {
            var gate = EpisodeLaunchGate.Evaluate(false, ShellSessionState.RoomPreparation,
                RoomPreparationStatus.AwaitingConfirmation);

            Assert.IsTrue(gate.ScanReady);
            Assert.IsFalse(gate.CanLaunchEpisodeOne);
            Assert.IsFalse(gate.ButtonEnabled);
            Assert.AreEqual("LOCKED", gate.Label);
        }

        [Test]
        public void TheHubStaysUpUntilEpisodeOneHasStarted()
        {
            Assert.IsFalse(EpisodeLaunchGate.HubClosesAfterEpisodeStart(ShellSessionState.RoomConfirmed));
            Assert.IsFalse(EpisodeLaunchGate.HubClosesAfterEpisodeStart(ShellSessionState.RoomPreparation));
            Assert.IsTrue(EpisodeLaunchGate.HubClosesAfterEpisodeStart(ShellSessionState.SetupAccepted));
        }

        [Test]
        public void ARunningEpisodeDoesNotOfferASecondLaunch()
        {
            var gate = EpisodeLaunchGate.Evaluate(true, ShellSessionState.SetupAccepted,
                RoomPreparationStatus.Confirmed);

            Assert.IsTrue(gate.ScanReady);
            Assert.IsFalse(gate.CanLaunchEpisodeOne);
            Assert.IsFalse(gate.ButtonEnabled);
            Assert.AreEqual("EPISODE ONE", gate.Label);
        }

        [Test]
        public void TheWallSetAppearsOnlyOnceTheRoomIsConfirmed()
        {
            Assert.IsFalse(EpisodeLaunchGate.WallSetVisible(ShellSessionState.Library));
            Assert.IsFalse(EpisodeLaunchGate.WallSetVisible(ShellSessionState.StartReady));
            Assert.IsFalse(EpisodeLaunchGate.WallSetVisible(ShellSessionState.RoomPreparation));
            Assert.IsFalse(EpisodeLaunchGate.WallSetVisible(ShellSessionState.Restricted));
            Assert.IsTrue(EpisodeLaunchGate.WallSetVisible(ShellSessionState.RoomConfirmed));
            Assert.IsTrue(EpisodeLaunchGate.WallSetVisible(ShellSessionState.SetupAccepted));
        }

        [Test]
        public void TheKnifeSwitchStartsAConfirmedRoomExactlyOnce()
        {
            Assert.IsTrue(EpisodeLaunchGate.KnifeSwitchStartsTrial(
                ShellSessionState.RoomConfirmed, closed: false));

            Assert.IsFalse(EpisodeLaunchGate.KnifeSwitchStartsTrial(
                ShellSessionState.Library, closed: false));
            Assert.IsFalse(EpisodeLaunchGate.KnifeSwitchStartsTrial(
                ShellSessionState.RoomPreparation, closed: false));
            Assert.IsFalse(EpisodeLaunchGate.KnifeSwitchStartsTrial(
                ShellSessionState.Restricted, closed: false));
            Assert.IsFalse(EpisodeLaunchGate.KnifeSwitchStartsTrial(
                ShellSessionState.SetupAccepted, closed: false));
            Assert.IsFalse(EpisodeLaunchGate.KnifeSwitchStartsTrial(
                ShellSessionState.RoomConfirmed, closed: true));

            Assert.IsTrue(EpisodeLaunchGate.TrialRunning(ShellSessionState.SetupAccepted));
            Assert.IsFalse(EpisodeLaunchGate.TrialRunning(ShellSessionState.RoomConfirmed));
        }

        [Test]
        public void TheBladesReadClosedExactlyWhileATrialRuns()
        {
            Assert.IsTrue(WallKnifeSwitch.RendersClosed(ShellSessionState.SetupAccepted));
            Assert.IsFalse(WallKnifeSwitch.RendersClosed(ShellSessionState.RoomConfirmed));
            Assert.IsFalse(WallKnifeSwitch.RendersClosed(ShellSessionState.Library));
            Assert.IsFalse(EpisodeLaunchGate.KnifeSwitchStartsTrial(
                ShellSessionState.SetupAccepted, closed: false));
            Assert.IsTrue(EpisodeLaunchGate.KnifeSwitchStopsTrial(ShellSessionState.SetupAccepted));
            Assert.IsFalse(EpisodeLaunchGate.KnifeSwitchStopsTrial(ShellSessionState.RoomConfirmed));
        }

        [Test]
        public void PanelStartStaysTheFallbackWhenTheWallSetIsVisible()
        {
            var gate = EpisodeLaunchGate.Evaluate(true, ShellSessionState.RoomConfirmed,
                RoomPreparationStatus.Confirmed);

            Assert.IsTrue(EpisodeLaunchGate.WallSetVisible(ShellSessionState.RoomConfirmed));
            Assert.IsTrue(gate.CanLaunchEpisodeOne);
            Assert.IsTrue(gate.ButtonEnabled);
            Assert.AreEqual("START TRIAL", gate.Label);
            Assert.AreEqual(EpisodeHubAction.StartTrial, gate.Action);
            Assert.IsFalse(EpisodeLaunchGate.HubClosesAfterEpisodeStart(ShellSessionState.RoomConfirmed));
            Assert.IsTrue(EpisodeLaunchGate.HubClosesAfterEpisodeStart(ShellSessionState.SetupAccepted));
        }

        [Test]
        public void EveryKnifeSwitchInputKindReachesTheStartAndStopDecision()
        {
            foreach (var kind in new[]
                     {
                         KnifeSwitchInputKind.HandPinch, KnifeSwitchInputKind.ControllerTrigger,
                         KnifeSwitchInputKind.Ray, KnifeSwitchInputKind.Poke
                     })
            {
                Assert.AreEqual(KnifeSwitchOutcome.Started,
                    WallKnifeSwitch.Decide(kind, ShellSessionState.RoomConfirmed, closed: false,
                        roomPresent: true, runtimeTrialRunning: false),
                    kind.ToString());
                Assert.AreEqual(KnifeSwitchOutcome.Held,
                    WallKnifeSwitch.Decide(kind, ShellSessionState.RoomConfirmed, closed: false,
                        roomPresent: false, runtimeTrialRunning: false),
                    kind.ToString());
                Assert.AreEqual(KnifeSwitchOutcome.Held,
                    WallKnifeSwitch.Decide(kind, ShellSessionState.RoomPreparation, closed: false,
                        roomPresent: true, runtimeTrialRunning: false),
                    kind.ToString());
                Assert.AreEqual(KnifeSwitchOutcome.Stopped,
                    WallKnifeSwitch.Decide(kind, ShellSessionState.SetupAccepted, closed: true,
                        roomPresent: true, runtimeTrialRunning: true),
                    kind.ToString());
                Assert.AreEqual(KnifeSwitchOutcome.VisualOnly,
                    WallKnifeSwitch.Decide(kind, ShellSessionState.RoomConfirmed, closed: true,
                        roomPresent: true, runtimeTrialRunning: false),
                    kind.ToString());
            }

            Assert.AreEqual(KnifeSwitchOutcome.None,
                WallKnifeSwitch.Decide(KnifeSwitchInputKind.None, ShellSessionState.RoomConfirmed,
                    closed: false, roomPresent: true, runtimeTrialRunning: false));
        }

        [Test]
        public void TheStopDecisionNeedsARunningTrialAndALiveRuntime()
        {
            // Stop only while the session reports the trial running.
            Assert.IsTrue(EpisodeLaunchGate.CanStopTrial(ShellSessionState.SetupAccepted,
                runtimeTrialRunning: true));
            Assert.IsFalse(EpisodeLaunchGate.CanStopTrial(ShellSessionState.RoomConfirmed,
                runtimeTrialRunning: true));

            // A refused stop leaves the trial running: the session state is
            // unchanged and the blades decide nothing.
            Assert.IsFalse(EpisodeLaunchGate.CanStopTrial(ShellSessionState.SetupAccepted,
                runtimeTrialRunning: false));
            Assert.AreEqual("no_runtime_trial", EpisodeLaunchGate.StopRefusalReason(
                ShellSessionState.SetupAccepted, runtimeTrialRunning: false));
            Assert.IsTrue(EpisodeLaunchGate.TrialRunning(ShellSessionState.SetupAccepted));
            Assert.AreEqual(KnifeSwitchOutcome.Held,
                WallKnifeSwitch.Decide(KnifeSwitchInputKind.Poke, ShellSessionState.SetupAccepted,
                    closed: true, roomPresent: true, runtimeTrialRunning: false));
        }

        [Test]
        public void OneGestureMakesExactlyOneSwitchTransition()
        {
            var gesture = new KnifeSwitchGesture();
            var session = ShellSessionState.RoomConfirmed;
            var closed = false;

            Assert.AreEqual(KnifeSwitchOutcome.Started,
                gesture.Press(KnifeSwitchInputKind.Poke, session, closed, roomPresent: true,
                    runtimeTrialRunning: false));
            session = ShellSessionState.SetupAccepted;
            closed = true;

            Assert.AreEqual(KnifeSwitchOutcome.None,
                gesture.Press(KnifeSwitchInputKind.Poke, session, closed, roomPresent: true,
                    runtimeTrialRunning: true),
                "the held select must not stop the trial it just started");

            gesture.Release();
            Assert.AreEqual(KnifeSwitchOutcome.Stopped,
                gesture.Press(KnifeSwitchInputKind.Poke, session, closed, roomPresent: true,
                    runtimeTrialRunning: true));
            session = ShellSessionState.RoomConfirmed;
            closed = false;

            Assert.AreEqual(KnifeSwitchOutcome.None,
                gesture.Press(KnifeSwitchInputKind.Poke, session, closed, roomPresent: true,
                    runtimeTrialRunning: false),
                "the select that stopped must not restart in the same gesture");

            gesture.Release();
            Assert.AreEqual(KnifeSwitchOutcome.Started,
                gesture.Press(KnifeSwitchInputKind.Poke, session, closed, roomPresent: true,
                    runtimeTrialRunning: false),
                "a fresh gesture starts the next trial in the same confirmed room");
        }
    }
}
