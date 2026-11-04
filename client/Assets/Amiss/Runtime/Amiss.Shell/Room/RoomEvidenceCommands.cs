#if DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Amiss.Shell.Room
{
    public sealed partial class RoomPreparationController
    {
        string lastEvidenceCommand = "";
        bool setupAutomation;

        void PollEvidenceCommand()
        {
            if (setupAutomation)
                DriveSetupAutomation();
            var path = Path.Combine(Application.persistentDataPath, "room-command.txt");
            string raw;
            try
            {
                raw = File.ReadAllText(path).Trim();
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            if (string.IsNullOrEmpty(raw) || raw == lastEvidenceCommand)
                return;
            lastEvidenceCommand = raw;
            EvidenceCommand(raw.Split('#')[0]);
        }

        void DriveSetupAutomation()
        {
            if (Preparation.Status == RoomPreparationStatus.SpaceSetupRequested)
                return;
            if (Recovery.ResumeRequired)
            {
                if (Recovery.Focused)
                    RequestResume();
                return;
            }
            if (services.Session.State == ShellSessionState.Library ||
                services.Session.State == ShellSessionState.StartReady)
            {
                EvidenceCommand("select");
                return;
            }
            if (services.Session.State == ShellSessionState.SetupAccepted)
            {
                StopSetupAutomation("complete");
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
                    if (Preparation.Status == RoomPreparationStatus.AwaitingConfirmation)
                        StopSetupAutomation("confirmation_refused");
                    break;
                case RoomPreparationStatus.Confirmed:
                    AcceptSetup();
                    if (services.Session.State != ShellSessionState.SetupAccepted)
                        StopSetupAutomation("setup_refused");
                    break;
                case RoomPreparationStatus.Restricted:
                    StopSetupAutomation("restricted");
                    break;
            }
        }

        void StopSetupAutomation(string outcome)
        {
            setupAutomation = false;
            Record("setup_automation", outcome, true);
        }

        public void EvidenceCommand(string command)
        {
            switch (command)
            {
                case "setup":
                    setupAutomation = true;
                    DriveSetupAutomation();
                    break;
                case "stop_setup":
                    setupAutomation = false;
                    break;
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
                case "no_current_room":
                    NoCurrentRoomFixture();
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
                case "reset_wall":
                    ResetPanelToNearestWall();
                    break;
                case "wall_fixture":
                    ScannedWallFixture();
                    break;
                case "toggle_walls":
                    var episodeHub = FindFirstObjectByType<EpisodeHub>();
                    var wallButton = episodeHub == null
                        ? null
                        : episodeHub.transform.Find("HubCanvas/Panel/Walls")?
                            .GetComponent<UnityEngine.UI.Button>();
                    if (wallButton == null)
                    {
                        Record("virtual_walls", "button_unavailable", true);
                        break;
                    }
                    wallButton.onClick.Invoke();
                    Record("virtual_walls", VirtualWallsEnabled ? "on" : "off", true);
                    break;
                case "remount_fixture":
                    RemountFixture();
                    break;
                case "panel_state":
                    Record("panel_diagnostic", PanelDiagnostic(), true);
                    break;
                case "list_state":
                    var listReport = ListShowingReport();
                    File.WriteAllText(Path.Combine(Application.persistentDataPath, "list-state.txt"), listReport);
                    Record("list_state", listReport.Split('\n')[0], true);
                    break;
                case "panel_probe":
                    var instrument = FindFirstObjectByType<InstrumentRoot>();
                    var background = instrument.GetComponent<Canvas>().transform.Find("Background")
                        .GetComponent<UnityEngine.UI.Image>();
                    background.material = null;
                    background.color = new Color(0.7f, 0f, 0.7f, 1f);
                    Record("render_diagnostic", "opaque_panel", true);
                    break;
                case "head_panel":
                    var headPanel = FindFirstObjectByType<InstrumentRoot>();
                    headPanel.transform.SetParent(GetEyeTransform(), false);
                    headPanel.transform.localPosition = new Vector3(0f, -0.25f, 1.5f);
                    headPanel.transform.localRotation = Quaternion.identity;
                    Record("render_diagnostic", "head_panel", true);
                    break;
                case "overlay_probe":
                    var probe = new GameObject("DiagnosticOverlay");
                    probe.transform.SetParent(GetEyeTransform(), false);
                    probe.transform.localPosition = new Vector3(0f, 0f, 1f);
                    probe.transform.localScale = Vector3.one;
                    var directOverlay = probe.AddComponent<OVROverlay>();
                    directOverlay.currentOverlayType = OVROverlay.OverlayType.Overlay;
                    directOverlay.currentOverlayShape = OVROverlay.OverlayShape.Quad;
                    directOverlay.compositionDepth = -100;
                    var color = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    color.SetPixels(new[] { Color.magenta, Color.magenta, Color.magenta, Color.magenta });
                    color.Apply();
                    directOverlay.textures[0] = color;
                    Record("render_diagnostic", "direct_overlay", true);
                    break;
                case "panel_texture":
                    var texturePanel = FindFirstObjectByType<InstrumentRoot>();
                    var overlay = texturePanel.GetComponent<OVROverlayCanvas>();
                    if (overlay.Overlay == null || overlay.Overlay.textures[0] == null)
                    {
                        Record("render_diagnostic", "texture_unavailable", true);
                        break;
                    }
                    var source = (RenderTexture)overlay.Overlay.textures[0];
                    var previous = RenderTexture.active;
                    RenderTexture.active = source;
                    var pixels = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                    pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                    pixels.Apply();
                    File.WriteAllBytes(Path.Combine(Application.persistentDataPath, "panel-texture.png"),
                        pixels.EncodeToPNG());
                    RenderTexture.active = previous;
                    Destroy(pixels);
                    Record("render_diagnostic", "texture_written", true);
                    break;
                case "passthrough_off":
                    foreach (var layer in FindObjectsByType<OVRPassthroughLayer>(FindObjectsSortMode.None))
                        layer.hidden = true;
                    Record("render_diagnostic", "passthrough_hidden", true);
                    break;
                case "passthrough_on":
                    foreach (var layer in FindObjectsByType<OVRPassthroughLayer>(FindObjectsSortMode.None))
                        layer.hidden = false;
                    Record("render_diagnostic", "passthrough_visible", true);
                    break;
                case "render_probe":
                    if (OVRManager.instance != null)
                        OVRManager.instance.isInsightPassthroughEnabled = false;
                    foreach (var layer in FindObjectsByType<OVRPassthroughLayer>(FindObjectsSortMode.None))
                        layer.hidden = true;
                    var probeCamera = GetEyeTransform().GetComponent<Camera>();
                    probeCamera.clearFlags = CameraClearFlags.SolidColor;
                    probeCamera.backgroundColor = new Color(0.7f, 0f, 0.7f, 1f);
                    Record("render_diagnostic", "opaque_camera_passthrough_disabled", true);
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
                    Record("sim_state", SimulationStateSummary(), true);
                    Record("readout", ReadoutSummary(), true);
                    Record("evaluation", EvaluationStateSummary(), true);
                    Record("evaluation_reason", EvaluationReasonDetail(), true);
                    break;
                case "exit":
                    setupAutomation = false;
                    ExitToLibrary();
                    break;
            }
        }

        string PanelDiagnostic()
        {
            var instrument = FindFirstObjectByType<InstrumentRoot>();
            var eyeTransform = GetEyeTransform();
            var eye = eyeTransform != null ? eyeTransform.GetComponent<Camera>() : null;
            if (instrument == null || eye == null)
                return "unavailable";
            var viewport = eye.WorldToViewportPoint(instrument.transform.position);
            var canvas = instrument.GetComponent<Canvas>();
            var graphics = instrument.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Length;
            var overlay = instrument.GetComponent<OVROverlayCanvas>();
            var overlayDetails = overlay == null ? "missing" :
                $"enabled={overlay.overlayEnabled};priority={overlay.IsCanvasPriority};" +
                $"hidden={(overlay.Overlay != null ? overlay.Overlay.hidden.ToString() : "unready")};" +
                $"distance={overlay.GetViewDistance()}";
            var placement = Recovery.Panel;
            var placementDetails = placement == null
                ? "unplaced"
                : $"mode={placement.MountMode};source={placement.Source};status={placement.Status};" +
                  $"revision={placement.Revision};reason={placement.Reason};" +
                  $"wall={placement.WallProvenance};y={placement.Position.y:F2}";
            return $"viewport={viewport.x:F2},{viewport.y:F2},{viewport.z:F2};" +
                   $"active={instrument.gameObject.activeInHierarchy};canvas={canvas != null && canvas.enabled};" +
                   $"graphics={graphics};scale={instrument.transform.lossyScale.x:F4};overlay={overlayDetails};" +
                   $"placement={placementDetails}";
        }

        string ListShowingReport()
        {
            var instrument = FindFirstObjectByType<InstrumentRoot>();
            var session = services != null ? services.Session : null;
            var declared = session != null ? session.Cards.Count : 0;
            var sessionState = session != null ? session.State.ToString() : "missing";
            if (instrument == null)
                return "verdict=not_showing\nreason=panel_missing\nsession=" + sessionState +
                       "\ndeclared=" + declared + "\nvisibleCards=0\nwearer=unconfirmed\n";

            var cards = new List<string>();
            foreach (var child in instrument.GetComponentsInChildren<Transform>(true))
            {
                var name = child.name;
                if (name.Length < 5 || !name.StartsWith("Card") || name.StartsWith("CardText"))
                    continue;
                if (!int.TryParse(name.Substring(4), out _))
                    continue;
                if (!child.gameObject.activeInHierarchy)
                    continue;
                var label = child.GetComponentInChildren<TMP_Text>(true);
                var text = label != null ? label.text ?? "" : "";
                cards.Add(name + "=" + text.Replace("\r", " ").Replace("\n", " | ").Trim());
            }

            var canvas = instrument.GetComponent<Canvas>();
            var canvasOn = canvas != null && canvas.isActiveAndEnabled;
            var overlay = instrument.GetComponent<OVROverlayCanvas>();
            var layer = overlay != null ? overlay.Overlay : null;
            var submitted = overlay != null && overlay.overlayEnabled && layer != null &&
                            layer.isOverlayVisible && !layer.hidden;
            var eyeTransform = GetEyeTransform();
            var eye = eyeTransform != null ? eyeTransform.GetComponent<Camera>() : null;
            var inView = false;
            var viewportText = "none";
            if (eye != null)
            {
                var viewport = eye.WorldToViewportPoint(instrument.transform.position);
                inView = viewport.z > 0.05f &&
                         viewport.x >= 0f && viewport.x <= 1f &&
                         viewport.y >= 0f && viewport.y <= 1f;
                viewportText = viewport.x.ToString("F2") + "," + viewport.y.ToString("F2") + "," +
                               viewport.z.ToString("F2");
            }

            string verdict;
            string reason;
            if (cards.Count == 0 || !canvasOn)
            {
                verdict = "not_showing";
                reason = cards.Count == 0 ? "no_active_card" : "canvas_off";
            }
            else if (eye == null)
            {
                verdict = "not_showing";
                reason = "no_eye";
            }
            else if (!inView)
            {
                verdict = "off_screen";
                reason = "panel_outside_view";
            }
            else if (!submitted)
            {
                verdict = "not_submitted";
                reason = layer == null ? "overlay_unready" :
                    layer.hidden ? "overlay_hidden" :
                    !layer.isOverlayVisible ? "overlay_not_visible" : "overlay_disabled";
            }
            else
            {
                verdict = "submitted";
                reason = "compositor_accepted";
            }

            return "verdict=" + verdict +
                   "\nreason=" + reason +
                   "\nsession=" + sessionState +
                   "\ndeclared=" + declared +
                   "\nvisibleCards=" + cards.Count +
                   (cards.Count > 0 ? "\n" + string.Join("\n", cards) : "") +
                   "\ncanvas=" + canvasOn +
                   "\noverlayEnabled=" + (overlay != null && overlay.overlayEnabled) +
                   "\noverlayHidden=" + (layer != null && layer.hidden) +
                   "\noverlayVisible=" + (layer != null && layer.isOverlayVisible) +
                   "\ninView=" + inView +
                   "\nviewport=" + viewportText +
                   "\nwearer=unconfirmed\n";
        }

        void RemountFixture()
        {
            var panel = Recovery.Panel;
            var confirmation = Recovery.CurrentConfirmation;
            if (panel == null || confirmation == null)
                return;
            foreach (var wall in confirmation.Walls)
            {
                if (Mathf.Abs(Vector3.Dot(panel.Position - wall.Center, wall.Normal)) < 0.5f)
                    continue;
                var release = wall.Center + wall.Normal * 0.25f;
                release.y = panel.Position.y;
                TryRemountPanel(release, panel.Rotation);
                return;
            }
        }

        void ScannedWallFixture()
        {
            var id = Preparation.PreparationId;
            if (!Preparation.RequestPermission(id))
                return;
            Preparation.CompletePermission(id, true);
            GetUserPose(out var position, out var forward);
            var region = new Bounds(new Vector3(position.x, position.y + 1.25f, position.z),
                new Vector3(4f, 2.5f, 4f));
            var manual = RoomDraft.CreateManual(region, position, forward);
            var surfaces = manual.Surfaces.Select(surface => new CollisionSurface(
                surface.SurfaceId, surface.SemanticLabel, surface.Primary,
                BoundaryProvenance.Scanned, surface.Role, surface.Center, surface.Size,
                surface.Rotation, surface.Normal, surface.Approximation)).ToArray();
            Preparation.CompleteSceneLoad(id,
                new RoomDraft(region, surfaces, position, forward, 0.9f));
            Record("scene_load", "scanned_wall_fixture", true);
            SyncSession();
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
            Preparation.CompleteSceneLoad(id, null, RoomFailureReason.NoScene);
            Preparation.RequestSpaceSetup(id);
            Preparation.CompleteSpaceSetup(id, true);
            Preparation.CompleteSceneLoad(id, null, RoomFailureReason.NoScene);
            Record("scene_load", "no_scene_after_setup", true);
            SyncSession();
        }

        void NoCurrentRoomFixture()
        {
            var id = Preparation.PreparationId;
            if (!Preparation.RequestPermission(id))
                return;
            Preparation.CompletePermission(id, true);
            Preparation.CompleteSceneLoad(id, null, RoomFailureReason.NoCurrentRoom);
            Record("scene_load", "no_current_room", true);
            SyncSession();
        }
    }
}
#endif
