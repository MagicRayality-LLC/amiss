using System;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>What one sandbag placement attempt produced.</summary>
    public enum SandbagPlaceOutcome
    {
        /// <summary>The placement was refused: no Trial, no writer face, or a contact is already open.</summary>
        NotAccepted,

        /// <summary>The bag floats at the hover anchor with one open contact.</summary>
        Placed
    }

    public sealed class SandbagPlaceResult
    {
        public SandbagPlaceResult(SandbagPlaceOutcome outcome, int tick, string reasonCode,
            string contactId)
        {
            Outcome = outcome;
            Tick = tick;
            ReasonCode = reasonCode ?? "";
            ContactId = contactId ?? "";
        }

        public SandbagPlaceOutcome Outcome { get; }
        public int Tick { get; }
        public string ReasonCode { get; }
        public string ContactId { get; }
        public bool WasPlaced => Outcome == SandbagPlaceOutcome.Placed;
    }

    /// <summary>What one absorption poll produced.</summary>
    public enum SandbagAbsorbOutcome
    {
        /// <summary>No bag is floating; there is nothing to absorb.</summary>
        NotPlaced,

        /// <summary>The bag still floats; no ball has reached it yet.</summary>
        NoAbsorption,

        /// <summary>The first valid impact closed the contact with measured absorption.</summary>
        Absorbed,

        /// <summary>The open contact died (abort or Trial end) before any impact.</summary>
        Aborted
    }

    public sealed class SandbagAbsorbResult
    {
        public SandbagAbsorbResult(SandbagAbsorbOutcome outcome, int tick, string contactId,
            string targetBallId, double absorbedJ)
        {
            Outcome = outcome;
            Tick = tick;
            ContactId = contactId ?? "";
            TargetBallId = targetBallId ?? "";
            AbsorbedJ = absorbedJ;
        }

        public SandbagAbsorbOutcome Outcome { get; }
        public int Tick { get; }
        public string ContactId { get; }
        public string TargetBallId { get; }
        public double AbsorbedJ { get; }
    }

    /// <summary>The bag's one-shot lifecycle: floating, then consumed.</summary>
    public enum SandbagState
    {
        /// <summary>No bag floats; <see cref="SandbagTool.Place"/> may arm one.</summary>
        Ready,

        /// <summary>A bag floats at the hover anchor with one open contact.</summary>
        Placed,

        /// <summary>The floating bag absorbed once and dispersed; placing again floats a fresh bag.</summary>
        Consumed
    }

    /// <summary>
    /// The P0 sandbag of approved design 07 section 4.2: a floating one-shot
    /// absorber that slows the first ball that reaches it. The bag has no
    /// physics body and never falls; it is a hover region the scientific
    /// writer resolves against its own ball state, plus one open contact that
    /// carries the writer-measured absorption into the ledger.
    ///
    /// Semantics mirror the headless oracle
    /// (<c>simulation/runtime.py</c>, <c>register_prop</c> with the default
    /// damping factor, one <c>prop_trigger</c>, first impact consumes):
    /// <list type="bullet">
    /// <item>one placement arms one hover region and opens one contact;</item>
    /// <item>the first valid ball impact damps that ball once, leaves it free,
    /// and consumes the bag — later balls are unaffected;</item>
    /// <item>the closed contact is one ledger entry with signed measured work,
    /// and its negative work raises the separate sandbag account;</item>
    /// <item>placing again after consumption floats a fresh bag with no
    /// inventory, cooldown, or score.</item>
    /// </list>
    ///
    /// The tool writes no ball state. It arms the region through
    /// <see cref="ISandbagContactHost"/>; the writer applies and measures the
    /// damping on its next fixed Ticks, and the runtime records the measured
    /// energy on the one open contact. <see cref="PumpAbsorption"/> is the
    /// only place this tool can close that contact, and it happens at most
    /// once per placement.
    /// </summary>
    public sealed class SandbagTool
    {
        public const string PropId = "sandbag";

        /// <summary>
        /// Prototype hover-body radius from 07 section 4.2's deferred boundary.
        /// A ball counts as reaching the bag when its centre enters this radius
        /// plus its own radius. Uncalibrated, labelled, never presented as measured.
        /// </summary>
        public const float DeclaredAbsorberRadiusM = 0.07f;

        /// <summary>
        /// Prototype damping factor; the absorbed ball keeps this fraction of
        /// its velocity. It mirrors the headless oracle's default prop
        /// registration, and 07 section 4.2 leaves the versioned
        /// mass/contact/restitution parameters to a later measurement.
        /// A smaller factor slows more; 1 would absorb nothing.
        /// </summary>
        public const double DeclaredDampingFactor = 0.5;

        /// <summary>
        /// The declared tool parameters are 07 section 4.2 prototype values, so
        /// the readout states that label instead of presenting them as calibrated.
        /// </summary>
        public const string DeclaredParametersEvidence = "uncalibrated_declared_not_measured";

        /// <summary>The bag parameter label the panel shows under the readout.</summary>
        public static string DeclaredParametersLine =>
            "Sandbag: absorber radius " + DeclaredAbsorberRadiusM.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture) + " m · damping factor " +
            DeclaredDampingFactor.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture) + " · " +
            DeclaredParametersEvidence;

        readonly ISandbagContactHost host;
        int contactCounter;
        string openContactId = "";
        Vector3 hoverCenter;

        public SandbagTool(ISandbagContactHost host)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public SandbagState State { get; private set; } = SandbagState.Ready;

        /// <summary>Where the floating bag hovers while <see cref="State"/> is Placed.</summary>
        public Vector3 HoverCenter => hoverCenter;

        /// <summary>Successful placements; placing again after consumption is a new bag.</summary>
        public int PlacementCount { get; private set; }

        /// <summary>Closed absorptions.</summary>
        public int AbsorptionCount { get; private set; }

        /// <summary>Contacts aborted before any impact.</summary>
        public int AbortedCount { get; private set; }

        /// <summary>Refused placements.</summary>
        public int RefusedCount { get; private set; }

        /// <summary>Cumulative measured absorption across this tool's bags.</summary>
        public double AbsorbedTotalJ { get; private set; }

        /// <summary>Measured absorption of the most recent closed contact.</summary>
        public double LastAbsorbedJ { get; private set; }

        /// <summary>Target ball of the most recent closed contact.</summary>
        public string LastTargetBallId { get; private set; } = "";

        public string OpenContactId => openContactId;

        /// <summary>
        /// Fired once per absorption with the hover anchor and the measured
        /// absorbed joules, so presentation can scale its dispersal. The
        /// ledger entry is already closed; this carries no physics authority.
        /// </summary>
        public event Action<Vector3, double> Absorbed;

        /// <summary>
        /// Floats a fresh bag at the anchor. Refused when no Trial is live,
        /// the writer has no absorber face, a contact is already open, or the
        /// Tick is invalid — never by arming a region the ledger cannot see.
        /// </summary>
        public SandbagPlaceResult Place(Vector3 centerM, int tick)
        {
            if (!host.HasTrial)
                return Refused(tick, "trial_not_prepared");
            if (!host.SupportsSandbagContacts)
                return Refused(tick, "writer_has_no_tool_effect_face");
            if (tick < 0)
                return Refused(tick, "contact_start_tick_invalid");
            if (openContactId.Length > 0)
                return Refused(tick, "contact_already_open");
            if (!IsFinite(centerM))
                return Refused(tick, "hover_anchor_invalid");

            var contactId = "sandbag-contact-" + tick.ToString("D8") + "-" +
                            (++contactCounter).ToString("D4");
            try
            {
                host.OpenSandbagContact(contactId, tick);
                host.SubmitHoverAbsorber(contactId, centerM, DeclaredAbsorberRadiusM);
            }
            catch (InvalidOperationException)
            {
                // A bat swing (or another bag) already holds the single open
                // contact slot; the bag stays grounded instead of absorbing
                // off-ledger.
                return Refused(tick, "contact_already_open");
            }
            catch (ArgumentException)
            {
                return Refused(tick, "hover_anchor_invalid");
            }

            openContactId = contactId;
            hoverCenter = centerM;
            State = SandbagState.Placed;
            PlacementCount++;
            return new SandbagPlaceResult(SandbagPlaceOutcome.Placed, tick, "", contactId);
        }

        /// <summary>
        /// Polls the open contact after the runtime advanced. The first poll
        /// that sees writer-measured damping closes the contact exactly once,
        /// records the absorption, and disperses the bag. A contact that died
        /// underneath (abort or Trial end) returns to Ready with no entry.
        /// </summary>
        public SandbagAbsorbResult PumpAbsorption()
        {
            if (openContactId.Length == 0)
                return new SandbagAbsorbResult(SandbagAbsorbOutcome.NotPlaced,
                    host.CurrentTick, "", "", 0.0);
            if (!host.HasTrial || !host.HasOpenContact ||
                !string.Equals(host.OpenContactId, openContactId, StringComparison.Ordinal))
            {
                var lost = openContactId;
                openContactId = "";
                State = SandbagState.Ready;
                AbortedCount++;
                return new SandbagAbsorbResult(SandbagAbsorbOutcome.Aborted,
                    host.CurrentTick, lost, "", 0.0);
            }
            if (!host.OpenContactHasHits)
                return new SandbagAbsorbResult(SandbagAbsorbOutcome.NoAbsorption,
                    host.CurrentTick, openContactId, "", 0.0);

            var contactId = openContactId;
            openContactId = "";
            var closure = host.CloseSandbagContact(contactId, "absorbed");
            host.ClearHoverAbsorber();
            State = SandbagState.Consumed;
            AbsorptionCount++;
            var absorbed = closure.Contacted && closure.WorkJ < 0.0 ? -closure.WorkJ : 0.0;
            LastAbsorbedJ = absorbed;
            AbsorbedTotalJ += absorbed;
            LastTargetBallId = closure.Entry?.TargetBallId ?? "";
            Absorbed?.Invoke(hoverCenter, absorbed);
            return new SandbagAbsorbResult(SandbagAbsorbOutcome.Absorbed, closure.EndTick,
                contactId, LastTargetBallId, absorbed);
        }

        /// <summary>
        /// Disarms a floating bag without absorbing. The wearer picked the bag
        /// back up or the visual moved away, so the hover region must not stay
        /// behind: the open contact closes with no measured work and no ledger
        /// entry, and the next float arms a fresh one. Returns false when no
        /// bag was floating.
        /// </summary>
        public bool Withdraw(string reason)
        {
            if (openContactId.Length == 0)
                return false;
            var contactId = openContactId;
            openContactId = "";
            hoverCenter = default;
            State = SandbagState.Ready;
            if (host.HasTrial && host.HasOpenContact &&
                string.Equals(host.OpenContactId, contactId, StringComparison.Ordinal))
            {
                try
                {
                    host.CloseSandbagContact(contactId, reason ?? "withdrawn");
                }
                catch (InvalidOperationException)
                {
                    // The Trial closed the contact underneath; it is already
                    // gone and no work is attached.
                }
                host.ClearHoverAbsorber();
            }
            return true;
        }

        /// <summary>The producer-owned readout; this tool never recomputes it.</summary>
        public TemperatureReadoutSnapshot Readout => host.Readout;

        SandbagPlaceResult Refused(int tick, string reason)
        {
            RefusedCount++;
            return new SandbagPlaceResult(SandbagPlaceOutcome.NotAccepted, tick, reason, "");
        }

        static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
