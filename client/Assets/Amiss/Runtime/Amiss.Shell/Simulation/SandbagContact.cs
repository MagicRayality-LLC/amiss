using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// A writer that resolves a hovering sandbag absorber against its own
    /// state. The tool supplies only the normalized hover region and the
    /// declared damping factor; the writer decides which free ball, if any, a
    /// Tick's motion carried into the region, damps that ball's velocity, and
    /// measures the ball-energy change it applied — the only value the energy
    /// ledger accepts (02 section 4.1).
    ///
    /// It mirrors the approved headless oracle
    /// (<c>simulation/runtime.py</c>, <c>register_prop</c> /
    /// <c>command_effect(prop_trigger)</c> / <c>record_facts</c>): one
    /// one-shot consumable, first valid impact only, velocity scaled by the
    /// damping factor, absorbed energy accumulated once, never repeated.
    /// Damping hits reuse the <see cref="AppliedSweepHit"/> record shape
    /// (interaction id, target ball id, signed measured energy delta).
    /// </summary>
    public interface ISandbagEffectWriter
    {
        /// <summary>
        /// Arms the one-shot hover region. Arming twice replaces the pending
        /// region; only the first valid impact consumes it.
        /// </summary>
        void SetHoverAbsorber(string interactionId, Vector3 centerM, float radiusM,
            double dampingFactor);

        /// <summary>Disarms without absorbing; a later impact absorbs nothing.</summary>
        void ClearHoverAbsorber();

        /// <summary>Measured damping of the most recently advanced Tick, in apply order.</summary>
        IReadOnlyList<AppliedSweepHit> LastAppliedDampings { get; }
    }

    /// <summary>The one semantic face a sandbag needs from the Trial owner.</summary>
    public interface ISandbagContactHost
    {
        bool HasTrial { get; }

        /// <summary>
        /// True when the bound writer can resolve a hover absorber. A writer
        /// without that face means the bag can hover but never absorb, which
        /// the tool reports instead of pretending a slowdown happened.
        /// </summary>
        bool SupportsSandbagContacts { get; }

        int CurrentTick { get; }
        bool HasOpenContact { get; }
        string OpenContactId { get; }

        /// <summary>True once the writer's measured damping reached the open contact.</summary>
        bool OpenContactHasHits { get; }

        TemperatureReadoutSnapshot Readout { get; }
        void OpenSandbagContact(string contactId, int tick);
        void SubmitHoverAbsorber(string contactId, Vector3 centerM, float radiusM);
        SweptClosure CloseSandbagContact(string contactId, string reason);
        void ClearHoverAbsorber();
    }
}
