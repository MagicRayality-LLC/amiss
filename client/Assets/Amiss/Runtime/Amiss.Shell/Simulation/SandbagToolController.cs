using System;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The one bridge from the floating bag to the P0 sandbag tool. It owns a
    /// <see cref="SandbagTool"/> on the runtime host, polls it after the
    /// runtime steps, and turns the tool's measured absorption into the
    /// approved celebratory dispersal. The requesting stage carries the hover
    /// anchor on the trial-start request: the bridge floats one bag there
    /// once per Trial. A manual <see cref="PlaceBag"/> call stays available
    /// for tests and later placement UX; without any placement the poll is a
    /// no-op.
    /// </summary>
    public sealed class SandbagToolController : MonoBehaviour
    {
        DeviceSimulationRuntime runtime;
        SandbagTool bag;
        string placedTrialId = "";

        public SandbagTool Bag => bag;
        public SandbagState State => bag?.State ?? SandbagState.Ready;
        public int AbsorptionCount => bag?.AbsorptionCount ?? 0;

        public void Initialize(DeviceSimulationRuntime deviceRuntime)
        {
            if (bag != null)
                bag.Absorbed -= OnAbsorbed;
            runtime = deviceRuntime;
            placedTrialId = "";
            bag = runtime != null ? new SandbagTool(runtime) : null;
            if (bag != null)
                bag.Absorbed += OnAbsorbed;
        }

        void OnDestroy()
        {
            if (bag != null)
                bag.Absorbed -= OnAbsorbed;
        }

        void Update()
        {
            Poll();
            FloatOrderedBag();
        }

        /// <summary>
        /// Floats the requesting stage's bag once per Trial. A consumed bag
        /// stays consumed; a fresh Trial with its own anchor floats fresh.
        /// Public so tests can drive the same step the frame loop drives.
        /// </summary>
        public void FloatOrderedBag()
        {
            if (bag == null || runtime == null || bag.State != SandbagState.Ready)
                return;
            var anchor = runtime.LiveSandbagAnchor;
            var trialId = runtime.LiveTrialId;
            if (!anchor.HasValue || string.IsNullOrEmpty(trialId) || trialId == placedTrialId)
                return;
            if (PlaceBag(anchor.Value).WasPlaced)
                placedTrialId = trialId;
        }

        /// <summary>
        /// Polls the open contact after the runtime advanced. Public so tests
        /// can drive the same poll the frame loop drives.
        /// </summary>
        public void Poll()
        {
            bag?.PumpAbsorption();
        }

        public SandbagPlaceResult PlaceBag(Vector3 anchorM) =>
            bag != null
                ? bag.Place(anchorM, runtime != null ? runtime.Tick : 0)
                : new SandbagPlaceResult(SandbagPlaceOutcome.NotAccepted, 0,
                    "tool_not_bound", "");

        static void OnAbsorbed(Vector3 centerM, double joulesJ) =>
            SandbagModel.TriggerDispersal(centerM, joulesJ);
    }
}
