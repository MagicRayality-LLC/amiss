using System;
using System.Collections.Generic;

namespace Amiss.Contracts
{
    /// <summary>
    /// The stable framework capability id registry. Episodes declare required
    /// ids from this registry; the shell publishes the subset it actually
    /// provides. Adding an id is a framework API change. Presence in the shell
    /// manifest claims presence and version, never measured performance.
    /// </summary>
    public static class ShellCapabilityIds
    {
        public const string SceneRoomCollision = "scene.room_collision";
        public const string SceneManualBoundary = "scene.manual_boundary";
        public const string InputHand = "input.hand";
        public const string InputController = "input.controller";
        public const string InputPanelEquivalent = "input.panel_equivalent";
        public const string PanelWorldAnchored = "panel.world_anchored";
        public const string PanelChartSlots = "panel.chart_slots";
        public const string VoiceDictation = "voice.dictation";
        public const string VoiceTts = "voice.tts";
        public const string TutorTransport = "tutor.transport";
        public const string SimBatchRun = "sim.batch_run";
        public const string SimReplayRecompute = "sim.replay_recompute";
        public const string PersistGenerations = "persist.generations";

        public static readonly IReadOnlyList<string> All = new[]
        {
            SceneRoomCollision,
            SceneManualBoundary,
            InputHand,
            InputController,
            InputPanelEquivalent,
            PanelWorldAnchored,
            PanelChartSlots,
            VoiceDictation,
            VoiceTts,
            TutorTransport,
            SimBatchRun,
            SimReplayRecompute,
            PersistGenerations
        };

        public static bool IsKnownId(string id)
        {
            foreach (var known in All)
                if (known == id)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// One versioned shell capability. Comparison against an episode
    /// requirement is set inclusion plus a version floor at load time.
    /// </summary>
    public sealed class ShellCapability
    {
        public string CapabilityId { get; init; } = "";
        public string ShellVersion { get; init; } = "";
    }

    /// <summary>
    /// The versioned manifest of capabilities this shell build publishes.
    /// The shell never invents a capability to satisfy a declaration.
    /// </summary>
    public sealed class ShellCapabilityManifest
    {
        public string ManifestVersion { get; init; } = "";
        public IReadOnlyList<ShellCapability> Capabilities { get; init; }
            = Array.Empty<ShellCapability>();

        public bool TryGet(string capabilityId, out ShellCapability capability)
        {
            foreach (var c in Capabilities)
            {
                if (c.CapabilityId == capabilityId)
                {
                    capability = c;
                    return true;
                }
            }
            capability = null;
            return false;
        }

        /// <summary>
        /// The capabilities published by this shell build. Room preparation,
        /// equivalent input, charts, voice, transport, simulation, and
        /// persistence capabilities are published by their owning build steps
        /// when they land; until then episodes requiring them are not runnable.
        /// </summary>
        public static ShellCapabilityManifest ThisBuild() => new()
        {
            ManifestVersion = FrameworkApiVersions.FrameworkApi,
            Capabilities = new[]
            {
                new ShellCapability
                {
                    CapabilityId = ShellCapabilityIds.PanelWorldAnchored,
                    ShellVersion = "1.0.0"
                }
            }
        };
    }
}
