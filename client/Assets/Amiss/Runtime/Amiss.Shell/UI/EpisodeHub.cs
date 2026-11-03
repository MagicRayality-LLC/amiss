using System.Collections.Generic;
using System.IO;
using Amiss.Shell.UI;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Amiss.Shell
{
    /// <summary>
    /// The episode hub. It is a separate world-fixed surface from the wall TV.
    /// It chooses the experiment and holds audio, tips, the microphone,
    /// the safety switch, and exit.
    /// The row matches the sci-fi episode select reference: the first episode
    /// can launch once a room scan has loaded, and every other episode
    /// displays locked. The panel is one cylinder, with the center farther
    /// from the wearer.
    /// </summary>
    public sealed class EpisodeHub : MonoBehaviour
    {
        public static bool IsOpen { get; private set; } = true;

        /// <summary>
        /// Radius of the panel cylinder, in meters. The 1.78 m panel then
        /// spans about 58 degrees, which is the gentle wrap in the reference.
        /// </summary>
        public const float CurveRadiusMeters = 1.75f;

        /// <summary>
        /// Extra frames after <see cref="OVRDisplay.RecenteredPose"/> during
        /// which the hub is snapped again. The system recenter count changes
        /// in the same frame as the event, while the pose the wearer sees is
        /// the render-step eye pose of the following frame.
        /// </summary>
        public const int RecenterSettleFrames = 1;

        /// <summary>
        /// Layer of the source canvas. Matches the <c>OverlaySource</c> layer.
        /// Eye cameras omit it, so the flat canvas is not drawn behind the
        /// curved overlay. The overlay camera still captures this layer.
        /// </summary>
        public const int OverlaySourceLayer = 6;

        const float PanelWidth = 1780f;
        const float PanelHeight = 960f;
        const float CardWidth = 200f;
        const float CardHeight = 530f;
        const float CardGap = 24f;

        ShellServices services;
        Canvas canvas;
        TMP_Text audioText;
        TMP_Text tipsText;
        TMP_Text micText;
        TMP_Text safetyText;
        TMP_Text launchLabel;
        TMP_Text statusText;
        TMP_Text detailTitle;
        TMP_Text detailBody;
        TMP_Text durationValue;
        TMP_Text availabilityValue;
        TMP_InputField modelKeyField;
        TMP_InputField witTokenField;
        TMP_Text modelKeyText;
        string tutorStatus = "";
#if DEVELOPMENT_BUILD
        float nextKeyImportPoll;
#endif
        Image launchOutline;
        Button launchButton;
        bool placed;
        bool closedForDashboard;
        bool palmWasPinch;
        float gestureLockUntil;
        OVRCameraRig anchorRig;
        bool recenterHooked;
        int recenterEventFrame = -1;
        OVRHand[] hands = System.Array.Empty<OVRHand>();
        readonly List<GameObject> interactionRoots = new();
        EpisodeSelectBoard.Slot[] slots = System.Array.Empty<EpisodeSelectBoard.Slot>();
        readonly List<CardVisual> cards = new();
        int selectedSlot;

        void Start()
        {
            services = FindFirstObjectByType<ShellServices>();
            Build();
            SetOpen(true);
        }

        void Update()
        {
            if (services == null)
                services = FindFirstObjectByType<ShellServices>();
            PollKeyImport();
            RefreshLabels();
            EnsureRecenterHook();
            if (!placed)
                TryPlace();
            if (!closedForDashboard && DashboardReady())
            {
                closedForDashboard = true;
                SetOpen(false);
            }
            if (RecallPressed())
                SetOpen(!IsOpen);
        }

        void OnDestroy()
        {
            if (!recenterHooked)
                return;
            if (anchorRig != null)
                anchorRig.UpdatedAnchors -= OnAnchorsUpdated;
            if (OVRManager.display != null)
                OVRManager.display.RecenteredPose -= OnSystemRecenter;
            OVRManager.TrackingOriginChangePending -= OnTrackingOriginChange;
        }

        void LateUpdate() => ExcludeFlatSourceFromEyes();

        /// <summary>
        /// True while <paramref name="frame"/> is the recenter event frame or
        /// one of the following settle frames.
        /// </summary>
        public static bool RecenterWindowOpen(int eventFrame, int frame) =>
            eventFrame >= 0 && frame >= eventFrame && frame <= eventFrame + RecenterSettleFrames;

        /// <summary>
        /// Places the startup hub on the center-eye forward ray. The hand that
        /// called recenter is not an input, so a right-hand recenter has no
        /// lateral offset.
        /// </summary>
        public static bool TrySnapToCenterEye(Transform hub, Vector3 eyePosition, Vector3 eyeForward)
        {
            if (hub == null || !InstrumentRoot.IsTrackedEye(eyePosition))
                return false;
            InstrumentRoot.PlaceInitialPanel(hub, eyePosition, eyeForward);
            return true;
        }

        /// <summary>
        /// Signed meters of the panel off the center-eye forward ray, in the
        /// eye's right direction. Zero is exact.
        /// </summary>
        public static float LateralMeters(Vector3 eyePosition, Vector3 eyeForward, Vector3 panelPosition)
        {
            eyeForward.y = 0f;
            if (eyeForward.sqrMagnitude <= 0.0001f)
                return 0f;
            var right = Vector3.Cross(Vector3.up, eyeForward.normalized);
            return Vector3.Dot(panelPosition - eyePosition, right);
        }

        void EnsureRecenterHook()
        {
            if (recenterHooked)
                return;
            if (!OVRManager.OVRManagerinitialized || OVRManager.display == null)
                return;
            anchorRig = FindFirstObjectByType<OVRCameraRig>();
            if (anchorRig == null)
                return;
            OVRManager.display.RecenteredPose += OnSystemRecenter;
            OVRManager.TrackingOriginChangePending += OnTrackingOriginChange;
            anchorRig.UpdatedAnchors += OnAnchorsUpdated;
            recenterHooked = true;
        }

        void OnSystemRecenter() => recenterEventFrame = Time.frameCount;

        void OnTrackingOriginChange(OVRManager.TrackingOrigin origin, OVRPose? previousPose) =>
            OnSystemRecenter();

        void OnAnchorsUpdated(OVRCameraRig rig)
        {
            if (!RecenterWindowOpen(recenterEventFrame, Time.frameCount))
                return;
            var eye = rig != null ? rig.centerEyeAnchor : null;
            if (eye == null || !TrySnapToCenterEye(transform, eye.position, eye.forward))
                return;
            placed = true;
            Debug.Log("[Amiss] hub recentered frame=" + Time.frameCount +
                      " pos=" + transform.position.ToString("F3") +
                      " lateral=" + LateralMeters(eye.position, eye.forward, transform.position).ToString("F3"));
        }

        public void SetOpen(bool open)
        {
            if (IsOpen == open && canvas != null && canvas.enabled == open)
                return;
            IsOpen = open;
            if (canvas != null)
                canvas.enabled = open;
            foreach (var root in interactionRoots)
            {
                if (root != null)
                    root.SetActive(open);
            }
            Debug.Log("[Amiss] hub open=" + open +
                      " t=" + Time.realtimeSinceStartup.ToString("F3"));
        }

        bool DashboardReady()
        {
            if (services == null || services.Session == null)
                return false;
            return EpisodeLaunchGate.HubClosesAfterEpisodeStart(services.Session.State);
        }

        bool RecallPressed()
        {
            if (OVRInput.GetDown(OVRInput.RawButton.Start))
                return true;
            if (Time.unscaledTime < gestureLockUntil)
                return false;
            if (hands.Length == 0)
                hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
            OVRHand palm = null;
            OVRHand other = null;
            foreach (var hand in hands)
            {
                if (hand == null || !hand.IsTracked || hand.IsSystemGestureInProgress)
                    continue;
                if (!hand.IsDominantHand)
                    palm = hand;
                else
                    other = hand;
            }
            var palmUp = palm != null && palm.IsPointerPoseValid &&
                         Vector3.Dot(palm.PointerPose.up, Vector3.up) > 0.75f;
            var pinch = other != null && other.GetFingerIsPinching(OVRHand.HandFinger.Index);
            var pressed = palmUp && pinch && !palmWasPinch;
            palmWasPinch = palmUp && pinch;
            if (!pressed)
                return false;
            gestureLockUntil = Time.unscaledTime + 0.6f;
            return true;
        }

        void TryPlace()
        {
            var eye = FindEye();
            if (eye == null)
                return;
            if (!InstrumentRoot.IsTrackedEye(eye.position))
                return;
            InstrumentRoot.PlaceInitialPanel(transform, eye.position, eye.forward);
            placed = true;
            Debug.Log("[Amiss] hub placed t=" + Time.realtimeSinceStartup.ToString("F3") +
                      " pos=" + transform.position.ToString("F2"));
        }

        static Transform FindEye()
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null && rig.centerEyeAnchor != null)
                return rig.centerEyeAnchor;
            var camera = Camera.main;
            return camera != null ? camera.transform : null;
        }

        void RefreshLabels()
        {
            if (launchLabel == null)
                return;
            var choice = CurrentChoice();
            launchButton.interactable = choice.ButtonEnabled;
            launchLabel.text = choice.Label;
            launchLabel.characterSpacing = choice.Label != null && choice.Label.Length > 14 ? 1f : 6f;
            var live = choice.ButtonEnabled;
            launchOutline.color = live ? EpisodeSelectBoard.Accent : EpisodeSelectBoard.Hairline;
            launchLabel.color = live ? EpisodeSelectBoard.Accent : EpisodeSelectBoard.Muted;
            if (statusText != null)
            {
                statusText.text = choice.Status;
                statusText.fontSize = choice.EmphasizeStatus ? 24f : 20f;
                statusText.color = choice.EmphasizeStatus
                    ? new Color(0.98f, 0.78f, 0.42f, 1f)
                    : EpisodeSelectBoard.Muted;
            }
            ApplyScanGate(choice.ScanReady);
            if (safetyText != null)
            {
                var safetyOn = services == null || services.Room == null || services.Room.SafetyCheckEnabled;
                safetyText.text = safetyOn ? "SAFETY ON" : "SAFETY OFF";
                safetyText.color = safetyOn
                    ? EpisodeSelectBoard.Accent
                    : new Color(0.98f, 0.78f, 0.42f, 1f);
            }
            var panel = services != null ? services.Panel : null;
            if (panel == null)
                return;
            audioText.text = panel.AudioMuted ? "AUDIO MUTED" : "AUDIO ON";
            tipsText.text = panel.TipsOff ? "TIPS OFF" : "TIPS ON";
            var permissionRequest = services.VoiceControl != null &&
                                    services.VoiceControl.MicrophonePermissionRequested;
            micText.text = MicLabel(panel, permissionRequest);
            audioText.color = panel.AudioMuted ? EpisodeSelectBoard.Muted : EpisodeSelectBoard.Accent;
            tipsText.color = panel.TipsOff ? EpisodeSelectBoard.Muted : EpisodeSelectBoard.Accent;
            micText.color = panel.MicrophoneAvailable || permissionRequest ||
                            panel.VoiceState != Voice.VoiceState.Closed
                ? EpisodeSelectBoard.Accent
                : EpisodeSelectBoard.Muted;
        }

        /// <summary>
        /// The hub microphone row reads the one voice projection and the
        /// independent consent and permission gates (06 sections 3 and 4.1).
        /// It states what is true, not what is hoped for: an outstanding system
        /// prompt is named as pending, never as granted.
        /// </summary>
        public static string MicLabel(UI.CorePanelController panel,
            bool permissionRequested = false)
        {
            if (panel == null)
                return "MIC UNAVAILABLE";
            switch (panel.VoiceState)
            {
                case Voice.VoiceState.Listening:
                    return "MIC LISTENING";
                case Voice.VoiceState.Transcribing:
                    return "MIC TRANSCRIBING";
                case Voice.VoiceState.WaitingModel:
                    return "MIC WAITING";
                case Voice.VoiceState.PlayingInterruptible:
                    return "MIC READY";
            }

            if (!panel.MicrophoneConsentAccepted || !panel.VoiceEnabled)
                return "MIC OFF";
            if (!panel.MicrophonePermissionGranted)
                return permissionRequested ? "MIC PERM PENDING" : "MIC NO PERM";
            if (!panel.DictationConfigured)
                return "MIC NO TOKEN";
            return "MIC READY";
        }

        bool SelectedIsOpen() =>
            slots != null && selectedSlot >= 0 && selectedSlot < slots.Length && !slots[selectedSlot].Locked;

        EpisodeLaunchGate CurrentChoice()
        {
            var session = services != null && services.Session != null
                ? services.Session.State
                : ShellSessionState.Library;
            var preparation = services != null && services.Room != null
                ? services.Room.Preparation
                : null;
            var room = preparation != null
                ? preparation.Status
                : Room.RoomPreparationStatus.Idle;
            var failure = preparation != null
                ? preparation.Failure
                : Room.RoomFailureReason.None;
            var boundary = preparation != null && preparation.Draft != null
                ? preparation.Draft.BoundaryLabel
                : "";
            var startBlock = services != null && services.Room != null
                ? services.Room.StartBlock
                : "";
            var safetyCheck = services == null || services.Room == null || services.Room.SafetyCheckEnabled;
            return EpisodeLaunchGate.Evaluate(SelectedIsOpen(), session, room, failure, boundary, startBlock,
                safetyCheck);
        }

        void ApplyScanGate(bool scanReady)
        {
            if (slots == null)
                return;
            for (var index = 0; index < cards.Count && index < slots.Length; index++)
                cards[index].Apply(index == selectedSlot, slots[index].Locked || !scanReady);
        }

        void SelectSlot(int index)
        {
            if (slots == null || index < 0 || index >= slots.Length)
                return;
            selectedSlot = index;
            for (var card = 0; card < cards.Count; card++)
                cards[card].Apply(card == index, slots[card].Locked);
            var slot = slots[index];
            detailTitle.text = slot.DetailTitle;
            detailBody.text = slot.Description;
            durationValue.text = slot.Duration;
            availabilityValue.text = slot.Status;
            var accent = slot.Locked ? EpisodeSelectBoard.Muted : EpisodeSelectBoard.Text;
            detailTitle.color = accent;
            availabilityValue.color = slot.Locked ? EpisodeSelectBoard.Muted : EpisodeSelectBoard.Accent;
            RefreshLabels();
        }

        void Build()
        {
            var canvasGo = new GameObject("HubCanvas", typeof(RectTransform), typeof(Canvas),
                typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = canvas.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            rect.localScale = Vector3.one * 0.001f;
            var overlay = canvasGo.AddComponent<OVROverlayCanvas>();
            overlay.compositionMode = OVROverlayCanvas.CompositionMode.PunchAHole;
            overlay.overlayEnabled = true;
            CurveOverlay(overlay);
            var ray = CopyInteraction("ISDK_RayCanvasInteraction", canvas);
            var poke = CopyInteraction("ISDK_PokeCanvasInteraction", canvas);
            BendInteractions(canvas);
            Debug.Log("[Amiss] hub interaction ray=" + ray + " poke=" + poke +
                      " curve=" + CurveRadiusMeters.ToString("F2") +
                      " t=" + Time.realtimeSinceStartup.ToString("F3"));

            var scrim = MakeImage(canvasGo.transform, "Scrim", EpisodeSelectBoard.Scrim, true);
            scrim.rectTransform.sizeDelta = new Vector2(PanelWidth + 36f, PanelHeight + 28f);
            var panel = MakeImage(canvasGo.transform, "Panel", EpisodeSelectBoard.Panel, true);
            panel.rectTransform.sizeDelta = new Vector2(PanelWidth - 24f, PanelHeight - 24f);
            CornerTicks(panel.transform);
            Header(panel.transform);
            Divider(panel.transform);

            slots = EpisodeSelectBoard.Build(services != null && services.Session != null
                ? services.Session.Cards
                : null);
            BuildCards(panel.transform);
            BuildDetail(panel.transform);
            BuildSettings(panel.transform);
            var hint = Text(panel.transform, "Hint", new Vector2(0f, -448f), new Vector2(1500f, 28f),
                18f, EpisodeSelectBoard.Muted, TextAlignmentOptions.Center);
            hint.text = "MENU BUTTON, OR PALM UP AND PINCH, RECALLS THIS HUB";
            hint.characterSpacing = 3f;
            SelectSlot(FirstOpenSlot());
            HideFlatSource(canvasGo);
        }

        int FirstOpenSlot()
        {
            for (var index = 0; index < slots.Length; index++)
            {
                if (!slots[index].Locked)
                    return index;
            }

            return 0;
        }

        void Header(Transform panel)
        {
            var mark = MakeImage(panel, "Mark", EpisodeSelectBoard.Accent, false);
            mark.sprite = EpisodeSelectBoard.Ring;
            mark.rectTransform.sizeDelta = new Vector2(34f, 34f);
            mark.rectTransform.anchoredPosition = new Vector2(-820f, 412f);
            var bar = MakeImage(panel, "MarkBar", EpisodeSelectBoard.Accent, false);
            bar.rectTransform.sizeDelta = new Vector2(3f, 22f);
            bar.rectTransform.anchoredPosition = new Vector2(-831f, 412f);
            var title = Text(panel, "Title", new Vector2(-560f, 418f), new Vector2(460f, 48f),
                40f, EpisodeSelectBoard.Text, TextAlignmentOptions.Left);
            title.text = "AMISS";
            title.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
            title.characterSpacing = 14f;
            var subtitle = Text(panel, "Subtitle", new Vector2(-560f, 376f), new Vector2(460f, 28f),
                20f, EpisodeSelectBoard.Muted, TextAlignmentOptions.Left);
            subtitle.text = "SELECT EPISODE";
            subtitle.characterSpacing = 8f;
        }

        void BuildCards(Transform panel)
        {
            var rowWidth = slots.Length * CardWidth + (slots.Length - 1) * CardGap;
            var origin = -890f + CardWidth * 0.5f;
            if (origin + rowWidth > 40f)
                origin = 20f - rowWidth + CardWidth * 0.5f;
            for (var index = 0; index < slots.Length; index++)
            {
                var captured = index;
                var center = new Vector2(origin + index * (CardWidth + CardGap), 70f);
                cards.Add(BuildCard(panel, slots[index], center, () => SelectSlot(captured)));
            }
        }

        CardVisual BuildCard(Transform panel, EpisodeSelectBoard.Slot slot, Vector2 center,
            UnityEngine.Events.UnityAction clicked)
        {
            var root = new GameObject("Card" + slot.Number.ToString("00"), typeof(RectTransform));
            root.transform.SetParent(panel, false);
            var rootRect = root.GetComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(CardWidth + 16f, CardHeight + 16f);
            rootRect.anchoredPosition = center;
            var glow = root.AddComponent<Image>();
            glow.sprite = EpisodeSelectBoard.Rounded;
            glow.type = Image.Type.Sliced;
            glow.color = EpisodeSelectBoard.AccentDim;
            glow.raycastTarget = false;

            var outline = MakeImage(root.transform, "Outline", EpisodeSelectBoard.Hairline, true, true);
            outline.rectTransform.sizeDelta = new Vector2(CardWidth, CardHeight);
            var fill = MakeImage(outline.transform, "Fill",
                slot.Locked ? EpisodeSelectBoard.CardFillLocked : EpisodeSelectBoard.CardFill, true);
            fill.rectTransform.sizeDelta = new Vector2(CardWidth - 4f, CardHeight - 4f);
            fill.raycastTarget = false;
            var button = outline.gameObject.AddComponent<Button>();
            button.targetGraphic = outline;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(clicked);

            var art = new GameObject("Art", typeof(RectTransform));
            art.transform.SetParent(outline.transform, false);
            var artRect = art.GetComponent<RectTransform>();
            artRect.sizeDelta = new Vector2(CardWidth - 28f, 300f);
            artRect.anchoredPosition = new Vector2(0f, 70f);
            Image[] tintable;
            if (slot.Locked)
                tintable = BuildPadlock(art.transform);
            else
                tintable = BuildTemperatureMark(art.transform);

            var number = Text(outline.transform, "Number", new Vector2(0f, -168f),
                new Vector2(CardWidth - 24f, 40f), 30f, EpisodeSelectBoard.Text, TextAlignmentOptions.Center);
            number.text = slot.Number.ToString("00");
            number.fontStyle = FontStyles.Bold;
            var title = Text(outline.transform, "CardTitle", new Vector2(0f, -210f),
                new Vector2(CardWidth - 20f, 56f), 20f, EpisodeSelectBoard.Text, TextAlignmentOptions.Center);
            title.text = slot.Title;
            title.fontStyle = FontStyles.UpperCase;
            title.characterSpacing = 2f;

            return new CardVisual
            {
                Glow = glow,
                Outline = outline,
                Fill = fill,
                Number = number,
                Title = title,
                Tintable = tintable
            };
        }

        Image[] BuildTemperatureMark(Transform art)
        {
            var made = new List<Image>();
            made.Add(Line(art, "Stem", new Vector2(0f, -8f), new Vector2(2f, 168f)));
            made.Add(Line(art, "Cross", new Vector2(0f, 18f), new Vector2(92f, 2f)));
            made.Add(Line(art, "Base", new Vector2(6f, -78f), new Vector2(78f, 2f)));
            var ring = MakeImage(art, "Ring", EpisodeSelectBoard.Icon, false);
            ring.sprite = EpisodeSelectBoard.Ring;
            ring.rectTransform.sizeDelta = new Vector2(68f, 68f);
            ring.rectTransform.anchoredPosition = new Vector2(0f, 48f);
            made.Add(ring);
            var diagonal = Line(art, "Diagonal", new Vector2(-24f, -36f), new Vector2(2f, 72f));
            diagonal.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 48f);
            made.Add(diagonal);
            return made.ToArray();
        }

        Image[] BuildPadlock(Transform art)
        {
            var shackle = MakeImage(art, "Shackle", EpisodeSelectBoard.IconLocked, false);
            shackle.sprite = EpisodeSelectBoard.Ring;
            shackle.rectTransform.sizeDelta = new Vector2(52f, 52f);
            shackle.rectTransform.anchoredPosition = new Vector2(0f, 28f);
            var mask = MakeImage(art, "ShackleMask", EpisodeSelectBoard.CardFillLocked, false);
            mask.rectTransform.sizeDelta = new Vector2(58f, 28f);
            mask.rectTransform.anchoredPosition = new Vector2(0f, 12f);
            var body = new List<Image>
            {
                Line(art, "LockTop", new Vector2(0f, 2f), new Vector2(48f, 2f), EpisodeSelectBoard.IconLocked),
                Line(art, "LockBottom", new Vector2(0f, -40f), new Vector2(48f, 2f), EpisodeSelectBoard.IconLocked),
                Line(art, "LockLeft", new Vector2(-24f, -19f), new Vector2(2f, 42f), EpisodeSelectBoard.IconLocked),
                Line(art, "LockRight", new Vector2(24f, -19f), new Vector2(2f, 42f), EpisodeSelectBoard.IconLocked)
            };
            var hole = MakeImage(art, "Keyhole", EpisodeSelectBoard.IconLocked, false);
            hole.sprite = EpisodeSelectBoard.Ring;
            hole.rectTransform.sizeDelta = new Vector2(12f, 12f);
            hole.rectTransform.anchoredPosition = new Vector2(0f, -16f);
            body.Add(shackle);
            body.Add(hole);
            return body.ToArray();
        }

        void BuildDetail(Transform panel)
        {
            detailTitle = Text(panel, "DetailTitle", new Vector2(470f, 390f), new Vector2(700f, 130f),
                34f, EpisodeSelectBoard.Text, TextAlignmentOptions.TopLeft);
            detailTitle.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
            detailTitle.characterSpacing = 2f;
            detailBody = Text(panel, "DetailBody", new Vector2(470f, 230f), new Vector2(700f, 100f),
                24f, EpisodeSelectBoard.Muted, TextAlignmentOptions.TopLeft);
            var durationLabel = Text(panel, "DurationLabel", new Vector2(470f, 100f), new Vector2(700f, 28f),
                18f, EpisodeSelectBoard.Muted, TextAlignmentOptions.Left);
            durationLabel.text = "DURATION";
            durationLabel.characterSpacing = 4f;
            durationValue = Text(panel, "DurationValue", new Vector2(470f, 64f), new Vector2(700f, 36f),
                28f, EpisodeSelectBoard.Text, TextAlignmentOptions.Left);
            var availabilityLabel = Text(panel, "StatusLabel", new Vector2(470f, 8f), new Vector2(700f, 28f),
                18f, EpisodeSelectBoard.Muted, TextAlignmentOptions.Left);
            availabilityLabel.text = "STATUS";
            availabilityLabel.characterSpacing = 4f;
            availabilityValue = Text(panel, "StatusValue", new Vector2(470f, -28f), new Vector2(700f, 36f),
                28f, EpisodeSelectBoard.Accent, TextAlignmentOptions.Left);

            var launch = new GameObject("Launch", typeof(RectTransform), typeof(Image), typeof(Button));
            launch.transform.SetParent(panel, false);
            var launchRect = launch.GetComponent<RectTransform>();
            launchRect.sizeDelta = new Vector2(680f, 76f);
            launchRect.anchoredPosition = new Vector2(450f, -150f);
            launchOutline = launch.GetComponent<Image>();
            launchOutline.sprite = EpisodeSelectBoard.Rounded;
            launchOutline.type = Image.Type.Sliced;
            launchOutline.color = EpisodeSelectBoard.Accent;
            var fill = MakeImage(launch.transform, "Fill", new Color(0.02f, 0.04f, 0.05f, 0.55f), true);
            fill.rectTransform.sizeDelta = new Vector2(674f, 70f);
            fill.raycastTarget = false;
            launchButton = launch.GetComponent<Button>();
            launchButton.targetGraphic = launchOutline;
            launchButton.transition = Selectable.Transition.None;
            launchButton.onClick.AddListener(Confirm);
            launchLabel = Text(launch.transform, "Label", Vector2.zero, new Vector2(640f, 60f),
                26f, EpisodeSelectBoard.Accent, TextAlignmentOptions.Center);
            launchLabel.fontStyle = FontStyles.UpperCase;
            launchLabel.characterSpacing = 6f;
            launchLabel.text = "LAUNCH";
            statusText = Text(panel, "Status", new Vector2(470f, -248f), new Vector2(700f, 120f),
                22f, EpisodeSelectBoard.Muted, TextAlignmentOptions.TopLeft);
        }

        void BuildSettings(Transform panel)
        {
            var setting = new Color(0.07f, 0.09f, 0.12f, 0.95f);
            audioText = SettingButton(panel, "Audio", new Vector2(-250f, -330f), new Vector2(230f, 64f),
                setting, () => services.Panel?.Apply(new PanelCommand(PanelCommandKind.ToggleAudio)));
            tipsText = SettingButton(panel, "Tips", new Vector2(0f, -330f), new Vector2(230f, 64f),
                setting, () => services.Panel?.Apply(new PanelCommand(PanelCommandKind.ToggleTips)));
            micText = SettingButton(panel, "Mic", new Vector2(250f, -330f), new Vector2(230f, 64f),
                setting, ToggleMicrophone);
            safetyText = SettingButton(panel, "Safety", new Vector2(496f, -330f), new Vector2(230f, 64f),
                setting, ToggleSafety);
            safetyText.text = "SAFETY ON";
            var exit = SettingButton(panel, "Exit", new Vector2(742f, -330f), new Vector2(230f, 64f),
                new Color(0.22f, 0.07f, 0.07f, 0.95f), ExitApp);
            exit.text = "EXIT";
            exit.color = new Color(0.96f, 0.78f, 0.74f, 1f);
            BuildModelKeyRow(panel, setting);
        }

        /// <summary>
        /// The evaluation key row: one masked field, save, clear, and the
        /// defined local tutor action. The field is a password input, the
        /// stored value is never shown back, and the status reports presence
        /// and the masked tail only.
        /// </summary>
        void BuildModelKeyRow(Transform panel, Color setting)
        {
            var fieldGo = new GameObject("ModelKey", typeof(RectTransform), typeof(Image),
                typeof(RectMask2D), typeof(TMP_InputField));
            fieldGo.transform.SetParent(panel, false);
            var fieldRect = fieldGo.GetComponent<RectTransform>();
            fieldRect.sizeDelta = new Vector2(360f, 52f);
            fieldRect.anchoredPosition = new Vector2(-650f, -400f);
            var outline = fieldGo.GetComponent<Image>();
            outline.sprite = EpisodeSelectBoard.Rounded;
            outline.type = Image.Type.Sliced;
            outline.color = EpisodeSelectBoard.Hairline;
            var fill = MakeImage(fieldGo.transform, "Fill", setting, true);
            fill.rectTransform.sizeDelta = new Vector2(354f, 46f);
            fill.raycastTarget = false;

            var text = FieldText(fieldGo.transform, "Text", EpisodeSelectBoard.Text, "");
            var placeholder = FieldText(fieldGo.transform, "Placeholder",
                EpisodeSelectBoard.Muted, "META MODEL API KEY");
            modelKeyField = fieldGo.GetComponent<TMP_InputField>();
            modelKeyField.textViewport = fieldRect;
            modelKeyField.textComponent = text;
            modelKeyField.placeholder = placeholder;
            modelKeyField.lineType = TMP_InputField.LineType.SingleLine;
            modelKeyField.contentType = TMP_InputField.ContentType.Password;
            modelKeyField.characterLimit = 512;
            modelKeyField.onSubmit.AddListener(_ => SaveModelKey());

            var paste = SettingButton(panel, "PasteKey", new Vector2(-410f, -400f),
                new Vector2(100f, 52f), setting, PasteModelKey);
            paste.text = "PASTE";
            paste.fontSize = 17f;
            var save = SettingButton(panel, "SaveKey", new Vector2(-290f, -400f),
                new Vector2(120f, 52f), setting, SaveModelKey);
            save.text = "SAVE KEY";
            save.fontSize = 17f;
            var clear = SettingButton(panel, "ClearKey", new Vector2(-165f, -400f),
                new Vector2(110f, 52f), setting, ClearModelKey);
            clear.text = "CLEAR";
            clear.fontSize = 17f;
            modelKeyText = Text(panel, "ModelKeyStatus", new Vector2(215f, -400f),
                new Vector2(300f, 52f), 14f, EpisodeSelectBoard.Muted, TextAlignmentOptions.Left);
            modelKeyText.textWrappingMode = TextWrappingModes.Normal;
            BuildWitTokenField(panel, setting);
            RefreshModelKeyLabel();
        }

        /// <summary>
        /// The voice configuration seam. The Wit client token is device
        /// configuration: it is typed or pasted on the headset, imported
        /// from the PC in development builds, saved locally, and never
        /// committed, logged, or shown back beyond its masked tail
        /// (design 06 section 1).
        /// </summary>
        void BuildWitTokenField(Transform panel, Color setting)
        {
            var fieldGo = new GameObject("WitToken", typeof(RectTransform), typeof(Image),
                typeof(RectMask2D), typeof(TMP_InputField));
            fieldGo.transform.SetParent(panel, false);
            var fieldRect = fieldGo.GetComponent<RectTransform>();
            fieldRect.sizeDelta = new Vector2(220f, 52f);
            fieldRect.anchoredPosition = new Vector2(485f, -400f);
            var outline = fieldGo.GetComponent<Image>();
            outline.sprite = EpisodeSelectBoard.Rounded;
            outline.type = Image.Type.Sliced;
            outline.color = EpisodeSelectBoard.Hairline;
            var fill = MakeImage(fieldGo.transform, "Fill", setting, true);
            fill.rectTransform.sizeDelta = new Vector2(214f, 46f);
            fill.raycastTarget = false;

            var text = FieldText(fieldGo.transform, "Text", EpisodeSelectBoard.Text, "");
            var placeholder = FieldText(fieldGo.transform, "Placeholder",
                EpisodeSelectBoard.Muted, "WIT CLIENT TOKEN");
            witTokenField = fieldGo.GetComponent<TMP_InputField>();
            witTokenField.textViewport = fieldRect;
            witTokenField.textComponent = text;
            witTokenField.placeholder = placeholder;
            witTokenField.lineType = TMP_InputField.LineType.SingleLine;
            witTokenField.contentType = TMP_InputField.ContentType.Password;
            witTokenField.characterLimit = 512;
            witTokenField.onSubmit.AddListener(SaveWitToken);

            var paste = SettingButton(panel, "PasteWitToken", new Vector2(650f, -400f),
                new Vector2(90f, 52f), setting, PasteWitToken);
            paste.text = "PASTE";
            paste.fontSize = 17f;
            var save = SettingButton(panel, "SaveWitToken", new Vector2(785f, -400f),
                new Vector2(160f, 52f), setting, () => SaveWitToken(witTokenField.text));
            save.text = "SAVE TOKEN";
            save.fontSize = 17f;
        }

        void SaveWitToken(string value)
        {
            if (services == null || services.VoiceRig == null)
                return;
            var saved = services.VoiceRig.SaveToken(value);
            if (witTokenField != null)
                witTokenField.text = "";
            tutorStatus = saved
                ? "voice token saved " + services.VoiceRig.Tokens.MaskedTail
                : "voice token empty";
            RefreshModelKeyLabel();
            Debug.Log("[Amiss] wit token save present=" + saved);
        }

        void PasteModelKey()
        {
            if (services == null || services.EvaluationKeys == null)
                return;
            var stored = services.EvaluationKeys.Save(ClipboardText());
            Debug.Log("[Amiss] model key paste present=" + stored);
            tutorStatus = stored ? "key pasted" : "clipboard empty";
            RefreshModelKeyLabel();
        }

        void PasteWitToken() => SaveWitToken(ClipboardText());

        static string ClipboardText()
        {
            var clip = GUIUtility.systemCopyBuffer;
            return string.IsNullOrEmpty(clip) ? "" : clip.Trim();
        }

        /// <summary>
        /// PC provisioning for the evaluation/debug build: when
        /// <c>tools/set-keys.sh</c> has pushed a one-time import file, save
        /// its values through the same stores as typed entry. The file is
        /// deleted on the first attempt, success or failure, and only
        /// presence booleans are logged. Release builds have no import.
        /// </summary>
        void PollKeyImport()
        {
#if DEVELOPMENT_BUILD
            if (services == null || Time.unscaledTime < nextKeyImportPoll)
                return;
            nextKeyImportPoll = Time.unscaledTime + 1f;
            var path = Path.Combine(Application.persistentDataPath, Tutor.KeyImport.FileName);
            string raw = null;
            try
            {
                if (!File.Exists(path))
                    return;
                if (new FileInfo(path).Length <= Tutor.KeyImport.MaxFileBytes)
                    raw = File.ReadAllText(path);
            }
            catch (System.Exception)
            {
                raw = null;
            }
            finally
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (System.Exception)
                {
                    // A failed delete is retried by the next poll.
                }
            }

            var metaStored = false;
            var witStored = false;
            if (Tutor.KeyImport.TryParse(raw, out var meta, out var wit))
            {
                if (meta.Length > 0 && services.EvaluationKeys != null)
                    metaStored = services.EvaluationKeys.Save(meta);
                if (wit.Length > 0 && services.VoiceRig != null)
                    witStored = services.VoiceRig.SaveToken(wit);
            }

            tutorStatus = metaStored || witStored ? "keys imported from PC" : "key import rejected";
            RefreshModelKeyLabel();
            RefreshLabels();
            Debug.Log("[Amiss] key import meta=" + metaStored + " wit=" + witStored);
#endif
        }

        void ToggleMicrophone()
        {
            if (services == null || services.Panel == null)
                return;
            var accepted = services.Panel.Apply(new PanelCommand(PanelCommandKind.ToggleMicrophone));
            tutorStatus = services.Panel.LastCommandOutcome;
            RefreshModelKeyLabel();
            RefreshLabels();
            Debug.Log("[Amiss] mic toggle accepted=" + accepted +
                      " state=" + services.Panel.VoiceState +
                      " consent=" + (services.VoiceControl != null
                          ? services.VoiceControl.Consent.ToString()
                          : "none"));
        }

        TMP_Text FieldText(Transform parent, string name, Color color, string value)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(14f, 6f);
            rect.offsetMax = new Vector2(-14f, -6f);
            var label = go.GetComponent<TextMeshProUGUI>();
            label.fontSize = 18f;
            label.color = color;
            label.alignment = TextAlignmentOptions.Left;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.raycastTarget = false;
            label.text = value;
            return label;
        }

        void SaveModelKey()
        {
            if (services == null || services.EvaluationKeys == null || modelKeyField == null)
                return;
            var stored = services.EvaluationKeys.Save(modelKeyField.text);
            Debug.Log("[Amiss] model key save present=" + stored);
            modelKeyField.text = "";
            tutorStatus = stored ? "key saved" : "key empty";
            RefreshModelKeyLabel();
        }

        void ClearModelKey()
        {
            if (services == null || services.EvaluationKeys == null)
                return;
            services.EvaluationKeys.Clear();
            services.Tutor?.ClearKey();
            Debug.Log("[Amiss] model key cleared");
            tutorStatus = "key cleared";
            RefreshModelKeyLabel();
        }

        void RefreshModelKeyLabel()
        {
            if (modelKeyText == null || services == null)
                return;
            var present = services.Tutor != null && services.Tutor.KeyPresent;
            var key = present ? "MODEL KEY " + services.Tutor.MaskedTail : "NO MODEL KEY";
            modelKeyText.text = tutorStatus.Length > 0 ? key + "\n" + tutorStatus : key;
            modelKeyText.color = present ? EpisodeSelectBoard.Accent : EpisodeSelectBoard.Muted;
            if (modelKeyField != null && modelKeyField.placeholder is TMP_Text hint)
                hint.text = present ? "REPLACE KEY" : "META MODEL API KEY";
        }

        void ToggleSafety()
        {
            if (services == null || services.Room == null)
                return;
            services.Room.SetSafetyCheck(!services.Room.SafetyCheckEnabled);
            Debug.Log("[Amiss] safety check " + (services.Room.SafetyCheckEnabled ? "on" : "off"));
            RefreshLabels();
        }

        /// <summary>
        /// Leaves the application. This is not a return to the library.
        /// </summary>
        void ExitApp()
        {
            Debug.Log("[Amiss] hub exit");
            services?.VoiceControl?.EndTrialEpisode();
            services?.SimulationRuntime?.StopTrial("exit_app");
            Application.Quit();
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                        activity.Call("finish");
                }
            }
            catch (System.Exception)
            {
            }
#endif
        }

        void Divider(Transform panel)
        {
            var line = MakeImage(panel, "Divider", EpisodeSelectBoard.Hairline, false);
            line.rectTransform.sizeDelta = new Vector2(1.5f, 760f);
            line.rectTransform.anchoredPosition = new Vector2(70f, 20f);
        }

        void CornerTicks(Transform panel)
        {
            const float inset = 18f;
            const float length = 22f;
            var halfW = (PanelWidth - 24f) * 0.5f - inset;
            var halfH = (PanelHeight - 24f) * 0.5f - inset;
            Tick(panel, new Vector2(-halfW, halfH), length, 1f, 1f);
            Tick(panel, new Vector2(halfW, halfH), length, -1f, 1f);
            Tick(panel, new Vector2(-halfW, -halfH), length, 1f, -1f);
            Tick(panel, new Vector2(halfW, -halfH), length, -1f, -1f);
        }

        void Tick(Transform panel, Vector2 corner, float length, float xSign, float ySign)
        {
            Line(panel, "TickX", corner + new Vector2(xSign * length * 0.5f, 0f), new Vector2(length, 1.5f));
            Line(panel, "TickY", corner + new Vector2(0f, ySign * length * 0.5f), new Vector2(1.5f, length));
        }

        /// <summary>
        /// Moves the source canvas onto <see cref="OverlaySourceLayer"/>.
        /// Helper objects the overlay creates for itself stay on their own layers,
        /// so the curved hole punch remains in the eye view.
        /// </summary>
        public static void HideFlatSource(GameObject canvasRoot)
        {
            if (canvasRoot == null)
                return;
            AssignSourceLayer(canvasRoot.transform);
        }

        /// <summary>
        /// Drops <see cref="OverlaySourceLayer"/> from an eye camera.
        /// </summary>
        public static void ExcludeFlatSource(Camera camera)
        {
            if (camera == null)
                return;
            camera.cullingMask &= ~(1 << OverlaySourceLayer);
        }

        static void AssignSourceLayer(Transform node)
        {
            node.gameObject.layer = OverlaySourceLayer;
            for (var i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i).gameObject;
                if ((child.hideFlags & HideFlags.HideInHierarchy) != 0)
                    continue;
                AssignSourceLayer(child.transform);
            }
        }

        static void ExcludeFlatSourceFromEyes()
        {
            var cameras = Camera.allCameras;
            for (var i = 0; i < cameras.Length; i++)
                ExcludeFlatSource(cameras[i]);
        }

        /// <summary>
        /// Draws the hub on the compositor cylinder. Curved overlays stay mono.
        /// </summary>
        public static void CurveOverlay(OVROverlayCanvas overlay)
        {
            overlay.shape = OVROverlayCanvas.CanvasShape.Curved;
            overlay.curveRadius = CurveRadiusMeters;
            overlay.enable3D = false;
        }

        /// <summary>
        /// Maps a hit on the panel cylinder back to the flat canvas point that
        /// drew that part of the picture. The overlay bends arc length through
        /// the same sine the compositor uses.
        /// </summary>
        public static Vector3 CanvasPointForCylinderHit(Transform canvas, Vector3 worldHit, float radius)
        {
            var delta = worldHit - canvas.position;
            var x = Vector3.Dot(delta, canvas.right);
            var y = Vector3.Dot(delta, canvas.up);
            var z = Vector3.Dot(delta, canvas.forward);
            var angle = Mathf.Atan2(x, z + radius);
            return canvas.position + canvas.right * (angle * radius) + canvas.up * y;
        }

        void BendInteractions(Canvas target)
        {
            if (interactionRoots.Count == 0)
                return;
            var rect = target.GetComponent<RectTransform>();
            var width = rect.rect.width * target.transform.lossyScale.x;
            var height = rect.rect.height * target.transform.lossyScale.y;
            var axis = new GameObject("HubCylinder");
            axis.transform.SetParent(transform, false);
            axis.transform.localPosition = new Vector3(0f, 0f, -CurveRadiusMeters);
            axis.transform.localRotation = Quaternion.identity;
            axis.transform.localScale = Vector3.one;
            var cylinder = axis.AddComponent<Cylinder>();
            cylinder.Radius = CurveRadiusMeters;
            var surface = axis.AddComponent<CylinderSurface>();
            surface.InjectAllCylinderSurface(CylinderSurface.NormalFacing.In, cylinder, height);
            var clipper = axis.AddComponent<CylinderClipper>();
            clipper.Rotation = 0f;
            clipper.ArcDegrees = width / CurveRadiusMeters * Mathf.Rad2Deg;
            clipper.Bottom = -height * 0.5f;
            clipper.Top = height * 0.5f;
            var clipped = axis.AddComponent<ClippedCylinderSurface>();
            clipped.InjectAllClippedCylinderSurface(surface, new ICylinderClipper[] { clipper });
            foreach (var root in interactionRoots)
                Retarget(root, target.transform, clipped);
        }

        static void Retarget(GameObject copy, Transform canvas, ClippedCylinderSurface surface)
        {
            var bend = copy.AddComponent<HubCurvePointer>();
            bend.Bind(canvas, CurveRadiusMeters);
            var pointable = copy.GetComponent<PointableCanvas>();
            if (pointable != null)
                bend.InjectOptionalForwardElement(pointable);
            var ray = copy.GetComponent<RayInteractable>();
            if (ray != null)
            {
                ray.InjectSurface(surface);
                ray.InjectOptionalSelectSurface(surface);
                ray.InjectOptionalPointableElement(bend);
            }

            var poke = copy.GetComponent<PokeInteractable>();
            if (poke != null)
            {
                poke.InjectSurfacePatch(surface);
                poke.InjectOptionalPointableElement(bend);
            }

            foreach (var plane in copy.GetComponentsInChildren<PlaneSurface>(true))
                plane.gameObject.SetActive(false);
        }

        bool CopyInteraction(string sourceName, Canvas target)
        {
            var source = GameObject.Find(sourceName);
            if (source == null || target == null)
                return false;
            var copy = Instantiate(source, target.transform);
            copy.name = "Hub" + sourceName;
            var copyRect = copy.GetComponent<RectTransform>();
            copyRect.localPosition = Vector3.zero;
            copyRect.localRotation = Quaternion.identity;
            copyRect.localScale = Vector3.one;
            copyRect.anchorMin = Vector2.zero;
            copyRect.anchorMax = Vector2.one;
            copyRect.pivot = new Vector2(0.5f, 0.5f);
            copyRect.sizeDelta = Vector2.zero;
            var pointable = copy.GetComponent<Oculus.Interaction.PointableCanvas>();
            if (pointable != null)
                pointable.InjectCanvas(target);
            interactionRoots.Add(copy);
            return true;
        }

        void Confirm()
        {
            var session = services != null ? services.Session : null;
            if (session == null)
                return;
            var choice = CurrentChoice();
            if (!choice.ButtonEnabled)
                return;
            var before = session.State;
            switch (choice.Action)
            {
                case EpisodeHubAction.Launch:
                    services.Room?.LaunchSelectedEpisode();
                    break;
                case EpisodeHubAction.StartTrial:
                    services.Room?.AcceptSetup();
                    break;
                case EpisodeHubAction.AddMissingWalls:
                    services.Room?.AddMissingWalls();
                    break;
                case EpisodeHubAction.RedrawRoom:
                    services.Room?.RedrawRoom();
                    break;
                case EpisodeHubAction.RetrySetup:
                    services.Room?.RetryFailedSetup();
                    break;
                default:
                    RunPrimary(session);
                    break;
            }

            var room = services.Room != null ? services.Room.Preparation.Status.ToString() : "none";
            var failure = services.Room != null ? services.Room.Preparation.Failure.ToString() : "none";
            Debug.Log("[Amiss] hub launch " + before + " -> " + session.State +
                      " room=" + room +
                      " failure=" + failure +
                      " mode=" + choice.Action +
                      " t=" + Time.realtimeSinceStartup.ToString("F3"));
            RefreshLabels();
        }

        void RunPrimary(HeadsetShellSession session)
        {
            var index = slots[selectedSlot].SessionIndex;
            switch (session.State)
            {
                case ShellSessionState.Library:
                    if (index >= 0)
                        session.SelectCard(index);
                    else if (session.SelectedIndex < 0 && session.Cards.Count > 0)
                        session.SelectCard(0);
                    if (session.State != ShellSessionState.StartReady)
                        break;
                    goto case ShellSessionState.StartReady;
                case ShellSessionState.StartReady:
                    session.RequestStart();
                    services.Room?.BeginSelectedEpisode();
                    services.Room?.ContinueIntoRoom();
                    break;
                default:
                    services.Room?.PrimaryAction();
                    break;
            }
        }

        Image Line(Transform parent, string name, Vector2 position, Vector2 size) =>
            Line(parent, name, position, size, EpisodeSelectBoard.Icon);

        Image Line(Transform parent, string name, Vector2 position, Vector2 size, Color color)
        {
            var image = MakeImage(parent, name, color, false);
            image.rectTransform.sizeDelta = size;
            image.rectTransform.anchoredPosition = position;
            return image;
        }

        Image MakeImage(Transform parent, string name, Color color, bool rounded, bool raycast = false)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = raycast;
            if (rounded)
            {
                image.sprite = EpisodeSelectBoard.Rounded;
                image.type = Image.Type.Sliced;
            }

            return image;
        }

        TMP_Text Text(Transform parent, string name, Vector2 position, Vector2 size, float fontSize,
            Color color, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var labelRect = go.GetComponent<RectTransform>();
            labelRect.sizeDelta = size;
            labelRect.anchoredPosition = position;
            var label = go.GetComponent<TextMeshProUGUI>();
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = color;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.Normal;
            return label;
        }

        TMP_Text SettingButton(Transform parent, string name, Vector2 position, Vector2 size,
            Color color, UnityEngine.Events.UnityAction clicked)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var buttonRect = go.GetComponent<RectTransform>();
            buttonRect.sizeDelta = size;
            buttonRect.anchoredPosition = position;
            var image = go.GetComponent<Image>();
            image.sprite = EpisodeSelectBoard.Rounded;
            image.type = Image.Type.Sliced;
            image.color = EpisodeSelectBoard.Hairline;
            var fill = MakeImage(go.transform, "Fill", color, true);
            fill.rectTransform.sizeDelta = size - new Vector2(3f, 3f);
            fill.raycastTarget = false;
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(clicked);
            var label = Text(go.transform, "Label", Vector2.zero, size - new Vector2(12f, 8f), 20f,
                EpisodeSelectBoard.Text, TextAlignmentOptions.Center);
            label.characterSpacing = 2f;
            return label;
        }

        sealed class CardVisual
        {
            public Image Glow;
            public Image Outline;
            public Image Fill;
            public TMP_Text Number;
            public TMP_Text Title;
            public Image[] Tintable;

            public void Apply(bool selected, bool locked)
            {
                var accent = selected && !locked;
                Glow.enabled = accent;
                if (Fill != null)
                    Fill.color = locked ? EpisodeSelectBoard.CardFillLocked : EpisodeSelectBoard.CardFill;
                Outline.color = accent ? EpisodeSelectBoard.Accent : EpisodeSelectBoard.Hairline;
                var ink = locked ? EpisodeSelectBoard.Muted : accent ? EpisodeSelectBoard.Accent : EpisodeSelectBoard.Text;
                Number.color = ink;
                Title.color = ink;
                var art = locked ? EpisodeSelectBoard.IconLocked : accent ? EpisodeSelectBoard.Accent : EpisodeSelectBoard.Icon;
                if (Tintable == null)
                    return;
                foreach (var image in Tintable)
                {
                    if (image != null)
                        image.color = art;
                }
            }
        }
    }
}
