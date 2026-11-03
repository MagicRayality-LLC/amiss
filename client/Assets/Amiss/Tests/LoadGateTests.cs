using System.Collections.Generic;
using System.Linq;
using Amiss.Contracts;
using Amiss.Shell;
using Amiss.Shell.Episodes;
using NUnit.Framework;

namespace Amiss.Tests
{
    public static class DeclarationBuilders
    {
        public static EpisodeDeclaration Runnable() => new()
        {
            EpisodeId = "runnable-episode",
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
            RequiredInputBaseline = "controller-or-hand",
            ExpectedDurationMinutes = 5,
            CardQuestion = "Does the gate pass?",
            CardActionType = "Measure",
            CompletionStageId = "done",
            Availability = EpisodeAvailability.Bundled,
            ReadableSaveVersions = new[]
            {
                new SaveCompatibility
                {
                    SaveFormatVersion = FrameworkApiVersions.SaveFormat,
                    OldTrialHandling = OldTrialHandling.Current
                }
            }
        };

        public static bool ResolveEverything(string assetId) => true;
        public static bool ResolveNothing(string assetId) => false;

        /// <summary>A complete, gate-passing declaration differing only in availability.</summary>
        public static EpisodeDeclaration WithAvailability(
            EpisodeAvailability availability, string reason)
        {
            var b = Runnable();
            return new EpisodeDeclaration
            {
                EpisodeId = b.EpisodeId,
                EpisodeModuleVersion = b.EpisodeModuleVersion,
                FrameworkApiMin = b.FrameworkApiMin,
                FrameworkApiMax = b.FrameworkApiMax,
                RequiredCapabilities = b.RequiredCapabilities,
                SceneForm = b.SceneForm,
                MinimumVolumeM3 = b.MinimumVolumeM3,
                RequiredInputBaseline = b.RequiredInputBaseline,
                ExpectedDurationMinutes = b.ExpectedDurationMinutes,
                CardQuestion = b.CardQuestion,
                CardActionType = b.CardActionType,
                CompletionStageId = b.CompletionStageId,
                Availability = availability,
                AvailabilityReason = reason,
                Assets = b.Assets,
                ReadableSaveVersions = b.ReadableSaveVersions
            };
        }

        public sealed class SingleDeclarationSource : IEpisodeDeclarationSource
        {
            public SingleDeclarationSource(EpisodeDeclaration declaration) =>
                Declarations = new[] { declaration };

            public IReadOnlyList<EpisodeDeclaration> Declarations { get; }
        }
    }

    public class CapabilityManifestTests
    {
        [Test]
        public void RegistryContainsTheCanonicalIdSet()
        {
            Assert.AreEqual(13, ShellCapabilityIds.All.Count);
            Assert.That(ShellCapabilityIds.All.Distinct().Count(), Is.EqualTo(13));
            Assert.IsTrue(ShellCapabilityIds.IsKnownId("scene.room_collision"));
            Assert.IsTrue(ShellCapabilityIds.IsKnownId("persist.generations"));
            Assert.IsFalse(ShellCapabilityIds.IsKnownId("scene.room_collision_v2"));
        }

        [Test]
        public void ThisBuildPublishesOnlyTheWorldAnchoredPanel()
        {
            var manifest = ShellCapabilityManifest.ThisBuild();
            Assert.AreEqual(1, manifest.Capabilities.Count);
            Assert.AreEqual(ShellCapabilityIds.PanelWorldAnchored,
                manifest.Capabilities[0].CapabilityId);
            Assert.AreEqual("1.0.0", manifest.Capabilities[0].ShellVersion);
        }

        [Test]
        public void ManifestVersionTracksTheFrameworkApi()
        {
            Assert.AreEqual(FrameworkApiVersions.FrameworkApi,
                ShellCapabilityManifest.ThisBuild().ManifestVersion);
        }
    }

    public class LoadGateTests
    {
        [Test]
        public void RunnableDeclarationPasses()
        {
            var verdict = EpisodeLoadGate.Evaluate(
                DeclarationBuilders.Runnable(),
                ShellCapabilityManifest.ThisBuild(),
                DeclarationBuilders.ResolveEverything);
            Assert.IsTrue(verdict.Runnable);
            Assert.IsFalse(verdict.AvailabilityBlocked);
            Assert.That(verdict.Rejections, Is.Empty);
        }

        [Test]
        public void CompatibilityFailureIsReportedBeforeCapabilityFailure()
        {
            var declaration = DeclarationBuilders.Runnable();
            declaration = new EpisodeDeclaration
            {
                EpisodeId = declaration.EpisodeId,
                EpisodeModuleVersion = declaration.EpisodeModuleVersion,
                FrameworkApiMin = "99.0.0",
                FrameworkApiMax = "99.0.0",
                RequiredCapabilities = new[]
                {
                    new RequiredCapability
                    {
                        CapabilityId = ShellCapabilityIds.SimBatchRun,
                        MinimumShellVersion = "1.0.0"
                    }
                },
                SceneForm = declaration.SceneForm,
                MinimumVolumeM3 = declaration.MinimumVolumeM3,
                CardQuestion = declaration.CardQuestion,
                CardActionType = declaration.CardActionType,
                Availability = declaration.Availability,
                ReadableSaveVersions = declaration.ReadableSaveVersions
            };

            var verdict = EpisodeLoadGate.Evaluate(declaration,
                ShellCapabilityManifest.ThisBuild(), DeclarationBuilders.ResolveEverything);

            Assert.IsFalse(verdict.Runnable);
            Assert.That(verdict.Rejections, Is.Not.Empty);
            Assert.AreEqual(LoadGateStage.Compatibility, verdict.Rejections[0].Stage);
            Assert.AreEqual("shell_framework_too_old", verdict.Rejections[0].ReasonCategory);
        }

        [Test]
        public void UnknownCapabilityIdIsNotRunnable()
        {
            var declaration = DeclarationBuilders.Runnable();
            var verdict = EpisodeLoadGate.Evaluate(new EpisodeDeclaration
            {
                EpisodeId = declaration.EpisodeId,
                EpisodeModuleVersion = declaration.EpisodeModuleVersion,
                FrameworkApiMin = declaration.FrameworkApiMin,
                FrameworkApiMax = declaration.FrameworkApiMax,
                RequiredCapabilities = new[]
                {
                    new RequiredCapability
                    {
                        CapabilityId = "sim.time_travel",
                        MinimumShellVersion = "1.0.0"
                    }
                },
                SceneForm = declaration.SceneForm,
                MinimumVolumeM3 = declaration.MinimumVolumeM3,
                Availability = EpisodeAvailability.Bundled,
                ReadableSaveVersions = declaration.ReadableSaveVersions
            }, ShellCapabilityManifest.ThisBuild(), DeclarationBuilders.ResolveEverything);

            Assert.IsFalse(verdict.Runnable);
            CollectionAssert.AreEqual(
                new[] { LoadGateStage.Capabilities },
                verdict.Rejections.Select(r => r.Stage));
            Assert.AreEqual("unknown_capability_id", verdict.Rejections[0].ReasonCategory);
        }

        [Test]
        public void MissingCapabilityIsNamedInTheRejection()
        {
            var declaration = DeclarationBuilders.Runnable();
            var verdict = EpisodeLoadGate.Evaluate(new EpisodeDeclaration
            {
                EpisodeId = declaration.EpisodeId,
                EpisodeModuleVersion = declaration.EpisodeModuleVersion,
                FrameworkApiMin = declaration.FrameworkApiMin,
                FrameworkApiMax = declaration.FrameworkApiMax,
                RequiredCapabilities = new[]
                {
                    new RequiredCapability
                    {
                        CapabilityId = ShellCapabilityIds.SceneRoomCollision,
                        MinimumShellVersion = "1.0.0"
                    }
                },
                SceneForm = declaration.SceneForm,
                MinimumVolumeM3 = declaration.MinimumVolumeM3,
                Availability = EpisodeAvailability.Bundled,
                ReadableSaveVersions = declaration.ReadableSaveVersions
            }, ShellCapabilityManifest.ThisBuild(), DeclarationBuilders.ResolveEverything);

            Assert.IsFalse(verdict.Runnable);
            Assert.AreEqual("missing_capability", verdict.Rejections[0].ReasonCategory);
            StringAssert.Contains("scene.room_collision", verdict.Rejections[0].ReadableReason);
        }

        [Test]
        public void UndecidableMinimumVolumeIsNotRunnable()
        {
            var declaration = DeclarationBuilders.Runnable();
            var withNoVolume = new EpisodeDeclaration
            {
                EpisodeId = declaration.EpisodeId,
                EpisodeModuleVersion = declaration.EpisodeModuleVersion,
                FrameworkApiMin = declaration.FrameworkApiMin,
                FrameworkApiMax = declaration.FrameworkApiMax,
                SceneForm = declaration.SceneForm,
                MinimumVolumeM3 = null,
                Availability = EpisodeAvailability.Bundled,
                ReadableSaveVersions = declaration.ReadableSaveVersions
            };
            var verdict = EpisodeLoadGate.Evaluate(withNoVolume,
                ShellCapabilityManifest.ThisBuild(), DeclarationBuilders.ResolveEverything);

            Assert.IsFalse(verdict.Runnable);
            Assert.AreEqual("minimum_volume_missing", verdict.Rejections[0].ReasonCategory);
        }

        [Test]
        public void MissingCriticalAssetIsNotRunnable()
        {
            var declaration = DeclarationBuilders.Runnable();
            var withCriticalAsset = new EpisodeDeclaration
            {
                EpisodeId = declaration.EpisodeId,
                EpisodeModuleVersion = declaration.EpisodeModuleVersion,
                FrameworkApiMin = declaration.FrameworkApiMin,
                FrameworkApiMax = declaration.FrameworkApiMax,
                SceneForm = declaration.SceneForm,
                MinimumVolumeM3 = declaration.MinimumVolumeM3,
                Availability = EpisodeAvailability.Bundled,
                Assets = new[] { AssetManifestEntry.Critical("ball-set") },
                ReadableSaveVersions = declaration.ReadableSaveVersions
            };
            var verdict = EpisodeLoadGate.Evaluate(withCriticalAsset,
                ShellCapabilityManifest.ThisBuild(), DeclarationBuilders.ResolveNothing);

            Assert.IsFalse(verdict.Runnable);
            Assert.AreEqual("critical_asset_missing", verdict.Rejections[0].ReasonCategory);
        }

        [Test]
        public void DecorativeAssetMayStayUnresolved()
        {
            var declaration = DeclarationBuilders.Runnable();
            var withDecorativeAsset = new EpisodeDeclaration
            {
                EpisodeId = declaration.EpisodeId,
                EpisodeModuleVersion = declaration.EpisodeModuleVersion,
                FrameworkApiMin = declaration.FrameworkApiMin,
                FrameworkApiMax = declaration.FrameworkApiMax,
                RequiredCapabilities = declaration.RequiredCapabilities,
                SceneForm = declaration.SceneForm,
                MinimumVolumeM3 = declaration.MinimumVolumeM3,
                Availability = EpisodeAvailability.Bundled,
                Assets = new[] { new AssetManifestEntry { AssetId = "poster", ExperimentCritical = false } },
                ReadableSaveVersions = declaration.ReadableSaveVersions
            };
            var verdict = EpisodeLoadGate.Evaluate(withDecorativeAsset,
                ShellCapabilityManifest.ThisBuild(), DeclarationBuilders.ResolveNothing);

            Assert.IsTrue(verdict.Runnable);
        }

        [Test]
        public void SaveVersionWithoutClassificationIsRejected()
        {
            var declaration = DeclarationBuilders.Runnable();
            var verdict = EpisodeLoadGate.Evaluate(new EpisodeDeclaration
            {
                EpisodeId = declaration.EpisodeId,
                EpisodeModuleVersion = declaration.EpisodeModuleVersion,
                FrameworkApiMin = declaration.FrameworkApiMin,
                FrameworkApiMax = declaration.FrameworkApiMax,
                RequiredCapabilities = declaration.RequiredCapabilities,
                SceneForm = declaration.SceneForm,
                MinimumVolumeM3 = declaration.MinimumVolumeM3,
                Availability = EpisodeAvailability.Bundled,
                ReadableSaveVersions = new[]
                {
                    new SaveCompatibility
                    {
                        SaveFormatVersion = "0.9.0",
                        OldTrialHandling = OldTrialHandling.MigratableRecompute,
                        MigrationNote = ""
                    }
                }
            }, ShellCapabilityManifest.ThisBuild(), DeclarationBuilders.ResolveEverything);

            Assert.IsFalse(verdict.Runnable);
            Assert.AreEqual("save_note_missing", verdict.Rejections[0].ReasonCategory);
        }

        [Test]
        public void LockedEpisodeIsAdmissibleButCannotStart()
        {
            var verdict = EpisodeLoadGate.Evaluate(
                DeclarationBuilders.WithAvailability(
                    EpisodeAvailability.Locked, "Requires a not yet released content pack."),
                ShellCapabilityManifest.ThisBuild(), DeclarationBuilders.ResolveEverything);

            Assert.IsFalse(verdict.Runnable);
            Assert.IsTrue(verdict.AvailabilityBlocked);
            Assert.AreEqual(LoadGateStage.Availability, verdict.Rejections[0].Stage);
            Assert.AreEqual("locked", verdict.Rejections[0].ReasonCategory);
            StringAssert.Contains("content pack", verdict.AvailabilityReason);
        }

        [Test]
        public void NotInstalledEpisodeReportsItsReason()
        {
            var verdict = EpisodeLoadGate.Evaluate(
                DeclarationBuilders.WithAvailability(
                    EpisodeAvailability.NotInstalled, "Install it from the store."),
                ShellCapabilityManifest.ThisBuild(), DeclarationBuilders.ResolveEverything);

            Assert.IsTrue(verdict.AvailabilityBlocked);
            Assert.AreEqual("not_installed", verdict.Rejections[0].ReasonCategory);
        }

        [Test]
        public void InvalidVersionStringsAreRejectedNotGuessed()
        {
            var declaration = DeclarationBuilders.Runnable();
            var verdict = EpisodeLoadGate.Evaluate(new EpisodeDeclaration
            {
                EpisodeId = declaration.EpisodeId,
                EpisodeModuleVersion = declaration.EpisodeModuleVersion,
                FrameworkApiMin = "one.two.three",
                FrameworkApiMax = "1.0.0",
                SceneForm = declaration.SceneForm,
                MinimumVolumeM3 = declaration.MinimumVolumeM3,
                Availability = EpisodeAvailability.Bundled,
                ReadableSaveVersions = declaration.ReadableSaveVersions
            }, ShellCapabilityManifest.ThisBuild(), DeclarationBuilders.ResolveEverything);

            Assert.IsFalse(verdict.Runnable);
            Assert.AreEqual("framework_api_range_invalid", verdict.Rejections[0].ReasonCategory);
        }
    }

    public class TemperatureEpisodeLoadGateTests
    {
        static bool ResolvesTemperatureCriticalAssets(string assetId) =>
            WhatIsTemperatureEpisode.ResolveCriticalAsset(assetId);

        static EpisodeDeclaration With(
            EpisodeDeclaration declaration,
            IReadOnlyList<RequiredCapability> capabilities = null,
            EpisodeAvailability? availability = null,
            string availabilityReason = null,
            bool dropMinimumVolume = false) => new()
        {
            EpisodeId = declaration.EpisodeId,
            EpisodeModuleVersion = declaration.EpisodeModuleVersion,
            FrameworkApiMin = declaration.FrameworkApiMin,
            FrameworkApiMax = declaration.FrameworkApiMax,
            RequiredCapabilities = capabilities ?? declaration.RequiredCapabilities,
            SceneForm = declaration.SceneForm,
            MinimumVolumeM3 = dropMinimumVolume ? null : declaration.MinimumVolumeM3,
            BelowMinimum = declaration.BelowMinimum,
            NamedRestrictedSetupId = declaration.NamedRestrictedSetupId,
            RequiredInputBaseline = declaration.RequiredInputBaseline,
            ExpectedDurationMinutes = declaration.ExpectedDurationMinutes,
            CardQuestion = declaration.CardQuestion,
            CardActionType = declaration.CardActionType,
            CompletionStageId = declaration.CompletionStageId,
            Availability = availability ?? declaration.Availability,
            AvailabilityReason = availabilityReason ?? declaration.AvailabilityReason,
            Assets = declaration.Assets,
            HistoryNotes = declaration.HistoryNotes,
            ApparatusSimplifications = declaration.ApparatusSimplifications,
            ScaleTransforms = declaration.ScaleTransforms,
            ReadableSaveVersions = declaration.ReadableSaveVersions
        };

        [Test]
        public void TheBundledTemperatureDeclarationPassesThisBuildGate()
        {
            var verdict = EpisodeLoadGate.Evaluate(
                WhatIsTemperatureEpisode.Declaration(),
                ShellCapabilityManifest.ThisBuild(),
                ResolvesTemperatureCriticalAssets);

            Assert.IsTrue(verdict.Runnable,
                string.Join("; ", verdict.Rejections.Select(r => r.ReadableReason)));
            Assert.IsFalse(verdict.AvailabilityBlocked);
            Assert.That(verdict.Rejections, Is.Empty);
        }

        [Test]
        public void TheTemperatureDeclarationCarriesTheApprovedCardBoundary()
        {
            var declaration = WhatIsTemperatureEpisode.Declaration();

            Assert.AreEqual("what_is_temperature", declaration.EpisodeId);
            Assert.AreEqual(EpisodeAvailability.Bundled, declaration.Availability);
            Assert.AreEqual("full_room", declaration.SceneForm);
            Assert.AreEqual(3.0, declaration.MinimumVolumeM3);
            Assert.AreEqual(BelowMinimumBehavior.RefuseWithExplanation, declaration.BelowMinimum);
            Assert.AreEqual(15, declaration.ExpectedDurationMinutes);
            Assert.AreEqual("stage-7-transfer", declaration.CompletionStageId);
            Assert.That(declaration.CardQuestion, Does.Not.Contain("because"));
            Assert.That(declaration.RequiredCapabilities.Select(c => c.CapabilityId),
                Is.EquivalentTo(new[] { ShellCapabilityIds.PanelWorldAnchored }));
            Assert.AreEqual(7, declaration.Assets.Count);
            Assert.AreEqual(5, WhatIsTemperatureEpisode.CriticalAssetIds.Count);
        }

        [Test]
        public void AMissingTemperatureCriticalAssetRefusesTheDeclaration()
        {
            var verdict = EpisodeLoadGate.Evaluate(
                WhatIsTemperatureEpisode.Declaration(),
                ShellCapabilityManifest.ThisBuild(),
                DeclarationBuilders.ResolveNothing);

            Assert.IsFalse(verdict.Runnable);
            Assert.AreEqual(LoadGateStage.Assets, verdict.Rejections[0].Stage);
            Assert.AreEqual("critical_asset_missing", verdict.Rejections[0].ReasonCategory);
        }

        [Test]
        public void AnUndecidableTemperatureMinimumVolumeIsNotRunnable()
        {
            var verdict = EpisodeLoadGate.Evaluate(
                With(WhatIsTemperatureEpisode.Declaration(), dropMinimumVolume: true),
                ShellCapabilityManifest.ThisBuild(),
                ResolvesTemperatureCriticalAssets);

            Assert.IsFalse(verdict.Runnable);
            Assert.AreEqual("minimum_volume_missing", verdict.Rejections[0].ReasonCategory);
        }

        [Test]
        public void AnUnknownTemperatureCapabilityIsNotRunnable()
        {
            var verdict = EpisodeLoadGate.Evaluate(
                With(WhatIsTemperatureEpisode.Declaration(), capabilities: new[]
                {
                    new RequiredCapability
                    {
                        CapabilityId = "sim.time_travel",
                        MinimumShellVersion = "1.0.0"
                    }
                }),
                ShellCapabilityManifest.ThisBuild(),
                ResolvesTemperatureCriticalAssets);

            Assert.IsFalse(verdict.Runnable);
            Assert.AreEqual("unknown_capability_id", verdict.Rejections[0].ReasonCategory);
        }

        [Test]
        public void ANonBundledTemperatureDeclarationIsBlockedNotStarted()
        {
            var verdict = EpisodeLoadGate.Evaluate(
                With(WhatIsTemperatureEpisode.Declaration(),
                    availability: EpisodeAvailability.NotInstalled,
                    availabilityReason: "Install it from the store."),
                ShellCapabilityManifest.ThisBuild(),
                ResolvesTemperatureCriticalAssets);

            Assert.IsFalse(verdict.Runnable);
            Assert.IsTrue(verdict.AvailabilityBlocked);
            Assert.AreEqual("not_installed", verdict.Rejections[0].ReasonCategory);
        }
    }
}