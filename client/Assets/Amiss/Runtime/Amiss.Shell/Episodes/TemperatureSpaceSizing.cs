using System;
using System.Collections.Generic;
using System.Globalization;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;
using Amiss.Shell.Simulation;

namespace Amiss.Shell.Episodes
{
    /// <summary>
    /// The temperature model parameters of approved design 07 section 1.1 and
    /// section 2. They mirror the headless oracle's declaration; values 07
    /// section 11 leaves to a room/playability measurement stay labelled
    /// uncalibrated.
    /// </summary>
    public static class TemperatureModelDeclaration
    {
        public const string ModelVersion = "1.0.0";
        public const string CalculationVersion = "1.0.0";
        public const string DofFormula = "3N_free";
        public const float VMinM3 = 3f;
        public const float TargetNumberDensityPerM3 = 8f;
        public const int MinimumBallCount = 36;
        public const int MaximumBallCount = 72;
        public const float LargeRoomWarningM3 = 12f;
        public const float LargeRoomSubspaceM3 = 9f;
        public const string EvidenceStatus = "uncalibrated_declared_not_measured";
    }

    /// <summary>
    /// Immutable <c>SpaceSizing</c> record from 07 section 1.2.
    /// </summary>
    public sealed class SpaceSizing
    {
        public SpaceSizing(string status, float vEffM3, float vMinM3,
            string furnitureDeductionMode, float targetNumberDensityPerM3, int? computedN,
            bool isOpeningOverrideN, bool largeRoomSubspaceApplied, bool largeRoomWarning,
            float? countedVolumeM3, string reasonCode)
        {
            Status = status ?? "";
            VEffM3 = vEffM3;
            VMinM3 = vMinM3;
            FurnitureDeductionMode = furnitureDeductionMode ?? "";
            TargetNumberDensityPerM3 = targetNumberDensityPerM3;
            ComputedN = computedN;
            IsOpeningOverrideN = isOpeningOverrideN;
            LargeRoomSubspaceApplied = largeRoomSubspaceApplied;
            LargeRoomWarning = largeRoomWarning;
            CountedVolumeM3 = countedVolumeM3;
            ReasonCode = reasonCode ?? "";
        }

        public string Status { get; }
        public float VEffM3 { get; }
        public float VMinM3 { get; }
        public string FurnitureDeductionMode { get; }
        public float TargetNumberDensityPerM3 { get; }
        public int? ComputedN { get; }
        public bool IsOpeningOverrideN { get; }
        public bool LargeRoomSubspaceApplied { get; }
        public bool LargeRoomWarning { get; }
        public float? CountedVolumeM3 { get; }
        public string ReasonCode { get; }

        public float? ActualNumberDensityPerM3 =>
            ComputedN.HasValue && VEffM3 > 0f ? ComputedN.Value / VEffM3 : (float?)null;

        public bool IsValid => Status == "valid" && ComputedN.HasValue;

        /// <summary>Evidence lines for the Trial record.</summary>
        public IReadOnlyList<string> Record() => new[]
        {
            "space_sizing_status=" + Status,
            "space_sizing_v_eff_m3=" + Number(VEffM3),
            "space_sizing_v_min_m3=" + Number(VMinM3),
            "space_sizing_deduction=" + FurnitureDeductionMode,
            "space_sizing_target_density_per_m3=" + Number(TargetNumberDensityPerM3),
            "space_sizing_counted_volume_m3=" +
            (CountedVolumeM3.HasValue ? Number(CountedVolumeM3.Value) : ""),
            "space_sizing_computed_n=" +
            (ComputedN.HasValue ? ComputedN.Value.ToString(CultureInfo.InvariantCulture) : ""),
            "space_sizing_is_opening_override=" + (IsOpeningOverrideN ? "true" : "false"),
            "space_sizing_large_room_warning=" + (LargeRoomWarning ? "true" : "false"),
            "space_sizing_large_room_subspace_applied=" +
            (LargeRoomSubspaceApplied ? "true" : "false"),
            "space_sizing_actual_density_per_m3=" +
            (ActualNumberDensityPerM3.HasValue ? Number(ActualNumberDensityPerM3.Value) : ""),
            "space_sizing_reason_code=" + ReasonCode,
            "space_sizing_evidence=" + TemperatureModelDeclaration.EvidenceStatus
        };

        static string Number(float value) =>
            value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The 07 section 2 sizing rule, mirroring simulation/temperature.py:
    /// <c>N = even(clamp(round(rho_N * V_eff), 36, 72))</c>. Rounding uses
    /// round-half-to-even, matching Python's <c>round</c>. A below-minimum or
    /// invalid volume refuses with no balls; a large room reports the warning
    /// and an explicit subspace choice without changing <c>V_eff</c> itself.
    /// </summary>
    public static class TemperatureSpaceSizing
    {
        public const string DefaultDeductionMode =
            "region bounding volume minus overlapping obstacle bounding volumes";

        public static int EvenBallCount(int value) => value % 2 == 0 ? value : value - 1;

        public static SpaceSizing Compute(double vEffM3,
            string furnitureDeductionMode = DefaultDeductionMode,
            bool isOpeningOverrideN = false, int? openingBallCount = null,
            bool applyLargeRoomSubspace = false)
        {
            var mode = string.IsNullOrEmpty(furnitureDeductionMode)
                ? DefaultDeductionMode
                : furnitureDeductionMode;

            if (!double.IsFinite(vEffM3))
                return Refused(0f, mode, isOpeningOverrideN, "invalid_volume");
            if (vEffM3 < TemperatureModelDeclaration.VMinM3)
                return Refused((float)vEffM3, mode, isOpeningOverrideN, "below_minimum_volume");

            var largeRoomWarning =
                vEffM3 >= TemperatureModelDeclaration.LargeRoomWarningM3;
            var countedVolume = vEffM3;
            if (largeRoomWarning && applyLargeRoomSubspace)
                countedVolume = Math.Min(vEffM3, TemperatureModelDeclaration.LargeRoomSubspaceM3);

            int computedN;
            if (isOpeningOverrideN)
            {
                if (!openingBallCount.HasValue || openingBallCount.Value <= 0)
                    throw new ArgumentException("opening_ball_count_required");
                computedN = openingBallCount.Value;
            }
            else
            {
                var rounded = (int)Math.Round(
                    TemperatureModelDeclaration.TargetNumberDensityPerM3 * countedVolume);
                var clamped = Math.Min(Math.Max(rounded,
                    TemperatureModelDeclaration.MinimumBallCount),
                    TemperatureModelDeclaration.MaximumBallCount);
                computedN = EvenBallCount(clamped);
            }

            return new SpaceSizing("valid", (float)vEffM3,
                TemperatureModelDeclaration.VMinM3, mode,
                TemperatureModelDeclaration.TargetNumberDensityPerM3, computedN,
                isOpeningOverrideN, largeRoomWarning && applyLargeRoomSubspace,
                largeRoomWarning, (float)countedVolume, "");
        }

        static SpaceSizing Refused(float vEffM3, string mode, bool isOpeningOverrideN,
            string reasonCode) => new("refused", vEffM3,
            TemperatureModelDeclaration.VMinM3, mode,
            TemperatureModelDeclaration.TargetNumberDensityPerM3, null, isOpeningOverrideN,
            false, false, null, reasonCode);
    }

    /// <summary>
    /// Builds the one accepted-start request for the temperature episode from
    /// the confirmed room and its pinned collision representation. The ball
    /// count always comes from the sizing rule applied to the confirmed
    /// effective volume; nothing here can force 72.
    /// </summary>
    public static class TemperatureTrialSetup
    {
        public const string SeedScope = "what-is-temperature";

        public static TrialStartRequest CreateTrialStart(string trialId,
            RoomConfirmation confirmation, CollisionPin pin, out SpaceSizing sizing)
        {
            sizing = confirmation == null
                ? TemperatureSpaceSizing.Compute(double.NaN)
                : TemperatureSpaceSizing.Compute(confirmation.EffectiveVolumeM3);
            return new TrialStartRequest
            {
                TrialId = trialId ?? "",
                Confirmation = confirmation,
                Pin = pin,
                BallCount = sizing.ComputedN ?? 0,
                EffectiveVolumeM3 = confirmation?.EffectiveVolumeM3 ?? 0f,
                RootSeed = StableSeed(trialId),
                SeedStreamId = FrozenWriterConfiguration.DefaultSeedStreamId
            };
        }

        public static ulong StableSeed(string trialId) =>
            Simulation.StableSeed.From(SeedScope, trialId);
    }
}
