using System;
using Amiss.Shell.Simulation;
using UnityEngine;

namespace Amiss.Shell.Input
{
    /// <summary>
    /// The one bridge from adjudicated normalized input to the P0 bat. It reads
    /// the adapter's accepted command plus validity evidence — never a raw pose
    /// stream, an OVR button, or a render callback. Accepted swing samples are
    /// queued with the writer; the runtime records the writer-measured work on
    /// the one open contact, and the swing closes only after the writer has
    /// advanced past the Tick that carried its last sample.
    ///
    /// While the wearer holds the bat, the writer sweeps the measured barrel
    /// segment from grip to tip rather than the hand pointer pose. The visible
    /// tool is the barrel, so a ball the barrel passes through is hit even when
    /// it never touches the fist; without that frame the sample's normalized
    /// point pose remains the tool shape, which keeps the controller path and
    /// the test-only host on the same semantics.
    ///
    /// Production installs the declared candidate <see cref="InputLimitProfile"/>
    /// (<c>quest-pro-candidate-unmeasured</c>,
    /// <c>uncalibrated_declared_not_measured</c>), so accepted device samples
    /// reach the writer and can produce work; those limits stay unmeasured.
    /// <see cref="InputLimitProfile.Unmeasured"/> remains the fail-closed
    /// declaration, under which every sample is rejected with a stable reason
    /// and no swing produces work. That is the approved 05 section 5.2
    /// behaviour.
    /// </summary>
    public sealed class BatToolController : MonoBehaviour
    {
        OvrToolInputAdapter adapter;
        DeviceSimulationRuntime runtime;
        BatTool bat;
        WallBat batVisual;
        int nextBatVisualSearch;
        bool holdingBarrel;
        bool hasBarrelSample;
        Vector3 lastGripM;
        Vector3 lastTipM;
        int lastAcceptedTick = -1;
        InputSide lastAcceptedSide = InputSide.Unknown;

        public BatTool Bat => bat;
        public int ContactCount => bat?.ContactCount ?? 0;
        public int EmptySwingCount => bat?.EmptySwingCount ?? 0;
        public int InvalidSamples => bat?.InvalidCount ?? 0;

        public void Initialize(OvrToolInputAdapter toolAdapter,
            DeviceSimulationRuntime deviceRuntime)
        {
            if (adapter != null)
                adapter.SampleAdjudicated -= OnSample;
            adapter = toolAdapter;
            runtime = deviceRuntime;
            bat = new BatTool(runtime, adapter != null
                ? adapter.Profile
                : InputLimitProfile.Unmeasured());
            if (adapter != null)
                adapter.SampleAdjudicated += OnSample;
        }

        void OnDestroy()
        {
            if (adapter != null)
                adapter.SampleAdjudicated -= OnSample;
        }

        void Update()
        {
            var held = IsBatHeld();
            if (!held)
                hasBarrelSample = false;
            // A released bat ends its contact even when no further sample
            // arrives. The close waits for the writer to advance past the last
            // queued sample so measured work is never dropped.
            if (!holdingBarrel || bat == null || bat.OpenContactId.Length == 0)
                return;
            if (held || !bat.CanClose)
                return;
            bat.Close("released");
            holdingBarrel = false;
        }

        void OnSample(InputNormalizationResult normalized, int tick)
        {
            if (bat == null || runtime == null)
                return;
            if (normalized.Accepted)
            {
                // The adapter samples hands and controllers independently, so
                // both can report the same side and tick. One tool sample per
                // tick is the contract; a duplicate would queue two impulses.
                if (normalized.Command.TargetTick == lastAcceptedTick &&
                    normalized.Command.Side == lastAcceptedSide)
                    return;
                lastAcceptedTick = normalized.Command.TargetTick;
                lastAcceptedSide = normalized.Command.Side;
            }

            BatSweepFrame? frame = null;
            if (TryGetHeldSide(out var heldSide))
            {
                // Only the holding hand's tool is the bat; the other hand and
                // a controller in the other hand are not this contact. An
                // invalid sample still reaches Consume so it aborts the open
                // contact without an invented release.
                if (normalized.Accepted)
                {
                    if (normalized.Command.Side != InputSideOf(heldSide))
                        return;
                    if (TryReadBarrel(out var barrel))
                        frame = barrel;
                }
            }
            else if (holdingBarrel)
            {
                // The bat left the hand: close the barrel contact before any
                // later sample can attach a hand-pose hit to it.
                if (bat.CanClose)
                {
                    bat.Close("released");
                    holdingBarrel = false;
                }
                return;
            }

            bat.Consume(normalized, tick, frame);
            holdingBarrel = frame.HasValue;
            // The swing ends when the classifier leaves the active states and
            // the writer has advanced past the last queued sample, so no
            // measured work is lost by closing early.
            if (bat.Activity != SwingActivity.Inactive || !bat.CanClose)
                return;
            bat.Close();
            holdingBarrel = false;
        }

        bool TryGetHeldSide(out int side)
        {
            var visual = FindBatVisual();
            if (visual != null && visual.IsHeld && visual.HoldSide >= 0)
            {
                side = visual.HoldSide;
                return true;
            }
            side = -1;
            return false;
        }

        bool IsBatHeld()
        {
            var visual = FindBatVisual();
            return visual != null && visual.IsHeld;
        }

        /// <summary>
        /// Reads the held barrel once per frame and measures the velocity at
        /// each end from the change since the previous read. The bat follows
        /// the hand joints in <c>LateUpdate</c>, so the first read has no
        /// measured velocity and the classifier stays idle until the next one.
        /// </summary>
        bool TryReadBarrel(out BatSweepFrame frame)
        {
            frame = default;
            var visual = FindBatVisual();
            if (visual == null || !visual.TryGetHeldBarrel(out var gripM, out var tipM))
            {
                hasBarrelSample = false;
                return false;
            }

            var deltaTime = Time.unscaledDeltaTime;
            var gripVelocity = hasBarrelSample && deltaTime > 1e-5f
                ? (gripM - lastGripM) / deltaTime
                : Vector3.zero;
            var tipVelocity = hasBarrelSample && deltaTime > 1e-5f
                ? (tipM - lastTipM) / deltaTime
                : Vector3.zero;
            lastGripM = gripM;
            lastTipM = tipM;
            hasBarrelSample = true;
            frame = new BatSweepFrame(gripM, tipM, gripVelocity, tipVelocity);
            return frame.IsFinite;
        }

        WallBat FindBatVisual()
        {
            // The visual is created at start and hidden while docked; include
            // inactive objects and retry at a bounded rate if this scene has
            // none yet. A successful lookup is cached for the session.
            if (batVisual == null && Time.frameCount >= nextBatVisualSearch)
            {
                batVisual = FindFirstObjectByType<WallBat>(FindObjectsInactive.Include);
                nextBatVisualSearch = Time.frameCount + 30;
            }
            return batVisual;
        }

        static InputSide InputSideOf(int heldSide) => heldSide switch
        {
            0 => InputSide.Left,
            1 => InputSide.Right,
            _ => InputSide.Unknown
        };

        /// <summary>
        /// Closes any open swing as a tracking-loss abort. Focus loss and input
        /// degradation call this so no later sample can attach work to a swing
        /// that lost validity, and no synthetic release is invented.
        /// </summary>
        public void AbortOpenSwing()
        {
            if (bat != null)
                bat.Abort();
            else
                runtime?.AbortOpenToolContact();
            holdingBarrel = false;
        }
    }
}
