using Amiss.Shell.UI;
using UnityEngine;

namespace Amiss.Shell
{
    /// <summary>
    /// The shell's screen surface. It owns the state polling and the local
    /// command wiring only; the fixed-slot column is built and filled by
    /// <see cref="PanelView"/> from one <see cref="CorePanelPresentation"/>, and
    /// the running trial's wall glass by <see cref="TrialGlassView"/> from one
    /// <see cref="TrialGlassPresentation"/>. This object composes no slot text,
    /// no temperature value, and no slot height.
    /// </summary>
    public sealed class ShellLibraryUI : MonoBehaviour
    {
        readonly PanelView view = new();
        readonly TrialGlassView glass = new();
        readonly TemperatureHistoryBuffer history = new();
        ShellServices services;
        string lastRendered;
        string lastGlass;
        bool? wallSurface;

        void Start()
        {
            services = FindFirstObjectByType<ShellServices>();
            BuildPanel();
            if (FindFirstObjectByType<EpisodeHub>() == null)
                new GameObject("EpisodeHub").AddComponent<EpisodeHub>();
        }

        void Update()
        {
            if (services == null || view.Status == null)
                return;
            var wall = EpisodeLaunchGate.WallSetVisible(services.Session.State);
            if (wall)
                TrialGlassFeed.Sync(history, services.SimulationRuntime.TrialSummary != null,
                    services.SimulationRuntime.Readout);
            var presentation = Presentation();
            var key = CorePanelPresentationBuilder.Key(presentation);
            if (key != lastRendered)
            {
                lastRendered = key;
                RefreshPanel(presentation);
            }
            RefreshGlass();
            ApplySurface(wall);
        }

        void BuildPanel()
        {
            var session = services != null ? services.Session : null;
            var cardCount = session != null && session.Cards.Count > 0 ? session.Cards.Count : 1;
            var guideAvailable = services != null && services.GuideRunner.Available;
            view.Build(transform, cardCount, SelectChart, PrimaryAction, ToggleAudio,
                ToggleTips, TogglePause, Flow, guideAvailable);
            view.Footer.text = Application.isPlaying
                ? BuildIdentity.Compose(Application.version, Application.unityVersion)
                : BuildIdentity.Compose("editor", Application.unityVersion);
            glass.Build(transform);
            RefreshPanel(Presentation());
            RefreshGlass();
        }

        CorePanelPresentation Presentation()
        {
            if (services == null)
                return new CorePanelPresentation
                {
                    Status = "Space scan: unavailable; shell services are missing.",
                    Footer = BuildIdentity.Compose(Application.version, Application.unityVersion)
                };
            return CorePanelPresentationBuilder.Build(services.Session, services.Panel,
                services.SimulationRuntime, services.TutorTransport, services.Room,
                view.Footer.text);
        }

        void RefreshPanel(CorePanelPresentation presentation)
        {
            view.Render(presentation);
            view.AudioText.text = services.Panel.AudioMuted ? "Audio muted" : "Audio on";
            view.TipsText.text = services.Panel.TipsOff ? "Tips off" : "Tips on";
            var recovery = services.Room != null ? services.Room.Recovery : null;
            view.PauseText.text = services.SimulationRuntime.PauseRequested ||
                                  recovery != null && recovery.ResumeRequired ? "Resume" : "Pause";
            view.PrimaryActionText.text = PrimaryActionLabel();
            view.PrimaryAction.interactable = PrimaryActionEnabled();
        }

        /// <summary>
        /// Copies the producer's readout and the trial history into the wall
        /// glass. Only the two glass elements are rendered while the wall set
        /// is the active surface.
        /// </summary>
        void RefreshGlass()
        {
            var presentation = TrialGlassPresentation.Build(services.Session.State,
                services.SimulationRuntime.Readout, history);
            var key = TrialGlassPresentation.Key(presentation);
            if (key == lastGlass)
                return;
            lastGlass = key;
            glass.Render(presentation);
        }

        /// <summary>
        /// The wall glass carries the trial glass instead of the fixed column
        /// while the wall set is visible; the hub keeps its own surface.
        /// </summary>
        void ApplySurface(bool wall)
        {
            if (wallSurface == wall)
                return;
            wallSurface = wall;
            view.SetVisible(!wall);
            glass.SetVisible(wall);
            if (!wall)
                history.Reset();
        }

        void SelectChart(string slotId) => Apply(new PanelCommand(PanelCommandKind.SelectChart, slotId));

        void ToggleAudio() => Apply(new PanelCommand(PanelCommandKind.ToggleAudio));

        void ToggleTips() => Apply(new PanelCommand(PanelCommandKind.ToggleTips));

        void Flow(PanelCommandKind kind) => Apply(new PanelCommand(kind));

        void Apply(PanelCommand command)
        {
            services.Panel?.Apply(command);
            lastRendered = "";
        }

        void TogglePause()
        {
            var paused = services.SimulationRuntime.PauseRequested ||
                         services.Room != null && services.Room.Recovery.ResumeRequired;
            Apply(new PanelCommand(paused ? PanelCommandKind.Resume : PanelCommandKind.Pause));
        }

        void PrimaryAction()
        {
            if (!PrimaryActionEnabled())
                return;
            var session = services.Session;
            switch (session.State)
            {
                case ShellSessionState.Library:
                    if (session.SelectedIndex < 0 && session.Cards.Count > 0)
                        session.SelectCard(0);
                    break;
                case ShellSessionState.StartReady:
                    session.RequestStart();
                    services.Room?.BeginSelectedEpisode();
                    break;
                default:
                    services.Room?.PrimaryAction();
                    break;
            }
            lastRendered = "";
        }

        bool PrimaryActionEnabled()
        {
            if (services.Session.State == ShellSessionState.SetupAccepted || services.Room == null)
                return services.Session.State != ShellSessionState.SetupAccepted;
            return services.Room.Preparation.Status != Room.RoomPreparationStatus.PermissionRequested &&
                   services.Room.Preparation.Status != Room.RoomPreparationStatus.SpaceSetupRequested &&
                   services.Room.Preparation.Status != Room.RoomPreparationStatus.SceneLoading;
        }

        string PrimaryActionLabel()
        {
            switch (services.Session.State)
            {
                case ShellSessionState.Library:
                    return "Select episode";
                case ShellSessionState.StartReady:
                    return "Prepare room";
                case ShellSessionState.SetupAccepted:
                    return "Setup accepted";
            }
            switch (services.Room.Preparation.Status)
            {
                case Room.RoomPreparationStatus.Explanation:
                    return "Allow scene access";
                case Room.RoomPreparationStatus.PermissionRequested:
                    return "Waiting for permission";
                case Room.RoomPreparationStatus.SpaceSetupReady:
                    return "Start Space Setup";
                case Room.RoomPreparationStatus.SpaceSetupRequested:
                    return "Complete Space Setup";
                case Room.RoomPreparationStatus.SceneLoading:
                    return "Loading room";
                case Room.RoomPreparationStatus.AwaitingConfirmation:
                    return "Confirm room";
                case Room.RoomPreparationStatus.Confirmed:
                    return "Accept setup";
                case Room.RoomPreparationStatus.Restricted:
                    return "Retry room setup";
                default:
                    return "Continue";
            }
        }
    }
}
