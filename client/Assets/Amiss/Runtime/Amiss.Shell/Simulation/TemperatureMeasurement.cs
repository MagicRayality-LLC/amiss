using System;
using System.Collections.Generic;

namespace Amiss.Shell.Simulation
{
    /// <summary>Identity and extent of the declared measurement window.</summary>
    public sealed class MeasurementWindow
    {
        public MeasurementWindow(string windowId, int startTick, int endTick,
            float durationSeconds, int sampleCount)
        {
            if (string.IsNullOrEmpty(windowId))
                throw new ArgumentException("window_id_missing", nameof(windowId));
            if (startTick < 0 || endTick < startTick)
                throw new ArgumentException("window_ticks_invalid", nameof(startTick));
            if (!float.IsFinite(durationSeconds) || durationSeconds < 0f)
                throw new ArgumentException("window_duration_invalid", nameof(durationSeconds));
            if (sampleCount < 0)
                throw new ArgumentException("window_sample_count_invalid", nameof(sampleCount));
            WindowId = windowId;
            StartTick = startTick;
            EndTick = endTick;
            DurationSeconds = durationSeconds;
            SampleCount = sampleCount;
        }

        public string WindowId { get; }
        public int StartTick { get; }
        public int EndTick { get; }
        public float DurationSeconds { get; }
        public int SampleCount { get; }
    }

    /// <summary>
    /// The producer-owned display tuple of approved design 07 section 7.1.
    /// Kelvin is primary; Celsius and Fahrenheit are smaller secondary
    /// integers; only K carries the baseline ratio. Every value derives from
    /// one unrounded <c>T_kin</c> of one measurement, so a consumer never
    /// samples, smooths, or rounds from a second unit.
    /// </summary>
    public sealed class TemperatureDisplayTuple
    {
        public TemperatureDisplayTuple(int kelvin, int celsius, int fahrenheit,
            double kelvinRatioBaseline)
        {
            if (!double.IsFinite(kelvinRatioBaseline))
                throw new ArgumentException("kelvin_ratio_invalid",
                    nameof(kelvinRatioBaseline));
            Kelvin = kelvin;
            Celsius = celsius;
            Fahrenheit = fahrenheit;
            KelvinRatioBaseline = kelvinRatioBaseline;
        }

        public int Kelvin { get; }
        public int Celsius { get; }
        public int Fahrenheit { get; }
        public double KelvinRatioBaseline { get; }

        public static TemperatureDisplayTuple FromKelvin(double kelvin, double initialKelvin)
        {
            if (!double.IsFinite(kelvin) || !double.IsFinite(initialKelvin) ||
                initialKelvin <= 0.0)
                throw new ArgumentException("kelvin_baseline_invalid", nameof(initialKelvin));
            var celsius = kelvin - 273.15;
            var fahrenheit = celsius * 9.0 / 5.0 + 32.0;
            return new TemperatureDisplayTuple(
                (int)Math.Round(kelvin, MidpointRounding.ToEven),
                (int)Math.Round(celsius, MidpointRounding.ToEven),
                (int)Math.Round(fahrenheit, MidpointRounding.ToEven),
                Math.Round(kelvin / initialKelvin, 1, MidpointRounding.ToEven));
        }
    }

    /// <summary>
    /// One immutable temperature measurement from the free-ball set (07
    /// section 3.2). Below the statistical minimum the status is
    /// <c>not_applicable</c> and every temperature and display field is null:
    /// never zero kelvin, NaN, or a single-ball equivalent temperature.
    /// </summary>
    public sealed class TemperatureMeasurement
    {
        public const string ValidStatus = "valid";
        public const string NotApplicableStatus = "not_applicable";

        public TemperatureMeasurement(string measurementId, MeasurementWindow window,
            string status, int nTotal, int nFree, int nHeld, double kTotalJ,
            double kThermalJ, double kDriftJ, double kHeldJ, IReadOnlyList<double> vCmMPerS,
            double meanSpeedMPerS, double vRmsMPerS, double? tK, double? initialTK,
            bool equilibriumSufficient, TemperatureDisplayTuple displayTuple,
            string dofFormula, int statisticalMinimumFreeCount, string modelVersion,
            string calculationVersion)
        {
            if (status != ValidStatus && status != NotApplicableStatus)
                throw new ArgumentException("measurement_status_invalid", nameof(status));
            if (nTotal != nFree + nHeld)
                throw new ArgumentException("measurement_counts_inconsistent", nameof(nFree));
            if (status == ValidStatus)
            {
                if (!tK.HasValue || displayTuple == null)
                    throw new ArgumentException("valid_measurement_missing_temperature",
                        nameof(tK));
            }
            else if (tK.HasValue || displayTuple != null)
            {
                throw new ArgumentException("not_applicable_measurement_has_temperature",
                    nameof(tK));
            }
            MeasurementId = measurementId ?? "";
            Window = window;
            Status = status;
            NTotal = nTotal;
            NFree = nFree;
            NHeld = nHeld;
            KTotalJ = kTotalJ;
            KThermalJ = kThermalJ;
            KDriftJ = kDriftJ;
            KHeldJ = kHeldJ;
            VCmMPerS = vCmMPerS ?? Array.Empty<double>();
            MeanSpeedMPerS = meanSpeedMPerS;
            VRmsMPerS = vRmsMPerS;
            TK = tK;
            InitialTK = initialTK;
            EquilibriumSufficient = equilibriumSufficient && status == ValidStatus &&
                window != null && window.SampleCount >= 2;
            Display = displayTuple;
            DofFormula = dofFormula ?? "";
            StatisticalMinimumFreeCount = statisticalMinimumFreeCount;
            ModelVersion = modelVersion ?? "";
            CalculationVersion = calculationVersion ?? "";
        }

        public string MeasurementId { get; }
        public MeasurementWindow Window { get; }
        public string Status { get; }
        public bool Applicable => Status == ValidStatus;
        public int NTotal { get; }
        public int NFree { get; }
        public int NHeld { get; }
        public double KTotalJ { get; }
        public double KThermalJ { get; }
        public double KDriftJ { get; }
        public double KHeldJ { get; }
        public IReadOnlyList<double> VCmMPerS { get; }
        public double MeanSpeedMPerS { get; }
        public double VRmsMPerS { get; }
        public double? TK { get; }
        public double? InitialTK { get; }
        public bool EquilibriumSufficient { get; }
        public TemperatureDisplayTuple Display { get; }
        public string DofFormula { get; }
        public int StatisticalMinimumFreeCount { get; }
        public string ModelVersion { get; }
        public string CalculationVersion { get; }
    }

    /// <summary>Centered kinetic-energy accounts of one immutable view.</summary>
    public sealed class KineticEnergyAccounts
    {
        public KineticEnergyAccounts(int freeCount, int heldCount, double totalMassKg,
            double kTotalJ, double kThermalJ, double kDriftJ, double kHeldJ,
            IReadOnlyList<double> vCmMPerS)
        {
            FreeCount = freeCount;
            HeldCount = heldCount;
            TotalMassKg = totalMassKg;
            KTotalJ = kTotalJ;
            KThermalJ = kThermalJ;
            KDriftJ = kDriftJ;
            KHeldJ = kHeldJ;
            VCmMPerS = vCmMPerS ?? Array.Empty<double>();
        }

        public int FreeCount { get; }
        public int HeldCount { get; }
        public double TotalMassKg { get; }
        public double KTotalJ { get; }
        public double KThermalJ { get; }
        public double KDriftJ { get; }
        public double KHeldJ { get; }
        public IReadOnlyList<double> VCmMPerS { get; }
    }

    /// <summary>
    /// The declared measurement constants of the temperature model. Every
    /// value mirrors the approved headless oracle and is not a device result:
    /// <c>simulation/runtime.py</c> <c>DECLARED_BALL_MASS_KG</c> and
    /// <c>SCALE_POINT_*</c> (the effective <c>k_B</c> scale point),
    /// <c>ModelDeterminismDeclaration</c> (statistical minimum free count,
    /// model/calculation version), and <c>DEFAULT_UNIT_CONVENTION</c> (degrees
    /// of freedom). 07 section 3.2 fixes the <c>3N_free</c> formula; a strict
    /// momentum variant needs another explicit <c>dof_formula</c> version.
    /// </summary>
    public sealed class TemperatureMeasurementParameters
    {
        /// <summary>
        /// The 07 section 3.2 prototype scale point in the code's internal
        /// units: an equal-mass free set of the declared engine ball mass at
        /// <c>v_rms = 1.1 m/s</c> measures 300 K, so the declared model constant
        /// is <c>k_B_eff = m_ball * v_rms^2 / (3 * T) = 1.3444...e-3 J/K</c>. It
        /// is a model constant, not the physical Boltzmann constant.
        /// </summary>
        public const double ScalePointVRmsMPerS = 1.1;
        public const double ScalePointTK = 300.0;
        public const double EffectiveKBJPerK =
            FrozenWriterConfiguration.BallMassKg * ScalePointVRmsMPerS *
            ScalePointVRmsMPerS / (3.0 * ScalePointTK);

        public static readonly TemperatureMeasurementParameters Declared = new(
            statisticalMinimumFreeCount: 10,
            degreesOfFreedom: 3,
            effectiveKB: EffectiveKBJPerK,
            dofFormula: "3N_free",
            modelVersion: "1.0.0",
            calculationVersion: "1.0.0");

        public TemperatureMeasurementParameters(int statisticalMinimumFreeCount,
            int degreesOfFreedom, double effectiveKB, string dofFormula,
            string modelVersion, string calculationVersion)
        {
            if (statisticalMinimumFreeCount < 0)
                throw new ArgumentException("statistical_minimum_invalid",
                    nameof(statisticalMinimumFreeCount));
            if (degreesOfFreedom <= 0)
                throw new ArgumentException("degrees_of_freedom_invalid",
                    nameof(degreesOfFreedom));
            if (!double.IsFinite(effectiveKB) || effectiveKB <= 0.0)
                throw new ArgumentException("effective_kb_invalid", nameof(effectiveKB));
            if (string.IsNullOrEmpty(dofFormula))
                throw new ArgumentException("dof_formula_missing", nameof(dofFormula));
            StatisticalMinimumFreeCount = statisticalMinimumFreeCount;
            DegreesOfFreedom = degreesOfFreedom;
            EffectiveKB = effectiveKB;
            DofFormula = dofFormula;
            ModelVersion = modelVersion ?? "";
            CalculationVersion = calculationVersion ?? "";
        }

        public int StatisticalMinimumFreeCount { get; }
        public int DegreesOfFreedom { get; }
        public double EffectiveKB { get; }
        public string DofFormula { get; }
        public string ModelVersion { get; }
        public string CalculationVersion { get; }
    }

    /// <summary>
    /// The temperature episode's MeasurementFace. It reads one immutable
    /// published view and performs the 07 section 3.2 centered direct sum in
    /// double precision; a single-precision <c>K_total - K_drift</c>
    /// subtraction is never used. Held balls are absent from every free sum.
    /// </summary>
    public static class TemperatureMeasurementFace
    {
        public const string ComputationPathId = "direct-relative-summation-v1";

        public static KineticEnergyAccounts Energies(IReadOnlyList<PublishedBall> balls)
        {
            if (balls == null)
                throw new ArgumentNullException(nameof(balls));
            var freeCount = 0;
            var heldCount = 0;
            var totalMass = 0.0;
            for (var index = 0; index < balls.Count; index++)
            {
                var ball = balls[index];
                if (ball.Membership == "held")
                    heldCount++;
                else if (ball.Membership == "free")
                {
                    freeCount++;
                    totalMass += ball.MassKg;
                }
            }

            var vCm = new double[3];
            if (totalMass > 0.0)
                for (var axis = 0; axis < 3; axis++)
                {
                    var sum = 0.0;
                    for (var index = 0; index < balls.Count; index++)
                    {
                        var ball = balls[index];
                        if (ball.Membership != "free")
                            continue;
                        sum += Component(ball.VelocityMPerS, axis) * ball.MassKg;
                    }
                    vCm[axis] = sum / totalMass;
                }

            var kTotal = 0.0;
            var kThermal = 0.0;
            var kHeld = 0.0;
            for (var index = 0; index < balls.Count; index++)
            {
                var ball = balls[index];
                var measured = Component(ball.VelocityMPerS, 0);
                var y = Component(ball.VelocityMPerS, 1);
                var z = Component(ball.VelocityMPerS, 2);
                var speedSquared = measured * measured + y * y + z * z;
                if (ball.Membership == "held")
                {
                    kHeld += 0.5 * ball.MassKg * speedSquared;
                    continue;
                }
                if (ball.Membership != "free")
                    continue;
                kTotal += 0.5 * ball.MassKg * speedSquared;
                var dx = measured - vCm[0];
                var dy = y - vCm[1];
                var dz = z - vCm[2];
                kThermal += 0.5 * ball.MassKg * (dx * dx + dy * dy + dz * dz);
            }
            var kDrift = 0.5 * totalMass * (vCm[0] * vCm[0] + vCm[1] * vCm[1] +
                                            vCm[2] * vCm[2]);
            return new KineticEnergyAccounts(freeCount, heldCount, totalMass, kTotal,
                kThermal, kDrift, kHeld, vCm);
        }

        /// <summary>
        /// One temperature measurement from one view. <paramref name="initialTK"/>
        /// is the baseline of the same Trial; a valid measurement refuses
        /// without it rather than inventing a ratio denominator.
        /// </summary>
        public static TemperatureMeasurement Measure(string measurementId,
            IReadOnlyList<PublishedBall> balls, MeasurementWindow window, double? initialTK,
            bool equilibriumSufficient = false,
            TemperatureMeasurementParameters parameters = null)
        {
            parameters ??= TemperatureMeasurementParameters.Declared;
            var energies = Energies(balls);
            if (!double.IsFinite(energies.KTotalJ) ||
                !double.IsFinite(energies.KThermalJ) ||
                !double.IsFinite(energies.KDriftJ))
                throw new InvalidOperationException("non_finite_measurement_state");
            var applicable = energies.FreeCount >= parameters.StatisticalMinimumFreeCount;
            if (applicable && (!initialTK.HasValue || !double.IsFinite(initialTK.Value) ||
                               initialTK.Value <= 0.0))
                throw new ArgumentException("initial_temperature_required",
                    nameof(initialTK));

            double? tK = null;
            TemperatureDisplayTuple tuple = null;
            if (applicable)
            {
                tK = 2.0 * energies.KThermalJ / (energies.FreeCount *
                    parameters.DegreesOfFreedom * parameters.EffectiveKB);
                tuple = TemperatureDisplayTuple.FromKelvin(tK.Value, initialTK.Value);
            }

            var speeds = 0.0;
            var freeSpeeds = 0;
            for (var index = 0; index < balls.Count; index++)
            {
                var ball = balls[index];
                if (ball.Membership != "free")
                    continue;
                var x = Component(ball.VelocityMPerS, 0);
                var y = Component(ball.VelocityMPerS, 1);
                var z = Component(ball.VelocityMPerS, 2);
                speeds += Math.Sqrt(x * x + y * y + z * z);
                freeSpeeds++;
            }
            var meanSpeed = freeSpeeds > 0 ? speeds / freeSpeeds : 0.0;
            var vRms = energies.TotalMassKg > 0.0
                ? Math.Sqrt(2.0 * energies.KTotalJ / energies.TotalMassKg)
                : 0.0;

            return new TemperatureMeasurement(
                measurementId ?? "",
                window,
                applicable
                    ? TemperatureMeasurement.ValidStatus
                    : TemperatureMeasurement.NotApplicableStatus,
                energies.FreeCount + energies.HeldCount,
                energies.FreeCount,
                energies.HeldCount,
                energies.KTotalJ,
                energies.KThermalJ,
                energies.KDriftJ,
                energies.KHeldJ,
                energies.VCmMPerS,
                meanSpeed,
                vRms,
                tK,
                applicable ? initialTK : null,
                equilibriumSufficient,
                tuple,
                parameters.DofFormula,
                parameters.StatisticalMinimumFreeCount,
                parameters.ModelVersion,
                parameters.CalculationVersion);
        }

        static double Component(UnityEngine.Vector3 value, int axis) =>
            axis == 0 ? value.x : axis == 1 ? value.y : value.z;
    }
}
