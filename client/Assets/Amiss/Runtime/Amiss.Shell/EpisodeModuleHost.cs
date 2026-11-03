using System;
using System.Collections.Generic;
using Amiss.Contracts;

namespace Amiss.Shell
{
    public sealed class LifecycleResult
    {
        public bool Success { get; init; }
        public string ReasonCategory { get; init; } = "";
        public string ReadableReason { get; init; } = "";

        public static LifecycleResult Ok() => new() { Success = true };

        public static LifecycleResult Fail(string category, string readable) => new()
        {
            Success = false,
            ReasonCategory = category,
            ReadableReason = readable
        };
    }

    /// <summary>
    /// The lifecycle face every episode module provides. After unload no
    /// callback, audio source, or scene entity that could still write state
    /// remains; the host asserts the empty residue checklist before another
    /// episode may load. An action without a declaration is not executable,
    /// so a module that cannot provide its faces fails closed.
    /// </summary>
    public interface IEpisodeModule
    {
        EpisodeDeclaration Declaration { get; }
        LifecycleResult Load();
        LifecycleResult Unload(out string[] residue);
    }

    /// <summary>
    /// Owns the single current episode content root. Two episode roots are
    /// never live at once: mounting requires a registered module factory, a
    /// module whose declaration names the requested episode, and a successful
    /// module load. Returning to the library requires a residue-free unload.
    /// A build with no registered module for the requested id fails closed
    /// with a readable reason instead of running episode code.
    /// </summary>
    public sealed class EpisodeModuleHost
    {
        readonly IReadOnlyDictionary<string, Func<IEpisodeModule>> moduleFactories;

        /// <summary>A host with no episode modules registered.</summary>
        public EpisodeModuleHost() : this(null)
        {
        }

        public EpisodeModuleHost(
            IReadOnlyDictionary<string, Func<IEpisodeModule>> moduleFactories)
        {
            this.moduleFactories = moduleFactories;
        }

        public IEpisodeModule Current { get; private set; }

        public LifecycleResult Mount(EpisodeDeclaration declaration)
        {
            if (Current != null)
                return LifecycleResult.Fail(
                    "episode_already_mounted",
                    "Another episode is still mounted; unload it first.");

            if (declaration == null)
                return LifecycleResult.Fail(
                    "declaration_missing", "No episode declaration was given.");

            if (moduleFactories == null ||
                !moduleFactories.TryGetValue(declaration.EpisodeId, out var factory))
                return LifecycleResult.Fail(
                    "episode_module_not_available",
                    $"No episode module for '{declaration.EpisodeId}' is built into this shell.");

            IEpisodeModule module;
            try
            {
                module = factory();
            }
            catch (Exception)
            {
                return LifecycleResult.Fail(
                    "episode_module_factory_failed",
                    "The episode module factory failed; no episode code was mounted.");
            }

            if (module == null)
                return LifecycleResult.Fail(
                    "episode_module_missing",
                    "The episode module factory returned no module.");

            var declared = module.Declaration;
            if (declared == null || declared.EpisodeId != declaration.EpisodeId)
                return LifecycleResult.Fail(
                    "episode_module_identity_mismatch",
                    "The mounted module does not declare the requested episode; it was not loaded.");

            LifecycleResult loaded;
            try
            {
                loaded = module.Load();
            }
            catch (Exception)
            {
                return LifecycleResult.Fail(
                    "episode_module_load_failed",
                    "The episode module failed to load; no episode code is mounted.");
            }

            if (loaded == null)
                return LifecycleResult.Fail(
                    "episode_module_load_failed",
                    "The episode module returned no load result; no episode code is mounted.");
            if (!loaded.Success)
                return loaded;

            Current = module;
            Residue = Array.Empty<string>();
            return LifecycleResult.Ok();
        }

        public LifecycleResult Unmount()
        {
            if (Current == null)
            {
                Residue = Array.Empty<string>();
                return LifecycleResult.Ok();
            }

            var result = Current.Unload(out var residue);
            Residue = residue;
            if (!result.Success)
                return result;
            if (residue is { Length: > 0 })
                return LifecycleResult.Fail(
                    "episode_unload_residue",
                    "The episode left live objects behind: " + string.Join(", ", residue) + ".");
            Current = null;
            return LifecycleResult.Ok();
        }

        public string[] Residue { get; private set; } = Array.Empty<string>();
    }
}
