using System;
using System.Collections.Generic;
using System.IO;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// Device-local runtime evidence: the accepted start, the complete frozen
    /// writer configuration, the space sizing that produced the ball count,
    /// and the collision pin. It carries no raw geometry, image, or personal
    /// data, so it can be read back from the headset for review.
    /// </summary>
    [Serializable]
    public sealed class SimulationEvidenceManifest
    {
        public const string CurrentVersion = "1.0.0";

        public string simulation_evidence_version = CurrentVersion;
        public string application_version;
        public string unity_version;
        public string episode_id;
        public string trial_id;
        public string outcome;
        public string reason_code;
        public int ball_count;
        public int completed_tick;
        public float effective_volume_m3;
        public string boundary_label;
        public string collision_representation_id;
        public string collision_checksum;
        public string collision_format_version;
        public string writer_id;
        public string writer_configuration_id;
        public string[] writer_configuration = Array.Empty<string>();
        public string[] space_sizing = Array.Empty<string>();

        // Producer-owned readout captured verbatim from the runtime. A
        // not-applicable measurement leaves the numeric fields empty.
        public string readout_status;
        public string readout_display_primary_unit;
        public string readout_kelvin;
        public string readout_celsius;
        public string readout_fahrenheit;
        public string readout_kelvin_ratio_baseline;
        public int readout_n_free;
        public int readout_n_held;
        public double readout_k_thermal_j;
        public double readout_k_drift_j;
        public double readout_k_held_j;
        public bool readout_equilibrium_sufficient;
        public string readout_model_version;
        public string readout_calculation_version;
        public double readout_ledger_work_events_cumulative_ke_j;
        public int readout_ledger_contact_count;
        public double readout_ledger_e_sandbag_j;
        public double readout_ledger_closure_residual_j;
        public int readout_contact_commits;
        public string readout_panel_text;

        public static SimulationEvidenceManifest Create(string episodeId) => new()
        {
            application_version = Application.version,
            unity_version = Application.unityVersion,
            episode_id = episodeId ?? ""
        };

        public void CaptureTrial(TrialStartOutcome start, SimulationTrialSummary summary,
            RoomConfirmation confirmation, IReadOnlyList<string> sizing)
        {
            var started = start != null && start.Started;
            trial_id = Clean(start?.TrialId ?? summary?.TrialId);
            outcome = started ? "started" : "refused";
            reason_code = Clean(start?.ReasonCode);
            ball_count = started ? start.BallCount : 0;
            completed_tick = summary?.Tick ?? 0;
            effective_volume_m3 = confirmation?.EffectiveVolumeM3 ?? 0f;
            boundary_label = Clean(confirmation?.BoundaryLabel);
            collision_representation_id = Clean(start?.CollisionRepresentationId);
            collision_checksum = Clean(start?.CollisionChecksum);
            collision_format_version = started
                ? CollisionRepresentation.CurrentFormatVersion
                : "";
            writer_id = Clean(start?.WriterId);
            writer_configuration_id = Clean(start?.WriterConfigurationId);
            writer_configuration = Copy(start?.WriterConfigurationRecord);
            space_sizing = Copy(sizing);
        }

        public void CaptureTick(SimulationTrialSummary summary)
        {
            completed_tick = summary?.Tick ?? completed_tick;
        }

        /// <summary>
        /// Captures the producer-owned readout: the one display tuple and the
        /// one ledger, with the render text the panel shows. This is what a
        /// captured artifact can verify without any consumer recomputation.
        /// Values the measurement marks not-applicable stay empty rather than
        /// being written as a number.
        /// </summary>
        public void CaptureReadout(TemperatureReadoutSnapshot readout)
        {
            if (readout?.Measurement == null || readout.Ledger == null)
                return;
            var measurement = readout.Measurement;
            readout_status = Clean(measurement.Status);
            readout_display_primary_unit = "K";
            readout_kelvin = measurement.Display?.Kelvin.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "";
            readout_celsius = measurement.Display?.Celsius.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "";
            readout_fahrenheit = measurement.Display?.Fahrenheit.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "";
            readout_kelvin_ratio_baseline = measurement.Display == null
                ? ""
                : measurement.Display.KelvinRatioBaseline.ToString(
                    "0.0", System.Globalization.CultureInfo.InvariantCulture);
            readout_n_free = measurement.NFree;
            readout_n_held = measurement.NHeld;
            readout_k_thermal_j = measurement.KThermalJ;
            readout_k_drift_j = measurement.KDriftJ;
            readout_k_held_j = measurement.KHeldJ;
            readout_equilibrium_sufficient = measurement.EquilibriumSufficient;
            readout_model_version = Clean(measurement.ModelVersion);
            readout_calculation_version = Clean(measurement.CalculationVersion);
            readout_ledger_work_events_cumulative_ke_j = readout.Ledger.WorkEventsCumulativeKeJ;
            readout_ledger_contact_count = readout.Ledger.Contacts.Count;
            readout_ledger_e_sandbag_j = readout.Ledger.ESandbagJ;
            readout_ledger_closure_residual_j = readout.Ledger.ClosureResidualJ;
            readout_contact_commits = readout.ContactCommits;
            readout_panel_text = Clean(UI.TemperatureReadoutProjection.Compose(readout),
                PanelTextBound);
        }

        /// <summary>
        /// The panel body is four lines of derived text; the default 200-char
        /// bound of <see cref="Clean"/> would truncate the ledger line, so it
        /// gets its own declared bound instead of a widened global one.
        /// </summary>
        public const int PanelTextBound = 600;

        public string ToJson() => JsonUtility.ToJson(this, true);

        public string WriteAtomic(string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "simulation-evidence.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, ToJson());
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
            return path;
        }

        static string[] Copy(IReadOnlyList<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return Array.Empty<string>();
            var copy = new string[lines.Count];
            for (var index = 0; index < lines.Count; index++)
                copy[index] = Clean(lines[index]);
            return copy;
        }

        static string Clean(string value) => Clean(value, 200);

        static string Clean(string value, int bound)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return clean.Length <= bound ? clean : clean.Substring(0, bound);
        }
    }
}
