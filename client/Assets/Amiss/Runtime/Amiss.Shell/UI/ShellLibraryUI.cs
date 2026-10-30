using Amiss.Contracts;
using UnityEngine;
using UnityEngine.UI;

namespace Amiss.Shell
{
    /// <summary>
    /// World-anchored library panel. One panel instrument, fixed pose in the
    /// confirmed coordinate frame, no head-following transform. Builds its
    /// uGUI hierarchy procedurally so the committed scene stays minimal and
    /// reviewable.
    /// </summary>
    public sealed class ShellLibraryUI : MonoBehaviour
    {
        Text statusLine;
        Text footerLine;
        Text[] cardTexts;
        GameObject[] cardHighlights;
        ShellServices services;
        string lastRendered;

        void Start()
        {
            services = FindFirstObjectByType<ShellServices>();
            BuildPanel();
        }

        void Update()
        {
            if (services == null || statusLine == null)
                return;
            var rendered = ComposeStatus();
            if (rendered != lastRendered)
            {
                lastRendered = rendered;
                statusLine.text = rendered;
                RefreshCards();
            }
        }

        void BuildPanel()
        {
            var canvasGo = new GameObject("LibraryCanvas", typeof(Canvas));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = canvasGo.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(720, 460);
            rect.localScale = Vector3.one * 0.0012f;
            rect.localPosition = Vector3.zero;
            rect.localRotation = Quaternion.identity;

            var background = NewImage("Background", canvasGo.transform);
            background.color = new Color(0.05f, 0.06f, 0.08f, 0.82f);
            Stretch(background.rectTransform, new Vector4(12, 12, 12, 12));

            var title = NewText("Title", canvasGo.transform);
            title.text = "Amiss";
            title.fontSize = 44;
            title.fontStyle = FontStyle.Bold;
            title.rectTransform.anchorMin = new Vector2(0, 1);
            title.rectTransform.anchorMax = new Vector2(1, 1);
            title.rectTransform.pivot = new Vector2(0.5f, 1);
            title.rectTransform.anchoredPosition = new Vector2(0, -18);
            title.rectTransform.sizeDelta = new Vector2(-48, 52);
            title.alignment = TextAnchor.UpperLeft;

            var subtitle = NewText("Subtitle", canvasGo.transform);
            subtitle.text = "Episode library";
            subtitle.fontSize = 24;
            subtitle.color = new Color(0.78f, 0.82f, 0.88f);
            subtitle.rectTransform.anchorMin = new Vector2(0, 1);
            subtitle.rectTransform.anchorMax = new Vector2(1, 1);
            subtitle.rectTransform.pivot = new Vector2(0.5f, 1);
            subtitle.rectTransform.anchoredPosition = new Vector2(0, -70);
            subtitle.rectTransform.sizeDelta = new Vector2(-48, 30);
            subtitle.alignment = TextAnchor.UpperLeft;

            var session = services != null ? services.Session : null;
            var cardCount = session != null && session.Cards.Count > 0 ? session.Cards.Count : 1;
            cardTexts = new Text[cardCount];
            cardHighlights = new GameObject[cardCount];
            for (var i = 0; i < cardCount; i++)
            {
                var card = NewImage("Card" + i, canvasGo.transform);
                card.color = new Color(0.12f, 0.14f, 0.18f, 0.9f);
                card.rectTransform.anchorMin = new Vector2(0, 1);
                card.rectTransform.anchorMax = new Vector2(1, 1);
                card.rectTransform.pivot = new Vector2(0.5f, 1);
                card.rectTransform.anchoredPosition = new Vector2(0, -112 - i * 118);
                card.rectTransform.sizeDelta = new Vector2(-32, 108);
                cardHighlights[i] = card.gameObject;

                var highlight = NewImage("Highlight" + i, card.transform);
                highlight.color = new Color(0.85f, 0.72f, 0.25f, 0.95f);
                Stretch(highlight.rectTransform, new Vector4(4, 4, 4, 4));
                var inner = NewImage("Fill" + i, highlight.transform);
                inner.color = new Color(0.12f, 0.14f, 0.18f, 0.96f);
                Stretch(inner.rectTransform, new Vector4(3, 3, 3, 3));
                inner.raycastTarget = false;
                highlight.gameObject.SetActive(false);

                var text = NewText("CardText" + i, card.transform);
                cardTexts[i] = text;
                text.fontSize = 22;
                text.rectTransform.anchorMin = Vector2.zero;
                text.rectTransform.anchorMax = Vector2.one;
                text.rectTransform.offsetMin = new Vector2(14, 8);
                text.rectTransform.offsetMax = new Vector2(-14, -8);
                text.alignment = TextAnchor.UpperLeft;
            }

            statusLine = NewText("Status", canvasGo.transform);
            statusLine.fontSize = 22;
            statusLine.rectTransform.anchorMin = new Vector2(0, 0.5f);
            statusLine.rectTransform.anchorMax = new Vector2(1, 0.5f);
            statusLine.rectTransform.pivot = new Vector2(0.5f, 1);
            statusLine.rectTransform.anchoredPosition = new Vector2(0, -112 - cardCount * 118 - 8);
            statusLine.rectTransform.sizeDelta = new Vector2(-32, 96);
            statusLine.alignment = TextAnchor.UpperLeft;

            footerLine = NewText("Footer", canvasGo.transform);
            footerLine.fontSize = 14;
            footerLine.color = new Color(0.62f, 0.66f, 0.72f);
            footerLine.rectTransform.anchorMin = new Vector2(0, 0);
            footerLine.rectTransform.anchorMax = new Vector2(1, 0);
            footerLine.rectTransform.pivot = new Vector2(0.5f, 0);
            footerLine.rectTransform.anchoredPosition = new Vector2(0, 12);
            footerLine.rectTransform.sizeDelta = new Vector2(-32, 18);
            footerLine.alignment = TextAnchor.UpperLeft;

            if (Application.isPlaying)
                footerLine.text = BuildIdentity.Compose(Application.version, Application.unityVersion);
            else
                footerLine.text = BuildIdentity.Compose("editor", Application.unityVersion);
        }

        void RefreshCards()
        {
            if (services == null)
                return;
            var session = services.Session;
            var progress = new EpisodeProgress();
            for (var i = 0; i < cardTexts.Length; i++)
            {
                if (i >= session.Cards.Count)
                {
                    cardTexts[i].text = "No other episodes are installed.";
                    cardHighlights[i].SetActive(false);
                    continue;
                }
                var card = LibraryCard.Project(session.Cards[i], progress);
                cardTexts[i].text =
                    $"{card.Question}\n" +
                    $"{card.ExpectedDurationMinutes} min · {card.RequiredSpaceSummary} · {card.ActionType} · " +
                    $"{AvailabilityLabel(card)} · {(card.Completed ? "Completed" : "Not completed")}";
                cardHighlights[i].SetActive(i == session.SelectedIndex);
            }
        }

        static string AvailabilityLabel(LibraryCard card) => card.Availability switch
        {
            EpisodeAvailability.Bundled => "Bundled",
            EpisodeAvailability.NotInstalled => "Not installed: " + card.AvailabilityReason,
            EpisodeAvailability.Locked => "Locked: " + card.AvailabilityReason,
            _ => "Unavailable"
        };

        string ComposeStatus()
        {
            if (services == null)
                return "Shell services unavailable.";
            var session = services.Session;
            switch (session.State)
            {
                case ShellSessionState.Library:
                    return "Push the thumbstick left or right to choose an episode,\n" +
                           "pull the trigger to select it, press B to return.";
                case ShellSessionState.CardSelected:
                    return "Selected.";
                case ShellSessionState.StartBlocked:
                    return ComposeBlocked(session);
                case ShellSessionState.StartReady:
                    return "Load check: runnable. Next is room preparation, not a Trial.\n" +
                           "Trigger: prepare room · B: back to library.";
                case ShellSessionState.RoomPreparation:
                case ShellSessionState.RoomConfirmed:
                case ShellSessionState.SetupAccepted:
                case ShellSessionState.Restricted:
                    return ComposeRoom(services.Room);
                case ShellSessionState.MountFailed:
                    return session.MountFailureReason + "\nPress B to return to the library.";
                default:
                    return session.State.ToString();
            }
        }

        static string ComposeRoom(Room.RoomPreparationController room)
        {
            if (room == null)
                return "Room preparation is unavailable. B: library.";
            var recovery = room.Recovery;
            var prefix = recovery.SafetyHold
                ? "SAFETY HOLD: boundary invalid. Trigger: reconfirm; resume stays explicit.\n"
                : recovery.ResumeRequired ? "Paused locally. Trigger: explicit resume; no catch-up.\n" : "";
            if (recovery.SceneUpdateAvailable)
                prefix += "Scene update: existing collision pin unchanged; rescan is future-only.\n";
            if (recovery.Panel != null && recovery.Panel.Status == Room.PanelPlacementStatus.RecoveryNeeded)
                prefix += "Panel alignment uncertain. Right stick: relocate; Trial pin unchanged.\n";

            if (recovery.SetupAccepted)
                return prefix + "Actual setup accepted and collision pinned. No balls or Trial started.\n" +
                       "Y: future-only rescan · right stick: relocate panel · B: library.";
            var preparation = room.Preparation;
            switch (preparation.Status)
            {
                case Room.RoomPreparationStatus.Explanation:
                    return prefix + "Scene data is used locally for room collision. Nothing starts yet.\n" +
                           "Trigger: request scene permission · B: library.";
                case Room.RoomPreparationStatus.PermissionRequested:
                    return prefix + "Waiting for the single scene-permission request…";
                case Room.RoomPreparationStatus.SpaceSetupReady:
                    return prefix + "Permission granted. Trigger: enter on-device Space Setup · B: library.";
                case Room.RoomPreparationStatus.SpaceSetupRequested:
                    return prefix + "Complete Space Setup on device. B: cancel setup.";
                case Room.RoomPreparationStatus.SceneLoading:
                    return prefix + "Loading the device Scene Model; no room fallback is allowed…";
                case Room.RoomPreparationStatus.AwaitingConfirmation:
                    var draft = preparation.Draft;
                    var issue = preparation.Failure == Room.RoomFailureReason.None ? "" :
                        "\nNot confirmable yet: " + preparation.Failure + ". X: supplement · Y: rescan.";
                    return prefix + (draft != null ? draft.BoundaryLabel : "Room draft") +
                           "\nEffective region: " + Room.RoomGeometry.Meters(draft?.EffectiveVolumeM3 ?? 0f) +
                           " m3. Review surfaces and clear swing volume." + issue +
                           "\nTrigger: confirm room · X: virtual supplements · Y: rescan · B: exit.";
                case Room.RoomPreparationStatus.Confirmed:
                    return prefix + preparation.Confirmation.BoundaryLabel +
                           "\nRoom confirmed; collision checksum is pinned. No balls or Trial.\n" +
                           "Trigger: separately accept actual setup · Y: future rescan.";
                case Room.RoomPreparationStatus.Restricted:
                    return prefix + "Restricted: " + preparation.Failure +
                           ". No balls or Trial.\nTrigger: retry · X: manual virtual boundary · B: exit.";
                default:
                    return prefix + "Room preparation idle.";
            }
        }

        static string ComposeBlocked(HeadsetShellSession session)
        {
            var verdict = session.SelectedVerdict;
            if (verdict != null && verdict.AvailabilityBlocked)
                return $"This episode cannot start.\n{verdict.AvailabilityReason}\nPress B to return.";
            if (verdict == null || verdict.Rejections.Count == 0)
                return "This episode cannot start.";
            var text = "Load check failed:\n";
            foreach (var rejection in verdict.Rejections)
                text += $"- {rejection.ReadableReason} ({rejection.ReasonCategory})\n";
            return text + "Press B to return to the library.";
        }

        static Text NewText(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.color = new Color(0.92f, 0.94f, 0.97f);
            return text;
        }

        static UnityEngine.UI.Image NewImage(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(UnityEngine.UI.Image));
            go.transform.SetParent(parent, false);
            return go.GetComponent<UnityEngine.UI.Image>();
        }

        static void Stretch(RectTransform rt, Vector4 borders)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(borders.x, borders.y);
            rt.offsetMax = new Vector2(-borders.z, -borders.w);
        }
    }
}
