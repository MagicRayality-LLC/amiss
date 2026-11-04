using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// A writer that resolves the persistent palm cooling field against its own
    /// state. The field is the hourglass tube between two open palms (wide at
    /// the palms, pinched at the waist, like an optical trap for atoms): every
    /// free ball inside feels molasses drag v *= exp(-beta*dt) each Tick.
    /// It mirrors the headless oracle's set_palm_field / clear_palm_field.
    /// </summary>
    public interface IPalmCoolingWriter
    {
        void SetPalmField(Vector3 endpointA, Vector3 endpointB,
            float waistRadiusM, float mouthRadiusM, double betaPerS);
        void ClearPalmField();
        bool PalmFieldActive { get; }
        int LastPalmCooledCount { get; }
        double LastPalmAbsorbedJ { get; }
    }

    /// <summary>The one semantic face a palm cooling tool needs from the Trial owner.</summary>
    public interface IPalmCoolingHost
    {
        bool HasTrial { get; }
        bool SupportsPalmCooling { get; }
        void SubmitPalmField(Vector3 endpointA, Vector3 endpointB,
            float waistRadiusM, float mouthRadiusM, double betaPerS);
        void ClearPalmField();
    }
}
