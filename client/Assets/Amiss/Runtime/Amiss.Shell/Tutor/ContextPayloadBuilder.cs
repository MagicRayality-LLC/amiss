using System;
using System.Collections.Generic;
using System.Linq;
using Amiss.Contracts;
using Amiss.Shell.Evaluation;
using Amiss.Shell.Simulation;
using UnityEngine;

namespace Amiss.Shell.Tutor
{
    /// <summary>
    /// The live, committed facts one Context Payload is assembled from. The
    /// shell fills it from the simulation runtime, the confirmed room, the
    /// validator's Trial mirror, and the panel preferences; nothing here is a
    /// model-supplied value.
    /// </summary>
    public sealed class ContextPayloadSource
    {
        public string EpisodeId = "";
        public string EpisodeModuleVersion = "1.0.0";
        public string EpisodeModelVersion = "1.0.0";
        public string AppBuildVersion = "";
        public string SessionId = "";
        public string DiagnosticWallTimestamp = "";

        public string TrialId = "";
        public int TrialRevision = 1;
        public int Tick;
        public int ContactCommits;
        public bool Paused;

        public string CollisionRepresentationId = "";
        public string CollisionRepresentationVersion = "1.0.0";
        public Vector3 RegionSizeM;
        public double EffectiveVolumeM3;
        public bool VirtualBoundary;

        public TemperatureMeasurement Measurement;

        public PolicyTokenSet PolicyTokens;
        public bool Muted;
        public bool TipsEnabled = true;

        public string UtteranceId = "utterance-none";
        public string UtteranceText = "";
        public SessionMemory SessionMemory;
    }

    /// <summary>
    /// Builds the one client Context Payload of approved design 06 section 5.1
    /// from committed local state. The payload mirrors the oracle builder's
    /// six field groups, carries one comparison-package projection of the
    /// current Trial, and never contains policy text, a key, or room geometry
    /// beyond the declared space summary. The 08 size limits are enforced on
    /// the encoded value before a request is built.
    /// </summary>
    public static class ContextPayloadBuilder
    {
        /// <summary>08 section 4.1: the complete encoded Context Payload.</summary>
        public const int MaxPayloadBytes = 256 * 1024;

        /// <summary>08 section 4.1: the compressed session context.</summary>
        public const int MaxCompressedContextBytes = 32 * 1024;

        /// <summary>08 section 4.1: the current user utterance.</summary>
        public const int MaxUtteranceBytes = 8 * 1024;

        public const string ProjectionVersion = "1.0.0";

        static readonly string[] DisplayComponentIds = { "kelvin", "celsius", "fahrenheit" };

        public static Dictionary<string, object> Build(ContextPayloadSource source)
        {
            if (source == null)
                throw new EvaluationContractException("malformed_parameters",
                    "No context source was supplied.");
            if (string.IsNullOrEmpty(source.EpisodeId))
                throw new EvaluationContractException("malformed_parameters",
                    "The episode identity is missing, so no context payload is built.");
            if (string.IsNullOrEmpty(source.TrialId))
                throw new EvaluationContractException("no_trial",
                    "No committed Trial exists, so no context payload is built.");
            if (string.IsNullOrEmpty(source.CollisionRepresentationId) ||
                !(source.RegionSizeM.x > 0f) || !(source.RegionSizeM.y > 0f) ||
                !(source.RegionSizeM.z > 0f))
                throw new EvaluationContractException("no_room",
                    "No confirmed room exists, so no context payload is built.");
            if (!string.IsNullOrEmpty(source.UtteranceText) &&
                System.Text.Encoding.UTF8.GetByteCount(source.UtteranceText) > MaxUtteranceBytes)
                throw new EvaluationContractException("utterance_too_large",
                    "The current utterance exceeds the bounded utterance size.");

            var reference = Ref(source.TrialId, source.TrialRevision);
            var tokens = Tokens(source, reference);
            var compressed = CompressedContext(source);
            if (CanonicalJson.Bytes(compressed).Length > MaxCompressedContextBytes)
                throw new EvaluationContractException("bounded_context_exceeded",
                    "The compressed session context exceeds its bounded size.");

            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["context_payload_version"] = FrameworkApiVersions.ContextPayload,
                ["comparison_package"] = new List<object> { Projection(source, reference, tokens) },
                ["refs"] = new List<object> { reference },
                ["policy_tokens"] = tokens,
                ["compressed_session_context"] = compressed,
                ["current_user_utterance"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["utterance_id"] = string.IsNullOrEmpty(source.UtteranceId)
                        ? "utterance-none"
                        : source.UtteranceId,
                    ["text"] = source.UtteranceText ?? ""
                }
            };

            if (CanonicalJson.Bytes(payload).Length > MaxPayloadBytes)
                throw new EvaluationContractException("bounded_context_exceeded",
                    "The context payload exceeds its bounded size.");
            return payload;
        }

        static Dictionary<string, object> Ref(string trialId, int revision) =>
            new(StringComparer.Ordinal)
            {
                ["trial_id"] = trialId,
                ["trial_revision"] = Math.Max(1, revision)
            };

        /// <summary>
        /// The current guide tokens, or the oracle's declared unguided
        /// projection while no guide is bound. No rule text, question, or
        /// complete action catalogue ever enters this object.
        /// </summary>
        static Dictionary<string, object> Tokens(ContextPayloadSource source,
            Dictionary<string, object> reference)
        {
            var issued = source.PolicyTokens;
            var stage = issued != null && !string.IsNullOrEmpty(issued.StageId)
                ? issued.StageId
                : "unguided";
            var policy = issued != null && !string.IsNullOrEmpty(issued.PolicyVersion)
                ? issued.PolicyVersion
                : "none";
            var tokens = new List<object>();
            if (issued != null && issued.Tokens.Count > 0)
                foreach (var token in issued.Tokens)
                    tokens.Add(new Dictionary<string, object>(token, StringComparer.Ordinal));
            else
            {
                tokens.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["token_id"] = "unguided-stage",
                    ["kind"] = "stage",
                    ["value_id"] = "unguided"
                });
                tokens.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["token_id"] = "unguided-policy",
                    ["kind"] = "policy_version",
                    ["value_id"] = "none"
                });
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["episode_id"] = source.EpisodeId,
                ["stage_id"] = stage,
                ["policy_version"] = policy,
                ["refs"] = new List<object> { reference },
                ["tokens"] = tokens
            };
        }

        static Dictionary<string, object> CompressedContext(ContextPayloadSource source)
        {
            var context = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                // No session clock or profile store exists in this build, so the
                // payload carries the oracle's declared zero and a null profile.
                ["session_duration_seconds"] = 0.0,
                ["preferences"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["mute"] = source.Muted,
                    ["tips_enabled"] = source.TipsEnabled,
                    ["guidance_preference_id"] = "default"
                },
                ["minimal_profile"] = null
            };
            var memory = source.SessionMemory;
            if (memory == null)
                return context;

            var current = Ref(source.TrialId, source.TrialRevision);
            var recent = new List<object>();
            if (memory.TranscriptRetentionEnabled)
                foreach (var exchange in memory.RecentExchanges)
                {
                    if (!CurrentRefs(exchange.Refs, current))
                        continue;
                    recent.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["turn_id"] = exchange.TurnId,
                        ["user_text"] = exchange.UserText,
                        ["agent_speech"] = exchange.AgentSpeech.ToArray(),
                        ["refs"] = Refs(exchange.Refs),
                        ["stage_id"] = exchange.StageId,
                        ["policy_version"] = exchange.PolicyVersion
                    });
                }

            var events = new List<object>();
            foreach (var item in memory.SessionEvents)
            {
                if (!CurrentRefs(item.Refs, current))
                    continue;
                events.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["event_id"] = item.EventId,
                    ["kind"] = item.Kind,
                    ["tick"] = item.Tick,
                    ["refs"] = Refs(item.Refs)
                });
            }

            context["recent_exchanges"] = recent;
            context["session_events"] = events;
            while (CanonicalJson.Bytes(context).Length > MaxCompressedContextBytes &&
                   (recent.Count > 0 || events.Count > 0))
            {
                if (recent.Count > 0)
                    recent.RemoveAt(0);
                else
                    events.RemoveAt(0);
            }
            if (recent.Count == 0)
                context.Remove("recent_exchanges");
            if (events.Count == 0)
                context.Remove("session_events");
            return context;
        }

        static bool CurrentRefs(IEnumerable<TrialReference> remembered,
            Dictionary<string, object> current)
        {
            var any = false;
            foreach (var reference in remembered ?? Enumerable.Empty<TrialReference>())
            {
                any = true;
                if (reference.TrialId != (string)current["trial_id"] ||
                    reference.Revision != (int)current["trial_revision"])
                    return false;
            }
            return any;
        }

        static List<object> Refs(IEnumerable<TrialReference> refs) =>
            refs.Select(item => (object)Ref(item.TrialId, item.Revision)).ToList();

        /// <summary>
        /// One bounded projection of the committed current Trial. The groups
        /// are the device-owned subset of design 03 section 4.1: identity, the
        /// space summary, complete applicable measurements, progress, the
        /// current tokens, diagnostics, and the episode extension. The groups
        /// that need an owner the device does not yet have (immutable
        /// configuration, behaviour aggregates, session comfort) are not
        /// fabricated here.
        /// </summary>
        static Dictionary<string, object> Projection(ContextPayloadSource source,
            Dictionary<string, object> reference, Dictionary<string, object> tokens)
        {
            var tick = Math.Max(0, source.Tick);
            var identity = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["snapshot_envelope_version"] = FrameworkApiVersions.SnapshotEnvelope,
                ["app_build_version"] = source.AppBuildVersion ?? "",
                ["session_id"] = source.SessionId ?? "",
                ["episode_id"] = source.EpisodeId,
                ["episode_module_version"] = source.EpisodeModuleVersion,
                ["trial_identity"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["trial_id"] = source.TrialId,
                    ["episode_id"] = source.EpisodeId,
                    ["episode_model_version"] = source.EpisodeModelVersion,
                    ["episode_module_version"] = source.EpisodeModuleVersion,
                    ["app_build_version"] = source.AppBuildVersion ?? "",
                    ["contract_versions"] = ContractVersions(),
                    ["source_trial_ref"] = null
                },
                ["refs"] = new List<object> { reference },
                ["global_revision"] = Math.Max(1, tick),
                ["current_trial_ref"] = reference,
                ["tick"] = tick,
                ["diagnostic_wall_timestamp"] = source.DiagnosticWallTimestamp ?? ""
            };

            var space = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["scene_valid"] = true,
                ["collision_representation_id"] = source.CollisionRepresentationId ?? "",
                ["collision_representation_version"] = 1,
                ["dimensions_m"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = source.RegionSizeM.x,
                    ["y"] = source.RegionSizeM.y,
                    ["z"] = source.RegionSizeM.z
                },
                ["effective_volume_m3"] = Math.Max(0.0, source.EffectiveVolumeM3),
                ["virtual_boundary"] = source.VirtualBoundary
            };

            var measurements = new List<object>();
            if (source.Measurement != null)
                measurements.Add(Measurement(source.Measurement, reference));

            var stage = tokens.GetValueOrDefault("stage_id") as string;
            if (string.IsNullOrEmpty(stage))
                stage = "unguided";
            var policy = tokens["policy_version"] as string ?? "none";
            var progress = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["guide"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["stage_id"] = stage,
                    ["satisfied_evidence_fact_ids"] = new List<object>(),
                    ["policy_version"] = policy,
                    ["trial_ref"] = reference
                },
                ["trial_state"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["phase"] = "setup",
                    ["acceptance"] = "accepted",
                    ["activity"] = source.Paused ? "paused" : "running",
                    ["terminal_reason"] = null,
                    ["trial_revision"] = Math.Max(1, source.TrialRevision),
                    ["tick"] = tick
                },
                ["preferences"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["mute"] = source.Muted,
                    ["tips_enabled"] = source.TipsEnabled,
                    ["guidance_preference_id"] = "default"
                },
                ["last_proactive_speech_trial_ref"] = null
            };

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["projection_version"] = ProjectionVersion,
                ["identity"] = identity,
                ["space"] = space,
                ["measurements"] = measurements,
                ["progress"] = progress,
                ["policy_tokens"] = tokens,
                ["diagnostics"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["numerical_state"] = "ok",
                    ["tracking_state"] = "unknown",
                    ["scene_state"] = "valid",
                    ["network_state"] = "not_used",
                    ["voice_state"] = source.Muted ? "muted" : "unavailable",
                    ["rejected_proposal_id"] = null,
                    ["rejected_reason_code"] = null
                },
                ["episode_extension"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["namespace"] = source.EpisodeId,
                    ["schema_version"] = "1.0.0",
                    ["facts"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["contact_commits"] = Math.Max(0, source.ContactCommits)
                    }
                }
            };
        }

        static Dictionary<string, object> Measurement(TemperatureMeasurement measurement,
            Dictionary<string, object> reference)
        {
            var applicable = measurement.Applicable && measurement.Display != null;
            var window = measurement.Window;
            var record = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["measurement_id"] = measurement.MeasurementId,
                ["trial_ref"] = reference,
                ["value"] = applicable ? measurement.TK : null,
                ["unit_id"] = "kelvin",
                ["coordinate_frame_id"] = "mruk-world-lock",
                ["quantity_convention_id"] = string.IsNullOrEmpty(measurement.DofFormula)
                    ? "3N_free"
                    : measurement.DofFormula,
                ["window"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["window_id"] = window.WindowId,
                    ["start_tick"] = window.StartTick,
                    ["end_tick"] = window.EndTick,
                    ["duration_seconds"] = window.DurationSeconds
                },
                ["sample_count"] = window.SampleCount,
                ["uncertainty"] = null,
                ["sufficient"] = measurement.EquilibriumSufficient,
                ["applicability"] = applicable ? "valid" : "not_applicable",
                ["calculation_version"] = string.IsNullOrEmpty(measurement.CalculationVersion)
                    ? "1.0.0"
                    : measurement.CalculationVersion
            };
            if (applicable)
                record["display_tuple"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["display_tuple_id"] = DisplayTupleId(measurement.MeasurementId),
                    ["components"] = new List<object>
                    {
                        Component("kelvin", measurement.Display.Kelvin, "kelvin"),
                        Component("celsius", measurement.Display.Celsius, "celsius"),
                        Component("fahrenheit", measurement.Display.Fahrenheit, "fahrenheit")
                    }
                };
            return record;
        }

        static Dictionary<string, object> Component(string id, int value, string unit) =>
            new(StringComparer.Ordinal)
            {
                ["component_id"] = id,
                ["value"] = value,
                ["unit_id"] = unit
            };

        /// <summary>
        /// The local display-tuple identity. It is derived once from the exact
        /// measurement id, so the panel, the payload, the citation check, and
        /// the validator all name the same complete tuple.
        /// </summary>
        public static string DisplayTupleId(string measurementId) =>
            "display-" + (measurementId ?? "");

        /// <summary>The declared component ids of one complete display tuple.</summary>
        public static IReadOnlyList<string> DisplayComponents() =>
            Array.AsReadOnly(DisplayComponentIds);

        static Dictionary<string, object> ContractVersions() =>
            new(StringComparer.Ordinal)
            {
                ["framework_api"] = FrameworkApiVersions.FrameworkApi,
                ["snapshot_envelope"] = FrameworkApiVersions.SnapshotEnvelope,
                ["tool_schema"] = FrameworkApiVersions.ToolSchema,
                ["save_format"] = FrameworkApiVersions.SaveFormat,
                ["stage_data_format"] = FrameworkApiVersions.StageDataFormat,
                ["context_payload"] = FrameworkApiVersions.ContextPayload
            };
    }
}
