using System;
using Amiss.Shell.Room;
using TMPro;
using UnityEngine;

namespace Amiss.Shell
{
    public sealed class InstrumentRoot : MonoBehaviour
    {
        ShellServices services;
        GameObject chromeRoot;
        GameObject tvBody;
        GameObject bezel;
        GameObject bracket;
        GameObject speaker;
        TMP_Text placementText;
        int appliedRevision = -1;
        PanelPlacementStatus appliedStatus = PanelPlacementStatus.Unplaced;
        PanelMountMode appliedMode = PanelMountMode.ForwardFallback;
        string appliedReason = "";
        Vector3 appliedPosition;
        Quaternion appliedRotation;
        bool chromeVisible;
        bool grabbed;
        bool libraryPlaced;
        int libraryWaitFrames;
        bool overlayVisibilityKnown;
        bool overlayVisibleState;
        bool overlayHiddenKnown;
        bool overlayHiddenState;
        bool canvasKnown;
        bool canvasVisibleState;
        bool dashboardVisible = true;
        WallKnifeSwitch wallSwitch;
        WallBat wallBat;

        public const int LibraryPlaceFrameBudget = 120;
        public const float TrackedEyeHeightMeters = 0.5f;

        public bool IsGrabbed => grabbed;
        public string AppliedReason => appliedReason;

        void Start()
        {
            services = FindFirstObjectByType<ShellServices>();
            BuildTv();
            ApplyPanel(services != null && services.Room != null ? services.Room.Recovery.Panel : null);
            Debug.Log("[Amiss] panel built t=" + Time.realtimeSinceStartup.ToString("F3") +
                      " graphics=" + GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Length);
        }

        public static void PlaceInitialPanel(Transform panel, Vector3 eyePosition, Vector3 eyeForward)
        {
            eyeForward.y = 0f;
            var forward = eyeForward.sqrMagnitude > 0.0001f ? eyeForward.normalized : Vector3.forward;
            var position = eyePosition + forward * 1.5f;
            position.y = eyePosition.y - 0.25f;
            panel.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));
        }

        public void BeginGrab()
        {
            grabbed = true;
            ShowChrome(true);
            RefreshPlacementLabel();
        }

        public void UpdateGrab(Pose pose)
        {
            if (!grabbed)
                return;
            transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        public void EndGrab()
        {
            grabbed = false;
            RefreshPlacementLabel();
        }

        void LateUpdate()
        {
            NoteOverlayVisibility();
            if (!grabbed)
            {
                var panel = services != null && services.Room != null ? services.Room.Recovery.Panel : null;
                ApplyPanel(panel);
                if (panel != null)
                    libraryPlaced = true;
                SetDashboardVisible(DashboardAllowed());
            }
            if (wallSwitch != null)
                wallSwitch.Follow(transform, appliedMode == PanelMountMode.WallMounted);
            if (wallBat != null)
                wallBat.Follow(transform);
        }

        void NoteOverlayVisibility()
        {
            var canvas = GetComponent<Canvas>();
            NoteVisibility("canvasVisible", canvas != null && canvas.isActiveAndEnabled,
                ref canvasKnown, ref canvasVisibleState);
            var overlay = GetComponentInChildren<OVROverlay>(true);
            if (overlay == null)
                return;
            NoteVisibility("overlayVisible", overlay.isOverlayVisible,
                ref overlayVisibilityKnown, ref overlayVisibleState);
            NoteVisibility("overlayHidden", overlay.hidden,
                ref overlayHiddenKnown, ref overlayHiddenState);
        }

        static void NoteVisibility(string name, bool value, ref bool known, ref bool state)
        {
            if (known && state == value)
                return;
            known = true;
            state = value;
            Debug.Log("[Amiss] panel " + name + "=" + value +
                      " t=" + Time.realtimeSinceStartup.ToString("F3"));
        }

        bool DashboardAllowed()
        {
            if (services == null || services.Session == null)
                return false;
            return EpisodeLaunchGate.WallSetVisible(services.Session.State);
        }

        void SetDashboardVisible(bool visible)
        {
            if (dashboardVisible == visible)
                return;
            dashboardVisible = visible;
            var canvas = GetComponent<Canvas>();
            if (canvas != null)
                canvas.enabled = visible;
            var overlay = GetComponent<OVROverlayCanvas>();
            if (overlay != null)
                overlay.enabled = visible;
            ShowChrome(visible);
            RefreshPlacementLabel();
            Debug.Log("[Amiss] dashboard visible=" + visible +
                      " t=" + Time.realtimeSinceStartup.ToString("F3"));
        }

        /// <summary>
        /// Puts the unconfirmed library in front of the wearer once. A confirmed
        /// wall mount, a grab, and a later call do not move it. An eye still at
        /// the floor origin is ignored until <see cref="LibraryPlaceFrameBudget"/>
        /// frames have passed.
        /// </summary>
        public bool TryPlaceUnconfirmedLibrary(Transform eye, bool roomConfirmed)
        {
            if (libraryPlaced || grabbed || roomConfirmed)
                return false;
            libraryWaitFrames++;
            if (eye == null)
                return false;
            if (!IsTrackedEye(eye.position) && libraryWaitFrames < LibraryPlaceFrameBudget)
                return false;
            PlaceInitialPanel(transform, eye.position, eye.forward);
            libraryPlaced = true;
            Debug.Log("[Amiss] panel placed t=" + Time.realtimeSinceStartup.ToString("F3") +
                      " pos=" + transform.position.ToString("F2"));
            return true;
        }

        public static bool IsTrackedEye(Vector3 eyePosition) =>
            eyePosition.y > TrackedEyeHeightMeters;

        static Transform FindEye()
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null && rig.centerEyeAnchor != null)
                return rig.centerEyeAnchor;
            var camera = Camera.main;
            return camera != null ? camera.transform : null;
        }

        void ApplyPanel(PanelPlacement panel)
        {
            if (panel == null)
            {
                ShowChrome(false);
                appliedRevision = -1;
                appliedStatus = PanelPlacementStatus.Unplaced;
                return;
            }
            if (panel.Revision == appliedRevision && panel.Status == appliedStatus &&
                panel.MountMode == appliedMode && panel.Reason == appliedReason &&
                panel.Position == appliedPosition && panel.Rotation == appliedRotation)
                return;
            appliedRevision = panel.Revision;
            appliedStatus = panel.Status;
            appliedMode = panel.MountMode;
            appliedReason = panel.Reason ?? "";
            appliedPosition = panel.Position;
            appliedRotation = panel.Rotation;
            ShowChrome(panel.MountMode == PanelMountMode.WallMounted || panel.Status == PanelPlacementStatus.Placed);
            transform.SetPositionAndRotation(panel.Position, panel.Rotation);
            if (placementText != null)
            {
                var reason = string.IsNullOrEmpty(panel.Reason) ? "" : " · " + panel.Reason;
                var provenance = string.IsNullOrEmpty(panel.WallProvenance) ? "" : " · " + panel.WallProvenance;
                placementText.text = panel.MountMode == PanelMountMode.WallMounted
                    ? "TV mounted" + provenance + reason
                    : "TV has no usable wall";
            }
        }

        void ShowChrome(bool visible)
        {
            if (chromeVisible == visible)
                return;
            chromeVisible = visible;
            if (chromeRoot != null) chromeRoot.SetActive(visible);
            if (wallSwitch != null) wallSwitch.SetVisible(visible);
            if (wallBat != null) wallBat.SetVisible(visible);
            RefreshPlacementLabel();
            Debug.Log("[Amiss] panel chromeVisible=" + visible +
                      " t=" + Time.realtimeSinceStartup.ToString("F3"));
        }

        /// <summary>
        /// The placement line is a grab-time note, not glass content: while the
        /// wall set carries the trial glass the line stays off, and it returns
        /// only while the wearer holds the television to move it.
        /// </summary>
        void RefreshPlacementLabel()
        {
            if (placementText == null)
                return;
            var visible = chromeVisible && (!dashboardVisible || grabbed);
            if (placementText.gameObject.activeSelf != visible)
                placementText.gameObject.SetActive(visible);
        }

        void BuildTv()
        {
            if (bezel != null || chromeRoot != null)
                return;
            var screen = GetComponent<Canvas>();
            if (screen != null && screen.GetComponent<UnityEngine.UI.Image>() == null)
            {
                var background = gameObject.AddComponent<UnityEngine.UI.Image>();
                background.color = new Color(0.03f, 0.035f, 0.045f, 1f);
            }

            chromeRoot = new GameObject("TvChrome");
            chromeRoot.transform.SetParent(transform, false);
            chromeRoot.transform.localScale = Vector3.one * 1000f;

            if (!WallDashboardModel.TryMount(chromeRoot.transform, out var mountedSpeaker))
                BuildPrimitiveChrome();
            else
            {
                speaker = mountedSpeaker.gameObject;
                bezel = speaker;
            }

            placementText = BuildLabel("PlacementStatus", new Vector2(0f, -470f), 26f);
            BuildWallSwitch();
            BuildWallBat();
            chromeVisible = true;
            ShowChrome(false);
        }

        void BuildWallSwitch()
        {
            if (wallSwitch != null)
                return;
            var host = new GameObject("WallKnifeSwitch");
            if (transform.parent != null)
                host.transform.SetParent(transform.parent, false);
            wallSwitch = host.AddComponent<WallKnifeSwitch>();
        }

        void BuildWallBat()
        {
            if (wallBat != null)
                return;
            var host = new GameObject("WallBat");
            if (transform.parent != null)
                host.transform.SetParent(transform.parent, false);
            wallBat = host.AddComponent<WallBat>();
        }

        void BuildPrimitiveChrome()
        {
            bezel = BuildBox("Bezel", new Vector3(PanelMount.ScreenWidth + 2f * PanelMount.Bezel,
                    PanelMount.ScreenHeight + 2f * PanelMount.Bezel, PanelMount.BodyDepth),
                new Vector3(0f, 0f, PanelMount.BodyDepth * 0.5f), new Color(0.055f, 0.06f, 0.07f, 1f));
            tvBody = BuildBox("Body", new Vector3(PanelMount.ScreenWidth - 0.02f,
                    PanelMount.ScreenHeight - 0.02f, PanelMount.BodyDepth),
                new Vector3(0f, 0f, PanelMount.BodyDepth * 1.5f), new Color(0.02f, 0.022f, 0.025f, 1f));
            bracket = BuildBox("Bracket", new Vector3(0.30f, 0.22f, 0.06f),
                new Vector3(0f, -0.10f, PanelMount.BodyDepth + 0.03f),
                new Color(0.10f, 0.11f, 0.12f, 1f));
            speaker = BuildSpeaker();
        }

        GameObject BuildBox(string name, Vector3 size, Vector3 localPosition, Color color)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(chromeRoot.transform, false);
            box.transform.localPosition = localPosition;
            box.transform.localScale = size;
            var collider = box.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);
            var renderer = box.GetComponent<Renderer>();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.color = color;
            material.SetFloat("_Smoothness", 0.15f);
            renderer.sharedMaterial = material;
            return box;
        }

        TMP_Text BuildLabel(string name, Vector2 anchoredPosition, float fontSize)
        {
            var labelGo = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            labelGo.transform.SetParent(transform, false);
            var rect = labelGo.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(1400f, 70f);
            rect.anchoredPosition = anchoredPosition;
            var label = labelGo.GetComponent<TextMeshProUGUI>();
            label.fontSize = fontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.color = new Color(0.72f, 0.76f, 0.82f, 1f);
            label.raycastTarget = false;
            return label;
        }

        GameObject BuildSpeaker()
        {
            var box = BuildBox("WoodenSpeaker",
                new Vector3(PanelMount.SpeakerWidth, PanelMount.SpeakerHeight, PanelMount.SpeakerDepth),
                new Vector3(0f, PanelMount.ScreenHeight * 0.5f + PanelMount.Bezel + PanelMount.SpeakerGap +
                               PanelMount.SpeakerHeight * 0.5f, 0f),
                new Color(0.28f, 0.14f, 0.055f, 1f));
            var audio = box.AddComponent<AudioSource>();
            audio.spatialBlend = 1f;
            audio.playOnAwake = false;
            audio.dopplerLevel = 0f;
            return box;
        }
    }
}
