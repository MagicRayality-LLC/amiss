using System.Collections.Generic;
using Amiss.Contracts;

namespace Amiss.Shell.Episodes
{
    /// <summary>
    /// The bundled what_is_temperature episode declaration from approved
    /// design 07 section 1.1. The declaration carries the question, space,
    /// duration, action, completion, availability, stable asset ids, and the
    /// scale boundary; no formula or conclusion field exists. Values that 07
    /// section 11 leaves to a room/playability measurement stay declared
    /// candidates, not measured results.
    /// </summary>
    public static class WhatIsTemperatureEpisode
    {
        public const string EpisodeId = "what_is_temperature";
        public const string CompletionStageId = "stage-7-transfer";
        public const string OpeningGoal =
            "Explore what determines the temperature of a group of balls. " +
            "Use the bat to change their speeds, watch the temperature, and ask questions at any time.";

        // Stable ids for the enabled P0 scope. The racket and glove belong to
        // P1, so they stay decorative and cannot change P0 meaning when absent.
        public const string BatAssetId = "what-is-temperature-bat";
        public const string BallMaterialAssetId = "what-is-temperature-ball-material";
        public const string CollisionSoundAssetId = "what-is-temperature-collision-sound";
        public const string SandbagAssetId = "what-is-temperature-sandbag";
        public const string SandbagDispersalSoundAssetId =
            "what-is-temperature-sandbag-dispersal-sound";
        public const string RacketAssetId = "what-is-temperature-racket";
        public const string GloveAssetId = "what-is-temperature-glove";

        public static readonly IReadOnlyList<string> CriticalAssetIds = new[]
        {
            BatAssetId,
            BallMaterialAssetId,
            CollisionSoundAssetId,
            SandbagAssetId,
            SandbagDispersalSoundAssetId
        };

        /// <summary>
        /// The product library. One bundled episode is one card. The framework
        /// contract fixture stays off this surface.
        /// </summary>
        public sealed class DeclarationSource : IEpisodeDeclarationSource
        {
            public IReadOnlyList<EpisodeDeclaration> Declarations { get; } =
                new[] { Declaration() };
        }

        public static bool ResolveCriticalAsset(string assetId)
        {
            foreach (var id in CriticalAssetIds)
            {
                if (id == assetId)
                    return true;
            }
            return false;
        }

        public static EpisodeDeclaration Declaration() => new()
        {
            EpisodeId = EpisodeId,
            EpisodeModuleVersion = "1.0.0",
            FrameworkApiMin = "1.0.0",
            FrameworkApiMax = "1.0.0",

            // 07 section 1.1 admits only the capabilities and assets the
            // current build needs and publishes. The room, input, chart, and
            // speaker build steps publish their capability ids when they land;
            // declaring them now would make this real episode unrunnable.
            RequiredCapabilities = new[]
            {
                new RequiredCapability
                {
                    CapabilityId = ShellCapabilityIds.PanelWorldAnchored,
                    MinimumShellVersion = "1.0.0"
                }
            },

            SceneForm = "full_room",
            // 07 section 1.1 candidate V_min, unlocked by section 11.
            MinimumVolumeM3 = 3.0,
            BelowMinimum = BelowMinimumBehavior.RefuseWithExplanation,
            RequiredInputBaseline = "hand-and-controller-6dof",
            // The approved card range is 10-20 minutes; this is its midpoint.
            ExpectedDurationMinutes = 15,

            CardQuestion = "What determines the temperature of a group of balls?",
            CardActionType = "Swing, throw, and predict",
            CompletionStageId = CompletionStageId,
            Availability = EpisodeAvailability.Bundled,

            Assets = new[]
            {
                AssetManifestEntry.Critical(BatAssetId),
                AssetManifestEntry.Critical(BallMaterialAssetId),
                AssetManifestEntry.Critical(CollisionSoundAssetId),
                AssetManifestEntry.Critical(SandbagAssetId),
                AssetManifestEntry.Critical(SandbagDispersalSoundAssetId),
                new AssetManifestEntry { AssetId = RacketAssetId, ExperimentCritical = false },
                new AssetManifestEntry { AssetId = GloveAssetId, ExperimentCritical = false }
            },

            HistoryNotes = "No historical material is presented with this episode.",
            ApparatusSimplifications =
                "Model balls with enlarged dynamics; static surfaces do no work and " +
                "human bodies are not collision surfaces.",
            ScaleTransforms =
                "Model temperature, not room-temperature measurement; ball sizes and " +
                "speeds are enlarged and do not represent actual air-molecule " +
                "dimensions or speeds.",

            ReadableSaveVersions = new[]
            {
                new SaveCompatibility
                {
                    SaveFormatVersion = FrameworkApiVersions.SaveFormat,
                    OldTrialHandling = OldTrialHandling.Current
                }
            }
        };
    }
}
