using System;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The one bridge from the loose cloth bag to the P0 sandbag tool. It owns
    /// a <see cref="SandbagTool"/> on the runtime host, polls it after the
    /// runtime steps, and turns the tool's measured absorption into the
    /// approved celebratory dispersal and a fresh docked bag.
    ///
    /// The wearer's thrown bag is the placement path: the visual floats still
    /// where the throw stopped itself, and the controller arms the hover
    /// region at that float. Grabbing the floating bag again withdraws the
    /// region so no invisible absorber stays behind. A stage that orders a
    /// sandbag anchor on the trial-start request still floats one bag there
    /// once per Trial. Without a visual the manual <see cref="PlaceBag"/> call
    /// stays available for tests and later placement UX.
    /// </summary>
    public sealed class SandbagToolController : MonoBehaviour
    {
        DeviceSimulationRuntime runtime;
        SandbagTool bag;
        WallClothBag bagVisual;
        int nextVisualSearch;
        string currentTrialId = "";
        string placedTrialId = "";
        int armedSerial;

        public SandbagTool Bag => bag;
        public SandbagState State => bag?.State ?? SandbagState.Ready;
        public int AbsorptionCount => bag?.AbsorptionCount ?? 0;

        /// <summary>The grabbable bag visual, or null when this scene has none.</summary>
        public WallClothBag Visual => FindBagVisual();

        public void Initialize(DeviceSimulationRuntime deviceRuntime,
            WallClothBag visual = null)
        {
            if (bag != null)
                bag.Absorbed -= OnAbsorbed;
            runtime = deviceRuntime;
            bagVisual = visual;
            nextVisualSearch = 0;
            currentTrialId = "";
            placedTrialId = "";
            armedSerial = 0;
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
            NoteTrialChange();
            Poll();
            SyncVisual();
            FloatOrderedBag();
            ArmFloatingBag();
        }

        /// <summary>
        /// A new or ended Trial re-arms from scratch: a bag already floating is
        /// armed again in the new Trial, and an ordered anchor floats afresh.
        /// </summary>
        void NoteTrialChange()
        {
            var trialId = runtime != null ? runtime.LiveTrialId : "";
            if (trialId == currentTrialId)
                return;
            currentTrialId = trialId;
            placedTrialId = "";
            armedSerial = 0;
            if (string.IsNullOrEmpty(trialId))
                bag?.Withdraw("trial_ended");
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
            var visual = FindBagVisual();
            if (visual != null && (!visual.IsFloating ||
                (visual.FloatingPosition - anchor.Value).sqrMagnitude > 0.0001f))
                visual.PlaceAt(anchor.Value);
            if (PlaceBag(anchor.Value).WasPlaced)
                placedTrialId = trialId;
        }

        /// <summary>
        /// Arms the hover region exactly once for each still float the wearer
        /// produced by throwing or releasing the bag.
        /// </summary>
        public void ArmFloatingBag()
        {
            if (bag == null || runtime == null || !runtime.HasTrial ||
                bag.State != SandbagState.Ready)
                return;
            var visual = FindBagVisual();
            if (visual == null || !visual.IsFloating || visual.FloatSerial == armedSerial)
                return;
            if (PlaceBag(visual.FloatingPosition).WasPlaced)
                armedSerial = visual.FloatSerial;
        }

        /// <summary>
        /// Withdraws an armed region whose bag moved: the wearer grabbed the
        /// floating bag again, the bag was hidden, or the float changed. The
        /// next float arms a fresh region.
        /// </summary>
        public void SyncVisual()
        {
            if (bag == null || bag.State != SandbagState.Placed)
                return;
            var visual = FindBagVisual();
            if (visual == null)
                return;
            if (!visual.IsFloating ||
                (visual.FloatingPosition - bag.HoverCenter).sqrMagnitude > 0.0001f)
            {
                bag.Withdraw("bag_moved");
                armedSerial = 0;
            }
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

        void OnAbsorbed(Vector3 centerM, double joulesJ)
        {
            SandbagModel.TriggerDispersal(centerM, joulesJ);
            FindBagVisual()?.RespawnToDock();
            armedSerial = 0;
        }

        WallClothBag FindBagVisual()
        {
            // The bag is created with the wall set and hidden while the set is
            // not shown, so include inactive objects and retry at a bounded
            // rate. A successful lookup is cached for the session.
            if (bagVisual == null && Time.frameCount >= nextVisualSearch)
            {
                bagVisual = FindFirstObjectByType<WallClothBag>(
                    FindObjectsInactive.Include);
                nextVisualSearch = Time.frameCount + 30;
            }
            return bagVisual;
        }
    }
}
