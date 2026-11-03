using System;
using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The writer-measured effect of one applied sweep impulse: the energy
    /// delta the scientific writer observed on its own state. 02 section 4.1
    /// gives the writer this authority; no presentation or measurement
    /// consumer recomputes it.
    /// </summary>
    public sealed class AppliedSweepHit
    {
        public AppliedSweepHit(string interactionId, string entityId, double energyDeltaJ)
        {
            InteractionId = interactionId ?? "";
            EntityId = entityId ?? "";
            EnergyDeltaJ = energyDeltaJ;
        }

        public string InteractionId { get; }
        public string EntityId { get; }
        public double EnergyDeltaJ { get; }
    }

    /// <summary>
    /// A writer that resolves and applies swept tool impulses. The writer is
    /// the only side that knows its own state, so it also decides which balls
    /// a sweep reached and how much energy each one gained; the caller supplies
    /// only the normalized pose, velocity, swept radius, and declared impulse.
    ///
    /// It mirrors <c>HeadlessBallWriter._apply_sweep</c> of the approved
    /// oracle: for each free ball in ascending entity-id order, project the
    /// ball onto the swept segment (origin → origin + velocity·Δt, clamped to
    /// the segment), and when the distance to that segment is within
    /// <c>swept radius + ball radius</c>, add the impulse along the direction
    /// from the closest point to the ball centre and measure the energy delta.
    /// Effects are applied before integration, in queue order.
    /// </summary>
    public interface ISweepEffectWriter
    {
        void QueueSweep(string interactionId, Vector3 origin, Vector3 velocityMPerS,
            float sweptRadiusM, double impulseMagnitude);

        /// <summary>Measured hits of the most recently advanced Tick, in apply order.</summary>
        IReadOnlyList<AppliedSweepHit> LastAppliedSweeps { get; }
    }

    /// <summary>
    /// The closed result of one bat contact. A closure exists once per contact;
    /// a render callback, chart marker, measurement consumer, or repeated close
    /// cannot produce a second one. Only a contacted closure carries an entry
    /// and a semantic commit.
    /// </summary>
    public sealed class SweptClosure
    {
        public SweptClosure(string contactId, string propId, int startTick, int endTick,
            double workJ, string closureReason, EnergyContactEntry entry, bool semanticCommit)
        {
            ContactId = contactId ?? "";
            PropId = propId ?? "";
            StartTick = startTick;
            EndTick = endTick;
            WorkJ = workJ;
            ClosureReason = closureReason ?? "";
            Entry = entry;
            SemanticCommit = semanticCommit;
        }

        public string ContactId { get; }
        public string PropId { get; }
        public int StartTick { get; }
        public int EndTick { get; }
        public double WorkJ { get; }
        public string ClosureReason { get; }
        public EnergyContactEntry Entry { get; }
        public bool SemanticCommit { get; }
        public bool Contacted => Entry != null;
    }

    /// <summary>
    /// The producer-owned readout of approved design 07 sections 3.2 and 4.1:
    /// one measurement and one ledger from the same Trial, Tick, and published
    /// view. The panel renders these values; it never recomputes a temperature,
    /// a unit conversion, or an energy account.
    /// </summary>
    public sealed class TemperatureReadoutSnapshot
    {
        public TemperatureReadoutSnapshot(string trialId, int tick, string viewId,
            TemperatureMeasurement measurement, TemperatureEnergyLedger ledger,
            int contactCommits)
        {
            TrialId = trialId ?? "";
            Tick = tick;
            ViewId = viewId ?? "";
            Measurement = measurement;
            Ledger = ledger;
            ContactCommits = contactCommits;
        }

        public string TrialId { get; }
        public int Tick { get; }
        public string ViewId { get; }
        public TemperatureMeasurement Measurement { get; }
        public TemperatureEnergyLedger Ledger { get; }
        public int ContactCommits { get; }

        /// <summary>A display tuple exists only for an applicable measurement.</summary>
        public TemperatureDisplayTuple Display => Measurement?.Display;
    }

    /// <summary>The one semantic face a bat needs from the Trial owner.</summary>
    public interface IBatContactHost
    {
        bool HasTrial { get; }

        /// <summary>
        /// True when the bound writer can apply a tool sweep. A writer without
        /// that face means the bat cannot produce work at all, which the tool
        /// reports instead of pretending a swing happened.
        /// </summary>
        bool SupportsToolContacts { get; }

        int CurrentTick { get; }
        int ContactCommits { get; }
        TemperatureReadoutSnapshot Readout { get; }
        void OpenSweptContact(string contactId, string propId, int tick);
        void SubmitSweep(string contactId, Vector3 origin, Vector3 velocityMPerS,
            float sweptRadiusM);
        SweptClosure CloseSweptContact(string contactId, string reason);
    }
}
