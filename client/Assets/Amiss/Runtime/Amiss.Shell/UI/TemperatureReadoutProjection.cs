using System.Collections.Generic;
using System.Globalization;
using Amiss.Shell.Simulation;

namespace Amiss.Shell.UI
{
    /// <summary>
    /// Renders the producer-owned readout of approved design 07 section 7.1
    /// into the fixed <c>readout.primary</c> slot. It is a pure projection: it
    /// copies the one display tuple and the one ledger it is handed and never
    /// samples, smooths, converts, or rounds a temperature itself.
    ///
    /// A <c>not_applicable</c> measurement prints its state and shows no
    /// number, so the panel cannot present zero kelvin, NaN, or a single-ball
    /// equivalent temperature. The permanent scale label stays visible with
    /// every readout.
    /// </summary>
    public static class TemperatureReadoutProjection
    {
        /// <summary>
        /// The input line when the profile in force is the declared candidate
        /// this build installs (<c>quest-pro-candidate-unmeasured</c>). The
        /// line names the profile and states that its limits are uncalibrated,
        /// so no surface presents those numbers as measured. It stays one
        /// wrapped line in the readout column, which keeps the slot inside its
        /// eight-line budget.
        /// </summary>
        public static readonly string ProfileCandidate =
            CandidateLine(Input.InputLimitProfile.CandidateUnmeasured().ProfileVersion);

        /// <summary>
        /// The fail-closed declaration: a profile that is not locked admits no
        /// swing sample, so the slot says why no swing can produce work. It is
        /// a local state label, never a fabricated limit.
        /// </summary>
        public const string ProfileUnmeasured =
            "Input: swings refused — the input limit profile is declared-unmeasured.";

        /// <summary>
        /// The slot's input line, selected from the profile in force. A locked
        /// profile is the declared candidate, labelled with its own version and
        /// the uncalibrated status; the fail-closed
        /// <see cref="Input.InputLimitProfile.Unmeasured"/> keeps the refusal
        /// wording. A null profile means the build's own install, which is the
        /// declared candidate.
        /// </summary>
        public static string ProfileLine(Input.InputLimitProfile profile = null)
        {
            if (profile == null)
                return ProfileCandidate;
            return profile.Locked ? CandidateLine(profile.ProfileVersion) : ProfileUnmeasured;
        }

        static string CandidateLine(string profileVersion) =>
            "Input: declared candidate " + profileVersion + "; uncalibrated.";

        public const string ScaleLabel =
            "Model temperature (model K), not a room-temperature measurement.";

        public const string NoProducer =
            "Measurement unavailable: no episode producer is bound.";

        /// <summary>
        /// The <c>readout.primary</c> body. <paramref name="readout"/> is the
        /// producer's own snapshot; a null or trial-less snapshot keeps the
        /// honest unavailable text instead of inventing a value.
        /// <paramref name="profile"/> is the profile in force when the caller
        /// holds one; the build's own install is the declared candidate.
        /// </summary>
        public static string Compose(TemperatureReadoutSnapshot readout,
            Input.InputLimitProfile profile = null)
        {
            if (readout?.Measurement == null || readout.Ledger == null)
                return NoProducer;

            var measurement = readout.Measurement;
            var lines = new List<string>(6)
            {
                ScaleLabel,
                TemperatureLine(measurement),
                WindowLine(measurement),
                EnergyLine(readout.Ledger),
                BatTool.DeclaredParametersLine,
                ProfileLine(profile)
            };
            return string.Join("\n", lines);
        }

        /// <summary>
        /// The readout's own temperature line. With a display tuple it is the
        /// one-line tuple; without one it is the not-applicable sentence, and a
        /// missing producer keeps the unavailable sentence. The wall glass
        /// shows this same text instead of rewording a state.
        /// </summary>
        public static string TemperatureLine(TemperatureMeasurement measurement)
        {
            if (measurement == null)
                return NoProducer;
            var tuple = measurement.Display;
            if (!measurement.Applicable || tuple == null)
                return "Temperature: not applicable (" + measurement.NFree +
                       " free balls, minimum " + measurement.StatisticalMinimumFreeCount +
                       "); no temperature value is shown.";
            return "Temperature: " + tuple.Kelvin.ToString(CultureInfo.InvariantCulture) +
                   " K  ·  " + tuple.Celsius.ToString(CultureInfo.InvariantCulture) + " °C" +
                   "  ·  " + tuple.Fahrenheit.ToString(CultureInfo.InvariantCulture) + " °F" +
                   "  ·  " + Ratio(tuple.KelvinRatioBaseline) + "× initial (K baseline)";
        }

        static string WindowLine(TemperatureMeasurement measurement)
        {
            var window = measurement.Window;
            if (window == null)
                return "Window: unavailable; equilibrium not claimed.";
            var minutesLabel = "Window " + Seconds(window.StartTick) + "–" +
                               Seconds(window.EndTick) + " s · " +
                               window.SampleCount.ToString(CultureInfo.InvariantCulture) +
                               " samples · equilibrium " +
                               (measurement.EquilibriumSufficient
                                   ? "sufficient"
                                   : "not yet established");
            return minutesLabel;
        }

        static string EnergyLine(TemperatureEnergyLedger ledger) =>
            "Energy: net work " + Signed(ledger.WorkEventsCumulativeKeJ) +
            " J · K_thermal " + Amount(ledger.KThermalJ) +
            " J · K_drift " + Amount(ledger.KDriftJ) +
            " J · K_held " + Amount(ledger.KHeldJ) +
            " J · sandbag " + Amount(ledger.ESandbagJ) +
            " J · " + ledger.Contacts.Count.ToString(CultureInfo.InvariantCulture) +
            " contacts";

        static string Seconds(int tick) =>
            (tick * FrozenWriterConfiguration.FixedDeltaTimeSeconds)
            .ToString("0.##", CultureInfo.InvariantCulture);

        static string Ratio(double value) =>
            value.ToString("0.0", CultureInfo.InvariantCulture);

        static string Amount(double value) =>
            value.ToString("0.00", CultureInfo.InvariantCulture);

        static string Signed(double value) =>
            (value >= 0.0 ? "+" : "") + value.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
