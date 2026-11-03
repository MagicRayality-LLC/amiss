using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Amiss.Shell.UI
{
    /// <summary>
    /// Builds and fills the fixed-slot column of design 05 section 4.2. Every
    /// row takes its height from <see cref="PanelLayout"/>, so no slot renders
    /// more lines than the page budgets for it, and no text is assembled here:
    /// <see cref="Render"/> only copies slot text out of one
    /// <see cref="CorePanelPresentation"/>.
    /// </summary>
    public sealed class PanelView
    {
        readonly Dictionary<PanelSlot, TMP_Text> slots = new();
        readonly List<TMP_Text> cards = new();
        Image background;

        public Canvas Canvas { get; private set; }
        public RectTransform Content { get; private set; }
        public TMP_Text Title { get; private set; }
        public TMP_Text Status { get; private set; }
        public TMP_Text Safety { get; private set; }
        public TMP_Text Readout { get; private set; }
        public TMP_Text Progress { get; private set; }
        public TMP_Text Trial { get; private set; }
        public TMP_Text ChartStatus { get; private set; }
        public TMP_Text Subtitle { get; private set; }
        public TMP_Text Footer { get; private set; }
        public TMP_Text[] ChartTabs { get; private set; } = Array.Empty<TMP_Text>();
        public Button PrimaryAction { get; private set; }
        public TMP_Text PrimaryActionText { get; private set; }
        public TMP_Text AudioText { get; private set; }
        public TMP_Text TipsText { get; private set; }
        public TMP_Text PauseText { get; private set; }
        public IReadOnlyList<TMP_Text> CardTexts => cards;

        /// <summary>Slot text of a rendered row, or null for the control rows.</summary>
        public TMP_Text Slot(PanelSlot slot) => slots.TryGetValue(slot, out var text) ? text : null;

        /// <summary>
        /// Builds the column under <paramref name="parent"/>. The canvas is
        /// created when the parent has none, so a test can build the same page
        /// outside the scene.
        /// </summary>
        public void Build(Transform parent, int cardCount,
            Action<string> selectChart, Action primaryAction, Action toggleAudio,
            Action toggleTips, Action togglePause, Action<PanelCommandKind> flow,
            bool guideAvailable)
        {
            Canvas = parent.GetComponentInChildren<Canvas>();
            if (Canvas == null)
            {
                var canvasGo = new GameObject("LibraryCanvas", typeof(Canvas), typeof(RectTransform));
                canvasGo.transform.SetParent(parent, false);
                Canvas = canvasGo.GetComponent<Canvas>();
            }
            Canvas.renderMode = RenderMode.WorldSpace;
            var scaler = Canvas.GetComponent<CanvasScaler>();
            if (scaler != null)
                UnityEngine.Object.Destroy(scaler);
            var canvasRect = Canvas.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(PanelLayout.PageWidth, PanelLayout.PageHeight);
            canvasRect.localScale = Vector3.one * 0.001f;

            var background = new GameObject("Background", typeof(RectTransform), typeof(Image))
                .GetComponent<Image>();
            background.transform.SetParent(Canvas.transform, false);
            background.color = new Color(0.05f, 0.06f, 0.08f, 0.96f);
            background.raycastTarget = false;
            Stretch(background.rectTransform, PanelLayout.EdgeInset, PanelLayout.EdgeInset);
            this.background = background;

            var contentGo = new GameObject("FixedPanelSlots",
                typeof(RectTransform), typeof(VerticalLayoutGroup));
            contentGo.transform.SetParent(Canvas.transform, false);
            Content = contentGo.GetComponent<RectTransform>();
            Content.anchorMin = Vector2.zero;
            Content.anchorMax = Vector2.one;
            Content.offsetMin = new Vector2(PanelLayout.HorizontalInset, PanelLayout.VerticalInset);
            Content.offsetMax = new Vector2(-PanelLayout.HorizontalInset, -PanelLayout.VerticalInset);
            var layout = contentGo.GetComponent<VerticalLayoutGroup>();
            layout.spacing = PanelLayout.Spacing;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Title = AddSlot(PanelSlot.Title, "Title", PanelLayout.TitleFontSize, FontStyles.Bold);
            Title.text = CorePanelPresentation.TitleText;
            Status = AddSlot(PanelSlot.Status, "SessionStatus", PanelLayout.StatusFontSize,
                FontStyles.Bold);
            Safety = AddSlot(PanelSlot.Safety, "SafetyBanner", PanelLayout.ReadoutFontSize);

            var band = NewRow("ReadoutBand", Content, PanelLayout.Height(PanelSlot.Readout));
            Readout = AddText(PanelSlot.Readout, "PrimaryReadout", band.transform,
                PanelLayout.ReadoutFontSize);
            Readout.gameObject.AddComponent<LayoutElement>().flexibleWidth =
                PanelLayout.ReadoutColumnWidth;
            Progress = AddText(PanelSlot.Progress, "StageProgress", band.transform,
                PanelLayout.ReadoutFontSize);
            Progress.gameObject.AddComponent<LayoutElement>().flexibleWidth =
                PanelLayout.ProgressColumnWidth;

            Trial = AddSlot(PanelSlot.Trial, "CurrentTrial", PanelLayout.ReadoutFontSize);

            var capacity = Math.Max(1, Math.Min(cardCount, PanelLayout.MaxCards));
            for (var i = 0; i < capacity; i++)
            {
                var card = new GameObject("Card" + i, typeof(RectTransform), typeof(Image))
                    .GetComponent<Image>();
                card.transform.SetParent(Content, false);
                card.color = new Color(0.12f, 0.14f, 0.18f, 0.96f);
                var element = card.gameObject.AddComponent<LayoutElement>();
                element.minHeight = PanelLayout.CardHeight;
                element.preferredHeight = PanelLayout.CardHeight;
                element.flexibleHeight = 0;
                var text = NewText("CardText" + i, card.transform, PanelLayout.CardFontSize);
                Stretch(text.rectTransform, 6f, 3f);
                cards.Add(text);
            }

            // Tabs and the active chart line sit in one band: the four slot
            // buttons across most of the row and the chart status text under
            // them. That band is the chart slot row of design 05 section 4.2.
            var chartRow = NewRow("ChartSlotTabs", Content,
                Math.Max(PanelLayout.TabsRowHeight, PanelLayout.Height(PanelSlot.ChartStatus)));
            var tabStrip = NewRow("ChartTabs", chartRow.transform, PanelLayout.TabsRowHeight);
            var strip = tabStrip.AddComponent<LayoutElement>();
            strip.flexibleWidth = 1f;
            ChartTabs = new TMP_Text[CorePanelController.ChartSlots.Count];
            for (var i = 0; i < CorePanelController.ChartSlots.Count; i++)
            {
                var slotId = CorePanelController.ChartSlots[i];
                ChartTabs[i] = NewButton(slotId, tabStrip.transform, slotId,
                    () => selectChart(slotId)).GetComponentInChildren<TMP_Text>();
            }
            ChartStatus = AddText(PanelSlot.ChartStatus, "ChartData", chartRow.transform,
                PanelLayout.ChartFontSize);
            chartRow.GetComponent<HorizontalLayoutGroup>().childControlWidth = true;
            var chartElement = ChartStatus.gameObject.AddComponent<LayoutElement>();
            chartElement.minWidth = PanelLayout.ChartStatusWidth;
            chartElement.preferredWidth = PanelLayout.ChartStatusWidth;
            chartElement.flexibleWidth = 0f;
            Subtitle = AddSlot(PanelSlot.Subtitle, "CurrentSubtitle", PanelLayout.CardFontSize);

            var controls = NewRow("SessionControls", Content, PanelLayout.ControlsRowHeight);
            PrimaryAction = NewButton("PrimaryAction", controls.transform, "Continue",
                () => primaryAction());
            PrimaryActionText = PrimaryAction.GetComponentInChildren<TMP_Text>();
            var mic = NewButton(CorePanelController.MicrophoneControl, controls.transform, "Mic off",
                () => { });
            mic.interactable = false;
            AudioText = NewButton(CorePanelController.AudioControl, controls.transform, "Mute",
                () => toggleAudio()).GetComponentInChildren<TMP_Text>();
            TipsText = NewButton(CorePanelController.TipsControl, controls.transform, "Tips on",
                () => toggleTips()).GetComponentInChildren<TMP_Text>();
            PauseText = NewButton("PauseResume", controls.transform, "Pause", () => togglePause())
                .GetComponentInChildren<TMP_Text>();

            var flowRow = NewRow("FlowControls", Content, PanelLayout.ControlsRowHeight);
            NewButton("Skip", flowRow.transform, "Skip", () => flow?.Invoke(PanelCommandKind.Skip))
                .interactable = guideAvailable;
            NewButton("Replay", flowRow.transform, "Replay",
                () => flow?.Invoke(PanelCommandKind.Replay)).interactable = guideAvailable;
            NewButton("FreeExplore", flowRow.transform, "Free explore",
                () => flow?.Invoke(PanelCommandKind.FreeExplore)).interactable = guideAvailable;
            NewButton(CorePanelController.LocateControl, flowRow.transform, "Locate",
                () => flow?.Invoke(PanelCommandKind.Locate));
            NewButton("Relocate", flowRow.transform, "Move TV",
                () => flow?.Invoke(PanelCommandKind.Relocate));
            NewButton("ResetWall", flowRow.transform, "Reset wall",
                () => flow?.Invoke(PanelCommandKind.ResetWall));

            Footer = AddSlot(PanelSlot.Footer, "BuildIdentity", PanelLayout.FooterFontSize);
            Footer.color = new Color(0.62f, 0.66f, 0.72f);
        }

        /// <summary>Copies one presentation into the column; no text is composed here.</summary>
        public void Render(CorePanelPresentation presentation)
        {
            if (presentation == null)
                return;
            Title.text = presentation.Title;
            Status.text = presentation.Status;
            Safety.text = presentation.Safety;
            Readout.text = presentation.Readout;
            Progress.text = presentation.Progress;
            Trial.text = presentation.Trial;
            ChartStatus.text = presentation.ChartStatus;
            Subtitle.text = presentation.Subtitle;
            Footer.text = presentation.Footer;
            for (var i = 0; i < cards.Count; i++)
            {
                var used = i < presentation.Cards.Count;
                cards[i].transform.parent.gameObject.SetActive(used);
                if (used)
                    cards[i].text = presentation.Cards[i];
            }
        }

        /// <summary>
        /// Shows or hides the whole fixed column. The canvas itself is shared
        /// with the wall glass, so the page rows are switched as one group.
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (background != null)
                background.gameObject.SetActive(visible);
            if (Content != null)
                Content.gameObject.SetActive(visible);
        }

        /// <summary>Every rendered slot text, for the layout budget test.</summary>
        public IEnumerable<(PanelSlot Slot, string Text)> SlotTexts()
        {
            foreach (var slot in PanelLayout.Column)
            {
                if (slot == PanelSlot.Cards || slot == PanelSlot.Tabs || slot == PanelSlot.Controls)
                    continue;
                var text = Slot(slot);
                if (text != null)
                    yield return (slot, text.text);
            }
            foreach (var card in cards)
                if (card.transform.parent.gameObject.activeSelf)
                    yield return (PanelSlot.Cards, card.text);
        }

        TMP_Text AddSlot(PanelSlot slot, string name, float fontSize,
            FontStyles style = FontStyles.Normal)
        {
            var text = AddText(slot, name, Content, fontSize, style);
            var element = text.gameObject.AddComponent<LayoutElement>();
            element.minHeight = PanelLayout.Height(slot);
            element.preferredHeight = PanelLayout.Height(slot);
            element.flexibleHeight = 0;
            return text;
        }

        TMP_Text AddText(PanelSlot slot, string name, Transform parent, float fontSize,
            FontStyles style = FontStyles.Normal)
        {
            var text = NewText(name, parent, fontSize, style);
            slots[slot] = text;
            return text;
        }

        static GameObject NewRow(string name, Transform parent, float height)
        {
            var row = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(parent, false);
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = PanelLayout.ReadoutGap;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            var element = row.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;
            element.flexibleHeight = 0;
            return row;
        }

        static TMP_Text NewText(string name, Transform parent, float size,
            FontStyles style = FontStyles.Normal)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.fontStyle = style;
            text.color = new Color(0.92f, 0.94f, 0.97f);
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            return text;
        }

        static Button NewButton(string name, Transform parent, string label,
            UnityEngine.Events.UnityAction clicked)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.16f, 0.22f, 0.30f, 1f);
            var button = go.GetComponent<Button>();
            button.onClick.AddListener(clicked);
            var text = NewText("Label", go.transform, PanelLayout.ButtonFontSize, FontStyles.Bold);
            text.text = label;
            text.alignment = TextAlignmentOptions.Center;
            Stretch(text.rectTransform, 0f, 0f);
            return button;
        }

        static void Stretch(RectTransform rt, float horizontal, float vertical)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(horizontal, vertical);
            rt.offsetMax = new Vector2(-horizontal, -vertical);
        }
    }
}
