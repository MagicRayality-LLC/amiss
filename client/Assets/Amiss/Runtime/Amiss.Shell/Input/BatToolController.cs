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

        void OnSample(InputNormalizationResult normalized, int tick)
        {
            if (bat == null || runtime == null)
                return;
            bat.Consume(normalized, tick);
            // The swing ends when the classifier leaves the active states and
            // the writer has advanced past the last queued sample, so no
            // measured work is lost by closing early.
            if (bat.Activity != SwingActivity.Inactive || !bat.CanClose)
                return;
            bat.Close();
        }

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
        }
    }
}
