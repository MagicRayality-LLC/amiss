using System;
using System.Collections.Generic;
using Amiss.Contracts;

namespace Amiss.Shell.Fixtures
{
    /// <summary>
    /// Framework contract fixtures: the non-product fake episode declaration
    /// that mirrors simulation/fake_episode.py, plus its asset resolution.
    /// These exist to prove the shell library and load gate without shipping
    /// any experiment content; the card text states that explicitly. They are
    /// removed from the product surface when the real episode lands.
    /// </summary>
    public static class FrameworkContractFixtures
    {
        public const string FakeDiscreteEpisodeId = "fake-discrete";
        public const string FakeDiscreteAssetId = "fake-discrete-asset";

        /// <summary>
        /// Bundled, gate-passing contract fixture mirroring the Python fake
        /// episode: compact bench, decidable 1.0 m3 minimum, no required
        /// capabilities beyond the world-anchored panel.
        /// </summary>
        public static EpisodeDeclaration FakeDiscrete() => new()
        {
            EpisodeId = FakeDiscreteEpisodeId,
            EpisodeModuleVersion = "1.0.0",
            FrameworkApiMin = "1.0.0",
            FrameworkApiMax = "1.0.0",
            RequiredCapabilities = new[]
            {
                new RequiredCapability
                {
                    CapabilityId = ShellCapabilityIds.PanelWorldAnchored,
                    MinimumShellVersion = "1.0.0"
                }
            },
            SceneForm = "compact_bench",
            MinimumVolumeM3 = 1.0,
            BelowMinimum = BelowMinimumBehavior.RefuseWithExplanation,
            RequiredInputBaseline = "controller-or-hand",
            ExpectedDurationMinutes = 5,
            CardQuestion =
                "Framework contract fixture (not product content): does the shell " +
                "library and episode load gate work?",
            CardActionType = "Shell self-test",
            CompletionStageId = "fake-discrete-complete",
            Availability = EpisodeAvailability.Bundled,
            Assets = new[] { AssetManifestEntry.Critical(FakeDiscreteAssetId) },
            HistoryNotes = "Non-product contract fixture.",
            ApparatusSimplifications = "Discrete step only; no continuous evolution.",
            ScaleTransforms = "None.",
            ReadableSaveVersions = new[]
            {
                new SaveCompatibility
                {
                    SaveFormatVersion = FrameworkApiVersions.SaveFormat,
                    OldTrialHandling = OldTrialHandling.Current
                }
            }
        };

        /// <summary>Resolves stable asset ids declared by the fixtures.</summary>
        public static bool ResolveCriticalAsset(string assetId) =>
            assetId == FakeDiscreteAssetId;

        public sealed class DeclarationSource : IEpisodeDeclarationSource
        {
            public IReadOnlyList<EpisodeDeclaration> Declarations { get; } =
                new[] { FakeDiscrete() };
        }
    }
}
