using System;
using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>What one sandbag placement attempt produced.</summary>
    public enum SandbagPlaceOutcome
    {
        /// <summary>The placement was refused: no Trial, no writer face, or an invalid anchor.</summary>
        NotAccepted,

        /// <summary>The bag floats at the hover anchor with its own open contact.</summary>
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
    /// Several bags can float at once, and every one of them triggers. Each
    /// placement arms its own hover region and opens its own contact, mirroring
    /// the headless oracle's one-registered-prop-per-bag lifecycle:
    /// <list type="bullet">
    /// <item>one placement arms one hover region and opens one contact;</item>
    /// <item>the first valid ball impact removes the declared energy from that
    /// ball once, leaves it free, and consumes only that bag — other armed
    /// bags keep hovering and later balls still trigger them;</item>
    /// <item>each closed contact is its own ledger entry with signed measured
    /// work, and its negative work raises the separate sandbag account;</item>
    /// <item>placing again after consumption floats a fresh bag with no
    /// inventory, cooldown, or score.</item>
    /// </list>
    ///
    /// The tool writes no ball state. It arms each region through
    /// <see cref="ISandbagContactHost"/>; the writer applies and measures the
    /// absorption on its next fixed Ticks, and the runtime records the measured
    /// energy on the matching open contact. <see cref="PumpAbsorption"/> is the
    /// only place this tool can close contacts, and each placement closes at
    /// most once.
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
        /// Founder balance unit (2026-11-04): the bag removes 10 quanta from
        /// the first ball that reaches it. The writer clamps the removal to
        /// the ball's own kinetic energy so the ball never reverses.
        /// </summary>
        public const double DeclaredEnergyDeltaJ = EnergyQuantum.SandbagJ;

        /// <summary>
        /// The declared tool parameters are 07 section 4.2 prototype values, so
        /// the readout states that label instead of presenting them as calibrated.
        /// </summary>
        public const string DeclaredParametersEvidence = EnergyQuantum.EvidenceStatus;

        /// <summary>The bag parameter label the panel shows under the readout.</summary>
        public static string DeclaredParametersLine =>
            "Sandbag: absorber radius " + DeclaredAbsorberRadiusM.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture) + " m · absorption " +
            EnergyQuantum.JoulesText(DeclaredEnergyDeltaJ) + " (" +
            EnergyQuantum.SandbagUnits.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + " units) · " +
            DeclaredParametersEvidence;

        sealed class Placement
        {
            public Vector3 HoverCenter;
        }

        readonly ISandbagContactHost host;
        readonly SortedDictionary<string, Placement> placements =
            new(StringComparer.Ordinal);
        int contactCounter;
        string lastContactId = "";
        Vector3 hoverCenter;

        public SandbagTool(ISandbagContactHost host)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public SandbagState State { get; private set; } = SandbagState.Ready;

        /// <summary>Where the most recently placed bag hovers.</summary>
        public Vector3 HoverCenter => hoverCenter;

        /// <summary>True while the named placement still floats.</summary>
        public bool HasPlacement(string contactId) =>
            !string.IsNullOrEmpty(contactId) && placements.ContainsKey(contactId);

        /// <summary>Gets the hover anchor of the named live placement.</summary>
        public bool TryGetPlacementCenter(string contactId, out Vector3 center)
        {
            if (placements.TryGetValue(contactId ?? "", out var placement))
            {
                center = placement.HoverCenter;
                return true;
            }
            center = default;
            return false;
        }

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

        public string OpenContactId => lastContactId;

        /// <summary>
        /// Fired once per absorption with the contact id, hover anchor, and
        /// measured absorbed joules, so presentation can scale and place its
        /// dispersal. The ledger entry is already closed; this carries no
        /// physics authority.
        /// </summary>
        public event Action<string, Vector3, double> Absorbed;

        /// <summary>
        /// Floats a fresh bag at the anchor with its own contact. Refused when
        /// no Trial is live, the writer has no absorber face, or the Tick or
        /// anchor is invalid — never by arming a region the ledger cannot see.
        /// A second bag does not replace the first.
        /// </summary>
        public SandbagPlaceResult Place(Vector3 centerM, int tick)
        {
            if (!host.HasTrial)
                return Refused(tick, "trial_not_prepared");
            if (!host.SupportsSandbagContacts)
                return Refused(tick, "writer_has_no_tool_effect_face");
            if (tick < 0)
                return Refused(tick, "contact_start_tick_invalid");
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
                // The id is already open (a duplicate placement id); the bag
                // stays grounded instead of absorbing off-ledger.
                return Refused(tick, "contact_already_open");
            }
            catch (ArgumentException)
            {
                return Refused(tick, "hover_anchor_invalid");
            }

            placements[contactId] = new Placement { HoverCenter = centerM };
            lastContactId = contactId;
            hoverCenter = centerM;
            State = SandbagState.Placed;
            PlacementCount++;
            return new SandbagPlaceResult(SandbagPlaceOutcome.Placed, tick, "", contactId);
        }

        /// <summary>
        /// Polls every open contact after the runtime advanced. Each poll that
        /// sees writer-measured absorption closes that contact exactly once,
        /// records its absorption, and disperses that bag; other placements
        /// keep floating. A contact that died underneath (abort or Trial end)
        /// leaves no entry.
        /// </summary>
        public SandbagAbsorbResult PumpAbsorption()
        {
            if (placements.Count == 0)
                return new SandbagAbsorbResult(SandbagAbsorbOutcome.NotPlaced,
                    host.CurrentTick, "", "", 0.0);

            var ids = new List<string>(placements.Keys);
            SandbagAbsorbResult result = null;
            var absorbedAny = false;
            for (var index = 0; index < ids.Count; index++)
            {
                var contactId = ids[index];
                if (!placements.TryGetValue(contactId, out var placement))
                    continue;
                if (!host.HasTrial || !host.HasOpenContact(contactId))
                {
                    placements.Remove(contactId);
                    AbortedCount++;
                    if (result == null)
                        result = new SandbagAbsorbResult(SandbagAbsorbOutcome.Aborted,
                            host.CurrentTick, contactId, "", 0.0);
                    continue;
                }
                if (!host.OpenContactHasHits(contactId))
                {
                    if (result == null)
                        result = new SandbagAbsorbResult(
                            SandbagAbsorbOutcome.NoAbsorption, host.CurrentTick,
                            contactId, "", 0.0);
                    continue;
                }

                placements.Remove(contactId);
                var closure = host.CloseSandbagContact(contactId, "absorbed");
                host.ClearHoverAbsorber(contactId);
                AbsorptionCount++;
                var absorbed = closure != null && closure.Contacted &&
                               closure.WorkJ < 0.0
                    ? -closure.WorkJ
                    : 0.0;
                LastAbsorbedJ = absorbed;
                AbsorbedTotalJ += absorbed;
                LastTargetBallId = closure?.Entry?.TargetBallId ?? "";
                Absorbed?.Invoke(contactId, placement.HoverCenter, absorbed);
                absorbedAny = true;
                result = new SandbagAbsorbResult(SandbagAbsorbOutcome.Absorbed,
                    closure?.EndTick ?? host.CurrentTick, contactId,
                    LastTargetBallId, absorbed);
            }

            State = placements.Count > 0
                ? SandbagState.Placed
                : absorbedAny
                    ? SandbagState.Consumed
                    : SandbagState.Ready;
            return result ?? new SandbagAbsorbResult(SandbagAbsorbOutcome.NotPlaced,
                host.CurrentTick, "", "", 0.0);
        }

        /// <summary>
        /// Disarms every floating bag without absorbing. The wearer picked a
        /// bag back up, the visual moved away, or the Trial ended, so no hover
        /// region may stay behind: each open contact closes with no measured
        /// work and no ledger entry. Returns false when no bag was floating.
        /// </summary>
        public bool Withdraw(string reason)
        {
            if (placements.Count == 0)
                return false;
            var ids = new List<string>(placements.Keys);
            for (var index = 0; index < ids.Count; index++)
                Withdraw(ids[index], reason);
            return true;
        }

        /// <summary>Disarms one floating bag by contact id without absorbing.</summary>
        public bool Withdraw(string contactId, string reason)
        {
            if (string.IsNullOrEmpty(contactId) ||
                !placements.Remove(contactId))
                return false;
            if (placements.Count == 0)
                State = SandbagState.Ready;
            if (host.HasTrial && host.HasOpenContact(contactId))
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
                host.ClearHoverAbsorber(contactId);
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
