using Amiss.Shell.Simulation;
using UnityEngine;

namespace Amiss.Shell.Cooling
{
    /// <summary>
    /// The palm cooling tool: while both palms stay open and face each other,
    /// the hourglass field art shows between them and balls inside the tube
    /// lose one declared energy unit on each pass, like atoms crossing an
    /// optical trap. A ball slower than the capture threshold is held at the
    /// field centre instead.
    ///
    /// The visual (<see cref="PalmCoolingField"/>) owns the shape; this tool
    /// owns the physics push. Each frame with a live field it submits the two
    /// palm centres, the declared tube radii, the pass unit, and the capture
    /// threshold to the writer through <see cref="IPalmCoolingHost"/>; when the
    /// gesture drops it clears the field: every other ball flies free again,
    /// while a trapped ball is let go at rest. The writer
    /// applies and measures the pass debit itself, and the temperature readout
    /// follows the speed drop. No ledger contact is opened: a pass is a field
    /// event, not a tool impact.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public sealed class PalmCoolingTool : MonoBehaviour
    {
        /// <summary>Pinched waist radius at the tube middle (m). Matches the art.</summary>
        public const float DeclaredWaistRadiusM = 0.006f;

        /// <summary>Field footprint radius at each palm (m). Matches the art.</summary>
        public const float DeclaredMouthRadiusM = 0.07f;

        /// <summary>
        /// Founder balance unit (2026-11-04): one pass through the tube removes
        /// one unit from a ball above the capture threshold. Uncalibrated.
        /// </summary>
        public const double DeclaredPassEnergyDeltaJ = EnergyQuantum.PalmPassJ;

        /// <summary>
        /// Founder trap threshold (2026-11-04): a ball moving no faster than
        /// this inside the field is held at the field centre until the palms
        /// drop the field. Uncalibrated candidate.
        /// </summary>
        public const float DeclaredCaptureSpeedMPerS = 0.5f;

        public const string DeclaredParametersEvidence = EnergyQuantum.EvidenceStatus;

        public static string DeclaredParametersLine =>
            "Palm cooling: waist " + DeclaredWaistRadiusM.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture) + " m · mouth " +
            DeclaredMouthRadiusM.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture) + " m · pass " +
            EnergyQuantum.JoulesText(DeclaredPassEnergyDeltaJ) + " (" +
            EnergyQuantum.PalmPassUnits.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + " unit) · trap ≤ " +
            DeclaredCaptureSpeedMPerS.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture) + " m/s · " +
            DeclaredParametersEvidence;

        public PalmCoolingGesture gesture;
        public PalmCoolingField field;

        IPalmCoolingHost host;
        bool submitted;

        public bool IsCooling => submitted;

        void Reset()
        {
            gesture = GetComponent<PalmCoolingGesture>() ?? GetComponentInParent<PalmCoolingGesture>();
            field = GetComponent<PalmCoolingField>() ?? GetComponentInParent<PalmCoolingField>();
        }

        void Awake()
        {
            if (!gesture) gesture = GetComponent<PalmCoolingGesture>();
            if (!field) field = GetComponent<PalmCoolingField>();
        }

        /// <summary>Composition root hands the producer-owned runtime here.</summary>
        public void Bind(IPalmCoolingHost coolingHost) => host = coolingHost;

        void LateUpdate()
        {
            if (host == null || !host.HasTrial || !host.SupportsPalmCooling)
            {
                if (submitted) { submitted = false; }
                return;
            }
            float strength = gesture ? gesture.Strength : 0f;
            if (gesture != null && gesture.HasPose && strength > 0.001f)
            {
                host.SubmitPalmField(gesture.Left.center, gesture.Right.center,
                    DeclaredWaistRadiusM, DeclaredMouthRadiusM,
                    DeclaredPassEnergyDeltaJ, DeclaredCaptureSpeedMPerS);
                submitted = true;
            }
            else if (submitted)
            {
                host.ClearPalmField();
                submitted = false;
            }
        }

        void OnDisable()
        {
            if (submitted && host != null && host.HasTrial && host.SupportsPalmCooling)
            {
                try { host.ClearPalmField(); } catch (System.Exception) { }
            }
            submitted = false;
        }
    }
}
