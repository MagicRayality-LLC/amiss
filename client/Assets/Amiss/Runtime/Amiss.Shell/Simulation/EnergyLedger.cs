using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// One closed external prop contact with its signed measured work, from
    /// approved design 07 section 4.1. It is a runtime fact: nothing on the
    /// panel, no chart marker, and no model prose may add or edit an entry.
    /// </summary>
    public sealed class EnergyContactEntry
    {
        public EnergyContactEntry(string contactId, string propId, string targetBallId,
            int startTick, int endTick, double workJ, string closureReason)
        {
            if (string.IsNullOrEmpty(contactId))
                throw new ArgumentException("contact_id_missing", nameof(contactId));
            if (string.IsNullOrEmpty(propId))
                throw new ArgumentException("prop_id_missing", nameof(propId));
            if (string.IsNullOrEmpty(targetBallId))
                throw new ArgumentException("target_ball_id_missing", nameof(targetBallId));
            if (string.IsNullOrEmpty(closureReason))
                throw new ArgumentException("closure_reason_missing", nameof(closureReason));
            if (startTick < 0 || endTick < startTick)
                throw new ArgumentException("contact_ticks_invalid", nameof(startTick));
            if (!double.IsFinite(workJ))
                throw new ArgumentException("contact_work_invalid", nameof(workJ));
            ContactId = contactId;
            PropId = propId;
            TargetBallId = targetBallId;
            StartTick = startTick;
            EndTick = endTick;
            WorkJ = workJ;
            ClosureReason = closureReason;
        }

        public string ContactId { get; }
        public string PropId { get; }
        public string TargetBallId { get; }
        public int StartTick { get; }
        public int EndTick { get; }
        public double WorkJ { get; }
        public string ClosureReason { get; }

        public IReadOnlyList<string> Record() => new[]
        {
            "contact_id=" + ContactId,
            "contact_prop_id=" + PropId,
            "contact_target_ball_id=" + TargetBallId,
            "contact_start_tick=" + StartTick.ToString(CultureInfo.InvariantCulture),
            "contact_end_tick=" + EndTick.ToString(CultureInfo.InvariantCulture),
            "contact_work_j=" + EnergyNumber(WorkJ),
            "contact_closure_reason=" + ClosureReason
        };

        internal static string EnergyNumber(double value) =>
            value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One Trial-local energy ledger, mirroring the approved headless model in
    /// <c>simulation/temperature.py</c> (07 section 4.1):
    /// <c>K_total = K_thermal + K_drift</c> and
    /// <c>E_accounted = K_thermal + K_drift + K_held + E_sandbag</c>. The
    /// contact tuple holds external work events only, so its signed algebraic
    /// sum is <see cref="WorkEventsCumulativeKeJ"/>; a derived Trial's initial
    /// energy difference is never a work event.
    /// </summary>
    public sealed class TemperatureEnergyLedger
    {
        public TemperatureEnergyLedger(double initialTotalKeJ, double freeTotalKeJ,
            double kThermalJ, double kDriftJ, double kHeldJ = 0.0, double eSandbagJ = 0.0,
            IReadOnlyList<EnergyContactEntry> contacts = null,
            double? setupEnergyDiffFromSourceJ = null)
        {
            RequireNonNegative(initialTotalKeJ, nameof(initialTotalKeJ));
            RequireNonNegative(freeTotalKeJ, nameof(freeTotalKeJ));
            RequireNonNegative(kThermalJ, nameof(kThermalJ));
            RequireNonNegative(kDriftJ, nameof(kDriftJ));
            RequireNonNegative(kHeldJ, nameof(kHeldJ));
            RequireNonNegative(eSandbagJ, nameof(eSandbagJ));
            if (setupEnergyDiffFromSourceJ.HasValue &&
                !double.IsFinite(setupEnergyDiffFromSourceJ.Value))
                throw new ArgumentException("setup_energy_diff_invalid",
                    nameof(setupEnergyDiffFromSourceJ));
            InitialTotalKeJ = initialTotalKeJ;
            FreeTotalKeJ = freeTotalKeJ;
            KThermalJ = kThermalJ;
            KDriftJ = kDriftJ;
            KHeldJ = kHeldJ;
            ESandbagJ = eSandbagJ;
            Contacts = contacts == null || contacts.Count == 0
                ? EmptyContacts
                : new ReadOnlyCollection<EnergyContactEntry>(
                    new List<EnergyContactEntry>(contacts));
            SetupEnergyDiffFromSourceJ = setupEnergyDiffFromSourceJ;
        }

        static readonly IReadOnlyList<EnergyContactEntry> EmptyContacts =
            Array.AsReadOnly(Array.Empty<EnergyContactEntry>());

        public double InitialTotalKeJ { get; }
        public double FreeTotalKeJ { get; }
        public double KThermalJ { get; }
        public double KDriftJ { get; }
        public double KHeldJ { get; }
        public double ESandbagJ { get; }
        public IReadOnlyList<EnergyContactEntry> Contacts { get; }
        public double? SetupEnergyDiffFromSourceJ { get; }

        public double KTotalJ => KThermalJ + KDriftJ;
        public double EAccountedJ => KThermalJ + KDriftJ + KHeldJ + ESandbagJ;
        public double ClosureResidualJ => FreeTotalKeJ - KTotalJ;

        /// <summary>
        /// The signed algebraic sum of external prop work. Summation keeps the
        /// recorded contact order, the same order the Trial log carries.
        /// </summary>
        public double WorkEventsCumulativeKeJ
        {
            get
            {
                var total = 0.0;
                for (var index = 0; index < Contacts.Count; index++)
                    total += Contacts[index].WorkJ;
                return total;
            }
        }

        /// <summary>
        /// Appends exactly one contact entry. The caller is the runtime at the
        /// contact's declared closure, so one physical contact cannot be
        /// counted twice by a render callback, chart, or measurement consumer.
        /// </summary>
        public TemperatureEnergyLedger WithContact(EnergyContactEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            var contacts = new List<EnergyContactEntry>(Contacts.Count + 1);
            contacts.AddRange(Contacts);
            contacts.Add(entry);
            return new TemperatureEnergyLedger(InitialTotalKeJ, FreeTotalKeJ, KThermalJ,
                KDriftJ, KHeldJ, ESandbagJ, contacts, SetupEnergyDiffFromSourceJ);
        }

        /// <summary>Moves the measured accounts to one published Tick's values.</summary>
        public TemperatureEnergyLedger WithState(double freeTotalKeJ, double kThermalJ,
            double kDriftJ, double kHeldJ) =>
            new(InitialTotalKeJ, freeTotalKeJ, kThermalJ, kDriftJ, kHeldJ, ESandbagJ,
                Contacts, SetupEnergyDiffFromSourceJ);

        public IReadOnlyList<string> Record() => new[]
        {
            "ledger_initial_total_ke_j=" + EnergyContactEntry.EnergyNumber(InitialTotalKeJ),
            "ledger_free_total_ke_j=" + EnergyContactEntry.EnergyNumber(FreeTotalKeJ),
            "ledger_k_thermal_j=" + EnergyContactEntry.EnergyNumber(KThermalJ),
            "ledger_k_drift_j=" + EnergyContactEntry.EnergyNumber(KDriftJ),
            "ledger_k_held_j=" + EnergyContactEntry.EnergyNumber(KHeldJ),
            "ledger_e_sandbag_j=" + EnergyContactEntry.EnergyNumber(ESandbagJ),
            "ledger_work_events_cumulative_ke_j=" +
            EnergyContactEntry.EnergyNumber(WorkEventsCumulativeKeJ),
            "ledger_contact_count=" + Contacts.Count.ToString(CultureInfo.InvariantCulture),
            "ledger_closure_residual_j=" + EnergyContactEntry.EnergyNumber(ClosureResidualJ),
            "ledger_setup_energy_diff_from_source_j=" +
            (SetupEnergyDiffFromSourceJ.HasValue
                ? EnergyContactEntry.EnergyNumber(SetupEnergyDiffFromSourceJ.Value)
                : "")
        };

        static void RequireNonNegative(double value, string name)
        {
            if (!double.IsFinite(value) || value < 0.0)
                throw new ArgumentException(name + "_invalid", name);
        }
    }
}
