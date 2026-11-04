using System;
using System.Collections.Generic;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The temperature Trial's producer side: one energy ledger, the open
    /// swept contact, one measurement per published view, and the readout
    /// projection. It is owned by <see cref="DeviceSimulationRuntime"/>, so
    /// presentation cannot reach it. Work entries, semantic commits, and
    /// display tuples are produced here and only read elsewhere.
    ///
    /// Contact work is the writer-measured ball-energy change of the applied
    /// swept impulse (02 section 4.1); a strike that removes energy therefore
    /// records negative work, and no consumer can re-derive or "repair" it.
    /// One physical contact produces exactly one ledger entry at its closure.
    /// </summary>
    public sealed class TemperatureTrialRuntime
    {
        sealed class SweptContactState
        {
            public string ContactId;
            public string PropId;
            public int StartTick;
            public double AccumulatedJ;
            public string LastTargetId = "";
            public bool Contacted;
        }

        readonly Func<IReadOnlyList<PublishedBall>> viewReader;
        readonly Func<int> tickReader;
        readonly Func<string> trialIdReader;

        SweptContactState open;
        double initialTotalKeJ = -1.0;
        double? initialTK;
        TemperatureEnergyLedger ledger = new(0.0, 0.0, 0.0, 0.0);
        TemperatureMeasurement measurement;
        TemperatureReadoutSnapshot readout;
        int lastTick;
        string lastViewId;

        public TemperatureTrialRuntime(
            Func<IReadOnlyList<PublishedBall>> viewReader,
            Func<int> tickReader,
            Func<string> trialIdReader,
            TemperatureMeasurementParameters parameters = null)
        {
            this.viewReader = viewReader ?? throw new ArgumentNullException(nameof(viewReader));
            this.tickReader = tickReader ?? throw new ArgumentNullException(nameof(tickReader));
            this.trialIdReader = trialIdReader ?? throw new ArgumentNullException(nameof(trialIdReader));
            Parameters = parameters ?? TemperatureMeasurementParameters.Declared;
        }

        public TemperatureMeasurementParameters Parameters { get; }
        public Action<string, int, int> SessionEventCommitted;

        /// <summary>Opens one swept bat contact; a second open contact is refused.</summary>
        public void OpenContact(string contactId, string propId, int startTick)
        {
            if (open != null)
                throw new InvalidOperationException("contact_already_open");
            if (string.IsNullOrEmpty(contactId))
                throw new ArgumentException("contact_id_missing", nameof(contactId));
            if (startTick < 0)
                throw new ArgumentException("contact_start_tick_invalid", nameof(startTick));
            open = new SweptContactState
            {
                ContactId = contactId,
                PropId = propId ?? "",
                StartTick = startTick
            };
        }

        public bool HasOpenContact => open != null;
        public string OpenContactId => open?.ContactId ?? "";

        /// <summary>
        /// Applies the writer-measured hits of one accepted sample to the one
        /// open contact. Exactly one physical contact accumulates here and
        /// becomes one ledger entry at closure.
        /// </summary>
        public void RecordAppliedHits(string contactId, IReadOnlyList<AppliedSweepHit> hits)
        {
            if (open == null || open.ContactId != contactId)
                throw new InvalidOperationException("contact_not_open");
            if (hits == null || hits.Count == 0)
                return;
            open.Contacted = true;
            open.LastTargetId = hits[0].EntityId;
            for (var index = 0; index < hits.Count; index++)
                open.AccumulatedJ += hits[index].EnergyDeltaJ;
        }

        /// <summary>
        /// Closes the open contact exactly once. The closure itself is the
        /// semantic event (02 section 4.1), so every closure is one commit —
        /// mirroring the oracle's <c>close_interaction</c>, which the kernel
        /// applies for every interaction closure. A contacted closure also
        /// appends exactly one signed ledger entry; an empty swing or a
        /// tracking-loss abort records zero work and appends no entry, so no
        /// work, release, or stage evidence is invented from an invalid input.
        /// </summary>
        public SweptClosure CloseContact(string contactId, string reason)
        {
            if (open == null || open.ContactId != contactId)
                throw new InvalidOperationException("contact_not_open");
            var active = open;
            open = null;
            var endTick = Math.Max(active.StartTick, tickReader());
            var closureReason = string.IsNullOrEmpty(reason) ? "separated" : reason;
            var aborted = string.Equals(closureReason, TrackingLostAborted,
                StringComparison.Ordinal);
            Revision++;
            if (aborted)
                AbortedClosures++;
            else
                ClosedContacts++;

            EnergyContactEntry entry = null;
            if (active.Contacted && !aborted)
            {
                entry = new EnergyContactEntry(active.ContactId, active.PropId,
                    active.LastTargetId, active.StartTick, endTick, active.AccumulatedJ,
                    closureReason);
                ledger = ledger.WithContact(entry);
                if (string.Equals(entry.PropId, SandbagTool.PropId, StringComparison.Ordinal) &&
                    entry.WorkJ < 0.0)
                {
                    // The sandbag's measured absorption leaves the free-ball
                    // accounts and joins the separate cumulative sandbag
                    // account, so E_accounted keeps balancing (07 section 4.1).
                    ledger = ledger.WithSandbagAbsorption(-entry.WorkJ);
                }
            }

            // The closure is now part of the readout, so it is republished from
            // the same view the measurement came from. The panel never has to
            // merge a second source to show the closure or its work.
            if (lastViewId != null)
                readout = new TemperatureReadoutSnapshot(trialIdReader(), lastTick,
                    lastViewId, measurement, ledger, Revision);
            if (entry != null)
                SessionEventCommitted?.Invoke("effective_action", endTick, Revision);
            return new SweptClosure(active.ContactId, active.PropId, active.StartTick,
                endTick, entry?.WorkJ ?? 0.0, closureReason, entry, true);
        }

        public const string TrackingLostAborted = "tracking_lost_aborted";

        /// <summary>
        /// True once the writer's measured tool effect reached the open contact.
        /// The sandbag polls this to close its contact on the first absorption.
        /// </summary>
        public bool HasOpenContactHits => open?.Contacted == true;

        /// <summary>Semantic commits: one per contact closure, contacted or not.</summary>
        public int Revision { get; private set; }

        /// <summary>Closures that recorded measured work.</summary>
        public int ClosedContacts { get; private set; }

        /// <summary>Closures aborted by a lost or invalid input sample.</summary>
        public int AbortedClosures { get; private set; }

        public TemperatureEnergyLedger Ledger => ledger;
        public TemperatureMeasurement Measurement => measurement;
        public TemperatureReadoutSnapshot Readout => readout;

        public void Reset()
        {
            open = null;
            initialTotalKeJ = -1.0;
            initialTK = null;
            ledger = new TemperatureEnergyLedger(0.0, 0.0, 0.0, 0.0);
            measurement = null;
            readout = null;
            lastTick = 0;
            lastViewId = null;
            Revision = 0;
            ClosedContacts = 0;
            AbortedClosures = 0;
        }

        /// <summary>
        /// Publishes the readout of one completed Tick. The measurement and the
        /// ledger both read the same immutable view: one unrounded
        /// <c>T_kin</c>, one display tuple, one ledger. A below-minimum free
        /// count yields <c>not_applicable</c> with every numeric field null.
        /// </summary>
        public TemperatureReadoutSnapshot Publish(int tick, string viewId)
        {
            var hadSufficientMeasurement = readout?.Measurement?.EquilibriumSufficient == true;
            var balls = viewReader() ?? Array.Empty<PublishedBall>();
            var energies = TemperatureMeasurementFace.Energies(balls);
            if (initialTK == null && energies.FreeCount >=
                Parameters.StatisticalMinimumFreeCount && energies.TotalMassKg > 0.0)
                initialTK = 2.0 * energies.KThermalJ / (energies.FreeCount *
                    Parameters.DegreesOfFreedom * Parameters.EffectiveKB);

            measurement = TemperatureMeasurementFace.Measure(
                "temperature-" + tick.ToString("D8"), balls,
                new MeasurementWindow("window-" + tick.ToString("D8"), 0, tick,
                    tick * FrozenWriterConfiguration.FixedDeltaTimeSeconds, tick + 1),
                initialTK, false, Parameters);

            if (initialTotalKeJ < 0.0)
            {
                initialTotalKeJ = energies.KTotalJ;
                ledger = new TemperatureEnergyLedger(initialTotalKeJ, energies.KTotalJ,
                    energies.KThermalJ, energies.KDriftJ, energies.KHeldJ,
                    ledger.ESandbagJ, ledger.Contacts, ledger.SetupEnergyDiffFromSourceJ);
            }
            else
            {
                ledger = ledger.WithState(energies.KTotalJ, energies.KThermalJ,
                    energies.KDriftJ, energies.KHeldJ);
            }
            readout = new TemperatureReadoutSnapshot(trialIdReader(), tick, viewId ?? "",
                measurement, ledger, Revision);
            if (measurement.EquilibriumSufficient && !hadSufficientMeasurement)
                SessionEventCommitted?.Invoke("measurement_committed", tick, Revision);
            lastTick = tick;
            lastViewId = viewId ?? "";
            return readout;
        }
    }
}
