using System;
using UnityEngine;

namespace Amiss.Shell.Room
{
    public sealed partial class RoomPreparationController : MonoBehaviour
    {
        ShellServices services;
        bool lastWorldLock = true;

        public RoomPreparation Preparation { get; private set; }
        public ShellRecovery Recovery { get; private set; }
        public MetaRoomAdapter Adapter { get; private set; }
        public EvidenceManifest Evidence { get; private set; }
        public bool EvidenceWriteFailed { get; private set; }
        public string EvidencePath { get; private set; } = "";

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
                    ConfirmRoom();
                    break;
                case RoomPreparationStatus.Confirmed:
                    AcceptSetup();
                    break;
                case RoomPreparationStatus.Restricted:
                    Rescan();
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
            Adapter.RequestSpatialPermission(granted =>
            {
                if (!Preparation.CompletePermission(id, granted))
                    return;
                Record("permission", granted ? "granted" : "denied");
                SyncSession();
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
            Record("scene_load", Preparation.Status == RoomPreparationStatus.Restricted ? "restricted" : "loaded");
            SyncSession();
        }

        public void ConfirmRoom()
        {
            var id = Preparation.PreparationId;
            if (!Preparation.Confirm(id) || !Recovery.ConfirmRoom(Preparation))
            {
                Record("room_confirm", "refused");
                return;
            }
            services.Session.MarkRoomConfirmed();
            Record("room_confirm", "confirmed");
        }

        public void AcceptSetup()
        {
            if (Preparation.Confirmation == null)
                return;
            var pin = Recovery.AcceptTrialSetup("trial-local-0001", Preparation.Confirmation.CollisionChecksum);
            if (pin == null)
            {
                Record("setup_accept", "refused");
                return;
            }
            services.Session.MarkSetupAccepted();
            Record("setup_accept", "accepted");
        }

        public void ManualOrSupplement()
        {
            var id = Preparation.PreparationId;
            if (Preparation.Status == RoomPreparationStatus.Restricted)
            {
                GetUserPose(out var position, out var forward);
                if (Preparation.ChooseManualBoundary(id, position, forward))
                    Record("manual_boundary", "drafted");
                return;
            }
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
            Recovery.BeginFutureRescan();
            Preparation.Rescan();
            services.Session.MarkRoomPreparation();
            Record("rescan", "future_trial_only");
        }

        public void ExitToLibrary()
        {
            Preparation.Cancel();
            services.Session.ReturnToLibrary();
            Record("exit", "library");
        }

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
            GetUserPose(out var position, out var forward);
            var candidate = position + forward * 1.5f + Vector3.up * 1.3f;
            var accepted = Recovery.RelocatePanel(candidate, Quaternion.LookRotation(forward, Vector3.up));
            Record("panel_recovery", accepted ? "relocated" : "candidate_refused");
        }

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
                Recovery.FocusReturned();
            else
                Recovery.FocusLost();
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

        void GetUserPose(out Vector3 position, out Vector3 forward)
        {
            var eye = Camera.main != null ? Camera.main.transform : null;
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
