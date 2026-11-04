using System;
using System.Collections.Generic;
using Amiss.Shell.Room;
using Amiss.Shell.Simulation;
using UnityEngine;

namespace Amiss.Shell.Ports
{
    // Seams the shell exposes for the remaining runtime responsibilities.
    // Each port is owned by a later build step; until then the shell runs
    // with the port silent and nothing behind it executes. The dependency
    // direction is fixed: platform adapters live in this client tree, pure
    // contract logic lives in simulation/ and schema/ at the repository root,
    // and nothing here may write physical state, measurements, stages, or
    // saves directly.

    /// <summary>
    /// Sole owner of physical facts and Trials. The deterministic headless
    /// core lives in simulation/ and remains the contract oracle; the device
    /// binding behind this port owns one <see cref="ScientificWriterSlot"/> per
    /// Trial, receives the pinned room collision representation, and advances
    /// only from accepted, Tick-stamped requests. User actions reach it as a
    /// <see cref="TrialStartRequest"/> and never write ball state themselves.
    /// </summary>
    public interface SimulationRuntimePort
    {
        /// <summary>True once a runtime binding is registered in this build.</summary>
        bool Available { get; }
        bool PauseRequested { get; }
        string PauseReason { get; }
        void RequestPause(string reason);
        void RequestResume();

        /// <summary>
        /// Hands one accepted Trial to the runtime. The request carries the
        /// confirmed room, the pinned collision representation, and the ball
        /// count the episode's sizing rule derived from that volume. The
        /// runtime validates all of it, binds exactly one writer, and refuses
        /// with a reason instead of starting an ungrounded Trial.
        /// </summary>
        TrialStartOutcome TryStartTrial(TrialStartRequest request);

        /// <summary>Read-only identity of the one live Trial, or null.</summary>
        SimulationTrialSummary TrialSummary { get; }

        /// <summary>
        /// The producer-owned display tuple and energy accounts of the live
        /// Trial, or null when no episode producer is bound. Presentation
        /// renders this and never recomputes a temperature or energy value.
        /// </summary>
        TemperatureReadoutSnapshot Readout => null;

        /// <summary>Frees the live Trial and its writer; safe when none is live.</summary>
        void StopTrial(string reason);
    }

    /// <summary>
    /// Local guide: stage, accumulated evidence, allowed-action set, and
    /// policy token issuance/revocation. Mechanical execution is pure logic
    /// in simulation/; the on-device binding arrives with the episode.
    /// </summary>
    public interface GuideRunnerPort
    {
        bool Available { get; }
    }

    /// <summary>
    /// Tutor model transport: recorded fixture, hosted, or local-live binding
    /// behind one port. Model replies carry no execution right; they only
    /// propose, and validation decides. Offline/default play never waits for it.
    /// </summary>
    public interface TutorTransportPort
    {
        bool Available { get; }

        /// <summary>
        /// The one visible provenance line of 10 section 4: model, mode, and
        /// request id of the last admitted reply, or the readable fail-closed
        /// reason when the path is closed.
        /// </summary>
        string TraceLine { get; }
    }

    /// <summary>
    /// Complete-generation local persistence: saves, recovery, quotas, and
    /// deletion. Arrives with the persistence build step; progress feeds stay
    /// empty until then.
    /// </summary>
    public interface PersistenceAdapterPort
    {
        bool Available { get; }
    }

    public sealed class UnavailablePort
    {
        public const string Reason = "This shell build does not bind this responsibility yet.";
    }

    /// <summary>Why a Trial start was refused; a readable reason code is public.</summary>
    public enum TrialStartRefusal
    {
        None,
        RuntimeNotBound,
        RequestInvalid,
        LiveSlotOccupied,
        RoomNotConfirmed,
        CollisionPinMissing,
        CollisionPinMismatch,
        CollisionIntegrityFailed,
        CollisionUnsupported,
        SizingInvalid,
        SizingMismatch,
        BallCountOutOfRange,
        SpawnFailed,
        WriterAlreadyBound,
        WriterConfigurationRejected,
        WriterSetupFailed
    }

    public static class TrialStartRefusalCodes
    {
        public static string Code(this TrialStartRefusal refusal)
        {
            switch (refusal)
            {
                case TrialStartRefusal.None: return "";
                case TrialStartRefusal.RuntimeNotBound: return "simulation_runtime_unbound";
                case TrialStartRefusal.RequestInvalid: return "trial_start_request_invalid";
                case TrialStartRefusal.LiveSlotOccupied: return "live_trial_slot_occupied";
                case TrialStartRefusal.RoomNotConfirmed: return "room_not_confirmed";
                case TrialStartRefusal.CollisionPinMissing: return "collision_pin_missing";
                case TrialStartRefusal.CollisionPinMismatch: return "collision_pin_mismatch";
                case TrialStartRefusal.CollisionIntegrityFailed: return "collision_integrity_failed";
                case TrialStartRefusal.CollisionUnsupported: return "collision_representation_unsupported";
                case TrialStartRefusal.SizingInvalid: return "space_sizing_invalid";
                case TrialStartRefusal.SizingMismatch: return "space_sizing_mismatch";
                case TrialStartRefusal.BallCountOutOfRange: return "ball_count_out_of_range";
                case TrialStartRefusal.SpawnFailed: return "ball_spawn_failed";
                case TrialStartRefusal.WriterAlreadyBound: return "writer_already_bound";
                case TrialStartRefusal.WriterConfigurationRejected:
                    return "writer_configuration_rejected";
                case TrialStartRefusal.WriterSetupFailed: return "writer_setup_failed";
                default: return "trial_start_refused";
            }
        }
    }

    /// <summary>
    /// One accepted-start handoff. Every field is a value or an immutable
    /// record owned by the caller: the shell supplies the confirmed room and
    /// pin, the episode supplies the sized ball count and the named setup
    /// seed. The runtime derives no physical result from this request; it
    /// only gates, binds the writer, and publishes the initial setup.
    /// </summary>
    public sealed class TrialStartRequest
    {
        public string TrialId { get; set; } = "";
        public RoomConfirmation Confirmation { get; set; }
        public CollisionPin Pin { get; set; }
        public int BallCount { get; set; }
        public float EffectiveVolumeM3 { get; set; }
        public ulong RootSeed { get; set; }
        public string SeedStreamId { get; set; } = "";

        /// <summary>
        /// Optional sandbag hover anchor for this Trial. Null means no bag
        /// floats; a finite anchor floats one bag there once, for the stage
        /// that ordered a sandbag trial. A non-finite anchor refuses the start.
        /// </summary>
        public Vector3? SandbagAnchorM { get; set; }
    }

    public sealed class TrialStartOutcome
    {
        public bool Started { get; private init; }
        public TrialStartRefusal Refusal { get; private init; } = TrialStartRefusal.None;
        public string ReasonCode { get; private init; } = "";
        public string TrialId { get; private init; } = "";
        public int BallCount { get; private init; }
        public string CollisionRepresentationId { get; private init; } = "";
        public string CollisionChecksum { get; private init; } = "";
        public string WriterId { get; private init; } = "";
        public string WriterConfigurationId { get; private init; } = "";
        public IReadOnlyList<string> WriterConfigurationRecord { get; private init; } =
            Array.Empty<string>();

        public static TrialStartOutcome Refused(string trialId, TrialStartRefusal refusal,
            string detail = "") => new()
        {
            Started = false,
            Refusal = refusal,
            ReasonCode = string.IsNullOrEmpty(detail)
                ? refusal.Code()
                : refusal.Code() + ":" + detail,
            TrialId = trialId ?? ""
        };

        public static TrialStartOutcome Success(string trialId, int ballCount,
            string representationId, string checksum, string writerId,
            string writerConfigurationId, IReadOnlyList<string> record) => new()
        {
            Started = true,
            TrialId = trialId,
            BallCount = ballCount,
            CollisionRepresentationId = representationId ?? "",
            CollisionChecksum = checksum ?? "",
            WriterId = writerId ?? "",
            WriterConfigurationId = writerConfigurationId ?? "",
            WriterConfigurationRecord = record ?? Array.Empty<string>()
        };
    }

    /// <summary>Read-only identity of the live Trial for presentation and evidence.</summary>
    public sealed class SimulationTrialSummary
    {
        public SimulationTrialSummary(string trialId, int tick, int ballCount,
            string collisionRepresentationId, string collisionChecksum,
            string writerId, string writerConfigurationId, bool paused)
        {
            TrialId = trialId ?? "";
            Tick = tick;
            BallCount = ballCount;
            CollisionRepresentationId = collisionRepresentationId ?? "";
            CollisionChecksum = collisionChecksum ?? "";
            WriterId = writerId ?? "";
            WriterConfigurationId = writerConfigurationId ?? "";
            Paused = paused;
        }

        public string TrialId { get; }
        public int Tick { get; }
        public int BallCount { get; }
        public string CollisionRepresentationId { get; }
        public string CollisionChecksum { get; }
        public string WriterId { get; }
        public string WriterConfigurationId { get; }
        public bool Paused { get; }

        public string ShortChecksum =>
            CollisionChecksum.Length > 12 ? CollisionChecksum.Substring(0, 12) : CollisionChecksum;
    }
}
