using System;
using System.Collections.Generic;

namespace Amiss.Contracts
{
    public enum LoadGateStage
    {
        Compatibility = 0,
        Capabilities = 1,
        SpaceDecidability = 2,
        Assets = 3,
        SaveVersions = 4,
        Availability = 5
    }

    public sealed class LoadRejection
    {
        public LoadGateStage Stage { get; init; }
        public string Field { get; init; } = "";
        public string ReasonCategory { get; init; } = "";
        public string ReadableReason { get; init; } = "";
    }

    /// <summary>
    /// Result of the episode load gate. Runnable means the declaration is
    /// admissible and mounted episode code may load; not runnable always
    /// carries at least one readable reason. There is no load-with-checks-
    /// disabled path.
    /// </summary>
    public sealed class LoadVerdict
    {
        public bool Runnable { get; init; }
        public IReadOnlyList<LoadRejection> Rejections { get; init; }
            = Array.Empty<LoadRejection>();
        /// <summary>True when the declaration is admissible but not startable
        /// because availability is not bundled; the card stays visible with
        /// its reason instead.</summary>
        public bool AvailabilityBlocked { get; init; }
        public string AvailabilityReason { get; init; } = "";
    }

    /// <summary>
    /// Pure checks over an episode declaration, the shell capability manifest,
    /// and critical asset resolution. No episode code runs before this gate
    /// passes. Checks run in the fixed order below; a declaration failing an
    /// earlier stage reports it first.
    /// </summary>
    public static class EpisodeLoadGate
    {
        public static LoadVerdict Evaluate(
            EpisodeDeclaration declaration,
            ShellCapabilityManifest shell,
            Func<string, bool> criticalAssetResolver)
        {
            if (declaration == null) throw new ArgumentNullException(nameof(declaration));
            if (shell == null) throw new ArgumentNullException(nameof(shell));
            if (criticalAssetResolver == null)
                throw new ArgumentNullException(nameof(criticalAssetResolver));

            var rejections = new List<LoadRejection>();

            EvaluateCompatibility(declaration, shell, rejections);
            EvaluateCapabilities(declaration, shell, rejections);
            EvaluateSpace(declaration, rejections);
            EvaluateAssets(declaration, criticalAssetResolver, rejections);
            EvaluateSaveVersions(declaration, rejections);

            rejections.Sort((a, b) => ((int)a.Stage).CompareTo((int)b.Stage));

            if (rejections.Count > 0)
                return new LoadVerdict { Runnable = false, Rejections = rejections };

            return EvaluateAvailability(declaration);
        }

        static void EvaluateCompatibility(
            EpisodeDeclaration d, ShellCapabilityManifest shell, List<LoadRejection> r)
        {
            RejectIf(string.IsNullOrWhiteSpace(d.EpisodeId), r, LoadGateStage.Compatibility,
                "episode_id", "episode_id_missing", "The episode has no id.");

            RejectIf(string.IsNullOrWhiteSpace(d.EpisodeModuleVersion), r,
                LoadGateStage.Compatibility, "episode_module_version",
                "episode_module_version_missing", "The episode has no module version.");

            var haveApi = ContractVersion.TryParse(FrameworkApiVersions.FrameworkApi, out var shellApi);
            if (!haveApi)
                throw new InvalidOperationException("shell framework api version is not a valid dotted version");

            if (!ContractVersion.TryParse(d.FrameworkApiMin, out var min))
            {
                Reject(r, LoadGateStage.Compatibility, "framework_api_min", "framework_api_range_invalid",
                    "The episode declares no readable minimum framework version.");
                return;
            }
            if (!ContractVersion.TryParse(d.FrameworkApiMax, out var max))
            {
                Reject(r, LoadGateStage.Compatibility, "framework_api_max", "framework_api_range_invalid",
                    "The episode declares no readable maximum framework version.");
                return;
            }
            if (min > max)
            {
                Reject(r, LoadGateStage.Compatibility, "framework_api_range", "framework_api_range_invalid",
                    "The episode framework version range is empty.");
                return;
            }
            if (shellApi < min)
            {
                Reject(r, LoadGateStage.Compatibility, "framework_api", "shell_framework_too_old",
                    $"This shell runs framework {shellApi}; the episode needs at least {min}.");
            }
            else if (shellApi > max)
            {
                Reject(r, LoadGateStage.Compatibility, "framework_api", "episode_module_too_old",
                    $"This shell runs framework {shellApi}; the episode supports at most {max}.");
            }
        }

        static void EvaluateCapabilities(
            EpisodeDeclaration d, ShellCapabilityManifest shell, List<LoadRejection> r)
        {
            foreach (var required in d.RequiredCapabilities)
            {
                if (string.IsNullOrWhiteSpace(required.CapabilityId))
                {
                    Reject(r, LoadGateStage.Capabilities, "required_capabilities",
                        "capability_id_missing", "The episode requires a capability with no id.");
                    continue;
                }
                if (!ShellCapabilityIds.IsKnownId(required.CapabilityId))
                {
                    Reject(r, LoadGateStage.Capabilities, required.CapabilityId,
                        "unknown_capability_id",
                        $"The required capability '{required.CapabilityId}' is not a framework capability id.");
                    continue;
                }
                if (!shell.TryGet(required.CapabilityId, out var provided))
                {
                    Reject(r, LoadGateStage.Capabilities, required.CapabilityId,
                        "missing_capability",
                        $"This shell does not provide '{required.CapabilityId}'.");
                    continue;
                }
                if (!ContractVersion.TryParse(required.MinimumShellVersion, out var floor))
                {
                    Reject(r, LoadGateStage.Capabilities, required.CapabilityId,
                        "capability_version_invalid",
                        $"The required capability version '{required.MinimumShellVersion}' is not readable.");
                    continue;
                }
                if (!ContractVersion.TryParse(provided.ShellVersion, out var have))
                {
                    Reject(r, LoadGateStage.Capabilities, required.CapabilityId,
                        "capability_version_invalid",
                        $"The shell capability version '{provided.ShellVersion}' is not readable.");
                    continue;
                }
                if (have < floor)
                {
                    Reject(r, LoadGateStage.Capabilities, required.CapabilityId,
                        "capability_version_too_old",
                        $"Capability '{required.CapabilityId}' is {have} here; the episode needs at least {floor}.");
                }
            }
        }

        static void EvaluateSpace(EpisodeDeclaration d, List<LoadRejection> r)
        {
            if (string.IsNullOrWhiteSpace(d.SceneForm))
            {
                Reject(r, LoadGateStage.SpaceDecidability, "scene_form", "scene_form_missing",
                    "The episode declares no scene form.");
            }
            if (d.MinimumVolumeM3 is not > 0.0)
            {
                Reject(r, LoadGateStage.SpaceDecidability, "minimum_volume_m3",
                    "minimum_volume_missing",
                    "The episode declares no decidable minimum volume.");
                return;
            }
            if (d.BelowMinimum == BelowMinimumBehavior.NamedRestrictedSetup &&
                string.IsNullOrWhiteSpace(d.NamedRestrictedSetupId))
            {
                Reject(r, LoadGateStage.SpaceDecidability, "below_minimum_setup",
                    "below_minimum_setup_missing",
                    "The episode names a restricted setup for small rooms but does not identify it.");
            }
        }

        static void EvaluateAssets(
            EpisodeDeclaration d, Func<string, bool> resolver, List<LoadRejection> r)
        {
            foreach (var asset in d.Assets)
            {
                if (!asset.ExperimentCritical)
                    continue;
                if (string.IsNullOrWhiteSpace(asset.AssetId))
                {
                    Reject(r, LoadGateStage.Assets, "assets", "asset_id_missing",
                        "The episode lists an experiment-critical asset with no stable id.");
                    continue;
                }
                if (!resolver(asset.AssetId))
                {
                    Reject(r, LoadGateStage.Assets, asset.AssetId, "critical_asset_missing",
                        $"An experiment-critical asset is missing: {asset.AssetId}.");
                }
            }
        }

        static void EvaluateSaveVersions(EpisodeDeclaration d, List<LoadRejection> r)
        {
            if (d.ReadableSaveVersions.Count == 0)
            {
                Reject(r, LoadGateStage.SaveVersions, "readable_save_versions",
                    "save_versions_missing",
                    "The episode declares no readable save versions.");
                return;
            }
            foreach (var save in d.ReadableSaveVersions)
            {
                if (string.IsNullOrWhiteSpace(save.SaveFormatVersion))
                {
                    Reject(r, LoadGateStage.SaveVersions, "readable_save_versions",
                        "save_version_missing",
                        "A claimed-readable save version has no number.");
                    continue;
                }
                if (save.OldTrialHandling == OldTrialHandling.Current)
                    continue;
                if (save.OldTrialHandling == OldTrialHandling.MigratableRecompute &&
                    string.IsNullOrWhiteSpace(save.MigrationNote))
                {
                    Reject(r, LoadGateStage.SaveVersions, save.SaveFormatVersion,
                        "save_note_missing",
                        $"Save version {save.SaveFormatVersion} claims migratable recompute with no migration note.");
                }
            }
        }

        static LoadVerdict EvaluateAvailability(EpisodeDeclaration d)
        {
            if (d.Availability == EpisodeAvailability.Bundled)
                return new LoadVerdict { Runnable = true };

            var reason = string.IsNullOrWhiteSpace(d.AvailabilityReason)
                ? "This episode is not available in this build."
                : d.AvailabilityReason;
            return new LoadVerdict
            {
                Runnable = false,
                AvailabilityBlocked = true,
                AvailabilityReason = reason,
                Rejections = new[]
                {
                    new LoadRejection
                    {
                        Stage = LoadGateStage.Availability,
                        Field = "availability",
                        ReasonCategory = d.Availability == EpisodeAvailability.NotInstalled
                            ? "not_installed"
                            : "locked",
                        ReadableReason = reason
                    }
                }
            };
        }

        static void Reject(
            List<LoadRejection> r, LoadGateStage stage, string field,
            string category, string readable) =>
            r.Add(new LoadRejection
            {
                Stage = stage, Field = field, ReasonCategory = category, ReadableReason = readable
            });

        static void RejectIf(
            bool condition, List<LoadRejection> r, LoadGateStage stage, string field,
            string category, string readable)
        {
            if (condition)
                Reject(r, stage, field, category, readable);
        }
    }
}
