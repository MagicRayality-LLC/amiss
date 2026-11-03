using System;
using System.Collections.Generic;
using System.Linq;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;

namespace Amiss.Shell.UI
{
    public enum PanelCommandKind
    {
        SelectChart,
        ToggleAudio,
        ToggleTips,
        ToggleMicrophone,
        Pause,
        Resume,
        Skip,
        Replay,
        FreeExplore,
        Locate,
        Relocate,
        ResetWall
    }

    public readonly struct PanelCommand
    {
        public PanelCommandKind Kind { get; }
        public string SlotId { get; }

        public PanelCommand(PanelCommandKind kind, string slotId = "")
        {
            Kind = kind;
            SlotId = slotId ?? "";
        }
    }

    public sealed class CorePanelController
    {
        public const string PrimaryReadout = "readout.primary";
        public const string StageProgress = "progress.stage";
        public const string CurrentTrial = "trial.current";
        public const string CurrentSubtitle = "subtitle.current";
        public const string MicrophoneControl = "control.mic";
        public const string AudioControl = "control.audio";
        public const string TipsControl = "control.tips";
        public const string FlowControl = "control.flow";
        public const string LocateControl = "control.locate";
        public const string SafetyBanner = "safety.banner";
        public static readonly IReadOnlyList<string> ChartSlots = Array.AsReadOnly(
            new[] { "chart.1", "chart.2", "chart.3", "chart.4" });

        readonly SimulationRuntimePort runtime;
        readonly GuideRunnerPort guide;
        readonly RoomPreparationController room;
        readonly Voice.VoiceController voice;

        public CorePanelController(SimulationRuntimePort runtime, GuideRunnerPort guide,
            RoomPreparationController room, Voice.VoiceController voice = null)
        {
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            this.guide = guide ?? throw new ArgumentNullException(nameof(guide));
            this.room = room;
            this.voice = voice;
        }

        public string ActiveChartSlot { get; private set; } = "chart.1";
        public bool AudioMuted { get; private set; }
        public bool TipsOff { get; private set; }

        /// <summary>
        /// The real microphone projection of design 06 section 3: consent,
        /// provider configuration, and the system permission together. It is
        /// a fact of the voice layer, never a placeholder.
        /// </summary>
        public bool MicrophoneAvailable => voice != null && voice.MicrophoneAvailable;

        public bool MicrophoneConsentAccepted => voice != null && voice.ConsentAccepted;

        public bool MicrophonePermissionGranted =>
            voice != null && voice.MicrophonePermissionGranted;

        public bool DictationConfigured => voice != null && voice.DictationConfigured;

        public Voice.VoiceState VoiceState =>
            voice != null ? voice.State : Voice.VoiceState.Closed;

        public string LastCommandOutcome { get; private set; } = "No panel command yet.";

        /// <summary>
        /// The accepted same-text speech unit of the last admitted tutor reply.
        /// It is the one seam where model-authored text meets the text field,
        /// so it is escaped here: a stray <c>&lt;</c> in a reply stays literal
        /// instead of swallowing the rest of the subtitle.
        /// </summary>
        public string SubtitleText { get; private set; } = "";

        /// <summary>Publishes one accepted speech unit; never a model-authored escape.</summary>
        public void ShowSubtitle(string text)
        {
            SubtitleText = EscapeRichText(text ?? "");
        }

        /// <summary>
        /// Wraps model-authored text so TextMesh Pro renders it literally. The
        /// one sequence that could end the literal block is neutralised with a
        /// zero-width space, which changes no visible character of the text.
        /// </summary>
        public static string EscapeRichText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            const string open = "<noparse>";
            const string close = "</noparse>";
            var literal = text.Replace(close, "</nopa\u200Bse>");
            return open + literal + close;
        }

        public bool Apply(PanelCommand command)
        {
            var accepted = false;
            switch (command.Kind)
            {
                case PanelCommandKind.SelectChart:
                    accepted = ChartSlots.Contains(command.SlotId);
                    if (accepted)
                        ActiveChartSlot = command.SlotId;
                    break;
                case PanelCommandKind.ToggleAudio:
                    AudioMuted = !AudioMuted;
                    accepted = true;
                    break;
                case PanelCommandKind.ToggleTips:
                    TipsOff = !TipsOff;
                    accepted = true;
                    break;
                case PanelCommandKind.ToggleMicrophone:
                    if (voice == null)
                    {
                        LastCommandOutcome = "Microphone is unavailable until the voice eligibility gate.";
                        return false;
                    }

                    var outcome = voice.ToggleExplicitCapture();
                    LastCommandOutcome = outcome.Readable;
                    return outcome.Accepted;
                case PanelCommandKind.Pause:
                    runtime.RequestPause("panel_local");
                    accepted = true;
                    break;
                case PanelCommandKind.Resume:
                    runtime.RequestResume();
                    room?.RequestResume();
                    accepted = true;
                    break;
                case PanelCommandKind.Skip:
                case PanelCommandKind.Replay:
                case PanelCommandKind.FreeExplore:
                    accepted = guide.Available;
                    LastCommandOutcome = accepted
                        ? "Guide command accepted."
                        : "Guide controls are unavailable until an episode guide is bound.";
                    return accepted;
                case PanelCommandKind.Locate:
                    accepted = room != null && room.Recovery.LocatePanel() != null;
                    break;
                case PanelCommandKind.Relocate:
                    accepted = room != null && room.CanMovePanel;
                    LastCommandOutcome = accepted
                        ? "Grab the bezel or bracket to move the TV between walls."
                        : "The TV can move only after room confirmation.";
                    return accepted;
                case PanelCommandKind.ResetWall:
                    accepted = room != null && room.ResetPanelToNearestWall();
                    break;
            }
            LastCommandOutcome = accepted ? command.Kind + " accepted." : command.Kind + " refused.";
            return accepted;
        }
    }
}
