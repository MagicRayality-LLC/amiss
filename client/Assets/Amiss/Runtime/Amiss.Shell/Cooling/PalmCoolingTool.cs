using Amiss.Shell.Simulation;
using UnityEngine;

namespace Amiss.Shell.Cooling
{
    /// <summary>
    /// The palm cooling tool: while both palms stay open and face each other,
    /// the hourglass field art shows between them and every ball inside the
    /// tube slows down, like atoms in an optical trap / optical molasses.
    ///
    /// The visual (<see cref="PalmCoolingField"/>) owns the shape; this tool
    /// owns the physics push. Each frame with a live field it submits the two
    /// palm centres plus the declared tube radii and molasses coefficient to
    /// the writer through <see cref="IPalmCoolingHost"/>; when the gesture
    /// drops it clears the field so balls fly free again. The writer damps
    /// velocities itself (v *= exp(-beta*dt)) and the temperature readout
    /// follows the speed drop. No ledger contact is opened: continuous drag
    /// is not a one-shot impact.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public sealed class PalmCoolingTool : MonoBehaviour
    {
        /// <summary>Pinched waist radius at the tube middle (m). Matches the art.</summary>
        public const float DeclaredWaistRadiusM = 0.006f;

        /// <summary>Field footprint radius at each palm (m). Matches the art.</summary>
        public const float DeclaredMouthRadiusM = 0.07f;

        /// <summary>
        /// Prototype molasses coefficient in 1/s at full strength. A ball on
        /// the axis keeps exp(-beta*dt) of its speed per Tick.
        /// </summary>
        public const double DeclaredBetaPerS = 1.5;

        public const string DeclaredParametersEvidence = "uncalibrated_declared_not_measured";

        public static string DeclaredParametersLine =>
            "Palm cooling: waist " + DeclaredWaistRadiusM.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture) + " m · mouth " +
            DeclaredMouthRadiusM.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture) + " m · beta " +
            DeclaredBetaPerS.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture) + " /s · " +
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
                    DeclaredBetaPerS * strength);
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
