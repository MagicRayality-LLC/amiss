using System;
using System.Collections.Generic;
using Amiss.Shell.Ports;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The on-device binding behind <see cref="SimulationRuntimePort"/>. It owns
    /// the single live Trial slot, the pinned collision representation, the one
    /// scientific writer, the fixed Tick count, and the published views; it is
    /// the only code that writes ball state. The shell supplies an accepted
    /// start request, presentation reads immutable views, and input reaches the
    /// writer only through a future validated command path.
    /// </summary>
    public sealed class DeviceSimulationRuntime : SimulationRuntimePort, IBatContactHost,
        ISandbagContactHost, IPalmCoolingHost
    {
        sealed class LiveTrial
        {
            public string TrialId;
            public PinnedCollision Collision;
            public FrozenWriterConfiguration Configuration;
            public BallState[] Balls;
            public IScientificWriter Writer;
            public int CompletedTick;
            public int PublicationSequence;
            public TemperatureTrialRuntime Temperature;
            public Vector3? SandbagAnchorM;

            public TrialStateView CreateView() => TrialStateView.From(TrialId, CompletedTick,
                PublicationSequence, Balls, Collision.RepresentationId, Collision.Checksum,
                Writer.ConfigurationId);
        }

        readonly ScientificWriterSlot slot = new();
        readonly Func<IScientificWriter> writerFactory;
        LiveTrial trial;

        /// <summary>
        /// The product binding is the custom hard-sphere writer of 02 section
        /// 3.1. The frozen Unity rigidbody configuration failed the locked
        /// gate and stays archived only, so no default path can reach it.
        /// </summary>
        public DeviceSimulationRuntime(Func<IScientificWriter> writerFactory = null)
        {
            this.writerFactory = writerFactory ?? (() => new CustomHardSphereWriter());
        }

        /// <summary>
        /// Creates one writer with the runtime's declared factory. The solver
        /// gate needs the product writer for its elasticity fixtures, so it
        /// asks here instead of naming a concrete type.
        /// </summary>
        public IScientificWriter CreateWriter() => writerFactory();

        public bool Available => true;
        public bool PauseRequested { get; private set; }
        public string PauseReason { get; private set; } = "";
        public string StopReason { get; private set; } = "";
        public Action<string, string, int, int> SessionEventCommitted;

        /// <summary>
        /// Sticky numerical hold from 02 section 3.1. Once a declared contact
        /// bound is exhausted the Trial stops advancing; only a new Trial
        /// recovers, and no permissive step or energy correction follows.
        /// </summary>
        public string NumericalHold { get; private set; } = "";

        public void RequestPause(string reason)
        {
            var changed = trial != null && !PauseRequested;
            PauseRequested = true;
            PauseReason = reason ?? "";
            if (changed)
                SessionEventCommitted?.Invoke("trial_paused", trial.TrialId,
                    trial.CompletedTick, trial.Temperature.Revision);
        }

        public void RequestResume()
        {
            if (!string.IsNullOrEmpty(NumericalHold))
                return;
            var changed = trial != null && PauseRequested;
            PauseRequested = false;
            PauseReason = "";
            if (changed)
                SessionEventCommitted?.Invoke("trial_resumed", trial.TrialId,
                    trial.CompletedTick, trial.Temperature.Revision);
        }

        public TrialStateView PreviousView { get; private set; }
        public TrialStateView CurrentView { get; private set; }
        public int Tick => trial?.CompletedTick ?? 0;
        public bool HasTrial => trial != null;
        public string LiveTrialId => trial?.TrialId ?? "";

        /// <summary>
        /// The requesting stage's sandbag hover anchor for the live Trial, or
        /// null when no bag was ordered. The bag controller floats one bag
        /// there once per Trial.
        /// </summary>
        public Vector3? LiveSandbagAnchor => trial?.SandbagAnchorM;

        public ScientificWriterSlot Writers => slot;

        public WriterBinding WriterBindingFor(string trialId) => slot.Binding(trialId);

        // ---- IBatContactHost: the one semantic face a bat may use ----------
        // Every member here mutates producer-owned state only. The tool never
        // writes ball state; the writer applies the queued energy and measures
        // its own pre/post ball energy, which is the only work value recorded.

        int IBatContactHost.CurrentTick => Tick;

        int IBatContactHost.ContactCommits => trial?.Temperature?.Revision ?? 0;

        TemperatureReadoutSnapshot IBatContactHost.Readout => trial?.Temperature?.Readout;

        bool IBatContactHost.SupportsToolContacts =>
            trial?.Writer is ISweepEffectWriter && trial.Temperature != null;

        void IBatContactHost.OpenSweptContact(string contactId, string propId, int tick)
        {
            RequireTrial();
            trial.Temperature.OpenContact(contactId, propId, tick);
        }

        void IBatContactHost.SubmitSweep(string contactId, SweptSegment segment,
            double energyDeltaJ)
        {
            RequireTrial();
            if (trial.Writer is not ISweepEffectWriter sweepWriter)
                throw new InvalidOperationException("writer_has_no_tool_effect_face");
            if (!trial.Temperature.HasOpenContact(contactId))
                throw new InvalidOperationException("contact_not_open");
            // The writer resolves the declared contact shape against its own
            // state when it advances, and measures the applied energy. The
            // quantum is the tool's declared balance value, never a panel or
            // model number.
            sweepWriter.QueueSweep(contactId, segment, energyDeltaJ);
        }

        SweptClosure IBatContactHost.CloseSweptContact(string contactId, string reason)
        {
            RequireTrial();
            return trial.Temperature.CloseContact(contactId, reason);
        }

        // ---- ISandbagContactHost: the one semantic face a sandbag may use --
        // Every member here mutates producer-owned state only. The tool never
        // writes ball state; the writer applies the queued absorption and
        // measures its own pre/post ball energy, which is the only absorption
        // recorded.

        bool ISandbagContactHost.HasTrial => HasTrial;

        bool ISandbagContactHost.SupportsSandbagContacts =>
            trial?.Writer is ISandbagEffectWriter && trial.Temperature != null;

        int ISandbagContactHost.CurrentTick => Tick;

        bool ISandbagContactHost.HasOpenContact(string contactId) =>
            trial?.Temperature?.HasOpenContact(contactId) == true;

        bool ISandbagContactHost.OpenContactHasHits(string contactId) =>
            trial?.Temperature?.HasOpenContactHits(contactId) == true;

        TemperatureReadoutSnapshot ISandbagContactHost.Readout => trial?.Temperature?.Readout;

        void ISandbagContactHost.OpenSandbagContact(string contactId, int tick)
        {
            RequireTrial();
            trial.Temperature.OpenContact(contactId, SandbagTool.PropId, tick);
        }

        void ISandbagContactHost.SubmitHoverAbsorber(string contactId, Vector3 centerM,
            float radiusM)
        {
            RequireTrial();
            if (trial.Writer is not ISandbagEffectWriter dampingWriter)
                throw new InvalidOperationException("writer_has_no_tool_effect_face");
            if (!trial.Temperature.HasOpenContact(contactId))
                throw new InvalidOperationException("contact_not_open");
            // The region, absorption cap, and speed floor are the episode's
            // declared prototype values, never a panel or model number.
            dampingWriter.SetHoverAbsorber(contactId, centerM, radiusM,
                SandbagTool.DeclaredEnergyDeltaJ,
                SandbagTool.DeclaredSpeedFloorFactor);
        }

        SweptClosure ISandbagContactHost.CloseSandbagContact(string contactId, string reason)
        {
            RequireTrial();
            return trial.Temperature.CloseContact(contactId, reason);
        }

        void ISandbagContactHost.ClearHoverAbsorber(string contactId)
        {
            if (trial?.Writer is ISandbagEffectWriter dampingWriter)
                dampingWriter.ClearHoverAbsorber(contactId);
        }

        // ---- IPalmCoolingHost: persistent field pose, no contact needed ----
        bool IPalmCoolingHost.HasTrial => HasTrial;

        bool IPalmCoolingHost.SupportsPalmCooling =>
            trial?.Writer is IPalmCoolingWriter && trial.Temperature != null;

        void IPalmCoolingHost.SubmitPalmField(Vector3 endpointA, Vector3 endpointB,
            float waistRadiusM, float mouthRadiusM, double passEnergyDeltaJ,
            float captureSpeedMPerS)
        {
            RequireTrial();
            if (trial.Writer is not IPalmCoolingWriter coolingWriter)
                throw new InvalidOperationException("writer_has_no_tool_effect_face");
            coolingWriter.SetPalmField(endpointA, endpointB,
                waistRadiusM, mouthRadiusM, passEnergyDeltaJ, captureSpeedMPerS);
        }

        void IPalmCoolingHost.ClearPalmField()
        {
            if (trial?.Writer is IPalmCoolingWriter coolingWriter)
                coolingWriter.ClearPalmField();
        }

        /// <summary>
        /// The producer-owned readout of the live Trial: one measurement and
        /// one ledger from the same published view. Presentation reads this and
        /// never recomputes a temperature, conversion, or energy value.
        /// </summary>
        public TemperatureReadoutSnapshot Readout => trial?.Temperature?.Readout;

        /// <summary>
        /// Aborts any open bat or sandbag contact as a tracking-loss closure.
        /// Called when the shell loses the input channel, so no later sample
        /// can attach work to a swing that lost validity. An armed hover
        /// absorber is disarmed with the contact, so a later ball cannot
        /// absorb off-ledger.
        /// </summary>
        public void AbortOpenToolContact()
        {
            if (trial?.Temperature == null || !trial.Temperature.HasAnyOpenContact)
                return;
            trial.Temperature.CloseOpenContacts(
                TemperatureTrialRuntime.TrackingLostAborted);
            if (trial.Writer is ISandbagEffectWriter dampingWriter)
                dampingWriter.ClearHoverAbsorbers();
        }

        void RequireTrial()
        {
            if (trial == null)
                throw new InvalidOperationException("trial_not_prepared");
        }

        public SimulationTrialSummary TrialSummary => trial == null
            ? null
            : new SimulationTrialSummary(trial.TrialId, trial.CompletedTick, trial.Balls.Length,
                trial.Collision.RepresentationId, trial.Collision.Checksum,
                trial.Writer.WriterId, trial.Writer.ConfigurationId, PauseRequested);

        public TrialStartOutcome TryStartTrial(TrialStartRequest request)
        {
            if (request == null)
                return TrialStartOutcome.Refused("", TrialStartRefusal.RequestInvalid);
            if (trial != null)
                return TrialStartOutcome.Refused(request.TrialId, TrialStartRefusal.LiveSlotOccupied);
            if (string.IsNullOrWhiteSpace(request.TrialId))
                return TrialStartOutcome.Refused("", TrialStartRefusal.RequestInvalid);

            if (!CollisionBridge.TryBind(request.Confirmation, request.Pin,
                    out var collision, out var refusal))
                return TrialStartOutcome.Refused(request.TrialId, refusal);

            if (request.BallCount <= 0 || request.BallCount % 2 != 0 ||
                request.BallCount > FrozenWriterConfiguration.SpawnAttemptBound)
                return TrialStartOutcome.Refused(request.TrialId, TrialStartRefusal.SizingInvalid);

            if (request.SandbagAnchorM.HasValue &&
                !Room.RoomGeometry.IsFinite(request.SandbagAnchorM.Value))
                return TrialStartOutcome.Refused(request.TrialId, TrialStartRefusal.RequestInvalid,
                    "sandbag_anchor_invalid");

            if (!Room.RoomGeometry.IsFinite(request.EffectiveVolumeM3) ||
                Mathf.Abs(request.EffectiveVolumeM3 - collision.EffectiveVolumeM3) > 0.0001f)
                return TrialStartOutcome.Refused(request.TrialId, TrialStartRefusal.SizingMismatch);

            var streams = new NamedRandomStreams(request.RootSeed);
            if (!BallSpawner.TrySpawn(streams, request.BallCount,
                    FrozenWriterConfiguration.BallRadiusM, FrozenWriterConfiguration.BallMassKg,
                    FrozenWriterConfiguration.InitialSpeedScaleMPerS, Vector3.zero,
                    collision.Region,
                    collision.Obstacles, FrozenWriterConfiguration.SpawnAttemptBound,
                    out var balls, out var spawnReason))
                return TrialStartOutcome.Refused(request.TrialId, TrialStartRefusal.SpawnFailed,
                    spawnReason);

            var seedStreamId = string.IsNullOrEmpty(request.SeedStreamId)
                ? FrozenWriterConfiguration.DefaultSeedStreamId
                : request.SeedStreamId;
            var configuration = new FrozenWriterConfiguration(collision.RepresentationId,
                collision.Checksum, collision.FormatVersion, request.RootSeed, seedStreamId);

            IScientificWriter writer;
            try
            {
                writer = writerFactory();
            }
            catch (Exception error)
            {
                return TrialStartOutcome.Refused(request.TrialId,
                    TrialStartRefusal.WriterSetupFailed, error.GetType().Name);
            }
            if (writer == null)
                return TrialStartOutcome.Refused(request.TrialId,
                    TrialStartRefusal.WriterSetupFailed, "writer_missing");

            if (!slot.TryBind(request.TrialId, writer,
                    writer.ConfigurationId, out var bindReason))
            {
                try
                {
                    writer.Teardown();
                }
                catch (Exception)
                {
                    // The writer was never bound; its teardown is best effort.
                }
                return TrialStartOutcome.Refused(request.TrialId,
                    writer.Disposition == WriterDisposition.ProductTrial
                        ? TrialStartRefusal.WriterAlreadyBound
                        : TrialStartRefusal.WriterConfigurationRejected,
                    bindReason);
            }

            var candidate = new LiveTrial
            {
                TrialId = request.TrialId,
                Collision = collision,
                Configuration = configuration,
                Balls = balls,
                Writer = writer,
                SandbagAnchorM = request.SandbagAnchorM,
                Temperature = new TemperatureTrialRuntime(
                    () => CurrentView?.Balls ?? Array.Empty<PublishedBall>(),
                    () => trial?.CompletedTick ?? 0,
                    () => request.TrialId)
            };
            candidate.Temperature.SessionEventCommitted = (kind, tick, revision) =>
                SessionEventCommitted?.Invoke(kind, request.TrialId, tick, revision);
            try
            {
                writer.Setup(new SimulationTrialSetup(request.TrialId, balls, collision,
                    configuration));
            }
            catch (Exception error)
            {
                slot.Release(request.TrialId);
                try
                {
                    writer.Teardown();
                }
                catch (Exception)
                {
                    // The writer failed to set up; its partial teardown is best effort.
                }
                return TrialStartOutcome.Refused(request.TrialId,
                    TrialStartRefusal.WriterSetupFailed, error.Message);
            }

            trial = candidate;
            StopReason = "";
            PauseRequested = false;
            PauseReason = "";
            NumericalHold = "";
            Publish(candidate);
            SessionEventCommitted?.Invoke("trial_started", request.TrialId,
                candidate.CompletedTick, candidate.Temperature.Revision);
            return TrialStartOutcome.Success(request.TrialId, balls.Length,
                collision.RepresentationId, collision.Checksum, writer.WriterId,
                writer.ConfigurationId, writer.ConfigurationRecord);
        }

        /// <summary>
        /// Advances at most <see cref="FrozenWriterConfiguration.MaxTicksPerSchedulingSlice"/>
        /// whole fixed Ticks. A paused runtime advances none; it never merges
        /// steps or catches up on wall-clock time. A step whose writer reports
        /// an exhausted contact bound is a numerical hold: it is not published,
        /// the Tick does not complete, and no later step advances the Trial.
        /// </summary>
        public StepResult Step(int demand)
        {
            if (trial == null)
                return new StepResult(0, 0, PauseRequested);
            if (!string.IsNullOrEmpty(NumericalHold))
                return new StepResult(0, trial.CompletedTick, true);
            if (PauseRequested || demand <= 0)
                return new StepResult(0, trial.CompletedTick, PauseRequested);

            var whole = Mathf.Min(demand,
                FrozenWriterConfiguration.MaxTicksPerSchedulingSlice);
            var executed = 0;
            for (var index = 0; index < whole; index++)
            {
                trial.Writer.Advance(trial.CompletedTick + 1,
                    FrozenWriterConfiguration.FixedDeltaTimeSeconds);
                if (trial.Writer is IContactFactWriter factWriter &&
                    factWriter.LastContactFacts.IterationBoundExhausted)
                {
                    NumericalHold = "contact_iteration_bound_exhausted";
                    RequestPause("numerical_hold:contact_iteration_bound_exhausted");
                    return new StepResult(executed, trial.CompletedTick, true);
                }
                RecordAppliedSweeps(trial);
                RecordAppliedDampings(trial);
                trial.CompletedTick++;
                Publish(trial);
                executed++;
            }
            return new StepResult(executed, trial.CompletedTick, false);
        }

        /// <summary>
        /// Moves the writer's measured tool effect into the open bat contact
        /// that names it. The writer measured it on its own state; no
        /// presentation value, panel reading, or model output contributes to
        /// the work.
        /// </summary>
        void RecordAppliedSweeps(LiveTrial live)
        {
            if (live.Writer is not ISweepEffectWriter sweepWriter ||
                live.Temperature == null)
                return;
            RecordHitsByContact(live.Temperature, sweepWriter.LastAppliedSweeps);
        }

        /// <summary>
        /// Moves each writer-measured damping into the open sandbag contact
        /// that names it. Several bags may absorb in one Tick, so hits are
        /// grouped by interaction id; a hit that names no open contact is
        /// dropped rather than attached to another tool's contact. The writer
        /// measured every value on its own state; no presentation value, panel
        /// reading, or model output contributes to the absorption.
        /// </summary>
        void RecordAppliedDampings(LiveTrial live)
        {
            if (live.Writer is not ISandbagEffectWriter dampingWriter ||
                live.Temperature == null)
                return;
            RecordHitsByContact(live.Temperature, dampingWriter.LastAppliedDampings);
        }

        static void RecordHitsByContact(TemperatureTrialRuntime temperature,
            IReadOnlyList<AppliedSweepHit> hits)
        {
            if (hits == null || hits.Count == 0)
                return;
            var order = new List<string>();
            var groups = new Dictionary<string, List<AppliedSweepHit>>(
                StringComparer.Ordinal);
            for (var index = 0; index < hits.Count; index++)
            {
                var hit = hits[index];
                if (hit == null)
                    continue;
                if (!groups.TryGetValue(hit.InteractionId, out var group))
                {
                    group = new List<AppliedSweepHit>();
                    groups[hit.InteractionId] = group;
                    order.Add(hit.InteractionId);
                }
                group.Add(hit);
            }
            for (var index = 0; index < order.Count; index++)
            {
                var contactId = order[index];
                if (temperature.HasOpenContact(contactId))
                    temperature.RecordAppliedHits(contactId, groups[contactId]);
            }
        }

        public void StopTrial(string reason)
        {
            StopReason = reason ?? "";
            if (trial == null)
                return;
            var active = trial;
            trial = null;
            try
            {
                active.Writer.Teardown();
            }
            catch (Exception)
            {
                // Teardown is best effort; the binding is released regardless.
            }
            slot.Release(active.TrialId);
            PreviousView = null;
            CurrentView = null;
            PauseRequested = false;
            PauseReason = "";
            NumericalHold = "";
        }

        void Publish(LiveTrial live)
        {
            live.PublicationSequence++;
            PreviousView = CurrentView;
            CurrentView = live.CreateView();
            // One measurement and one ledger per published view, produced by
            // the episode's own face and only read by presentation.
            live.Temperature.Publish(live.CompletedTick, CurrentView.ViewId);
        }
    }
}
