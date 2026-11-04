using System;
using Amiss.Shell.Input;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>What one bat sample or closure produced.</summary>
    public enum BatSwingOutcome
    {
        /// <summary>The sample was invalid: no swing, no work, no contact.</summary>
        NotAccepted,

        /// <summary>Accepted, but no swing is underway, so nothing was queued.</summary>
        Idle,

        /// <summary>A swing sample was queued for the writer's next fixed Tick.</summary>
        Submitted,

        /// <summary>The swing closed without touching a ball.</summary>
        NoContact,

        /// <summary>The swing closed on a ball with signed measured work.</summary>
        Contacted
    }

    public sealed class BatSwingResult
    {
        public BatSwingResult(BatSwingOutcome outcome, int tick, string reasonCode,
            string contactId, SweptClosure closure = null)
        {
            Outcome = outcome;
            Tick = tick;
            ReasonCode = reasonCode ?? "";
            ContactId = contactId ?? "";
            Closure = closure;
        }

        public BatSwingOutcome Outcome { get; }
        public int Tick { get; }
        public string ReasonCode { get; }
        public string ContactId { get; }
        public SweptClosure Closure { get; }
        public bool ProducedWork => Closure != null && Closure.Contacted;
    }

    /// <summary>
    /// The held bat's barrel in world space with the measured velocity at each
    /// end. The grip runs from the fist, the tip from the far end of the model;
    /// the input bridge measures both from the live held transform, so a
    /// rotating swing moves the tip faster than the grip. A missing or
    /// non-finite frame falls back to the normalized hand/controller point.
    /// </summary>
    public readonly struct BatSweepFrame
    {
        public BatSweepFrame(Vector3 gripM, Vector3 tipM, Vector3 gripVelocityMPerS,
            Vector3 tipVelocityMPerS)
        {
            GripM = gripM;
            TipM = tipM;
            GripVelocityMPerS = gripVelocityMPerS;
            TipVelocityMPerS = tipVelocityMPerS;
        }

        public Vector3 GripM { get; }
        public Vector3 TipM { get; }
        public Vector3 GripVelocityMPerS { get; }
        public Vector3 TipVelocityMPerS { get; }

        public bool IsFinite =>
            Finite(GripM) && Finite(TipM) && Finite(GripVelocityMPerS) &&
            Finite(TipVelocityMPerS);

        static bool Finite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) &&
            float.IsFinite(value.z);
    }

    /// <summary>
    /// The P0 bat of approved design 07 section 4.2, driven only by normalized
    /// semantic commands. Its semantics follow the approved oracle's swept
    /// contact path (<c>simulation/runtime.py</c>: <c>open_contact</c>,
    /// <c>submit_sweep_sample</c>, <c>TrialMechanics.close_contact</c>) and
    /// <c>HeadlessBallWriter._apply_sweep</c>:
    ///
    /// <list type="bullet">
    /// <item>one accepted contact is one ledger entry with signed work;</item>
    /// <item>an empty swing closes with zero work and adds no entry;</item>
    /// <item>an invalid or tracking-lost sample aborts the open contact, and no
    /// work, release, or stage evidence is invented from it;</item>
    /// <item>one contacted closure is one semantic commit, never one per sample.</item>
    /// </list>
    ///
    /// The tool writes no ball state. It queues the held bat's barrel segment
    /// (or the normalized point pose when no measured barrel is supplied) with
    /// the writer through <see cref="IBatContactHost"/>; the writer applies
    /// and measures the sweep on its next fixed Tick, and the runtime records
    /// the measured energy on the one open contact. <see cref="InputLimitProfile"/>
    /// supplies the swing limits: production installs the declared candidate
    /// (<c>quest-pro-candidate-unmeasured</c>,
    /// <c>uncalibrated_declared_not_measured</c>), so accepted device samples
    /// are admitted and can produce work. Those limits stay unmeasured;
    /// <see cref="InputLimitProfile.Unmeasured"/> is the fail-closed
    /// declaration that rejects every sample. The semantics here are also
    /// proven with an explicit test-only profile.
    /// </summary>
    public sealed class BatTool
    {
        public const string PropId = "bat";

        /// <summary>The bare hand's own sweep: a fist hit, not a bat strike.</summary>
        public const string FistPropId = "fist";

        /// <summary>
        /// Prototype swept radius from 07 section 4.2's deferred boundary. It is
        /// uncalibrated, so it is labelled and never presented as measured.
        /// </summary>
        public const float DeclaredSweptRadiusM = 0.12f;

        /// <summary>A fist is a smaller contact shape than the barrel.</summary>
        public const float FistSweptRadiusM = 0.06f;

        /// <summary>
        /// Founder balance unit (2026-11-04): a bat strike adds 10 quanta and a
        /// bare fist adds 1. The writer measures the applied change, so the
        /// ledger still records real work; only the tool strength is fixed.
        /// </summary>
        public const double DeclaredEnergyDeltaJ = EnergyQuantum.BatJ;
        public const double DeclaredFistEnergyDeltaJ = EnergyQuantum.FistJ;

        /// <summary>
        /// The declared tool parameters are 07 section 4.2 prototype values, so
        /// the readout states that label instead of presenting them as
        /// calibrated.
        /// </summary>
        public const string DeclaredParametersEvidence = EnergyQuantum.EvidenceStatus;

        /// <summary>The bat parameter label the panel shows under the readout.</summary>
        public static string DeclaredParametersLine =>
            "Bat " + DeclaredSweptRadiusM.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture) +
            " m: " + EnergyQuantum.JoulesText(DeclaredEnergyDeltaJ) + " (" +
            EnergyQuantum.BatUnits.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + "), " +
            EnergyQuantum.JoulesText(DeclaredFistEnergyDeltaJ) + " fist (" +
            EnergyQuantum.FistUnits.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + ") · " +
            DeclaredParametersEvidence;

        readonly IBatContactHost host;
        readonly SwingActivityClassifier classifier;
        int contactCounter;
        string openContactId = "";
        int lastSubmittedTick = -1;
        bool openIsBat;

        public BatTool(IBatContactHost host, InputLimitProfile profile)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            classifier = new SwingActivityClassifier(profile);
        }

        public InputLimitProfile Profile { get; }

        public SwingActivity Activity => classifier.Activity;
        public InputDiagnosticLog Diagnostics { get; } = new();
        public int ContactCount { get; private set; }
        public int EmptySwingCount { get; private set; }
        public int InvalidCount { get; private set; }
        public int AbortedContactCount { get; private set; }
        public string OpenContactId => openContactId;

        /// <summary>
        /// True when an open swing's queued samples have all been advanced by
        /// the writer, so closing now cannot drop measured work.
        /// </summary>
        public bool CanClose => openContactId.Length > 0 && host.CurrentTick > lastSubmittedTick;

        /// <summary>
        /// Consumes one normalized sample with its validity evidence. Only an
        /// accepted, in-swing sample can touch a ball; a rejected sample aborts
        /// the open swing without work or a synthetic release. Without a
        /// measured <see cref="BatSweepFrame"/> the sample's tool pose is the
        /// point the writer sweeps, which keeps the controller and test paths
        /// on the same semantics.
        /// </summary>
        public BatSwingResult Consume(InputNormalizationResult normalized, int tick) =>
            Consume(normalized, tick, null);

        /// <summary>
        /// Consumes one normalized sample together with the held bat's measured
        /// barrel. The barrel, not the fist, is the tool contact shape; its tip
        /// velocity also classifies the swing, because a rotating bat moves the
        /// tip far faster than the hand pose. A non-finite frame falls back to
        /// the point pose so a broken measurement never widens contact.
        /// </summary>
        public BatSwingResult Consume(InputNormalizationResult normalized, int tick,
            BatSweepFrame? sweepFrame)
        {
            if (!normalized.Accepted)
            {
                classifier.Abort(tick);
                Diagnostics.Record(normalized.InvalidReason);
                InvalidCount++;
                // A lost or invalid sample closes the open swing once, as an
                // abort. It records no work and invents no release.
                return AbortLocal(TemperatureTrialRuntime.TrackingLostAborted, tick,
                    normalized.InvalidReason.ToString());
            }
            if (!host.HasTrial)
                return new BatSwingResult(BatSwingOutcome.NotAccepted, tick,
                    "trial_not_prepared", "");
            if (!host.SupportsToolContacts)
                return new BatSwingResult(BatSwingOutcome.NotAccepted, tick,
                    "writer_has_no_tool_effect_face", "");

            var command = normalized.Command;
            var barrel = sweepFrame.HasValue && sweepFrame.Value.IsFinite
                ? sweepFrame.Value
                : (BatSweepFrame?)null;
            if (barrel.HasValue)
            {
                // The held tool is the bat, so its tip is the swing speed the
                // profile limits classify. The sample's validity evidence still
                // belongs to the tracked hand that carried it.
                command = new NormalizedToolCommand(command.TargetTick, command.Side,
                    command.Pose, barrel.Value.TipVelocityMPerS, command.AngularVelocity);
            }
            classifier.Update(command);
            if (classifier.Activity == SwingActivity.Inactive)
                return new BatSwingResult(BatSwingOutcome.Idle, tick, "swing_inactive", "");
            if (openContactId.Length == 0)
            {
                openContactId = "bat-contact-" + tick.ToString("D8") + "-" +
                                (++contactCounter).ToString("D4");
                openIsBat = barrel.HasValue;
                host.OpenSweptContact(openContactId,
                    openIsBat ? PropId : FistPropId, tick);
            }
            // The tool shape is chosen when the contact opens: a held barrel
            // is the bat (10 quanta), a bare hand is the fist (1 quantum). A
            // lost barrel mid-swing falls back to the same point pose so the
            // contact keeps one identity and one quantum.
            host.SubmitSweep(openContactId, openIsBat && barrel.HasValue
                ? new SweptSegment(barrel.Value.GripM, barrel.Value.TipM,
                    barrel.Value.GripVelocityMPerS, barrel.Value.TipVelocityMPerS,
                    DeclaredSweptRadiusM)
                : SweptSegment.Point(command.Pose.position, command.LinearVelocity,
                    openIsBat ? DeclaredSweptRadiusM : FistSweptRadiusM),
                openIsBat ? DeclaredEnergyDeltaJ : DeclaredFistEnergyDeltaJ);
            lastSubmittedTick = tick;
            return new BatSwingResult(BatSwingOutcome.Submitted, tick, "", openContactId);
        }

        /// <summary>
        /// Closes the open swing once its samples have advanced. This is the
        /// only place a bat can produce a ledger entry or a semantic commit, and
        /// it happens at most once per contact.
        /// </summary>
        public BatSwingResult Close(string reason = "separated")
        {
            if (openContactId.Length == 0)
                return new BatSwingResult(BatSwingOutcome.NoContact, host.CurrentTick,
                    "no_open_contact", "");
            var contactId = openContactId;
            openContactId = "";
            lastSubmittedTick = -1;
            var closure = host.CloseSweptContact(contactId, reason);
            if (!closure.Contacted)
            {
                EmptySwingCount++;
                return new BatSwingResult(BatSwingOutcome.NoContact, closure.EndTick,
                    closure.ClosureReason, contactId, closure);
            }
            ContactCount++;
            return new BatSwingResult(BatSwingOutcome.Contacted, closure.EndTick,
                closure.ClosureReason, contactId, closure);
        }

        /// <summary>Closes an open swing as a tracking-loss abort with no work.</summary>
        public BatSwingResult Abort(string reason = TemperatureTrialRuntime.TrackingLostAborted) =>
            AbortLocal(reason, host.CurrentTick, reason);

        BatSwingResult AbortLocal(string reason, int tick, string code)
        {
            if (openContactId.Length == 0)
                return new BatSwingResult(BatSwingOutcome.NotAccepted, tick, code, "");
            var contactId = openContactId;
            openContactId = "";
            lastSubmittedTick = -1;
            var closure = host.CloseSweptContact(contactId, reason);
            AbortedContactCount++;
            // The caller sees the invalid-input reason; the contact's own
            // closure reason stays on the closure record.
            return new BatSwingResult(BatSwingOutcome.NotAccepted, closure.EndTick,
                code, contactId, closure);
        }

        /// <summary>The producer-owned readout; this tool never recomputes it.</summary>
        public TemperatureReadoutSnapshot Readout => host.Readout;
    }
}
