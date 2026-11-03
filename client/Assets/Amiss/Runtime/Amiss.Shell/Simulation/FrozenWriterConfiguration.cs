using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The complete frozen writer configuration from approved design 02
    /// section 3.1. Every field the design lists is named here and repeated in
    /// the Trial evidence record; changing any value is a new measurement, not
    /// a tweak to an existing result. Values the temperature design leaves to
    /// a later measurement carry the uncalibrated label instead of a claim.
    /// </summary>
    public sealed class FrozenWriterConfiguration
    {
        /// <summary>
        /// The measured-candidate Unity rigidbody writer that failed the locked
        /// gate of 02 section 3.1. Its configuration record and every
        /// measurement stay archived; it is never bound to a Trial.
        /// </summary>
        public const string ArchivedUnityConfigurationId = "unity-rigidbody-frozen-v1";
        public const string FixedDeltaTimeId = "fixed-dt-0.05";

        /// <summary>Fixed model Tick. Mirrors the headless oracle's 0.05 s.</summary>
        public const float FixedDeltaTimeSeconds = 0.05f;

        public const int SolverIterations = 6;
        public const int SolverVelocityIterations = 1;

        /// <summary>Zero so a rebound is never reclassified as inelastic.</summary>
        public const float BounceThresholdMPerS = 0f;
        public const float ContactOffsetM = 0.01f;
        public const CollisionDetectionMode DetectionMode =
            CollisionDetectionMode.ContinuousSpeculative;
        public const RigidbodyInterpolation Interpolation = RigidbodyInterpolation.None;

        /// <summary>
        /// Enhanced Determinism is a project physics setting with no public
        /// runtime API in this editor version; ProjectSettings/DynamicsManager.asset
        /// carries it and an edit-mode test keeps the two in step.
        /// </summary>
        public const string EnhancedDeterminism = "project_setting_enabled";

        public const float DynamicFriction = 0f;
        public const float StaticFriction = 0f;
        public const float Bounciness = 1f;
        public const PhysicsMaterialCombine FrictionCombine = PhysicsMaterialCombine.Minimum;
        public const PhysicsMaterialCombine BounceCombine = PhysicsMaterialCombine.Maximum;

        /// <summary>Engine mass for the frozen fixture; uncalibrated, not a device result.</summary>
        public const float BallMassKg = 1f;

        /// <summary>Prototype diameter of about 10 cm from 07 section 3.3; uncalibrated.</summary>
        public const float BallRadiusM = 0.05f;

        public const float InitialSpeedScaleMPerS = 1f;
        public const int SpawnAttemptBound = 256;
        public const int ContactIterationBound = 4096;
        public const int MaxTicksPerSchedulingSlice = 2;
        public const string Uncalibrated = "uncalibrated_declared_not_measured";
        public const string SolverGateEvidence = "solver_gate_pending_not_measured";
        public const string DefaultSeedStreamId = "initial-setup";

        public static readonly Vector3 Gravity = Vector3.zero;
        public const float LinearDamping = 0f;
        public const float AngularDamping = 0f;
        public const bool SleepEnabled = false;

        public FrozenWriterConfiguration(string collisionRepresentationId,
            string collisionChecksum, string collisionFormatVersion, ulong rootSeed,
            string seedStreamId)
        {
            CollisionRepresentationId = collisionRepresentationId ?? "";
            CollisionChecksum = collisionChecksum ?? "";
            CollisionFormatVersion = collisionFormatVersion ?? "";
            RootSeed = rootSeed;
            SeedStreamId = string.IsNullOrEmpty(seedStreamId) ? DefaultSeedStreamId : seedStreamId;
        }

        public string CollisionRepresentationId { get; }
        public string CollisionChecksum { get; }
        public string CollisionFormatVersion { get; }
        public ulong RootSeed { get; }
        public string SeedStreamId { get; }

        /// <summary>
        /// The complete field record, in a stable order, as key=value lines.
        /// It names every 02 section 3.1 field plus the declared-uncalibrated
        /// ball mass and radius.
        /// </summary>
        public IReadOnlyList<string> Record()
        {
            return new[]
            {
                "writer_configuration_id=" + ArchivedUnityConfigurationId,
                "writer_kind=unity_rigidbody",
                "fixed_delta_time_id=" + FixedDeltaTimeId,
                "fixed_delta_time_s=" + Number(FixedDeltaTimeSeconds),
                "solver_iterations=" + SolverIterations,
                "solver_velocity_iterations=" + SolverVelocityIterations,
                "bounce_threshold_m_per_s=" + Number(BounceThresholdMPerS),
                "contact_offset_m=" + Number(ContactOffsetM),
                "collision_detection_mode=" + DetectionMode,
                "interpolation=" + Interpolation,
                "enhanced_determinism=" + EnhancedDeterminism,
                "material_dynamic_friction=" + Number(DynamicFriction),
                "material_static_friction=" + Number(StaticFriction),
                "material_bounciness=" + Number(Bounciness),
                "material_friction_combine=" + FrictionCombine,
                "material_bounce_combine=" + BounceCombine,
                "ball_mass_kg=" + Number(BallMassKg),
                "ball_radius_m=" + Number(BallRadiusM),
                "ball_mass_radius_evidence=" + Uncalibrated,
                "gravity_m_per_s2=" + Number(Gravity.x) + "," + Number(Gravity.y) + "," +
                Number(Gravity.z),
                "linear_damping=" + Number(LinearDamping),
                "angular_damping=" + Number(AngularDamping),
                "sleep_enabled=" + (SleepEnabled ? "true" : "false"),
                "initial_speed_scale_m_per_s=" + Number(InitialSpeedScaleMPerS),
                "spawn_attempt_bound=" + SpawnAttemptBound,
                "contact_iteration_bound=" + ContactIterationBound,
                "max_ticks_per_scheduling_slice=" + MaxTicksPerSchedulingSlice,
                "seed_expansion_id=" + NamedRandomStreams.ExpansionId,
                "stream_layout_version=" + NamedRandomStreams.LayoutVersion,
                "seed_stream_id=" + SeedStreamId,
                "root_seed=" + RootSeed.ToString(CultureInfo.InvariantCulture),
                "collision_representation_id=" + CollisionRepresentationId,
                "collision_checksum=" + CollisionChecksum,
                "collision_format_version=" + CollisionFormatVersion,
                "solver_gate_evidence=" + SolverGateEvidence
            };
        }

        static string Number(float value) =>
            value.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
