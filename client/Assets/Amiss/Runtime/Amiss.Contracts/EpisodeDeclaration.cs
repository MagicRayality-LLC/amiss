using System;
using System.Collections.Generic;

namespace Amiss.Contracts
{
    public enum EpisodeAvailability
    {
        Bundled,
        NotInstalled,
        Locked
    }

    public enum OldTrialHandling
    {
        Current,
        MigratableRecompute,
        ReadOnlyRetain,
        ReplayUnsupported
    }

    public enum BelowMinimumBehavior
    {
        RefuseWithExplanation,
        NamedRestrictedSetup
    }

    public sealed class SaveCompatibility
    {
        public string SaveFormatVersion { get; init; } = "";
        public OldTrialHandling OldTrialHandling { get; init; } = OldTrialHandling.Current;
        public string MigrationNote { get; init; } = "";
    }

    public sealed class AssetManifestEntry
    {
        public string AssetId { get; init; } = "";
        public bool ExperimentCritical { get; init; }

        /// <summary>Decorative assets may degrade; experiment-critical assets must resolve.</summary>
        public static AssetManifestEntry Critical(string id) => new() { AssetId = id, ExperimentCritical = true };
    }

    public sealed class RequiredCapability
    {
        public string CapabilityId { get; init; } = "";
        public string MinimumShellVersion { get; init; } = "1.0.0";
    }

    /// <summary>
    /// Immutable episode declaration the shell reads at the load gate. The shell
    /// never edits a declaration, and a declaration that fails the gate is never
    /// repaired with defaults: it stays not runnable with readable reasons.
    /// The declaration carries no conclusion text; the library card projection
    /// has no field that could hold one.
    /// </summary>
    public sealed class EpisodeDeclaration
    {
        public string EpisodeId { get; init; } = "";
        public string EpisodeModuleVersion { get; init; } = "";

        // Compatibility: the episode must cover the shell's framework API version.
        public string FrameworkApiMin { get; init; } = "";
        public string FrameworkApiMax { get; init; } = "";

        public IReadOnlyList<RequiredCapability> RequiredCapabilities { get; init; }
            = Array.Empty<RequiredCapability>();

        // Space and input.
        public string SceneForm { get; init; } = "";
        /// <summary>Minimum experiment volume in cubic meters. Null or non-positive is undecidable.</summary>
        public double? MinimumVolumeM3 { get; init; }
        public BelowMinimumBehavior BelowMinimum { get; init; } = BelowMinimumBehavior.RefuseWithExplanation;
        public string NamedRestrictedSetupId { get; init; } = "";
        public string RequiredInputBaseline { get; init; } = "";
        public int ExpectedDurationMinutes { get; init; }

        // Library card source data. The one-sentence question is the card text;
        // no conclusion field exists on this type.
        public string CardQuestion { get; init; } = "";
        public string CardActionType { get; init; } = "";
        public string CompletionStageId { get; init; } = "";

        // Availability. Mandatory even while the delivery form is undecided.
        public EpisodeAvailability Availability { get; init; } = EpisodeAvailability.Bundled;
        public string AvailabilityReason { get; init; } = "";

        public IReadOnlyList<AssetManifestEntry> Assets { get; init; }
            = Array.Empty<AssetManifestEntry>();

        // History and scale boundary, presented with declaration and Trial cards.
        public string HistoryNotes { get; init; } = "";
        public string ApparatusSimplifications { get; init; } = "";
        public string ScaleTransforms { get; init; } = "";

        public IReadOnlyList<SaveCompatibility> ReadableSaveVersions { get; init; }
            = Array.Empty<SaveCompatibility>();
    }
}
