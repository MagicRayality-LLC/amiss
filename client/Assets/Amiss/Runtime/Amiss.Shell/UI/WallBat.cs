using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell
{
    /// <summary>Where the loose bat currently is.</summary>
    public enum WallBatState
    {
        Hidden,
        Docked,
        Flying,
        Held,
        Resting
    }

    /// <summary>
    /// Dock and hold math for the loose bat, in meters. Pure functions so the
    /// edit-mode suite can pin them without a headset.
    /// </summary>
    public static class WallBatPose
    {
        /// <summary>Grip position under the TV in TV-local space.</summary>
        public static readonly Vector3 DockLocalPosition = new Vector3(0.30f, -0.50f, 0.06f);

        /// <summary>
        /// Barrel-down lean: 180 about X hangs the barrel straight down, plus
        /// 12 degrees tipping the tip toward the room and 12 degrees of side
        /// lean so it reads as resting under the set rather than floating.
        /// </summary>
        public static readonly Vector3 DockLocalEuler = new Vector3(192f, 0f, -12f);

        /// <summary>Pinch within this of the grip grabs the bat directly.</summary>
        public const float CloseGrabRadiusMeters = 0.16f;

        /// <summary>Pinch within this of the barrel grabs the bat directly.</summary>
        public const float BarrelGrabRadiusMeters = 0.12f;

        /// <summary>Far pinch-fetch works within this range.</summary>
        public const float FetchRangeMeters = 5f;

        /// <summary>The pointing cone for a far fetch, in degrees.</summary>
        public const float FetchHalfAngleDegrees = 8f;

        /// <summary>How long a fetched bat takes to fly to the hand.</summary>
        public const float FetchFlySeconds = 0.28f;

        /// <summary>Grip anchor relative to the hand pointer while held.</summary>
        public static readonly Vector3 HeldPositionOffset = new Vector3(0f, -0.03f, 0.02f);

        /// <summary>
        /// Bat length runs along local +Y with the origin at the grip, so
        /// pitching +Y onto the pointer forward hangs the barrel off the fist.
        /// </summary>
        public static readonly Vector3 HeldEulerOffset = new Vector3(90f, 0f, 0f);

        /// <summary>Drift below this speed freezes the released bat.</summary>
        public const float ReleaseStopSpeedMeters = 0.15f;

        /// <summary>Exponential damping applied to release drift.</summary>
        public const float DriftDamping = 4f;

        /// <summary>Release drift is clamped to this speed.</summary>
        public const float MaxReleaseSpeedMeters = 8f;

        public static Pose DockPose(Pose television) => new Pose(
            television.position + television.rotation * DockLocalPosition,
            television.rotation * Quaternion.Euler(DockLocalEuler));

        public static Pose HoldPose(Pose pointer, Vector3 gripLocalOffset)
        {
            var rotation = pointer.rotation * Quaternion.Euler(HeldEulerOffset);
            var anchor = pointer.position + pointer.rotation * HeldPositionOffset;
            return new Pose(anchor - rotation * gripLocalOffset, rotation);
        }

        public static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            var denom = ab.sqrMagnitude;
            if (denom < 1e-10f)
                return Vector3.Distance(point, a);
            var t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / denom);
            return Vector3.Distance(point, a + ab * t);
        }

        public static bool AimsAt(Vector3 handPosition, Vector3 handForward, Vector3 target)
        {
            var to = target - handPosition;
            var distance = to.magnitude;
            if (distance < 0.3f || distance > FetchRangeMeters)
                return false;
            var angle = Vector3.Angle(handForward, to / distance);
            return angle <= FetchHalfAngleDegrees;
        }

        public static Vector3 DampDrift(Vector3 velocity, float deltaTime) =>
            velocity * Mathf.Exp(-DriftDamping * Mathf.Max(0f, deltaTime));
    }

    /// <summary>
    /// The loose wooden bat docked under the television. Hands only: a pinch
    /// near the grip or barrel grabs it, and a pinch while pointing at it
    /// from across the room fetches it to the hand, the early Quest behaviour.
    /// While held the bat rides the hand pointer; the swing itself is still
    /// measured from normalized hand samples by the bat tool, so this script
    /// never writes physics or Trial state. Release leaves a short damped
    /// drift, then the bat hangs where it stopped until fetched again.
    /// </summary>
    public sealed class WallBat : MonoBehaviour
    {
        Transform gripPoint;
        Transform tipPoint;
        Vector3 gripLocalOffset;
        bool built;
        bool shown;
        bool held;
        bool flying;
        bool docked = true;
        bool drifting;
        int holdSide = -1;
        int flySide = -1;
        Vector3 flyFrom;
        float flyT;
        Vector3 driftVelocity;
        Vector3 lastHoldVelocity;
        readonly Dictionary<int, bool> wasPinching = new();

        void Awake()
        {
            Build();
            gameObject.SetActive(false);
            shown = false;
        }

        void Update()
        {
            if (!shown || !built)
                return;
            var hands = FindObjectsByType<OVRHand>(FindObjectsInactive.Exclude);
            for (var i = 0; i < hands.Length; i++)
            {
                var hand = hands[i];
                if (hand == null || !hand.IsTracked || !hand.IsPointerPoseValid)
                    continue;
                var side = SideOf(hand);
                if (side < 0)
                    continue;
                var pointer = new Pose(hand.PointerPose.position, hand.PointerPose.rotation);
                var pinching = hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                var edge = pinching && !IsPinching(side);
                wasPinching[side] = pinching;
                if (held && side == holdSide)
                {
                    if (pinching)
                        HoldTick(pointer);
                    else
                        ReleaseHold();
                }
                else if (flying && side == flySide)
                {
                    if (pinching)
                        FlyTick(pointer, Time.deltaTime);
                    else
                        CancelFly();
                }
                else if (!held && !flying && edge)
                {
                    if (TryCloseGrab(pointer.position))
                    {
                        holdSide = side;
                        HoldTick(pointer);
                    }
                    else if (TryFetch(pointer.position, pointer.forward))
                    {
                        flySide = side;
                    }
                }
            }
            if (!held && !flying && drifting)
                DriftTick(Time.deltaTime);
        }

        /// <summary>
        /// Loads the visual once. Awake runs it; the edit-mode suite calls it
        /// without a play session.
        /// </summary>
        public void Build()
        {
            if (built)
                return;
            built = true;
            if (!WallBatModel.TryMount(transform, out gripPoint, out tipPoint))
                return;
            gripLocalOffset = transform.InverseTransformPoint(gripPoint.position);
        }

        public WallBatState State
        {
            get
            {
                if (!shown)
                    return WallBatState.Hidden;
                if (held)
                    return WallBatState.Held;
                if (flying)
                    return WallBatState.Flying;
                if (docked)
                    return WallBatState.Docked;
                return WallBatState.Resting;
            }
        }

        public bool IsHeld => held;
        public bool IsDocked => docked;

        public void SetVisible(bool visible)
        {
            if (!visible)
            {
                held = false;
                flying = false;
                drifting = false;
                driftVelocity = Vector3.zero;
                holdSide = -1;
                flySide = -1;
                docked = true;
                wasPinching.Clear();
            }
            shown = visible;
            if (gameObject.activeSelf != visible)
                gameObject.SetActive(visible);
        }

        /// <summary>
        /// Returns the untouched bat to its dock under the television. A bat
        /// the wearer has moved stays where it was left.
        /// </summary>
        public void Follow(Transform television)
        {
            if (television == null || !docked || held || flying || !built)
                return;
            var pose = WallBatPose.DockPose(
                new Pose(television.position, television.rotation));
            transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        public bool TryCloseGrab(Vector3 handPosition)
        {
            if (held || flying || gripPoint == null)
                return false;
            if (Vector3.Distance(handPosition, gripPoint.position) <= WallBatPose.CloseGrabRadiusMeters)
                return BeginHold();
            var tip = tipPoint != null ? tipPoint.position : transform.TransformPoint(Vector3.up * 0.7f);
            if (WallBatPose.DistanceToSegment(handPosition, gripPoint.position, tip) <=
                WallBatPose.BarrelGrabRadiusMeters)
                return BeginHold();
            return false;
        }

        public bool TryFetch(Vector3 handPosition, Vector3 handForward)
        {
            if (held || flying || gripPoint == null)
                return false;
            if (!WallBatPose.AimsAt(handPosition, handForward.normalized, gripPoint.position))
                return false;
            flying = true;
            flyFrom = transform.position;
            flyT = 0f;
            drifting = false;
            driftVelocity = Vector3.zero;
            return true;
        }

        public void HoldTick(Pose pointer)
        {
            if (gripPoint == null)
                return;
            var pose = WallBatPose.HoldPose(pointer, gripLocalOffset);
            var delta = Time.deltaTime;
            if (delta > 1e-5f)
                lastHoldVelocity = (pose.position - transform.position) / delta;
            transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        public void ReleaseHold()
        {
            held = false;
            holdSide = -1;
            docked = false;
            driftVelocity = Vector3.ClampMagnitude(lastHoldVelocity, WallBatPose.MaxReleaseSpeedMeters);
            drifting = driftVelocity.magnitude >= WallBatPose.ReleaseStopSpeedMeters;
            lastHoldVelocity = Vector3.zero;
            Debug.Log("[Amiss] bat released drift=" + driftVelocity.magnitude.ToString("F2"));
        }

        public void FlyTick(Pose pointer, float deltaTime)
        {
            if (gripPoint == null)
            {
                CancelFly();
                return;
            }
            flyT += Mathf.Max(0f, deltaTime) / WallBatPose.FetchFlySeconds;
            var target = WallBatPose.HoldPose(pointer, gripLocalOffset);
            var t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(flyT));
            transform.SetPositionAndRotation(
                Vector3.Lerp(flyFrom, target.position, t),
                Quaternion.Slerp(transform.rotation, target.rotation, t));
            lastHoldVelocity = Vector3.zero;
            if (flyT >= 1f)
            {
                flying = false;
                BeginHold();
                HoldTick(pointer);
            }
        }

        public void CancelFly()
        {
            flying = false;
            flySide = -1;
            docked = false;
            drifting = false;
            driftVelocity = Vector3.zero;
        }

        public void DriftTick(float deltaTime)
        {
            driftVelocity = WallBatPose.DampDrift(driftVelocity, deltaTime);
            if (driftVelocity.magnitude < WallBatPose.ReleaseStopSpeedMeters)
            {
                drifting = false;
                driftVelocity = Vector3.zero;
                return;
            }
            transform.position += driftVelocity * Mathf.Max(0f, deltaTime);
        }

        bool BeginHold()
        {
            held = true;
            flying = false;
            flySide = -1;
            docked = false;
            drifting = false;
            driftVelocity = Vector3.zero;
            lastHoldVelocity = Vector3.zero;
            Debug.Log("[Amiss] bat grabbed");
            return true;
        }

        bool IsPinching(int side) =>
            wasPinching.TryGetValue(side, out var heldDown) && heldDown;

        static int SideOf(OVRHand hand)
        {
            var current = hand != null ? hand.transform : null;
            while (current != null)
            {
                if (current.name.Contains("Left"))
                    return 0;
                if (current.name.Contains("Right"))
                    return 1;
                current = current.parent;
            }
            return -1;
        }
    }
}
