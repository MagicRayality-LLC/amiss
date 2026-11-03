using System;
using System.Collections.Generic;
using Amiss.Contracts;

namespace Amiss.Shell
{
    public enum ShellSessionState
    {
        Library,
        CardSelected,
        StartBlocked,
        StartReady,
        RoomPreparation,
        RoomConfirmed,
        SetupAccepted,
        Restricted,
        MountFailed
    }

    /// <summary>
    /// Library-first session state machine. Application entry is always the
    /// library: no spatial permission, room setup, microphone, or Trial
    /// starts before the user selects a runnable card. Non-bundled cards
    /// stay visible with a readable reason and cannot start. A card
    /// selection never accepts a proposed Trial setup; that is a later,
    /// separate user action.
    /// </summary>
    public sealed class HeadsetShellSession
    {
        readonly IEpisodeDeclarationSource declarations;
        readonly ShellCapabilityManifest capabilities;
        readonly EpisodeModuleHost episodeHost;
        readonly Func<string, bool> criticalAssetResolver;

        public HeadsetShellSession(
            IEpisodeDeclarationSource declarationSource,
            ShellCapabilityManifest shellCapabilities,
            EpisodeModuleHost episodeHost,
            Func<string, bool> criticalAssetResolver)
        {
            this.declarations = declarationSource
                ?? throw new ArgumentNullException(nameof(declarationSource));
            this.capabilities = shellCapabilities
                ?? throw new ArgumentNullException(nameof(shellCapabilities));
            this.episodeHost = episodeHost ?? throw new ArgumentNullException(nameof(episodeHost));
            this.criticalAssetResolver = criticalAssetResolver
                ?? throw new ArgumentNullException(nameof(criticalAssetResolver));
        }

        public ShellSessionState State { get; private set; } = ShellSessionState.Library;
        public IReadOnlyList<EpisodeDeclaration> Cards => declarations.Declarations;
        public int SelectedIndex { get; private set; } = -1;
        public EpisodeDeclaration Selected { get; private set; }
        public LoadVerdict SelectedVerdict { get; private set; }
        public string MountFailureReason { get; private set; } = "";

        /// <summary>
        /// Selects a card and runs the load gate before any episode code is
        /// considered. The gate verdict decides the reachable next state.
        /// </summary>
        public void SelectCard(int index)
        {
            var cards = declarations.Declarations;
            if (index < 0 || index >= cards.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            SelectedIndex = index;
            Selected = cards[index];
            SelectedVerdict = EpisodeLoadGate.Evaluate(Selected, capabilities, criticalAssetResolver);
            MountFailureReason = "";

            if (!SelectedVerdict.Runnable)
                State = ShellSessionState.StartBlocked;
            else
                State = ShellSessionState.StartReady;
        }

        public void RequestStart()
        {
            if (State != ShellSessionState.StartReady || SelectedVerdict is not { Runnable: true })
                return;
            State = ShellSessionState.RoomPreparation;
        }

        public void MarkRestricted()
        {
            if (State == ShellSessionState.RoomPreparation)
                State = ShellSessionState.Restricted;
        }

        public void MarkRoomPreparation()
        {
            if (State != ShellSessionState.Library && State != ShellSessionState.StartBlocked)
                State = ShellSessionState.RoomPreparation;
        }

        public void MarkRoomConfirmed()
        {
            if (State == ShellSessionState.RoomPreparation || State == ShellSessionState.Restricted)
                State = ShellSessionState.RoomConfirmed;
        }

        public void MarkSetupAccepted()
        {
            if (State == ShellSessionState.RoomConfirmed)
                State = ShellSessionState.SetupAccepted;
        }

        /// <summary>
        /// A finished trial returns to the confirmed room, not to the library:
        /// the same room can start another trial without re-confirming it.
        /// </summary>
        public void MarkTrialStopped()
        {
            if (State == ShellSessionState.SetupAccepted)
                State = ShellSessionState.RoomConfirmed;
        }

        /// <summary>
        /// Returns to the library. Any mounted episode must unload with an
        /// empty residue checklist before the library state is restored.
        /// </summary>
        public void ReturnToLibrary()
        {
            var result = episodeHost.Unmount();
            if (!result.Success)
            {
                MountFailureReason = result.ReadableReason;
                State = ShellSessionState.MountFailed;
                return;
            }

            State = ShellSessionState.Library;
            SelectedIndex = -1;
            Selected = null;
            SelectedVerdict = null;
            MountFailureReason = "";
        }
    }

    /// <summary>
    /// Source of episode declarations shown in the library. Implementations
    /// must not present duplicate episode ids; the shell reads declarations
    /// and never edits them.
    /// </summary>
    public interface IEpisodeDeclarationSource
    {
        IReadOnlyList<EpisodeDeclaration> Declarations { get; }
    }
}
