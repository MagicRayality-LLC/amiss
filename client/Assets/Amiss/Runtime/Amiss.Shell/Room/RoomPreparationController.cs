using System;
using System.Globalization;
using Amiss.Shell.Episodes;
using Amiss.Shell.Simulation;
using UnityEngine;

namespace Amiss.Shell.Room
{
    public sealed partial class RoomPreparationController : MonoBehaviour
    {
        ShellServices services;
        bool lastWorldLock = true;
        SimulationEvidenceManifest simulationEvidence;

        public RoomPreparation Preparation { get; private set; }
        public ShellRecovery Recovery { get; private set; }
        public MetaRoomAdapter Adapter { get; private set; }
        public EvidenceManifest Evidence { get; private set; }
        public bool EvidenceWriteFailed { get; private set; }
        public string EvidencePath { get; private set; } = "";
        public string StartBlock { get; private set; } = "";
        public bool SafetyCheckEnabled { get; private set; } = true;
        public bool VirtualWallsEnabled { get; private set; } = true;

        void Awake()
        {
            services = GetComponent<ShellServices>();
            Adapter = GetComponent<MetaRoomAdapter>();
            Preparation = new RoomPreparation();
            Recovery = new ShellRecovery();
            Evidence = EvidenceManifest.Create();
            Preparation.Changed += SyncSession;
            Recovery.Changed += SyncRecovery;
            if (Adapter != null)
                Adapter.SceneUpdated += OnSceneUpdated;
        }

        void OnDestroy()
        {
            Preparation.Changed -= SyncSession;
            Recovery.Changed -= SyncRecovery;
            if (Adapter != null)
                Adapter.SceneUpdated -= OnSceneUpdated;
        }

        void Update()
        {
#if DEVELOPMENT_BUILD
            PollEvidenceCommand();
#endif
            if (Preparation.Confirmation == null || Adapter == null)
                return;
            var active = Adapter.WorldLockActive;
            if (lastWorldLock && !active)
                Recovery.RelocalizationChanged(false);
            lastWorldLock = active;
        }

        public void BeginSelectedEpisode()
        {
            if (services.Session.State != ShellSessionState.RoomPreparation ||
                services.Session.Selected?.MinimumVolumeM3 is not double minimum)
                return;
            Recovery.BeginFutureRescan();
            Preparation.Begin(minimum);
            Record("room_prepare", "explanation");
        }

        public void PrimaryAction()
        {
            if (Recovery.SafetyHold)
            {
                ReconfirmSafety();
                return;
            }
            if (Recovery.ResumeRequired)
            {
                RequestResume();
                return;
            }
            switch (Preparation.Status)
            {
                case RoomPreparationStatus.Explanation:
                    RequestPermission();
                    break;
                case RoomPreparationStatus.SpaceSetupReady:
                    RequestSpaceSetup();
                    break;
                case RoomPreparationStatus.AwaitingConfirmation:
                    if (Preparation.Failure == RoomFailureReason.DisconnectedRegion)
                        AddMissingWalls();
                    else if (Preparation.Failure == RoomFailureReason.BelowMinimum)
                        RedrawRoom();
                    else
                        LaunchSelectedEpisode();
                    break;
                case RoomPreparationStatus.Confirmed:
                    if (StartBlock == "below_minimum_volume")
                        RedrawRoom();
                    else if (StartBlock == "invalid_volume")
                        RetryFailedSetup();
                    else
                        AcceptSetup();
                    break;
                case RoomPreparationStatus.Restricted:
                    if (Preparation.Failure == RoomFailureReason.SpaceSetupCancelled ||
                        Preparation.Failure == RoomFailureReason.SpaceSetupFailed ||
                        Preparation.Failure == RoomFailureReason.NoScene ||
                        Preparation.Failure == RoomFailureReason.NoCurrentRoom)
                        RedrawRoom();
                    else
                        RetryFailedSetup();
                    break;
            }
        }

        public void RequestPermission()
        {
            if (Adapter == null)
                return;
            var id = Preparation.PreparationId;
            if (!Preparation.RequestPermission(id))
                return;
            Record("permission", "requested");
            Adapter.RequestSpatialPermission(async granted =>
            {
                if (!Preparation.CompletePermission(id, granted))
                    return;
                Record("permission", granted ? "granted" : "denied");
                SyncSession();
                if (Preparation.Status == RoomPreparationStatus.SceneLoading)
                    await LoadScene(id);
            });
        }

        public async void RequestSpaceSetup()
        {
            if (Adapter == null)
                return;
            var id = Preparation.PreparationId;
            if (!Preparation.RequestSpaceSetup(id))
                return;
            Record("space_setup", "requested");
            var succeeded = await Adapter.RequestSpaceSetup();
            if (!Preparation.CompleteSpaceSetup(id, succeeded))
                return;
            Record("space_setup", succeeded ? "returned" : "failed");
            if (Preparation.Status == RoomPreparationStatus.SceneLoading)
                await LoadScene(id);
        }

        public void CancelSpaceSetup()
        {
            var id = Preparation.PreparationId;
            if (Preparation.CompleteSpaceSetup(id, false, true))
                Record("space_setup", "cancelled");
        }

        async System.Threading.Tasks.Task LoadScene(string id)
        {
            GetUserPose(out var position, out var forward);
            var result = await Adapter.LoadDeviceScene(position, forward);
            if (!Preparation.CompleteSceneLoad(id, result.Draft, result.Failure))
                return;
            var sceneOutcome = Preparation.Status == RoomPreparationStatus.AwaitingConfirmation
                ? "loaded"
                : Preparation.Status == RoomPreparationStatus.SpaceSetupReady
                    ? "space_setup_ready"
                    : "restricted:" + Preparation.Failure;
            Record("scene_load", sceneOutcome);
            SyncSession();
            if (Preparation.Status == RoomPreparationStatus.SpaceSetupReady ||
                Preparation.Status == RoomPreparationStatus.Restricted)
                RelocatePanel();
            TryEnterLoadedScan();
        }

        /// <summary>
        /// The press that starts room preparation also asks for scene access.
        /// When the headset already allows it, that same press loads the scan.
        /// </summary>
        public void ContinueIntoRoom()
        {
            if (Preparation.Status == RoomPreparationStatus.Explanation)
                RequestPermission();
        }

        /// <summary>
        /// A loaded scan is the room. Missing edges become virtual walls on
        /// that scan, then a clear scan starts the episode. A missing scan
        /// does not invent a room.
        /// </summary>
        void TryEnterLoadedScan()
        {
            if (Preparation.Status != RoomPreparationStatus.AwaitingConfirmation)
                return;
            if (Preparation.Failure == RoomFailureReason.DisconnectedRegion)
                AddMissingWalls();
            if (Preparation.Failure == RoomFailureReason.None ||
                Preparation.Failure == RoomFailureReason.UnsafeRegion)
                LaunchSelectedEpisode();
        }

        /// <summary>
        /// Starts episode one after the room scan has loaded. The scan
        /// itself does not start a trial. This call is the wearer's launch:
        /// it confirms the loaded room and stops there. Closing the knife
        /// switch, or pressing the hub's START TRIAL, starts the trial.
        /// </summary>
        public void LaunchSelectedEpisode()
        {
            if (Preparation.Status != RoomPreparationStatus.AwaitingConfirmation)
                return;
            if (Preparation.Failure == RoomFailureReason.None ||
                Preparation.Failure == RoomFailureReason.UnsafeRegion)
            {
                GetUserPose(out var position, out var forward);
                Preparation.RetargetUser(position, forward);
            }
            var safetyOff = !SafetyCheckEnabled &&
                Preparation.Failure == RoomFailureReason.UnsafeRegion;
            if (Preparation.Failure != RoomFailureReason.None && !safetyOff)
            {
                Record("room_confirm", "refused:" + Preparation.Failure);
                return;
            }
            if (safetyOff)
                Record("room_confirm", "safety_off");
            ConfirmRoom();
        }

        public void AddMissingWalls()
        {
            StartBlock = "";
            if (Preparation.AddVirtualSupplements(Preparation.PreparationId))
                Record("virtual_supplements", "added");
        }

        public void RedrawRoom()
        {
            StartBlock = "";
            if (!Preparation.OfferReplacementScan(Preparation.PreparationId))
                return;
            if (services.Session.State == ShellSessionState.Restricted)
                services.Session.MarkRoomPreparation();
            Record("space_setup", "offered_after_failure");
            RequestSpaceSetup();
        }

        public void RetryFailedSetup()
        {
            StartBlock = "";
            Rescan();
            RequestPermission();
        }

        public void SetSafetyCheck(bool enabled) => SafetyCheckEnabled = enabled;

        public void SetVirtualWalls(bool enabled) => VirtualWallsEnabled = enabled;

        public void ConfirmRoom()
        {
            var id = Preparation.PreparationId;
            var allowUnsafe = !SafetyCheckEnabled;
            if (!Preparation.Confirm(id, allowUnsafe) || !Recovery.ConfirmRoom(Preparation))
            {
                var reason = Preparation.Failure == RoomFailureReason.None
                    ? "recovery"
                    : Preparation.Failure.ToString();
                Record("room_confirm", "refused:" + reason);
                return;
            }
            services.Session.MarkRoomConfirmed();
            Record("room_confirm", "confirmed");
        }

        public void AcceptSetup()
        {
            StartBlock = "";
            if (Preparation.Confirmation == null)
                return;
            var trialId = "trial-local-0001";
            var pin = Recovery.AcceptTrialSetup(trialId, Preparation.Confirmation.CollisionChecksum);
            if (pin == null)
            {
                StartBlock = Recovery.SafetyValid ? "could_not_lock" : "safety";
                Record("setup_accept", "refused");
                return;
            }

            var request = TemperatureTrialSetup.CreateTrialStart(trialId,
                Preparation.Confirmation, pin, out var sizing);
            var outcome = services.SimulationRuntime.TryStartTrial(request);
            simulationEvidence = SimulationEvidenceManifest.Create(WhatIsTemperatureEpisode.EpisodeId);
            simulationEvidence.CaptureTrial(outcome, services.SimulationRuntime.TrialSummary,
                Preparation.Confirmation, sizing.Record());
            WriteSimulationEvidence();
            Record("simulation_start", outcome.Started
                ? "started;" + outcome.BallCount + "balls;" + outcome.WriterConfigurationId
                : "refused;" + outcome.ReasonCode);
            if (!outcome.Started)
            {
                StartBlock = string.IsNullOrEmpty(outcome.ReasonCode) ? "not_started" : outcome.ReasonCode;
                Record("setup_accept", "runtime_refused");
                return;
            }
            StartBlock = "";
            services.Session.MarkSetupAccepted();
            services.SessionMemory?.RecordEvent("setup_accepted", 0, trialId, 1);
            services.Panel?.ShowSubtitle(WhatIsTemperatureEpisode.OpeningGoal);
            services.VoiceControl?.StartTrialEpisode(WhatIsTemperatureEpisode.OpeningGoal);
            Record("setup_accept", "accepted");
        }

        /// <summary>True while the runtime holds the one live Trial.</summary>
        public bool HasRunningTrial => services != null && services.SimulationRuntime?.TrialSummary != null;

        /// <summary>
        /// Ends the running trial as a first-class trial end: the final tick and
        /// readout are captured into the same local evidence file the start
        /// wrote, the runtime releases its writer and views, the accepted setup
        /// is cleared, and the session returns to <c>RoomConfirmed</c> so the
        /// same confirmed room can start a new trial without re-confirming it.
        /// A missing live trial is refused and changes nothing.
        /// </summary>
        public bool StopRunningTrial(string reason)
        {
            var runtime = services.SimulationRuntime;
            var session = services.Session.State;
            var runtimeTrialRunning = runtime != null && runtime.TrialSummary != null;
            if (!EpisodeLaunchGate.CanStopTrial(session, runtimeTrialRunning))
            {
                var refusal = EpisodeLaunchGate.StopRefusalReason(session, runtimeTrialRunning);
                Record("trial_stop", "refused:" + refusal);
                return false;
            }

            WriteSimulationEvidence();
            runtime.StopTrial(reason ?? "trial_stop");
            services.VoiceControl?.EndTrialEpisode();
            simulationEvidence = null;
            StartBlock = "";
            Recovery.TrialStopped();
            services.Session.MarkTrialStopped();
            Record("trial_stop", "stopped:" + (reason ?? "trial_stop"));
            return true;
        }

        public void ManualOrSupplement()
        {
            var id = Preparation.PreparationId;
            if (Preparation.Status == RoomPreparationStatus.AwaitingConfirmation &&
                Preparation.AddVirtualSupplements(id))
                Record("virtual_supplements", "added");
        }

        public void Rescan()
        {
            if (services.Session.State != ShellSessionState.RoomPreparation &&
                services.Session.State != ShellSessionState.RoomConfirmed &&
                services.Session.State != ShellSessionState.SetupAccepted &&
                services.Session.State != ShellSessionState.Restricted)
                return;
            StartBlock = "";
            Recovery.BeginFutureRescan();
            Preparation.Rescan();
            services.Session.MarkRoomPreparation();
            Record("rescan", "future_trial_only");
        }

        public void ExitToLibrary()
        {
            services.SimulationRuntime.StopTrial("exit_to_library");
            services.VoiceControl?.EndTrialEpisode();
            simulationEvidence = null;
            Preparation.Cancel();
            services.Session.ReturnToLibrary();
            Record("exit", "library");
        }

        /// <summary>
        /// Runtime evidence for the accepted start: complete writer record,
        /// sizing, collision pin, and the current Tick. It rewrites one local
        /// file and carries no raw geometry or personal data.
        /// </summary>
        public void WriteSimulationEvidence()
        {
            if (simulationEvidence == null)
                return;
            simulationEvidence.CaptureTick(services.SimulationRuntime?.TrialSummary);
            simulationEvidence.CaptureReadout(services.SimulationRuntime?.Readout);
            try
            {
                simulationEvidence.WriteAtomic(Application.persistentDataPath);
            }
            catch (Exception)
            {
                EvidenceWriteFailed = true;
            }
        }

        public string SimulationStateSummary()
        {
            var summary = services.SimulationRuntime?.TrialSummary;
            if (summary == null)
                return "none";
            WriteSimulationEvidence();
            return summary.TrialId + ";tick=" + summary.Tick + ";balls=" + summary.BallCount +
                   ";pin=" + summary.ShortChecksum + ";cfg=" + summary.WriterConfigurationId +
                   ";paused=" + summary.Paused;
        }

        /// <summary>
        /// The producer-owned readout as one evidence line. It is copied from
        /// the runtime, never recomputed here, so a captured artifact can show
        /// what the panel was given rather than what a consumer derived.
        /// </summary>
        public string ReadoutSummary()
        {
            var readout = services.SimulationRuntime?.Readout;
            var measurement = readout?.Measurement;
            if (readout == null || measurement == null)
                return "unavailable;no_episode_producer";
            var tuple = measurement.Display;
            return "status=" + measurement.Status +
                   ";contact_commits=" + readout.ContactCommits +
                   ";free=" + measurement.NFree +
                   ";held=" + measurement.NHeld +
                   ";K=" + (tuple?.Kelvin.ToString(CultureInfo.InvariantCulture) ?? "none") +
                   ";C=" + (tuple?.Celsius.ToString(CultureInfo.InvariantCulture) ?? "none") +
                   ";F=" + (tuple?.Fahrenheit.ToString(CultureInfo.InvariantCulture) ?? "none") +
                   ";ratio=" + (tuple == null ? "none" : tuple.KelvinRatioBaseline.ToString(
                       "0.0", CultureInfo.InvariantCulture)) +
                   ";k_thermal_j=" + measurement.KThermalJ.ToString(
                       "0.########", CultureInfo.InvariantCulture) +
                   ";work_j=" + readout.Ledger.WorkEventsCumulativeKeJ.ToString(
                       "0.########", CultureInfo.InvariantCulture) +
                   ";contacts=" + readout.Ledger.Contacts.Count;
        }

        /// <summary>
        /// The evaluation tutor state as one evidence line: whether the port is
        /// bound, whether a key is held (presence only, never the value), the
        /// candidates and their acceptance, and the visible trace line. In this
        /// build it proves the path is closed for a readable reason.
        /// </summary>
        public string EvaluationStateSummary()
        {
            var tutor = services.Tutor;
            if (tutor == null)
                return "unbound";
            var last = tutor.LastFailure;
            return "available=" + tutor.Available +
                   ";key=" + (tutor.KeyPresent ? "present" : "absent") +
                   ";candidates=" + services.Candidates.Candidates.Count +
                   ";proposed=" + System.Linq.Enumerable.Count(
                       services.Candidates.Candidates,
                       candidate => candidate.Acceptance == "proposed") +
                   ";last_failure=" + (last == null
                       ? "none"
                       : last.ReasonCode + ":" + last.Kind) +
                   ";trace=" + tutor.TraceLine;
        }

        /// <summary>
        /// The complete fail-closed explanation as its own evidence entry, so
        /// the 160-character entry bound cannot cut the readable reason.
        /// </summary>
        public string EvaluationReasonDetail() => services.Tutor?.FailClosedDetail ?? "";

        public void RequestResume()
        {
            if (!Recovery.RequestResume())
                return;
            services.SimulationRuntime.RequestResume();
            Record("focus_recovery", "explicit_resume");
        }

        public void ReconfirmSafety()
        {
            if (Preparation.Collision != null && Recovery.ReconfirmSafety(Preparation.Collision.Checksum))
                Record("safety", "reconfirmed_resume_required");
        }

        public void RelocatePanel()
        {
            if (Preparation.Confirmation == null)
            {
                var instrument = FindFirstObjectByType<InstrumentRoot>();
                var eye = GetEyeTransform();
                if (instrument == null || eye == null)
                {
                    Record("panel_recovery", "candidate_refused");
                    return;
                }
                InstrumentRoot.PlaceInitialPanel(instrument.transform, eye.position, eye.forward);
                Record("panel_recovery", "initial_repositioned");
                return;
            }
            var accepted = Recovery.ResetPanelToNearestWall();
            Record("panel_recovery", accepted ? "reset_nearest_wall" : Recovery.Panel?.Reason ?? "candidate_refused");
        }

        public bool ResetPanelToNearestWall()
        {
            if (Preparation.Confirmation == null)
                return false;
            var accepted = Recovery.ResetPanelToNearestWall();
            Record("panel_move", accepted ? "reset_nearest_wall" : Recovery.Panel?.Reason ?? "reset_refused");
            return accepted;
        }

        public bool TryRemountPanel(Vector3 releasePosition, Quaternion releaseRotation)
        {
            if (Preparation.Confirmation == null)
                return false;
            var accepted = Recovery.TryRemountPanel(releasePosition, releaseRotation, out var reason);
            Record("panel_move", accepted ? "wall_remount" : reason);
            return accepted;
        }

        public bool CanMovePanel => Preparation.Confirmation != null && Recovery.Panel != null;

        public void InvalidateSafetyForEvidence()
        {
            Recovery.InvalidateSafety();
            Record("safety", "hold");
        }

        public void SceneUpdatedForEvidence()
        {
            Recovery.SceneUpdated();
            Record("scene_update", "historical_pin_unchanged");
        }

        void OnSceneUpdated()
        {
            Recovery.SceneUpdated();
            Record("scene_update", "future_rescan_available");
        }

        void OnApplicationFocus(bool focused)
        {
            if (focused)
            {
                Recovery.FocusReturned();
            }
            else
            {
                // A swing that loses validity must close as an abort: no later
                // sample may attach work to it, and no release is invented.
                services.Bat?.AbortOpenSwing();
                Recovery.FocusLost();
            }
            Record("focus", focused ? "returned" : "lost");
        }

        void SyncSession()
        {
            if (Preparation.Restricted)
                services.Session.MarkRestricted();
        }

        void SyncRecovery()
        {
            if (Recovery.PauseRequested)
                services.SimulationRuntime.RequestPause(Recovery.HoldReason);
        }

        void Record(string scenario, string outcome, bool fixture = false)
        {
            Evidence.Record(scenario, outcome,
#if UNITY_ANDROID && !UNITY_EDITOR
                fixture ? "quest_fixture" : "quest_os",
#else
                fixture ? "editor_fixture" : "editor",
#endif
                Preparation);
            try
            {
                EvidencePath = Evidence.WriteAtomic(Application.persistentDataPath);
            }
            catch (Exception)
            {
                EvidenceWriteFailed = true;
            }
        }

        Transform GetEyeTransform()
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            return rig != null ? rig.centerEyeAnchor : Camera.main != null ? Camera.main.transform : null;
        }

        void GetUserPose(out Vector3 position, out Vector3 forward)
        {
            var eye = GetEyeTransform();
            forward = eye != null ? eye.forward : Vector3.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
            position = eye != null ? eye.position : new Vector3(0f, 1.6f, 0f);
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null)
                position.y = rig.trackingSpace.position.y;
            else
                position.y -= 1.6f;
        }
    }
}
