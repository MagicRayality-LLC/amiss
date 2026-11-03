using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The custom hard-sphere writer of approved design 02 section 3.1: the
    /// replacement scientific writer bound after the frozen Unity rigidbody
    /// configuration failed the locked measurement gate. It is the product
    /// binding for the temperature episode.
    ///
    /// PhysX takes no part. No ball body, collider, static or callback exists,
    /// and the writer creates no physics callback that could write ball pose
    /// or velocity; it reads only the pinned
    /// <see cref="PinnedCollision"/>'s region and advances the single
    /// ball-state copy in place. Presentation keeps interpolating the
    /// published views.
    ///
    /// The step mirrors the approved headless oracle
    /// (<c>simulation/runtime.py</c>, <c>HeadlessBallWriter.advance</c>)
    /// operation for operation: advance every non-held ball by one fixed
    /// <c>v * dt</c>, mirror an overshoot of the pinned region about the
    /// boundary with the normal velocity sign flipped, then enumerate free
    /// ball pairs in ascending entity-id order and resolve each overlapping
    /// approaching pair with the perfectly elastic normal impulse
    /// <c>2 (v_rel . n) / m_sum * n</c> along the unit contact normal. Contacts
    /// are counted per axis reflection and per applied pair impulse, the same
    /// way the oracle counts them, and the declared contact-iteration bound
    /// degrades the step into a reported numerical hold rather than tunnelling
    /// or rewriting energy.
    /// </summary>
    public sealed class CustomHardSphereWriter : IScientificWriter, IContactFactWriter,
        ISweepEffectWriter
    {
        public const string ConfigurationIdValue = "custom-hard-sphere-frozen-v2";
        public const string ContactOrdering = "ascending_entity_id_pair_enumeration";
        public const string BoundaryOrdering = "ascending_entity_id_then_axis_xyz";
        public const string EqualTimeTieBreak = "stable_entity_id_ordinal";
        public const string IntegrationId = "position_advance_then_contact_resolution";
        public const string SolverGateEvidence = "solver_gate_harness_record";

        readonly Func<int> iterationBound;

        BallState[] order;
        PinnedCollision collision;
        FrozenWriterConfiguration configuration;
        double[] px;
        double[] py;
        double[] pz;
        double[] vx;
        double[] vy;
        double[] vz;
        int[] freeOrder;
        bool configured;

        // Queued tool effects and their measured result. The writer resolves a
        // queued sweep against its own state before integration, in queue
        // order, and measures the ball-energy change it applied — the only
        // authority the energy ledger accepts (02 section 4.1).
        struct QueuedSweep
        {
            public string InteractionId;
            public Vector3 OriginM;
            public Vector3 VelocityMPerS;
            public float RadiusM;
            public double ImpulseMagnitude;
        }

        readonly List<QueuedSweep> queuedSweeps = new();
        AppliedSweepHit[] lastAppliedSweeps = Array.Empty<AppliedSweepHit>();

        public CustomHardSphereWriter(int contactIterationBound)
        {
            iterationBound = () => contactIterationBound;
        }

        public CustomHardSphereWriter()
        {
            iterationBound = () => FrozenWriterConfiguration.ContactIterationBound;
        }

        public string WriterId => ConfigurationIdValue;
        public string ConfigurationId => ConfigurationIdValue;
        public WriterDisposition Disposition => WriterDisposition.ProductTrial;
        public ContactStepFacts LastContactFacts { get; private set; }
        public int BodyCount => order?.Length ?? 0;
        public bool IsConfigured => configured;

        public void Setup(SimulationTrialSetup setup)
        {
            if (setup == null || setup.Collision == null)
                throw new ArgumentNullException(nameof(setup));
            if (configured)
                throw new InvalidOperationException("writer_already_setup");

            collision = setup.Collision;
            configuration = setup.Configuration;
            order = new BallState[setup.Balls.Count];
            for (var index = 0; index < setup.Balls.Count; index++)
                order[index] = setup.Balls[index];
            Array.Sort(order, (first, second) =>
                string.CompareOrdinal(first.EntityId, second.EntityId));

            var count = order.Length;
            px = new double[count];
            py = new double[count];
            pz = new double[count];
            vx = new double[count];
            vy = new double[count];
            vz = new double[count];
            freeOrder = new int[count];
            // The writer owns the numeric state for the life of the Trial, the
            // same ownership the frozen Unity writer had through PhysX: it
            // advances the state itself and the runtime reads the step back
            // into its single ball-state copy. Seeding happens once, here.
            for (var index = 0; index < count; index++)
            {
                var ball = order[index];
                px[index] = ball.PositionM.x;
                py[index] = ball.PositionM.y;
                pz[index] = ball.PositionM.z;
                vx[index] = ball.VelocityMPerS.x;
                vy[index] = ball.VelocityMPerS.y;
                vz[index] = ball.VelocityMPerS.z;
            }
            LastContactFacts = new ContactStepFacts(0, 0, false);
            configured = true;
        }

        public void QueueSweep(string interactionId, Vector3 origin, Vector3 velocityMPerS,
            float sweptRadiusM, double impulseMagnitude)
        {
            if (!configured)
                throw new InvalidOperationException("writer_not_setup");
            if (!float.IsFinite(sweptRadiusM) || sweptRadiusM <= 0f)
                throw new ArgumentException("swept_radius_invalid", nameof(sweptRadiusM));
            if (!double.IsFinite(impulseMagnitude) || impulseMagnitude <= 0.0)
                throw new ArgumentException("impulse_magnitude_invalid",
                    nameof(impulseMagnitude));
            queuedSweeps.Add(new QueuedSweep
            {
                InteractionId = interactionId ?? "",
                OriginM = origin,
                VelocityMPerS = velocityMPerS,
                RadiusM = sweptRadiusM,
                ImpulseMagnitude = impulseMagnitude
            });
        }

        public IReadOnlyList<AppliedSweepHit> LastAppliedSweeps => lastAppliedSweeps;

        /// <summary>
        /// Resolves and applies every queued sweep before integration, in queue
        /// order, recording the measured pre/post ball energy of each applied
        /// impulse. The queue is always cleared, so a sample can never leak into
        /// a later Tick.
        /// </summary>
        void ApplyQueuedSweeps(double deltaSeconds)
        {
            if (queuedSweeps.Count == 0)
            {
                if (lastAppliedSweeps.Length != 0)
                    lastAppliedSweeps = Array.Empty<AppliedSweepHit>();
                return;
            }
            var applied = new List<AppliedSweepHit>();
            for (var queueIndex = 0; queueIndex < queuedSweeps.Count; queueIndex++)
            {
                var queued = queuedSweeps[queueIndex];
                var dx = queued.VelocityMPerS.x * deltaSeconds;
                var dy = queued.VelocityMPerS.y * deltaSeconds;
                var dz = queued.VelocityMPerS.z * deltaSeconds;
                var lengthSquared = dx * dx + dy * dy + dz * dz;
                // Ascending entity id: the order array is already sorted, so
                // the traversal is the oracle's stable entity order.
                for (var index = 0; index < order.Length; index++)
                {
                    if (order[index].Membership != "free")
                        continue;
                    var rx = px[index] - queued.OriginM.x;
                    var ry = py[index] - queued.OriginM.y;
                    var rz = pz[index] - queued.OriginM.z;
                    var parameter = lengthSquared > 0.0
                        ? Math.Clamp((rx * dx + ry * dy + rz * dz) / lengthSquared, 0.0, 1.0)
                        : 0.0;
                    var cx = queued.OriginM.x + dx * parameter;
                    var cy = queued.OriginM.y + dy * parameter;
                    var cz = queued.OriginM.z + dz * parameter;
                    var vxDelta = px[index] - cx;
                    var vyDelta = py[index] - cy;
                    var vzDelta = pz[index] - cz;
                    var distance = Math.Sqrt(vxDelta * vxDelta + vyDelta * vyDelta +
                                             vzDelta * vzDelta);
                    if (distance > queued.RadiusM + order[index].RadiusM)
                        continue;
                    var nx = 1.0;
                    var ny = 0.0;
                    var nz = 0.0;
                    if (distance > 0.0)
                    {
                        nx = vxDelta / distance;
                        ny = vyDelta / distance;
                        nz = vzDelta / distance;
                    }
                    var before = 0.5 * order[index].MassKg *
                                 (vx[index] * vx[index] + vy[index] * vy[index] +
                                  vz[index] * vz[index]);
                    vx[index] += nx * queued.ImpulseMagnitude;
                    vy[index] += ny * queued.ImpulseMagnitude;
                    vz[index] += nz * queued.ImpulseMagnitude;
                    var after = 0.5 * order[index].MassKg *
                                (vx[index] * vx[index] + vy[index] * vy[index] +
                                 vz[index] * vz[index]);
                    applied.Add(new AppliedSweepHit(queued.InteractionId,
                        order[index].EntityId, after - before));
                }
            }
            queuedSweeps.Clear();
            lastAppliedSweeps = applied.Count == 0
                ? Array.Empty<AppliedSweepHit>()
                : applied.ToArray();
        }

        /// <summary>
        /// One fixed Tick. The step arithmetic runs in double, in the oracle's
        /// operation order, over the writer-owned state; the resulting pose and
        /// velocity are then read back once into the runtime's
        /// <see cref="Vector3"/> ball-state copy, so the published values stay
        /// within one float32 rounding of the oracle instead of accumulating
        /// one per Tick. 02 section 2.3 disclaims bit identity, not the
        /// operations or their order.
        /// </summary>
        public void Advance(int tick, float deltaTimeSeconds)
        {
            if (!configured)
                throw new InvalidOperationException("writer_not_setup");

            var delta = (double)deltaTimeSeconds;
            var minimum = collision.Region.min;
            var maximum = collision.Region.max;
            var count = order.Length;
            var wallContacts = 0;
            ApplyQueuedSweeps(delta);
            for (var index = 0; index < count; index++)
            {
                if (order[index].Membership == "held")
                    continue;
                px[index] += vx[index] * delta;
                py[index] += vy[index] * delta;
                pz[index] += vz[index] * delta;
            }

            for (var index = 0; index < count; index++)
            {
                var ball = order[index];
                if (ball.Membership == "held")
                    continue;
                var radius = (double)ball.RadiusM;
                var lowerX = minimum.x + radius;
                var upperX = maximum.x - radius;
                if (px[index] < lowerX)
                {
                    px[index] = lowerX + (lowerX - px[index]);
                    vx[index] = Math.Abs(vx[index]);
                    wallContacts++;
                }
                else if (px[index] > upperX)
                {
                    px[index] = upperX - (px[index] - upperX);
                    vx[index] = -Math.Abs(vx[index]);
                    wallContacts++;
                }

                var lowerY = minimum.y + radius;
                var upperY = maximum.y - radius;
                if (py[index] < lowerY)
                {
                    py[index] = lowerY + (lowerY - py[index]);
                    vy[index] = Math.Abs(vy[index]);
                    wallContacts++;
                }
                else if (py[index] > upperY)
                {
                    py[index] = upperY - (py[index] - upperY);
                    vy[index] = -Math.Abs(vy[index]);
                    wallContacts++;
                }

                var lowerZ = minimum.z + radius;
                var upperZ = maximum.z - radius;
                if (pz[index] < lowerZ)
                {
                    pz[index] = lowerZ + (lowerZ - pz[index]);
                    vz[index] = Math.Abs(vz[index]);
                    wallContacts++;
                }
                else if (pz[index] > upperZ)
                {
                    pz[index] = upperZ - (pz[index] - upperZ);
                    vz[index] = -Math.Abs(vz[index]);
                    wallContacts++;
                }
            }

            var freeCount = 0;
            for (var index = 0; index < count; index++)
                if (order[index].Membership == "free")
                    freeOrder[freeCount++] = index;

            var bound = Math.Max(0, iterationBound());
            var resolutions = 0;
            var pairContacts = 0;
            var exhausted = false;
            for (var firstIndex = 0; firstIndex < freeCount; firstIndex++)
            {
                var first = freeOrder[firstIndex];
                for (var secondIndex = firstIndex + 1; secondIndex < freeCount; secondIndex++)
                {
                    var second = freeOrder[secondIndex];
                    var dx = px[second] - px[first];
                    var dy = py[second] - py[first];
                    var dz = pz[second] - pz[first];
                    var distanceSquared = dx * dx + dy * dy + dz * dz;
                    var reach = (double)order[first].RadiusM + order[second].RadiusM;
                    if (distanceSquared >= reach * reach || distanceSquared == 0.0)
                        continue;
                    var approaching = (vx[first] - vx[second]) * dx +
                                      (vy[first] - vy[second]) * dy +
                                      (vz[first] - vz[second]) * dz;
                    if (approaching <= 0.0)
                        continue;
                    if (resolutions >= bound)
                    {
                        exhausted = true;
                        continue;
                    }
                    var distance = Math.Sqrt(distanceSquared);
                    var normalX = dx / distance;
                    var normalY = dy / distance;
                    var normalZ = dz / distance;
                    var firstMass = (double)order[first].MassKg;
                    var secondMass = (double)order[second].MassKg;
                    var impulseScale = 2.0 * approaching /
                        ((firstMass + secondMass) * distance);
                    vx[first] -= impulseScale * normalX * secondMass;
                    vy[first] -= impulseScale * normalY * secondMass;
                    vz[first] -= impulseScale * normalZ * secondMass;
                    vx[second] += impulseScale * normalX * firstMass;
                    vy[second] += impulseScale * normalY * firstMass;
                    vz[second] += impulseScale * normalZ * firstMass;
                    resolutions++;
                    pairContacts++;
                }
            }

            for (var index = 0; index < count; index++)
            {
                var ball = order[index];
                ball.PositionM = new Vector3((float)px[index], (float)py[index],
                    (float)pz[index]);
                ball.VelocityMPerS = new Vector3((float)vx[index], (float)vy[index],
                    (float)vz[index]);
            }
            LastContactFacts = new ContactStepFacts(wallContacts, pairContacts, exhausted);
        }

        public void Teardown()
        {
            configured = false;
            order = null;
            collision = null;
            configuration = null;
            px = py = pz = vx = vy = vz = null;
            freeOrder = null;
            queuedSweeps.Clear();
            lastAppliedSweeps = Array.Empty<AppliedSweepHit>();
            LastContactFacts = new ContactStepFacts(0, 0, false);
        }

        /// <summary>
        /// The complete frozen configuration record for this writer. It names
        /// every 02 section 3.1 field that applies to a writer with no PhysX
        /// body, the declared-uncalibrated mass and radius, and the fixed Tick;
        /// it carries no engine field the writer does not use.
        /// </summary>
        public IReadOnlyList<string> ConfigurationRecord
        {
            get
            {
                var record = new List<string>
                {
                    "writer_configuration_id=" + ConfigurationIdValue,
                    "writer_kind=custom_hard_sphere",
                    "writer_disposition=product_trial",
                    "physx_ball_state_write=none",
                    "fixed_delta_time_id=" + FrozenWriterConfiguration.FixedDeltaTimeId,
                    "fixed_delta_time_s=" + Number(FrozenWriterConfiguration.FixedDeltaTimeSeconds),
                    "integration=" + IntegrationId,
                    "contact_ordering=" + ContactOrdering,
                    "boundary_ordering=" + BoundaryOrdering,
                    "equal_time_tie_break=" + EqualTimeTieBreak,
                    "contact_iteration_bound=" + iterationBound().ToString(
                        CultureInfo.InvariantCulture),
                    "pair_impulse=2*normal_relative_speed/(mass_sum*contact_normal)",
                    "normal_restitution_model=perfectly_elastic_normal_exchange",
                    "restitution=" + Number(1f),
                    "tangential_friction=" + Number(0f),
                    "ball_mass_kg=" + Number(FrozenWriterConfiguration.BallMassKg),
                    "ball_radius_m=" + Number(FrozenWriterConfiguration.BallRadiusM),
                    "ball_mass_radius_evidence=" + FrozenWriterConfiguration.Uncalibrated,
                    "gravity_m_per_s2=" + Number(FrozenWriterConfiguration.Gravity.x) + "," +
                    Number(FrozenWriterConfiguration.Gravity.y) + "," +
                    Number(FrozenWriterConfiguration.Gravity.z),
                    "linear_damping=" + Number(FrozenWriterConfiguration.LinearDamping),
                    "angular_damping=" + Number(FrozenWriterConfiguration.AngularDamping),
                    "sleep_enabled=false",
                    "initial_speed_scale_m_per_s=" +
                    Number(FrozenWriterConfiguration.InitialSpeedScaleMPerS),
                    "spawn_attempt_bound=" + FrozenWriterConfiguration.SpawnAttemptBound,
                    "max_ticks_per_scheduling_slice=" +
                    FrozenWriterConfiguration.MaxTicksPerSchedulingSlice,
                    "seed_expansion_id=" + NamedRandomStreams.ExpansionId,
                    "stream_layout_version=" + NamedRandomStreams.LayoutVersion,
                    "seed_stream_id=" + (configuration?.SeedStreamId ??
                        FrozenWriterConfiguration.DefaultSeedStreamId),
                    "root_seed=" + (configuration?.RootSeed ?? 0UL).ToString(
                        CultureInfo.InvariantCulture),
                    "collision_representation_id=" + (configuration?.CollisionRepresentationId ?? ""),
                    "collision_checksum=" + (configuration?.CollisionChecksum ?? ""),
                    "collision_format_version=" + (configuration?.CollisionFormatVersion ?? ""),
                    "solver_gate_evidence=" + SolverGateEvidence
                };
                return record;
            }
        }

        static string Number(float value) =>
            value.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
