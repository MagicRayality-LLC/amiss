#if DEVELOPMENT_BUILD
using UnityEngine;

namespace Amiss.Shell.Room
{
    public sealed partial class RoomPreparationController
    {
        string lastEvidenceCommand = "";

        void PollEvidenceCommand()
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = activity.Call<AndroidJavaObject>("getIntent");
            var raw = intent.Call<string>("getStringExtra", "amiss_room_command");
            if (string.IsNullOrEmpty(raw) || raw == lastEvidenceCommand)
                return;
            lastEvidenceCommand = raw;
            EvidenceCommand(raw.Split('#')[0]);
        }

        public void EvidenceCommand(string command)
        {
            switch (command)
            {
                case "select":
                    if (services.Session.State == ShellSessionState.Library && services.Session.SelectedIndex < 0)
                        services.Session.SelectCard(0);
                    if (services.Session.State == ShellSessionState.StartReady)
                    {
                        services.Session.RequestStart();
                        BeginSelectedEpisode();
                    }
                    break;
                case "primary":
                    PrimaryAction();
                    break;
                case "deny_permission":
                    DenyPermissionFixture();
                    break;
                case "no_scene":
                    NoSceneFixture();
                    break;
                case "cancel_setup":
                    CancelSpaceSetup();
                    break;
                case "load_scene":
                    LoadDeviceSceneFixture();
                    break;
                case "confirm":
                    ConfirmRoom();
                    break;
                case "accept":
                    AcceptSetup();
                    break;
                case "manual":
                case "supplement":
                    ManualOrSupplement();
                    break;
                case "rescan":
                    Rescan();
                    break;
                case "scene_update":
                    Recovery.SceneUpdated();
                    Record("scene_update", "historical_pin_unchanged", true);
                    break;
                case "relocalize":
                    Recovery.RelocalizationChanged(false);
                    Record("relocalization", "panel_recovery_only", true);
                    break;
                case "relocate":
                    RelocatePanel();
                    break;
                case "focus_lost":
                    Recovery.FocusLost();
                    Record("focus", "lost", true);
                    break;
                case "focus_return":
                    Recovery.FocusReturned();
                    Record("focus", "returned", true);
                    break;
                case "resume":
                    RequestResume();
                    break;
                case "safety_hold":
                    Recovery.InvalidateSafety();
                    Record("safety", "hold", true);
                    break;
                case "reconfirm":
                    ReconfirmSafety();
                    break;
                case "state":
                    Record("state", $"status={Preparation.Status};failure={Preparation.Failure};" +
                        $"focused={Recovery.Focused};resume={Recovery.ResumeRequired};" +
                        $"safety={Recovery.SafetyValid};pins={Recovery.HistoricalPins.Count}", true);
                    break;
                case "exit":
                    ExitToLibrary();
                    break;
            }
        }

        async void LoadDeviceSceneFixture()
        {
            var id = Preparation.PreparationId;
            if (!Preparation.RequestSpaceSetup(id))
                return;
            Preparation.CompleteSpaceSetup(id, true);
            Record("space_setup", "direct_device_load", true);
            await LoadScene(id);
        }

        void DenyPermissionFixture()
        {
            var id = Preparation.PreparationId;
            if (!Preparation.RequestPermission(id))
                return;
            Preparation.CompletePermission(id, false);
            Record("permission", "denied", true);
            SyncSession();
        }

        void NoSceneFixture()
        {
            var id = Preparation.PreparationId;
            if (!Preparation.RequestPermission(id))
                return;
            Preparation.CompletePermission(id, true);
            Preparation.RequestSpaceSetup(id);
            Preparation.CompleteSpaceSetup(id, true);
            Preparation.CompleteSceneLoad(id, null, RoomFailureReason.NoScene);
            Record("scene_load", "no_scene", true);
            SyncSession();
        }
    }
}
#endif
