using System;
using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The bridge from the loose cloth bags to the P0 sandbag tool. It owns one
    /// <see cref="SandbagTool"/> on the runtime host, polls it after the
    /// runtime steps, and turns each placed bag's measured absorption into the
    /// approved celebratory dispersal and a removed bag.
    ///
    /// The wearer's thrown bag is the placement path: each visual floats still
    /// where the throw stopped itself, and the controller arms a hover region
    /// for every floating bag. Grabbing a floating bag again withdraws that
    /// bag's region so no invisible absorber stays behind. A stage that orders
    /// a sandbag anchor on the trial-start request still floats one bag there
    /// once per Trial. Without a visual the manual <see cref="PlaceBag"/> call
    /// stays available for tests and later placement UX.
    ///
    /// Every armed bag triggers independently: the writer owns one one-shot
    /// hover absorber per bag, and the runtime records each bag's absorption on
    /// its own ledger contact.
    /// </summary>
    public sealed class SandbagToolController : MonoBehaviour
    {
        sealed class ArmedBag
        {
            public string ContactId;
            public int Serial;
        }

        DeviceSimulationRuntime runtime;
        SandbagTool bag;
        WallClothBag bagVisual;
        ClothBagSupply supply;
        readonly Dictionary<WallClothBag, ArmedBag> armedBags = new();
        int nextVisualSearch;
        string currentTrialId = "";
        string placedTrialId = "";

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
            supply = null;
            nextVisualSearch = 0;
            currentTrialId = "";
            placedTrialId = "";
            armedBags.Clear();
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
            armedBags.Clear();
            bag?.Withdraw("trial_ended");
        }

        /// <summary>
        /// Floats the requesting stage's bag once per Trial. A consumed bag
        /// stays consumed; a fresh Trial with its own anchor floats fresh.
        /// Public so tests can drive the same step the frame loop drives.
        /// </summary>
        public void FloatOrderedBag()
        {
            if (bag == null || runtime == null)
                return;
            var anchor = runtime.LiveSandbagAnchor;
            var trialId = runtime.LiveTrialId;
            if (!anchor.HasValue || string.IsNullOrEmpty(trialId) || trialId == placedTrialId)
                return;
            var visual = FindBagVisual();
            if (visual == null)
            {
                if (PlaceBag(anchor.Value).WasPlaced)
                    placedTrialId = trialId;
                return;
            }
            if (!visual.IsFloating ||
                (visual.FloatingPosition - anchor.Value).sqrMagnitude > 0.0001f)
                visual.PlaceAt(anchor.Value);
            ArmFloatingBag();
            if (armedBags.ContainsKey(visual))
                placedTrialId = trialId;
        }

        /// <summary>
        /// Arms one hover region for each still float the wearer produced by
        /// throwing or releasing a bag. Every floating bag is independent, so
        /// a new float never disarms an older one.
        /// </summary>
        public void ArmFloatingBag()
        {
            if (bag == null || runtime == null || !runtime.HasTrial)
                return;
            var candidates = BagCandidates();
            for (var index = 0; index < candidates.Count; index++)
            {
                var visual = candidates[index];
                if (visual == null)
                    continue;
                armedBags.TryGetValue(visual, out var armed);
                if (!visual.IsFloating)
                {
                    if (armed == null)
                        continue;
                    bag.Withdraw(armed.ContactId, "bag_moved");
                    armedBags.Remove(visual);
                    continue;
                }
                if (armed != null && armed.Serial == visual.FloatSerial &&
                    bag.HasPlacement(armed.ContactId))
                    continue;
                if (armed != null)
                {
                    bag.Withdraw(armed.ContactId, "bag_moved");
                    armedBags.Remove(visual);
                }
                var placed = bag.Place(visual.FloatingPosition, runtime.Tick);
                if (placed.WasPlaced)
                {
                    armedBags[visual] = new ArmedBag
                    {
                        ContactId = placed.ContactId,
                        Serial = visual.FloatSerial
                    };
                }
            }
        }

        /// <summary>
        /// Withdraws the armed region of any bag that moved: the wearer grabbed
        /// it again, the bag was hidden or consumed, or the float changed. The
        /// next float arms a fresh region.
        /// </summary>
        public void SyncVisual()
        {
            if (bag == null || armedBags.Count == 0)
                return;
            var keys = new List<WallClothBag>(armedBags.Keys);
            for (var index = 0; index < keys.Count; index++)
            {
                var visual = keys[index];
                if (visual == null)
                {
                    armedBags.Remove(visual);
                    continue;
                }
                var armed = armedBags[visual];
                if (!bag.TryGetPlacementCenter(armed.ContactId, out var center))
                {
                    armedBags.Remove(visual);
                    continue;
                }
                if (!visual.IsFloating ||
                    (visual.FloatingPosition - center).sqrMagnitude > 0.0001f)
                {
                    bag.Withdraw(armed.ContactId, "bag_moved");
                    armedBags.Remove(visual);
                }
            }
        }

        /// <summary>
        /// Polls every open contact after the runtime advanced. Public so tests
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

        void OnAbsorbed(string contactId, Vector3 centerM, double joulesJ)
        {
            SandbagModel.TriggerDispersal(centerM, joulesJ);
            WallClothBag consumed = null;
            foreach (var pair in armedBags)
            {
                if (!string.Equals(pair.Value.ContactId, contactId,
                        StringComparison.Ordinal))
                    continue;
                consumed = pair.Key;
                break;
            }
            if (consumed != null)
                armedBags.Remove(consumed);
            if (consumed == null)
                consumed = FindBagVisual();
            if (consumed == null)
                return;
            var owner = Supply;
            if (owner != null)
                owner.Consume(consumed);
            else
                consumed.RespawnToDock();
        }

        ClothBagSupply Supply
        {
            get
            {
                if (supply == null)
                    supply = FindFirstObjectByType<ClothBagSupply>(
                        FindObjectsInactive.Include);
                return supply;
            }
        }

        WallClothBag FindBagVisual()
        {
            if (bagVisual != null)
                return bagVisual;
            var owner = Supply;
            if (owner != null)
            {
                var docked = owner.DockedBag;
                if (docked != null)
                    return docked;
                var floating = owner.NewestFloatingBag;
                if (floating != null)
                    return floating;
                return owner.Count > 0 ? owner.Bags[0] : null;
            }
            // A scene without a supply keeps the original single-bag search.
            // Include inactive objects and retry at a bounded rate; a
            // successful lookup is cached for the session.
            if (bagVisual == null && Time.frameCount >= nextVisualSearch)
            {
                bagVisual = FindFirstObjectByType<WallClothBag>(
                    FindObjectsInactive.Include);
                nextVisualSearch = Time.frameCount + 30;
            }
            return bagVisual;
        }

        IReadOnlyList<WallClothBag> BagCandidates()
        {
            var owner = Supply;
            if (owner != null)
                return owner.Bags;
            return FindObjectsByType<WallClothBag>(FindObjectsInactive.Exclude);
        }
    }
}
