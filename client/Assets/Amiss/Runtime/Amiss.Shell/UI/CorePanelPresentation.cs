using System;
using System.Collections.Generic;
using System.Linq;
using Amiss.Contracts;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;
using Amiss.Shell.Simulation;

namespace Amiss.Shell.UI
{
    /// <summary>
    /// The complete text of the fixed panel column for one moment. Exactly one
    /// value per slot renders; no slot text is assembled anywhere else, so the
    /// on-screen column and the layout budget test read the same strings.
    /// </summary>
    public sealed class CorePanelPresentation
    {
        public const string TitleText = "Amiss";

        public string Title { get; init; } = TitleText;
        public string Status { get; init; } = "";
        public string Safety { get; init; } = "";
        public string Readout { get; init; } = "";
        public string Progress { get; init; } = "";
        public string Trial { get; init; } = "";
        public IReadOnlyList<string> Cards { get; init; } = Array.Empty<string>();
        public string ChartStatus { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string Footer { get; init; } = "";

        /// <summary>The text a slot renders, or null for the control rows.</summary>
        public string SlotText(PanelSlot slot) => slot switch
        {
            PanelSlot.Title => Title,
            PanelSlot.Status => Status,
            PanelSlot.Safety => Safety,
            PanelSlot.Readout => Readout,
            PanelSlot.Progress => Progress,
            PanelSlot.Trial => Trial,
            PanelSlot.ChartStatus => ChartStatus,
            PanelSlot.Subtitle => Subtitle,
            PanelSlot.Footer => Footer,
            _ => null
        };

        /// <summary>Declared font size of a slot's text in page pixels.</summary>
        public static float FontSize(PanelSlot slot) => slot switch
        {
            PanelSlot.Title => PanelLayout.TitleFontSize,
            PanelSlot.Status => PanelLayout.StatusFontSize,
            PanelSlot.Safety => PanelLayout.ReadoutFontSize,
            PanelSlot.Readout => PanelLayout.ReadoutFontSize,
            PanelSlot.Progress => PanelLayout.ReadoutFontSize,
            PanelSlot.Trial => PanelLayout.ReadoutFontSize,
            PanelSlot.Cards => PanelLayout.CardFontSize,
            PanelSlot.ChartStatus => PanelLayout.ChartFontSize,
            PanelSlot.Subtitle => PanelLayout.CardFontSize,
            PanelSlot.Footer => PanelLayout.FooterFontSize,
            _ => 0f
        };

        /// <summary>
        /// Advance of one average glyph. LiberationSans SDF advances about
        /// 0.5 em per mixed-case character; the declared 0.56 em keeps the
        /// budget conservative so a wide string never overflows its slot.
        /// </summary>
        public static float GlyphWidth(float fontSize) => fontSize * 0.56f;

        /// <summary>
        /// Lines a string occupies when wrapped at <paramref name="width"/>
        /// page pixels in <paramref name="fontSize"/> text. Every explicit
        /// newline starts a new line; a line wider than the slot costs the
        /// extra wrapped lines.
        /// </summary>
        public static int Lines(string text, float fontSize, float width)
        {
            if (string.IsNullOrEmpty(text))
                return 1;
            var capacity = Math.Max(1, (int)Math.Floor(width / GlyphWidth(fontSize)));
            var lines = 0;
            foreach (var line in text.Split('\n'))
                lines += Math.Max(1, (int)Math.Ceiling(line.Length / (double)capacity));
            return lines;
        }

        /// <summary>Lines the slot's own text needs in its own column.</summary>
        public int LinesUsed(PanelSlot slot)
        {
            var text = SlotText(slot);
            if (text == null)
                return 0;
            return Lines(text, FontSize(slot), PanelLayout.SlotWidth(slot));
        }

        /// <summary>True when every slot's text fits its declared budget.</summary>
        public bool Fits() => FirstOverflowingSlot() == null;

        /// <summary>
        /// The first slot whose text exceeds its budget, as a readable
        /// description; null when the whole column fits.
        /// </summary>
        public string FirstOverflowingSlot()
        {
            foreach (var slot in PanelLayout.Column)
            {
                if (slot == PanelSlot.Cards || slot == PanelSlot.Tabs ||
                    slot == PanelSlot.Controls)
                    continue;
                var used = LinesUsed(slot);
                var budget = PanelLayout.LineBudget(slot);
                if (used > budget)
                    return slot + ": " + used + " lines of " + budget;
            }
            for (var index = 0; index < Cards.Count; index++)
            {
                var used = Lines(Cards[index], PanelLayout.CardFontSize, PanelLayout.ContentWidth);
                if (used > PanelLayout.CardLines)
                    return "card " + index + ": " + used + " lines of " + PanelLayout.CardLines;
            }
            if (Cards.Count > PanelLayout.MaxCards)
                return Cards.Count + " cards of " + PanelLayout.MaxCards;
            return null;
        }
    }

    /// <summary>
    /// Builds the fixed-slot text from the shell's own state. This type only
    /// words the approved projection: it reads ports, session state, and the
    /// panel controller and never samples, converts, or rounds a measurement.
    /// </summary>
    public static class CorePanelPresentationBuilder
    {
        public static CorePanelPresentation Build(
            HeadsetShellSession session,
            CorePanelController panel,
            SimulationRuntimePort runtime,
            TutorTransportPort tutor,
            RoomPreparationController room,
            string buildIdentity) =>
            Build(session, panel, runtime, tutor, room?.Preparation, room?.Recovery, buildIdentity);

        /// <summary>
        /// The same projection from the room's own state values. The shell
        /// passes its controller; a test drives the same state machine
        /// directly, so the rendered text and the budget check agree.
        /// </summary>
        public static CorePanelPresentation Build(
            HeadsetShellSession session,
            CorePanelController panel,
            SimulationRuntimePort runtime,
            TutorTransportPort tutor,
            RoomPreparation preparation,
            ShellRecovery recovery,
            string buildIdentity)
        {
            if (session == null)
            {
                return new CorePanelPresentation
                {
                    Status = "Space scan: unavailable; shell services are missing.",
                    Footer = buildIdentity ?? ""
                };
            }

            var cards = session.Cards
                .Select((declaration, index) => CardText(declaration, index == session.SelectedIndex))
                .ToArray();
            return new CorePanelPresentation
            {
                Status = ComposeStatus(session, preparation) + "\n" +
                         (panel?.LastCommandOutcome ?? "") + "\n" +
                         PlacementSummary(recovery?.Panel),
                Safety = ComposeSafety(recovery),
                Readout = CorePanelController.PrimaryReadout + "\n" +
                          TemperatureReadoutProjection.Compose(runtime?.Readout),
                Progress = CorePanelController.StageProgress + "\n" + session.State +
                           "; no guide evidence is claimed.",
                Trial = CorePanelController.CurrentTrial + "\n" + TrialLine(runtime, recovery),
                Cards = cards.Length > 0
                    ? cards
                    : new[] { "No episode declarations are installed." },
                ChartStatus = (panel?.ActiveChartSlot ?? CorePanelController.ChartSlots[0]) +
                              ": no committed chart samples.",
                Subtitle = CorePanelController.CurrentSubtitle + "\n" +
                           (panel != null && panel.SubtitleText.Length > 0
                               ? panel.SubtitleText
                               : tutor?.TraceLine ?? ""),
                Footer = buildIdentity ?? ""
            };
        }

        /// <summary>Recomposes every visible slot, to detect any change.</summary>
        public static string Key(CorePanelPresentation presentation)
        {
            if (presentation == null)
                return "";
            return presentation.Title + "\n" + presentation.Status + "\n" + presentation.Safety +
                   "\n" + presentation.Readout + "\n" + presentation.Progress + "\n" +
                   presentation.Trial + "\n" + string.Join("\n", presentation.Cards) + "\n" +
                   presentation.ChartStatus + "\n" + presentation.Subtitle + "\n" +
                   presentation.Footer;
        }

        public static string ComposeSafety(ShellRecovery recovery)
        {
            var safety = recovery != null && recovery.SafetyHold
                ? "SAFETY HOLD: reconfirm, then explicitly resume."
                : recovery != null && recovery.ResumeRequired
                    ? "Local pause: explicit resume required."
                    : "Safety: no active hold.";
            return CorePanelController.SafetyBanner + "\n" + safety;
        }

        public static string TrialLine(SimulationRuntimePort runtime, ShellRecovery recovery)
        {
            var summary = runtime?.TrialSummary;
            var boundary = recovery?.CurrentConfirmation?.BoundaryLabel ?? "no confirmed boundary";
            if (summary == null)
                return "No Trial; " + boundary + "; no input delta or save is claimed.";
            var bucketedTick = summary.Tick - summary.Tick % 10;
            return "Trial " + summary.TrialId + " · " + summary.BallCount + " balls · pin " +
                   summary.ShortChecksum + " · " + summary.WriterConfigurationId +
                   " · tick " + bucketedTick;
        }

        public static string CardText(EpisodeDeclaration declaration, bool selected)
        {
            var card = LibraryCard.Project(declaration, new EpisodeProgress());
            return card.Question + "\n" + (selected ? "Selected · " : "") +
                   AvailabilityLabel(card) + (card.Completed ? " · completed" : " · not completed");
        }

        public static string AvailabilityLabel(LibraryCard card) => card.Availability switch
        {
            EpisodeAvailability.Bundled => "Bundled",
            EpisodeAvailability.NotInstalled => "Not installed: " + card.AvailabilityReason,
            EpisodeAvailability.Locked => "Locked: " + card.AvailabilityReason,
            _ => "Unavailable"
        };

        public static string PlacementSummary(PanelPlacement placement)
        {
            if (placement == null)
                return "TV: not placed; room confirmation is required.";
            if (placement.MountMode == PanelMountMode.WallMounted)
                return "TV: wall-mounted (" + placement.WallProvenance + ") · revision " +
                       placement.Revision +
                       (string.IsNullOrEmpty(placement.Reason) ? "" : " · " + placement.Reason);
            return "TV: no usable wall · revision " + placement.Revision;
        }

        static string ComposeStatus(HeadsetShellSession session, RoomPreparation preparation)
        {
            switch (session.State)
            {
                case ShellSessionState.Library:
                    return "Space scan: not started. Choose an episode with ray/poke or controller.";
                case ShellSessionState.CardSelected:
                    return "Space scan: not started. Episode selected.";
                case ShellSessionState.StartBlocked:
                    return "Space scan: not started. " + ComposeBlocked(session);
                case ShellSessionState.StartReady:
                    return "Space scan: ready to prepare. Load check passed; no Trial yet.";
                case ShellSessionState.RoomPreparation:
                case ShellSessionState.RoomConfirmed:
                case ShellSessionState.SetupAccepted:
                case ShellSessionState.Restricted:
                    return ComposeRoom(preparation);
                case ShellSessionState.MountFailed:
                    return "Space scan: not started. " + session.MountFailureReason;
                default:
                    return "Space scan: " + session.State;
            }
        }

        static string ComposeRoom(RoomPreparation preparation)
        {
            if (preparation == null)
                return "Space scan: unavailable.";
            switch (preparation.Status)
            {
                case RoomPreparationStatus.Explanation:
                    return "Space scan: permission needed. Scene data stays local.";
                case RoomPreparationStatus.PermissionRequested:
                    return "Space scan: waiting for permission.";
                case RoomPreparationStatus.SpaceSetupReady:
                    return "Space scan: no room here. Start Space Setup and draw this room.";
                case RoomPreparationStatus.SpaceSetupRequested:
                    return "Space scan: waiting for Space Setup on device.";
                case RoomPreparationStatus.SceneLoading:
                    return "Space scan: loading the device Scene Model; no fallback.";
                case RoomPreparationStatus.AwaitingConfirmation:
                    var draft = preparation.Draft;
                    return "Space scan: confirmation needed. " +
                           (draft != null ? draft.BoundaryLabel : "Room draft") + "; " +
                           RoomGeometry.Meters(draft?.EffectiveVolumeM3 ?? 0f) + " m3; " +
                           preparation.Failure + ". Confirm, supplement, or rescan.";
                case RoomPreparationStatus.Confirmed:
                    return "Space scan: room confirmed. " +
                           preparation.Confirmation.BoundaryLabel +
                           "; separately accept actual setup.";
                case RoomPreparationStatus.Restricted:
                    return "Space scan: restricted. " + preparation.Failure +
                           ". No balls or Trial. Retry the scan or exit.";
                default:
                    return "Space scan: idle.";
            }
        }

        static string ComposeBlocked(HeadsetShellSession session)
        {
            var verdict = session.SelectedVerdict;
            if (verdict != null && verdict.AvailabilityBlocked)
                return verdict.AvailabilityReason;
            if (verdict == null || verdict.Rejections.Count == 0)
                return "This episode cannot start.";
            var text = "Load check failed:";
            foreach (var rejection in verdict.Rejections)
                text += " " + rejection.ReadableReason + " (" + rejection.ReasonCategory + ").";
            return text;
        }
    }
}
