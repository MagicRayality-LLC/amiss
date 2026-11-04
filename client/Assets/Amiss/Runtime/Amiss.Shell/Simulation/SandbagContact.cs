using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// A writer that resolves a hovering sandbag absorber against its own
    /// state. The tool supplies only the normalized hover region and the
    /// declared energy quantum; the writer decides which free ball, if any, a
    /// Tick's motion carried into the region, removes that quantum from the
    /// ball's kinetic energy, and measures the ball-energy change it applied —
    /// the only value the energy ledger accepts (02 section 4.1).
    ///
    /// It mirrors the approved headless oracle
    /// (<c>simulation/runtime.py</c>, <c>register_prop</c> /
    /// <c>command_effect(prop_trigger)</c> / <c>record_facts</c>): one
    /// one-shot consumable, first valid impact only, absorption applied once,
    /// never repeated. Absorption hits reuse the <see cref="AppliedSweepHit"/>
    /// record shape (interaction id, target ball id, signed measured energy
    /// delta).
    /// </summary>
    public interface ISandbagEffectWriter
    {
        /// <summary>
        /// Arms one one-shot hover region. Each armed region is independent:
        /// arming a second region does not replace the first, and only the
        /// first valid impact consumes the region it names. Re-arming the same
        /// interaction id replaces that region's pending state.
        /// </summary>
        void SetHoverAbsorber(string interactionId, Vector3 centerM, float radiusM,
            double energyDeltaJ);

        /// <summary>Disarms one region without absorbing; a later impact there absorbs nothing.</summary>
        void ClearHoverAbsorber(string interactionId);

        /// <summary>Disarms every armed region, used when the input channel is lost.</summary>
        void ClearHoverAbsorbers();

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

        /// <summary>True while the named bag contact is open.</summary>
        bool HasOpenContact(string contactId);

        /// <summary>True once the writer's measured damping reached the named contact.</summary>
        bool OpenContactHasHits(string contactId);

        TemperatureReadoutSnapshot Readout { get; }
        void OpenSandbagContact(string contactId, int tick);
        void SubmitHoverAbsorber(string contactId, Vector3 centerM, float radiusM);
        SweptClosure CloseSandbagContact(string contactId, string reason);
        void ClearHoverAbsorber(string contactId);
    }
}
