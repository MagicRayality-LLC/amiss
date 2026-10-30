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
    /// never live at once: mounting requires a runnable load verdict, and
    /// returning to the library requires a residue-free unload. No episode
    /// module is registered in this build, so every mount fails closed with
    /// a readable reason instead of running episode code.
    /// </summary>
    public sealed class EpisodeModuleHost
    {
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

            return LifecycleResult.Fail(
                "episode_module_not_available",
                $"No episode module for '{declaration.EpisodeId}' is built into this shell yet. " +
                "The experiment episode arrives with the simulation integration step.");
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
