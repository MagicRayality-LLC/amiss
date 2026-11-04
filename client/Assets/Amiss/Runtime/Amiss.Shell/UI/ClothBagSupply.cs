using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell
{
    /// <summary>
    /// Keeps the loose cloth bags stocked under the television. A grab spawns
    /// a fresh docked bag, so the wearer can keep taking bags without an
    /// inventory or cooldown, and the room holds at most
    /// <see cref="MaxBagsInRoom"/> bags at once (founder requirement
    /// 2026-11-04). A consumed bag is removed; when the dock is empty the
    /// supply refills it.
    ///
    /// The supply is presentation only: it never touches the Trial, the hover
    /// region, or the writer-measured absorption. Still-floating bags push each
    /// other apart so two never overlap; that separation is visual geometry,
    /// not a physics body or an impulse.
    /// </summary>
    public sealed class ClothBagSupply : MonoBehaviour
    {
        /// <summary>Most bags this room may hold, docked, held, thrown, or floating.</summary>
        public const int MaxBagsInRoom = 100;

        readonly List<WallClothBag> bags = new();
        Transform dockedTo;
        bool shown;
        bool capLogged;

        public IReadOnlyList<WallClothBag> Bags => bags;

        public int Count => bags.Count;

        public bool IsFull => bags.Count >= MaxBagsInRoom;

        /// <summary>The bag waiting under the television, or null.</summary>
        public WallClothBag DockedBag
        {
            get
            {
                for (var index = 0; index < bags.Count; index++)
                    if (bags[index] != null && bags[index].IsDocked)
                        return bags[index];
                return null;
            }
        }

        /// <summary>The most recent still float, so the tool arms its region.</summary>
        public WallClothBag NewestFloatingBag
        {
            get
            {
                WallClothBag newest = null;
                for (var index = 0; index < bags.Count; index++)
                {
                    var bag = bags[index];
                    if (bag == null || !bag.IsFloating)
                        continue;
                    if (newest == null || bag.FloatTime >= newest.FloatTime)
                        newest = bag;
                }
                return newest;
            }
        }

        public void Build(Transform television = null)
        {
            if (television != null)
                dockedTo = television;
        }

        public void Follow(Transform television)
        {
            if (television != null)
                dockedTo = television;
            for (var index = bags.Count - 1; index >= 0; index--)
            {
                var bag = bags[index];
                if (bag == null)
                {
                    bags.RemoveAt(index);
                    continue;
                }
                bag.Follow(television);
            }
        }

        void LateUpdate()
        {
            if (shown)
                SeparateFloatingBags();
        }

        /// <summary>
        /// Pushes still-floating bags apart until no two overlap. Bags are
        /// presentation-only, so this is a positional separation, not a physics
        /// impulse: each pair moves half the overlap in opposite directions.
        /// The float anchor moves with the bag, and the tool re-arms its hover
        /// region at the corrected centre on the next frame.
        /// </summary>
        public void SeparateFloatingBags()
        {
            const int iterations = 4;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var moved = false;
                for (var first = 0; first < bags.Count; first++)
                {
                    var a = bags[first];
                    if (a == null || !a.IsFloating)
                        continue;
                    for (var second = first + 1; second < bags.Count; second++)
                    {
                        var b = bags[second];
                        if (b == null || !b.IsFloating)
                            continue;
                        var minimum = a.SeparationRadiusMeters +
                                      b.SeparationRadiusMeters;
                        if (!ClothBagPose.TrySeparate(a.FloatingPosition,
                                b.FloatingPosition, minimum,
                                out var aDelta, out var bDelta))
                            continue;
                        a.NudgeFloating(aDelta);
                        b.NudgeFloating(bDelta);
                        moved = true;
                    }
                }
                if (!moved)
                    break;
            }
        }

        /// <summary>
        /// Shows the docked bags, or removes every bag with the wall set. A
        /// hidden set is not a bag state: showing it again starts with one
        /// fresh docked bag rather than a pile of stale floats.
        /// </summary>
        public void SetVisible(bool visible)
        {
            shown = visible;
            if (!visible)
            {
                Clear();
                return;
            }
            EnsureDockedBag();
            for (var index = 0; index < bags.Count; index++)
                if (bags[index] != null)
                    bags[index].SetVisible(true);
        }

        /// <summary>Returns the waiting bag, spawning one when the dock is empty.</summary>
        public WallClothBag EnsureDockedBag()
        {
            if (!shown)
                return null;
            var docked = DockedBag;
            return docked != null ? docked : SpawnDocked();
        }

        public WallClothBag SpawnDocked()
        {
            if (!shown)
                return null;
            if (IsFull)
            {
                if (!capLogged)
                {
                    capLogged = true;
                    Debug.Log("[Amiss] cloth bag limit reached count=" + bags.Count);
                }
                return null;
            }

            var host = new GameObject("WallClothBag");
            host.transform.SetParent(transform, false);
            var bag = host.AddComponent<WallClothBag>();
            bags.Add(bag);
            bag.Grabbed += () => EnsureDockedBag();
            bag.Build();
            bag.Follow(dockedTo);
            bag.SetVisible(true);
            return bag;
        }

        /// <summary>
        /// Removes a consumed bag and refills the dock when it is empty. The
        /// absorption already happened in the writer; this only changes what
        /// the wearer sees.
        /// </summary>
        public bool Consume(WallClothBag bag)
        {
            if (bag == null || !bags.Remove(bag))
                return false;
            DestroyBag(bag.gameObject);
            EnsureDockedBag();
            return true;
        }

        public void Clear()
        {
            for (var index = 0; index < bags.Count; index++)
                if (bags[index] != null)
                    DestroyBag(bags[index].gameObject);
            bags.Clear();
        }

        static void DestroyBag(GameObject bag)
        {
            if (bag == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(bag);
            else
                Object.DestroyImmediate(bag);
        }
    }
}
