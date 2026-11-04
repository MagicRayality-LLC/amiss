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
    /// <c>v * dt</c>, reflect each axis overshoot about the pinned boundary
    /// with the normal velocity sign flipped until the ball is inside, sweep
    /// against each pinned wall plane in stable surface-id order, then enumerate
    /// free ball pairs in ascending entity-id order and resolve each overlapping
    /// approaching pair with the perfectly elastic normal impulse
    /// <c>2 (v_rel . n) / m_sum * n</c> along the unit contact normal. Contacts
    /// are counted per axis or plane reflection and per applied pair impulse,
    /// the same way the oracle counts them, and the declared contact-iteration
    /// bound degrades the step into a reported numerical hold rather than
    /// tunnelling or rewriting energy.
    /// </summary>
    public sealed class CustomHardSphereWriter : IScientificWriter, IContactFactWriter,
        ISweepEffectWriter, ISandbagEffectWriter, IPalmCoolingWriter
    {
        public const string ConfigurationIdValue = "custom-hard-sphere-frozen-v3";
        public const string ContactOrdering = "ascending_entity_id_pair_enumeration";
        public const string BoundaryOrdering = "ascending_entity_id_axis_xyz_then_surface_id";
        public const string EqualTimeTieBreak = "stable_entity_id_ordinal";
        public const string IntegrationId = "swept_static_boundary_then_contact_resolution";
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
            public Vector3 EndM;
            public Vector3 OriginVelocityMPerS;
            public Vector3 EndVelocityMPerS;
            public float RadiusM;
            public double EnergyDeltaJ;
        }

        readonly List<QueuedSweep> queuedSweeps = new();
        AppliedSweepHit[] lastAppliedSweeps = Array.Empty<AppliedSweepHit>();
        // One strike per ball per open tool contact: a swing submits a sweep
        // every accepted sample, but the founder's balance unit is per hit,
        // not per sample. The key changes with each new contact id.
        string quantumContactId = "";
        readonly HashSet<string> quantumHits = new();

        /// <summary>
        /// Below this absorbed energy a ball inside the hover region counts as
        /// numerically at rest, not as a valid impact: the bag keeps hovering
        /// instead of spending its one absorption on nothing (SRD TEMP-TOOL
        /// settles an absorption only for ΔE_sandbag &gt; 0). Nominal
        /// absorptions are a fraction of a joule, nine orders above this floor.
        /// </summary>
        const double MinimalAbsorptionJ = 1e-9;

        // Armed hover absorbers and their measured result. Each region persists
        // across Ticks until its first valid impact consumes it, mirroring the
        // oracle's unused → active → consumed lifecycle per prop. Several bags
        // can hover at once, so the regions are keyed by contact id and
        // resolved in stable id order.
        readonly SortedDictionary<string, HoverAbsorber> absorbers =
            new(StringComparer.Ordinal);
        AppliedSweepHit[] lastAppliedDampings = Array.Empty<AppliedSweepHit>();

        struct HoverAbsorber
        {
            public Vector3 CenterM;
            public float RadiusM;
            public double EnergyDeltaJ;
            public float SpeedFloorFactor;
        }

        // Persistent hourglass cooling field between two open palms. While set,
        // every free ball inside the tube (wide at the palms, pinched at the
        // waist, like an optical trap for atoms) feels molasses drag
        // F = -m*beta*v, applied as v *= exp(-beta*dt) each Tick. Unlike the
        // one-shot sandbag, the field stays armed and cools every ball that
        // passes through; the temperature readout follows the speed drop.
        bool palmFieldSet;
        Vector3 palmA;
        Vector3 palmB;
        float palmWaistRadiusM;
        float palmMouthRadiusM;
        double palmPassEnergyDeltaJ;
        float palmCaptureSpeedMPerS;
        bool[] palmTrapped;
        bool[] palmInside;
        int lastPalmCooledCount;
        int lastPalmTrappedCount;
        double lastPalmAbsorbedJ;

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
            palmTrapped = new bool[count];
            palmInside = new bool[count];
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

        public void QueueSweep(string interactionId, SweptSegment segment,
            double energyDeltaJ)
        {
            if (!configured)
                throw new InvalidOperationException("writer_not_setup");
            if (!float.IsFinite(segment.RadiusM) || segment.RadiusM <= 0f)
                throw new ArgumentException("swept_radius_invalid",
                    nameof(segment));
            if (!double.IsFinite(energyDeltaJ) || energyDeltaJ <= 0.0)
                throw new ArgumentException("energy_delta_invalid",
                    nameof(energyDeltaJ));
            if (!Finite(segment.OriginM) || !Finite(segment.EndM) ||
                !Finite(segment.OriginVelocityMPerS) ||
                !Finite(segment.EndVelocityMPerS))
                throw new ArgumentException("sweep_segment_invalid", nameof(segment));
            queuedSweeps.Add(new QueuedSweep
            {
                InteractionId = interactionId ?? "",
                OriginM = segment.OriginM,
                EndM = segment.EndM,
                OriginVelocityMPerS = segment.OriginVelocityMPerS,
                EndVelocityMPerS = segment.EndVelocityMPerS,
                RadiusM = segment.RadiusM,
                EnergyDeltaJ = energyDeltaJ
            });
        }

        static bool Finite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) &&
            float.IsFinite(value.z);

        public IReadOnlyList<AppliedSweepHit> LastAppliedSweeps => lastAppliedSweeps;

        public void SetHoverAbsorber(string interactionId, Vector3 centerM, float radiusM,
            double energyDeltaJ, float speedFloorFactor)
        {
            if (!configured)
                throw new InvalidOperationException("writer_not_setup");
            if (string.IsNullOrEmpty(interactionId))
                throw new ArgumentException("interaction_id_missing", nameof(interactionId));
            if (!float.IsFinite(centerM.x) || !float.IsFinite(centerM.y) ||
                !float.IsFinite(centerM.z))
                throw new ArgumentException("absorber_center_invalid", nameof(centerM));
            if (!float.IsFinite(radiusM) || radiusM <= 0f)
                throw new ArgumentException("absorber_radius_invalid", nameof(radiusM));
            if (!double.IsFinite(energyDeltaJ) || energyDeltaJ <= 0.0)
                throw new ArgumentException("absorber_energy_delta_invalid",
                    nameof(energyDeltaJ));
            if (!float.IsFinite(speedFloorFactor) || speedFloorFactor < 0f ||
                speedFloorFactor > 1f)
                throw new ArgumentException("absorber_speed_floor_invalid",
                    nameof(speedFloorFactor));
            absorbers[interactionId] = new HoverAbsorber
            {
                CenterM = centerM,
                RadiusM = radiusM,
                EnergyDeltaJ = energyDeltaJ,
                SpeedFloorFactor = speedFloorFactor
            };
        }

        public void ClearHoverAbsorber(string interactionId)
        {
            if (string.IsNullOrEmpty(interactionId))
                return;
            absorbers.Remove(interactionId);
        }

        public void ClearHoverAbsorbers() => absorbers.Clear();

        public IReadOnlyList<AppliedSweepHit> LastAppliedDampings => lastAppliedDampings;

        /// <summary>
        /// Arms the persistent hourglass field between two open palms. Mouth
        /// radius is the field footprint at each palm, waist radius the pinched
        /// middle, passEnergy the one balance unit a ball loses on each new
        /// pass through the tube, and captureSpeed the founder's trap
        /// threshold: a ball slower than it is held at the field centre until
        /// the field clears, then released at rest. Mirrors the headless
        /// oracle's set_palm_field.
        /// </summary>
        public void SetPalmField(Vector3 endpointA, Vector3 endpointB,
            float waistRadiusM, float mouthRadiusM, double passEnergyDeltaJ,
            float captureSpeedMPerS = 0f)
        {
            if (!configured)
                throw new InvalidOperationException("writer_not_setup");
            if (!float.IsFinite(endpointA.x) || !float.IsFinite(endpointA.y) ||
                !float.IsFinite(endpointA.z) || !float.IsFinite(endpointB.x) ||
                !float.IsFinite(endpointB.y) || !float.IsFinite(endpointB.z))
                throw new ArgumentException("palm_field_endpoint_invalid");
            if (!float.IsFinite(waistRadiusM) || waistRadiusM <= 0f ||
                !float.IsFinite(mouthRadiusM) || mouthRadiusM <= 0f)
                throw new ArgumentException("palm_field_radius_invalid");
            if (!double.IsFinite(passEnergyDeltaJ) || passEnergyDeltaJ <= 0.0)
                throw new ArgumentException("palm_field_pass_energy_invalid");
            if (!float.IsFinite(captureSpeedMPerS) || captureSpeedMPerS < 0f)
                throw new ArgumentException("palm_field_capture_speed_invalid");
            if ((endpointB - endpointA).sqrMagnitude <= 0f)
                throw new ArgumentException("palm_field_endpoints_coincide");
            palmA = endpointA;
            palmB = endpointB;
            palmWaistRadiusM = waistRadiusM;
            palmMouthRadiusM = mouthRadiusM;
            palmPassEnergyDeltaJ = passEnergyDeltaJ;
            palmCaptureSpeedMPerS = captureSpeedMPerS;
            palmFieldSet = true;
        }

        public void ClearPalmField()
        {
            if (palmTrapped != null)
            {
                // Release every held ball at rest: the trap has cooled it, so
                // dropping the field must not hand back the speed it caught.
                for (var index = 0; index < palmTrapped.Length; index++)
                {
                    if (!palmTrapped[index])
                        continue;
                    palmTrapped[index] = false;
                    if (order[index].Membership != "held")
                        continue;
                    order[index].Membership = "free";
                    vx[index] = 0.0;
                    vy[index] = 0.0;
                    vz[index] = 0.0;
                    order[index].VelocityMPerS = Vector3.zero;
                }
            }
            if (palmInside != null)
                Array.Clear(palmInside, 0, palmInside.Length);
            palmFieldSet = false;
            lastPalmCooledCount = 0;
            lastPalmTrappedCount = 0;
            lastPalmAbsorbedJ = 0.0;
        }

        public bool PalmFieldActive => palmFieldSet;

        /// <summary>Free balls cooled on the most recent Tick.</summary>
        public int LastPalmCooledCount => lastPalmCooledCount;

        /// <summary>Balls trapped at the field centre on the most recent Tick.</summary>
        public int LastPalmTrappedCount => lastPalmTrappedCount;

        /// <summary>Measured kinetic energy removed by the field last Tick (J).</summary>
        public double LastPalmAbsorbedJ => lastPalmAbsorbedJ;

        /// <summary>
        /// Applies the hourglass field each Tick. A free ball inside the tube
        /// slower than the declared capture speed is trapped: it leaves the
        /// free set, keeps the velocity it had, and is pinned to the field
        /// centre until the field clears. Every other inside ball loses the
        /// declared pass energy on each new pass, clamped to its own kinetic
        /// energy. Tube radius interpolates from the mouth at each palm to the
        /// pinched waist in the middle as r(t) = waist +
        /// (mouth-waist)*|2t-1|^1.5. Runs after pair contacts so the cooling
        /// measurement sees the post-collision velocities, like the oracle.
        /// </summary>
        void ApplyPalmField(double deltaSeconds)
        {
            lastPalmCooledCount = 0;
            lastPalmTrappedCount = 0;
            lastPalmAbsorbedJ = 0.0;
            if (!palmFieldSet || deltaSeconds <= 0.0)
                return;
            var axisX = (double)palmB.x - palmA.x;
            var axisY = (double)palmB.y - palmA.y;
            var axisZ = (double)palmB.z - palmA.z;
            var lengthSq = axisX * axisX + axisY * axisY + axisZ * axisZ;
            if (lengthSq <= 0.0)
                return;
            var centerX = 0.5 * ((double)palmA.x + palmB.x);
            var centerY = 0.5 * ((double)palmA.y + palmB.y);
            var centerZ = 0.5 * ((double)palmA.z + palmB.z);
            // A trapped ball rides the moving field centre. It keeps the
            // velocity it had at capture while pinned, and the field's clear
            // path releases it at rest so the trap always cools its catch.
            for (var index = 0; index < order.Length; index++)
            {
                if (!palmTrapped[index])
                    continue;
                if (order[index].Membership != "held")
                {
                    palmTrapped[index] = false;
                    continue;
                }
                px[index] = centerX;
                py[index] = centerY;
                pz[index] = centerZ;
            }

            for (var index = 0; index < order.Length; index++)
            {
                if (order[index].Membership != "free")
                {
                    palmInside[index] = false;
                    continue;
                }
                var relX = px[index] - palmA.x;
                var relY = py[index] - palmA.y;
                var relZ = pz[index] - palmA.z;
                var fraction = (relX * axisX + relY * axisY + relZ * axisZ) / lengthSq;
                var inside = false;
                if (fraction >= 0.0 && fraction <= 1.0)
                {
                    var closestX = palmA.x + axisX * fraction;
                    var closestY = palmA.y + axisY * fraction;
                    var closestZ = palmA.z + axisZ * fraction;
                    var dx = px[index] - closestX;
                    var dy = py[index] - closestY;
                    var dz = pz[index] - closestZ;
                    var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    var tubeR = palmWaistRadiusM +
                        (palmMouthRadiusM - palmWaistRadiusM) *
                        Math.Pow(Math.Abs(2.0 * fraction - 1.0), 1.5);
                    inside = distance <= tubeR + 0.5 * order[index].RadiusM;
                }
                if (!inside)
                {
                    palmInside[index] = false;
                    continue;
                }
                var speed = Math.Sqrt(vx[index] * vx[index] +
                    vy[index] * vy[index] + vz[index] * vz[index]);
                if (palmCaptureSpeedMPerS > 0f && speed <= palmCaptureSpeedMPerS)
                {
                    order[index].Membership = "held";
                    palmTrapped[index] = true;
                    palmInside[index] = false;
                    px[index] = centerX;
                    py[index] = centerY;
                    pz[index] = centerZ;
                    lastPalmTrappedCount++;
                    continue;
                }
                if (palmInside[index])
                    continue;
                // A new pass through the tube removes exactly one balance
                // unit, clamped to the ball's own kinetic energy.
                palmInside[index] = true;
                var mass = (double)order[index].MassKg;
                var before = 0.5 * mass *
                    (vx[index] * vx[index] + vy[index] * vy[index] + vz[index] * vz[index]);
                var removed = Math.Min(palmPassEnergyDeltaJ, before);
                if (removed <= MinimalAbsorptionJ)
                    continue;
                var factor = Math.Sqrt(Math.Max(0.0, 1.0 - removed / before));
                vx[index] *= factor;
                vy[index] *= factor;
                vz[index] *= factor;
                var after = 0.5 * mass *
                    (vx[index] * vx[index] + vy[index] * vy[index] + vz[index] * vz[index]);
                lastPalmCooledCount++;
                lastPalmAbsorbedJ += before - after;
            }
        }

        /// <summary>
        /// Resolves every armed hover absorber after motion and contacts, in
        /// stable interaction-id order. Each region independently damps the
        /// first free ball whose centre entered it, reports that measured
        /// energy loss, and is consumed; the other armed regions stay armed.
        /// The declared energy is a cap, and the declared speed floor limits
        /// how much one bag may take: a slowed ball keeps flying at no less
        /// than the floor fraction of its speed, so it can still reach another
        /// floating bag. Every damped ball stays free with the free-ball count
        /// unchanged.
        /// </summary>
        void ApplyHoverAbsorber()
        {
            if (absorbers.Count == 0)
            {
                if (lastAppliedDampings.Length != 0)
                    lastAppliedDampings = Array.Empty<AppliedSweepHit>();
                return;
            }
            var applied = new List<AppliedSweepHit>();
            var consumed = new List<string>();
            foreach (var pair in absorbers)
            {
                var absorber = pair.Value;
                for (var index = 0; index < order.Length; index++)
                {
                    if (order[index].Membership != "free")
                        continue;
                    var dx = px[index] - absorber.CenterM.x;
                    var dy = py[index] - absorber.CenterM.y;
                    var dz = pz[index] - absorber.CenterM.z;
                    var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (distance > absorber.RadiusM + order[index].RadiusM)
                        continue;
                    var mass = (double)order[index].MassKg;
                    var before = 0.5 * mass *
                        (vx[index] * vx[index] + vy[index] * vy[index] +
                         vz[index] * vz[index]);
                    var removed = Math.Min(absorber.EnergyDeltaJ, before);
                    if (removed <= MinimalAbsorptionJ)
                        continue;
                    var factor = Math.Sqrt(Math.Max(0.0, 1.0 - removed / before));
                    // The energy cap alone can take the whole speed of a ball
                    // that carries about one declared quantum. The floor keeps
                    // the ball flying so a bag slows it instead of stopping it,
                    // and every later floating bag can still trigger.
                    factor = Math.Max(factor, absorber.SpeedFloorFactor);
                    var dampedX = vx[index] * factor;
                    var dampedY = vy[index] * factor;
                    var dampedZ = vz[index] * factor;
                    var after = 0.5 * mass *
                        (dampedX * dampedX + dampedY * dampedY + dampedZ * dampedZ);
                    vx[index] = dampedX;
                    vy[index] = dampedY;
                    vz[index] = dampedZ;
                    applied.Add(new AppliedSweepHit(pair.Key, order[index].EntityId,
                        after - before));
                    consumed.Add(pair.Key);
                    break;
                }
            }
            for (var index = 0; index < consumed.Count; index++)
                absorbers.Remove(consumed[index]);
            lastAppliedDampings = applied.Count > 0
                ? applied.ToArray()
                : Array.Empty<AppliedSweepHit>();
        }

        /// <summary>
        /// Resolves and applies every queued sweep before integration, in queue
        /// order, recording the measured pre/post ball energy after adding the
        /// declared energy. The queue is always cleared, so a sample can never
        /// leak into a later Tick.
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
                if (!string.Equals(queued.InteractionId, quantumContactId,
                        StringComparison.Ordinal))
                {
                    quantumContactId = queued.InteractionId;
                    quantumHits.Clear();
                }
                var segmentX = (double)queued.EndM.x - queued.OriginM.x;
                var segmentY = (double)queued.EndM.y - queued.OriginM.y;
                var segmentZ = (double)queued.EndM.z - queued.OriginM.z;
                var segmentLengthSquared = segmentX * segmentX + segmentY * segmentY +
                                           segmentZ * segmentZ;
                // Ascending entity id: the order array is already sorted, so
                // the traversal is the oracle's stable entity order.
                for (var index = 0; index < order.Length; index++)
                {
                    if (order[index].Membership != "free")
                        continue;
                    // The tool contact shape is the segment from grip to tip;
                    // the ball couples to the closest point on that segment and
                    // to the velocity measured there, so a rotating barrel's
                    // tip moves faster than its grip.
                    var rx = px[index] - queued.OriginM.x;
                    var ry = py[index] - queued.OriginM.y;
                    var rz = pz[index] - queued.OriginM.z;
                    var toolParameter = segmentLengthSquared > 0.0
                        ? Math.Clamp((rx * segmentX + ry * segmentY +
                            rz * segmentZ) / segmentLengthSquared, 0.0, 1.0)
                        : 0.0;
                    var originX = queued.OriginM.x + segmentX * toolParameter;
                    var originY = queued.OriginM.y + segmentY * toolParameter;
                    var originZ = queued.OriginM.z + segmentZ * toolParameter;
                    var velocityX = queued.OriginVelocityMPerS.x +
                        ((double)queued.EndVelocityMPerS.x -
                            queued.OriginVelocityMPerS.x) * toolParameter;
                    var velocityY = queued.OriginVelocityMPerS.y +
                        ((double)queued.EndVelocityMPerS.y -
                            queued.OriginVelocityMPerS.y) * toolParameter;
                    var velocityZ = queued.OriginVelocityMPerS.z +
                        ((double)queued.EndVelocityMPerS.z -
                            queued.OriginVelocityMPerS.z) * toolParameter;
                    // A held-still tool must add no energy even if tracking
                    // jitter keeps a contact open or a ball drifts into it.
                    var toolSpeed = Math.Sqrt(velocityX * velocityX +
                        velocityY * velocityY + velocityZ * velocityZ);
                    if (toolSpeed < EnergyQuantum.MinimumStrikeSpeedMPerS)
                        continue;
                    var dx = velocityX * deltaSeconds;
                    var dy = velocityY * deltaSeconds;
                    var dz = velocityZ * deltaSeconds;
                    var lengthSquared = dx * dx + dy * dy + dz * dz;
                    var sx = px[index] - originX;
                    var sy = py[index] - originY;
                    var sz = pz[index] - originZ;
                    var parameter = lengthSquared > 0.0
                        ? Math.Clamp((sx * dx + sy * dy + sz * dz) / lengthSquared, 0.0, 1.0)
                        : 0.0;
                    var cx = originX + dx * parameter;
                    var cy = originY + dy * parameter;
                    var cz = originZ + dz * parameter;
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
                    if (distance <= 1e-9)
                    {
                        // A ball on the swept path has no meaningful
                        // perpendicular; push it along the tool's motion
                        // instead of a rounding-noise direction. A still tool
                        // keeps the stable +x fallback.
                        var speed = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (speed > 0.0)
                        {
                            nx = dx / speed;
                            ny = dy / speed;
                            nz = dz / speed;
                        }
                    }
                    else
                    {
                        nx = vxDelta / distance;
                        ny = vyDelta / distance;
                        nz = vzDelta / distance;
                    }
                    if (quantumHits.Contains(order[index].EntityId))
                        continue;
                    var before = 0.5 * order[index].MassKg *
                                 (vx[index] * vx[index] + vy[index] * vy[index] +
                                  vz[index] * vz[index]);
                    // Add the declared kinetic energy along the push
                    // direction. Solving |v + p*n|^2 = |v|^2 + 2E/m for the
                    // scalar impulse p keeps the added energy fixed instead
                    // of letting a fast swing inject an unbounded amount.
                    var mass = (double)order[index].MassKg;
                    var alongNormal = nx * vx[index] + ny * vy[index] +
                                      nz * vz[index];
                    var impulse = -alongNormal + Math.Sqrt(
                        alongNormal * alongNormal +
                        2.0 * queued.EnergyDeltaJ / mass);
                    vx[index] += nx * impulse;
                    vy[index] += ny * impulse;
                    vz[index] += nz * impulse;
                    quantumHits.Add(order[index].EntityId);
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

        static double ReflectAxis(double position, ref double velocity, double lower,
            double upper, ref int contacts, int bound, ref bool exhausted)
        {
            while (position < lower || position > upper)
            {
                if (contacts >= bound)
                {
                    exhausted = true;
                    break;
                }
                if (position < lower)
                {
                    position = lower + (lower - position);
                    velocity = Math.Abs(velocity);
                }
                else
                {
                    position = upper - (position - upper);
                    velocity = -Math.Abs(velocity);
                }
                contacts++;
            }
            return position;
        }

        void SweepWallPlanes(int index, double radius, ref int contacts, int bound,
            ref bool exhausted)
        {
            var previous = order[index].PositionM;
            foreach (var surface in collision.Surfaces)
            {
                if (surface.Role != Room.CollisionSurfaceRole.Wall ||
                    surface.Primary != Room.PrimaryRepresentation.Plane)
                    continue;
                var nx = (double)surface.Normal.x;
                var ny = (double)surface.Normal.y;
                var nz = (double)surface.Normal.z;
                var offset = nx * surface.Center.x + ny * surface.Center.y +
                             nz * surface.Center.z;
                var startDistance = nx * previous.x + ny * previous.y +
                                    nz * previous.z - offset;
                var endDistance = nx * px[index] + ny * py[index] +
                                  nz * pz[index] - offset;
                if (endDistance >= radius || endDistance >= startDistance)
                    continue;

                var fraction = startDistance > radius
                    ? (startDistance - radius) / (startDistance - endDistance)
                    : 0.0;
                var contactX = previous.x + (px[index] - previous.x) * fraction;
                var contactY = previous.y + (py[index] - previous.y) * fraction;
                var contactZ = previous.z + (pz[index] - previous.z) * fraction;
                var local = Quaternion.Inverse(surface.Rotation) * new Vector3(
                    (float)(contactX - surface.Center.x),
                    (float)(contactY - surface.Center.y),
                    (float)(contactZ - surface.Center.z));
                if (Math.Abs(local.x) > surface.Size.x * 0.5f + 0.0001f ||
                    Math.Abs(local.y) > surface.Size.y * 0.5f + 0.0001f)
                    continue;
                if (contacts >= bound)
                {
                    exhausted = true;
                    return;
                }

                var remainingX = px[index] - contactX;
                var remainingY = py[index] - contactY;
                var remainingZ = pz[index] - contactZ;
                var contactCorrection = radius -
                    (nx * contactX + ny * contactY + nz * contactZ - offset);
                contactX += nx * contactCorrection;
                contactY += ny * contactCorrection;
                contactZ += nz * contactCorrection;
                var remainingNormal = nx * remainingX + ny * remainingY + nz * remainingZ;
                px[index] = contactX + remainingX - 2.0 * remainingNormal * nx;
                py[index] = contactY + remainingY - 2.0 * remainingNormal * ny;
                pz[index] = contactZ + remainingZ - 2.0 * remainingNormal * nz;
                var normalVelocity = nx * vx[index] + ny * vy[index] + nz * vz[index];
                vx[index] -= 2.0 * normalVelocity * nx;
                vy[index] -= 2.0 * normalVelocity * ny;
                vz[index] -= 2.0 * normalVelocity * nz;
                contacts++;
            }
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
            var bound = Math.Max(0, iterationBound());
            var exhausted = false;
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
                px[index] = ReflectAxis(px[index], ref vx[index], minimum.x + radius,
                    maximum.x - radius, ref wallContacts, bound, ref exhausted);
                py[index] = ReflectAxis(py[index], ref vy[index], minimum.y + radius,
                    maximum.y - radius, ref wallContacts, bound, ref exhausted);
                pz[index] = ReflectAxis(pz[index], ref vz[index], minimum.z + radius,
                    maximum.z - radius, ref wallContacts, bound, ref exhausted);
                SweepWallPlanes(index, radius, ref wallContacts, bound, ref exhausted);
            }

            var freeCount = 0;
            for (var index = 0; index < count; index++)
                if (order[index].Membership == "free")
                    freeOrder[freeCount++] = index;

            var resolutions = wallContacts;
            var pairContacts = 0;
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

            ApplyPalmField(delta);

            ApplyHoverAbsorber();

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
            absorbers.Clear();
            lastAppliedDampings = Array.Empty<AppliedSweepHit>();
            palmFieldSet = false;
            palmTrapped = null;
            palmInside = null;
            lastPalmCooledCount = 0;
            lastPalmTrappedCount = 0;
            lastPalmAbsorbedJ = 0.0;
            order = null;
            collision = null;
            configuration = null;
            px = py = pz = vx = vy = vz = null;
            freeOrder = null;
            queuedSweeps.Clear();
            quantumContactId = "";
            quantumHits.Clear();
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
