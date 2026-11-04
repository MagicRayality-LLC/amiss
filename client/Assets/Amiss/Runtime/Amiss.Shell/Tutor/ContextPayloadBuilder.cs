using System;
using System.Collections.Generic;
using System.Linq;
using Amiss.Contracts;
using Amiss.Shell.Episodes;
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
        public TemperatureEnergyLedger Ledger;
        public SpaceSizing Sizing;
        public int BallCount;

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

            // Dialogue and mechanical events belong to the session's Trial
            // identity, not to one contact-commit revision. Founder
            // requirement (2026-11-04): the teacher keeps the whole session's
            // conversation. The trial_id freshness check still drops records
            // from another Trial; the serialized ref carries the current
            // revision so the payload stays within the public contract.
            var current = Ref(source.TrialId, source.TrialRevision);
            var currentRefs = new List<object> { current };
            var recent = new List<object>();
            if (memory.TranscriptRetentionEnabled)
                foreach (var exchange in memory.RecentExchanges)
                {
                    if (!SameTrial(exchange.Refs, source.TrialId))
                        continue;
                    recent.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["turn_id"] = exchange.TurnId,
                        ["user_text"] = exchange.UserText,
                        ["agent_speech"] = exchange.AgentSpeech.ToArray(),
                        ["refs"] = currentRefs,
                        ["stage_id"] = exchange.StageId,
                        ["policy_version"] = exchange.PolicyVersion
                    });
                }

            var events = new List<object>();
            foreach (var item in memory.SessionEvents)
            {
                if (!SameTrial(item.Refs, source.TrialId))
                    continue;
                events.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["event_id"] = item.EventId,
                    ["kind"] = item.Kind,
                    ["tick"] = item.Tick,
                    ["refs"] = currentRefs
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

        /// <summary>
        /// A remembered record belongs to the current Trial when every Trial it
        /// names is that same Trial. The revision label is not compared: an
        /// effective-action contact commit advances it during one conversation.
        /// </summary>
        static bool SameTrial(IEnumerable<TrialReference> remembered, string trialId)
        {
            var any = false;
            foreach (var reference in remembered ?? Enumerable.Empty<TrialReference>())
            {
                any = true;
                if (reference.TrialId != trialId)
                    return false;
            }
            return any;
        }

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
                    ["facts"] = Facts(source)
                }
            };
        }

        /// <summary>
        /// The current statistics numbers the instructor may read, in the
        /// versioned episode extension of 07 section 8 and SRD `TEMP-SNAP-1`.
        /// Every value is a producer-owned aggregate or declared
        /// not-applicable state; no per-ball position, velocity, or force
        /// array enters model context. A measurement that is not applicable
        /// keeps its nulls instead of a fabricated zero.
        /// </summary>
        static Dictionary<string, object> Facts(ContextPayloadSource source)
        {
            var measurement = source.Measurement;
            var ledger = source.Ledger;
            var sizing = source.Sizing ??
                         TemperatureSpaceSizing.Compute(source.EffectiveVolumeM3);
            var window = measurement?.Window;
            var applicable = measurement != null && measurement.Applicable &&
                             measurement.Display != null;

            var space = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["v_eff_m3"] = sizing.VEffM3,
                ["v_min_m3"] = sizing.VMinM3,
                ["furniture_deduction_mode"] = sizing.FurnitureDeductionMode ?? "",
                ["target_number_density_per_m3"] = sizing.TargetNumberDensityPerM3,
                ["computed_n"] = sizing.ComputedN.HasValue
                    ? (object)sizing.ComputedN.Value
                    : null,
                ["is_opening_override_n"] = sizing.IsOpeningOverrideN,
                ["large_room_subspace_applied"] = sizing.LargeRoomSubspaceApplied,
                ["large_room_warning"] = sizing.LargeRoomWarning,
                ["status"] = string.IsNullOrEmpty(sizing.Status) ? "invalid" : sizing.Status
            };

            var nTotal = measurement != null ? measurement.NTotal
                : Math.Max(0, source.BallCount);
            var nFree = measurement != null ? measurement.NFree
                : Math.Max(0, source.BallCount);
            var nHeld = measurement != null ? measurement.NHeld : 0;
            var groups = new List<object>();
            if (measurement != null && nFree > 0)
                groups.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["group_id"] = "group-1x",
                    ["mass_ratio"] = 1.0,
                    ["mass_kg"] = (double)FrozenWriterConfiguration.BallMassKg,
                    ["radius_m"] = (double)FrozenWriterConfiguration.BallRadiusM,
                    ["count_free"] = nFree,
                    ["mean_ke_j"] = measurement.KTotalJ / nFree,
                    ["v_rms_m_per_s"] = measurement.VRmsMPerS
                });
            var particles = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["n_total"] = nTotal,
                ["n_free"] = nFree,
                ["n_held"] = nHeld,
                ["density_model"] = "uniform_density_spheres",
                ["groups"] = groups,
                ["tracked_ball"] = null
            };

            var thermal = ledger != null ? ledger.KThermalJ
                : measurement?.KThermalJ ?? 0.0;
            var drift = ledger != null ? ledger.KDriftJ
                : measurement?.KDriftJ ?? 0.0;
            var kTotal = thermal + drift;
            var energy = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["initial_total_ke_j"] = ledger != null
                    ? (object)ledger.InitialTotalKeJ
                    : null,
                ["free_total_ke_j"] = ledger != null
                    ? (object)ledger.FreeTotalKeJ
                    : measurement?.KTotalJ,
                ["free_thermal_ke_j"] = thermal,
                ["free_drift_ke_j"] = drift,
                ["drift_fraction"] = kTotal > 0.0 ? drift / kTotal : 0.0,
                ["held_ke_j"] = ledger != null ? ledger.KHeldJ
                    : measurement?.KHeldJ ?? 0.0,
                ["sandbag_absorbed_ke_cumulative_j"] = ledger?.ESandbagJ ?? 0.0,
                ["work_events_cumulative_ke_j"] = ledger?.WorkEventsCumulativeKeJ ?? 0.0,
                ["setup_energy_diff_from_source_j"] =
                    ledger?.SetupEnergyDiffFromSourceJ
            };

            var readout = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["status"] = measurement == null ? "not_applicable" : measurement.Status,
                ["instant_k"] = measurement?.TK,
                // The producer publishes one instant value and its window; a
                // separate window mean is not derived here.
                ["window_mean_k"] = null,
                ["window_sec"] = window != null ? window.DurationSeconds : 0.0,
                ["sample_count"] = window != null ? window.SampleCount : 0,
                ["dof_formula"] = measurement != null && !string.IsNullOrEmpty(
                    measurement.DofFormula)
                    ? measurement.DofFormula
                    : TemperatureModelDeclaration.DofFormula,
                ["display_tuple"] = applicable
                    ? new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["k"] = measurement.Display.Kelvin,
                        ["c"] = measurement.Display.Celsius,
                        ["f"] = measurement.Display.Fahrenheit,
                        ["ratio_to_baseline_k"] = measurement.Display.KelvinRatioBaseline
                    }
                    : null
            };

            var vCm = new List<object>(3);
            for (var axis = 0; axis < 3; axis++)
                vCm.Add(measurement != null && axis < measurement.VCmMPerS.Count
                    ? measurement.VCmMPerS[axis]
                    : 0.0);
            var kinematics = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["v_cm_vec"] = vCm,
                // The mass-weighted mean velocity is the center-of-mass velocity.
                ["mean_velocity_vec"] = new List<object>(vCm),
                ["mean_speed_mps"] = measurement?.MeanSpeedMPerS ?? 0.0,
                ["v_rms_mps"] = measurement?.VRmsMPerS ?? 0.0,
                ["isotropy_score"] = null,
                ["window_stability_score"] = null,
                ["intergroup_energy_gap"] = null,
                ["equilibrium_sufficient"] = measurement?.EquilibriumSufficient ?? false
            };

            var contacts = ledger != null ? ledger.Contacts : null;
            var counts = new Dictionary<string, object>(StringComparer.Ordinal);
            var latest = contacts != null && contacts.Count > 0
                ? contacts[contacts.Count - 1]
                : null;
            if (contacts != null)
                for (var index = 0; index < contacts.Count; index++)
                {
                    var prop = contacts[index].PropId;
                    counts[prop] = counts.TryGetValue(prop, out var count) ? (int)count + 1 : 1;
                }
            var props = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["current_prop"] = null,
                ["contact_counts_by_prop"] = counts,
                ["latest_contact"] = latest == null
                    ? null
                    : new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["contact_id"] = latest.ContactId,
                        ["prop_id"] = latest.PropId,
                        ["target_ball_id"] = latest.TargetBallId,
                        ["start_tick"] = latest.StartTick,
                        ["end_tick"] = latest.EndTick,
                        ["delta_energy_j"] = latest.WorkJ,
                        ["closure_reason"] = latest.ClosureReason
                    },
                ["held_ball_count"] = nHeld,
                ["assisted_slow_release"] = false,
                ["sandbag_absorbed_ke_cumulative_j"] = ledger?.ESandbagJ ?? 0.0,
                ["slow_ball_removed_temp_rise"] = false
            };

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["space_sizing"] = space,
                ["particles"] = particles,
                ["energy_ledger"] = energy,
                ["temperature_readout"] = readout,
                ["kinematics_and_equilibrium"] = kinematics,
                ["prop_summary"] = props,
                ["contact_commits"] = Math.Max(0, source.ContactCommits)
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
