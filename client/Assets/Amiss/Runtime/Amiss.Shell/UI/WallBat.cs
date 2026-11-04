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
        public const float CloseGrabRadiusMeters = 0.20f;

        /// <summary>Pinch within this of the barrel grabs the bat directly.</summary>
        public const float BarrelGrabRadiusMeters = 0.15f;

        /// <summary>Far pinch-fetch works within this range.</summary>
        public const float FetchRangeMeters = 5f;

        /// <summary>The pointing cone for a far fetch, in degrees.</summary>
        public const float FetchHalfAngleDegrees = 10f;

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

        /// <summary>
        /// A pinch dropout shorter than this keeps the hold: one missed pinch
        /// frame would otherwise drop the bat into drift and read as lag.
        /// </summary>
        public const float PinchGraceSeconds = 0.15f;

        /// <summary>
        /// Hold pose from live hand joints instead of the filtered pointer
        /// ray. The model grip lands exactly on the grip anchor — the pinch
        /// midpoint for a precision pinch, the fist point for a full-hand
        /// grab — and local +Y (the barrel) runs along the hand's long axis,
        /// so both hands and any wrist roll hold the bat in the fist by
        /// construction. Pure function of its inputs so the edit-mode suite
        /// can pin it.
        /// </summary>
        public static Pose JointHoldPose(Vector3 anchor, Vector3 barrelDir,
            Vector3 pinchAxis, Vector3 gripLocalOffset)
        {
            var up = barrelDir.sqrMagnitude > 1e-10f
                ? barrelDir.normalized
                : Vector3.up;
            var across = pinchAxis - up * Vector3.Dot(pinchAxis, up);
            if (across.sqrMagnitude < 1e-10f)
                across = Vector3.Cross(up, Mathf.Abs(up.y) < 0.9f
                    ? Vector3.up
                    : Vector3.right);
            across.Normalize();
            var rotation = Quaternion.LookRotation(Vector3.Cross(across, up), up);
            return new Pose(anchor - rotation * gripLocalOffset, rotation);
        }

        /// <summary>
        /// True when an open tracked hand must release the hold now: only
        /// after the grace window past the last pinch or closed-fist frame.
        /// </summary>
        public static bool ShouldRelease(float now, float lastPinchTime) =>
            now - lastPinchTime >= PinchGraceSeconds;

        /// <summary>
        /// Where the model grip lands: the index-pinch midpoint for a
        /// precision pinch, the fist point for a full-hand grab. Pure so
        /// the edit-mode suite can pin it.
        /// </summary>
        public static Vector3 GripAnchor(bool indexPinching,
            Vector3 pinchMid, Vector3 fistPoint) =>
            indexPinching ? pinchMid : fistPoint;

        /// <summary>
        /// Fraction of the palm-long-axis component removed from the
        /// knuckle line. Zero keeps the raw index-to-little knuckle axis
        /// for wearer comparison.
        /// </summary>
        public const float GripAxisProjection = 0f;

        /// <summary>
        /// A gripped handle runs across the closed fist, from the index
        /// knuckle toward the little-finger knuckle. Part of the component
        /// along the wrist-to-middle-knuckle axis is removed so the knuckle
        /// arc does not add the full tilt.
        /// </summary>
        public static Vector3 BarrelAxis(Vector3 wrist, Vector3 middleKnuckle,
            Vector3 indexKnuckle, Vector3 pinkyKnuckle)
        {
            var longAxis = middleKnuckle - wrist;
            var knuckleAxis = indexKnuckle - pinkyKnuckle;
            if (longAxis.sqrMagnitude > 1e-10f)
                knuckleAxis -= longAxis * (Vector3.Dot(knuckleAxis, longAxis) /
                    longAxis.sqrMagnitude * GripAxisProjection);
            return knuckleAxis.sqrMagnitude > 1e-10f
                ? knuckleAxis.normalized
                : Vector3.up;
        }

        /// <summary>Forward tilt of the held barrel from the hand axis.</summary>
        public const float ForwardTiltDegrees = 25f;

        /// <summary>
        /// Tilts the barrel toward the hand's pointing direction by the
        /// given angle, keeping the result unit length.
        /// </summary>
        public static Vector3 TiltToward(Vector3 axis, Vector3 forward,
            float degrees)
        {
            var target = forward - axis * Vector3.Dot(forward, axis);
            if (target.sqrMagnitude < 1e-10f)
                return axis;
            var turn = Vector3.Cross(axis, target.normalized);
            return (Quaternion.AngleAxis(degrees, turn) * axis).normalized;
        }

        /// <summary>Curled fingertips within this palm radius count toward a fist.</summary>
        public const float FistTipRadiusMeters = 0.075f;

        /// <summary>
        /// A full-hand grab does not require a thumb-to-finger pinch. Three
        /// curled fingers are enough, so an extended index or little finger
        /// does not make an otherwise closed grip unusable.
        /// </summary>
        public static bool IsClosedFist(Vector3 palm, Vector3 indexTip,
            Vector3 middleTip, Vector3 ringTip, Vector3 pinkyTip, float handScale)
        {
            var radius = FistTipRadiusMeters * Mathf.Max(0.5f, handScale);
            var radiusSquared = radius * radius;
            var curled = 0;
            if ((indexTip - palm).sqrMagnitude <= radiusSquared)
                curled++;
            if ((middleTip - palm).sqrMagnitude <= radiusSquared)
                curled++;
            if ((ringTip - palm).sqrMagnitude <= radiusSquared)
                curled++;
            if ((pinkyTip - palm).sqrMagnitude <= radiusSquared)
                curled++;
            return curled >= 3;
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
    /// or closed fist near the grip or barrel grabs it, and the same gesture
    /// while pointing at it from across the room fetches it to the hand.
    /// While held the bat rides the pinching fingers' midpoint or palm from
    /// live hand joints — never the filtered pointer ray — with the barrel
    /// across the knuckles and 25° forward, so it sits in the fist at any
    /// wrist roll; the swing itself is still measured from normalized hand
    /// samples by the
    /// bat tool, so this script never writes physics or Trial state. The
    /// follow runs in <c>LateUpdate</c> on the freshest tracking before
    /// render, with no smoothing, and a short grip-dropout grace so one
    /// missed gesture frame neither releases the bat nor reads as lag.
    /// Release leaves a short damped drift, then the bat hangs where it
    /// stopped until fetched again.
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
        float lastPinchTime;
        readonly Vector3[] lastAcross = { Vector3.right, Vector3.right };
        Transform trackingSpace;
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

        void LateUpdate()
        {
            if (!shown || !built)
                return;
            var hands = FindObjectsByType<OVRHand>(FindObjectsInactive.Exclude);
            for (var i = 0; i < hands.Length; i++)
            {
                var hand = hands[i];
                if (hand == null || !hand.IsTracked)
                    continue;
                var side = SideOf(hand);
                if (side < 0)
                    continue;
                var pointerValid = hand.IsPointerPoseValid;
                var pointer = pointerValid
                    ? new Pose(hand.PointerPose.position, hand.PointerPose.rotation)
                    : new Pose(hand.transform.position, hand.transform.rotation);
                var indexPinching = hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                var middlePinching = hand.GetFingerIsPinching(OVRHand.HandFinger.Middle);
                var ringPinching = hand.GetFingerIsPinching(OVRHand.HandFinger.Ring);
                var pinkyPinching = hand.GetFingerIsPinching(OVRHand.HandFinger.Pinky);
                var pinching = indexPinching || middlePinching || ringPinching ||
                    pinkyPinching;
                var hasFrame = TryGetPinchFrame(hand, side, pointer.forward,
                    out var pinchMid, out var fistPoint, out var barrelDir,
                    out var pinchAxis, out var fistClosed);
                var gripping = pinching || fistClosed;
                var edge = gripping && !IsPinching(side);
                wasPinching[side] = gripping;
                var anchor = WallBatPose.GripAnchor(indexPinching, pinchMid,
                    fistPoint);
                var grabPoint = !pinching && fistClosed && hasFrame
                    ? fistPoint
                    : pointer.position;
                if (held && side == holdSide)
                {
                    if (gripping)
                    {
                        lastPinchTime = Time.time;
                        if (hasFrame || pointerValid)
                            HoldOnFrame(hasFrame, pointer, anchor, barrelDir, pinchAxis);
                    }
                    else if (!WallBatPose.ShouldRelease(Time.time, lastPinchTime))
                    {
                        if (hasFrame || pointerValid)
                            HoldOnFrame(hasFrame, pointer, anchor, barrelDir, pinchAxis);
                    }
                    else
                    {
                        ReleaseHold();
                    }
                }
                else if (flying && side == flySide)
                {
                    if (gripping && (hasFrame || pointerValid))
                        FlyTick(HoldTarget(hasFrame, pointer, anchor, barrelDir,
                            pinchAxis), Time.deltaTime);
                    else if (!gripping)
                        CancelFly();
                }
                else if (!held && !flying && edge)
                {
                    if ((hasFrame || pointerValid) && TryCloseGrab(grabPoint))
                    {
                        holdSide = side;
                        lastPinchTime = Time.time;
                        HoldOnFrame(hasFrame, pointer, anchor, barrelDir, pinchAxis);
                    }
                    else if (pointerValid && TryFetch(pointer.position, pointer.forward))
                    {
                        flySide = side;
                    }
                }
            }
            if (!held && !flying && drifting)
                DriftTick(Time.deltaTime);
        }

        void HoldOnFrame(bool hasFrame, Pose pointer, Vector3 anchor,
            Vector3 barrelDir, Vector3 pinchAxis)
        {
            if (hasFrame)
                JointHoldTick(anchor, barrelDir, pinchAxis);
            else
                HoldTick(pointer);
        }

        Pose HoldTarget(bool hasFrame, Pose pointer, Vector3 anchor,
            Vector3 barrelDir, Vector3 pinchAxis) => hasFrame
            ? WallBatPose.JointHoldPose(anchor, barrelDir, pinchAxis,
                gripLocalOffset)
            : WallBatPose.HoldPose(pointer, gripLocalOffset);

        /// <summary>
        /// Resolves the live pinch frame from hand joints: the pinching
        /// fingers' midpoint, the knuckle axis plus forward tilt for the
        /// barrel, and a best-effort roll axis cached per side for the
        /// frames where the pinching tips touch and their separation
        /// degenerates.
        /// </summary>
        bool TryGetPinchFrame(OVRHand hand, int side, Vector3 forward,
            out Vector3 pinchMid, out Vector3 fistPoint, out Vector3 barrelDir,
            out Vector3 pinchAxis, out bool fistClosed)
        {
            pinchMid = default;
            fistPoint = default;
            barrelDir = Vector3.up;
            pinchAxis = lastAcross[side];
            fistClosed = false;
            if (!(hand is OVRSkeleton.IOVRSkeletonDataProvider provider))
                return false;
            var data = provider.GetSkeletonPoseData();
            if (!data.IsDataValid || data.BoneTranslations == null ||
                data.BoneTranslations.Length <= (int)OVRSkeleton.BoneId.XRHand_LittleTip)
                return false;
            var space = TrackingSpaceOf(hand);
            if (space == null)
                return false;
            Vector3 Joint(OVRSkeleton.BoneId id) => space.TransformPoint(
                data.BoneTranslations[(int)id].FromFlippedZVector3f());
            var indexTip = Joint(OVRSkeleton.BoneId.XRHand_IndexTip);
            var thumbTip = Joint(OVRSkeleton.BoneId.XRHand_ThumbTip);
            var middleTip = Joint(OVRSkeleton.BoneId.XRHand_MiddleTip);
            var ringTip = Joint(OVRSkeleton.BoneId.XRHand_RingTip);
            var pinkyTip = Joint(OVRSkeleton.BoneId.XRHand_LittleTip);
            var palm = Joint(OVRSkeleton.BoneId.XRHand_Palm);
            var indexKnuckle = Joint(OVRSkeleton.BoneId.XRHand_IndexProximal);
            var middleKnuckle = Joint(OVRSkeleton.BoneId.XRHand_MiddleProximal);
            var pinkyKnuckle = Joint(OVRSkeleton.BoneId.XRHand_LittleProximal);
            var wrist = Joint(OVRSkeleton.BoneId.XRHand_Wrist);
            pinchMid = (indexTip + thumbTip) * 0.5f;
            fistPoint = palm;
            fistClosed = WallBatPose.IsClosedFist(fistPoint, indexTip, middleTip,
                ringTip, pinkyTip, data.RootScale > 0f ? data.RootScale : hand.HandScale);
            barrelDir = WallBatPose.TiltToward(WallBatPose.BarrelAxis(wrist,
                middleKnuckle, indexKnuckle, pinkyKnuckle), forward,
                WallBatPose.ForwardTiltDegrees);
            var spread = thumbTip - indexTip;
            if (spread.sqrMagnitude > 0.015f * 0.015f)
            {
                lastAcross[side] = spread.normalized;
                pinchAxis = lastAcross[side];
            }
            return true;
        }

        Transform TrackingSpaceOf(OVRHand hand)
        {
            if (trackingSpace == null)
            {
                var rig = FindFirstObjectByType<OVRCameraRig>();
                if (rig != null && rig.trackingSpace != null)
                    trackingSpace = rig.trackingSpace;
                else
                {
                    var current = hand != null ? hand.transform : null;
                    while (current != null)
                    {
                        if (current.name == "TrackingSpace")
                        {
                            trackingSpace = current;
                            break;
                        }
                        current = current.parent;
                    }
                }
            }
            return trackingSpace;
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

        /// <summary>0 left, 1 right, -1 unheld; matches the input side order.</summary>
        public int HoldSide => holdSide;

        /// <summary>
        /// The held bat's barrel segment in world space: the grip point under
        /// the fist and the far tip point of the model. False when the bat is
        /// not held or the visual did not mount. The input bridge sweeps this
        /// segment, so a ball the barrel passes through is reached instead of
        /// only one that touches the fist.
        /// </summary>
        public bool TryGetHeldBarrel(out Vector3 gripM, out Vector3 tipM)
        {
            gripM = default;
            tipM = default;
            if (!held || gripPoint == null || tipPoint == null)
                return false;
            gripM = gripPoint.position;
            tipM = tipPoint.position;
            return true;
        }

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

        /// <summary>
        /// Joint-anchored follow: the model grip lands on the pinch midpoint
        /// with the barrel along the hand, called from <c>LateUpdate</c>.
        /// The edit-mode suite drives this with explicit vectors.
        /// </summary>
        public void JointHoldTick(Vector3 anchor, Vector3 barrelDir,
            Vector3 pinchAxis)
        {
            if (gripPoint == null)
                return;
            var pose = WallBatPose.JointHoldPose(anchor, barrelDir, pinchAxis,
                gripLocalOffset);
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
            FlyTickTo(WallBatPose.HoldPose(pointer, gripLocalOffset), deltaTime);
        }

        public void FlyTickTo(Pose target, float deltaTime)
        {
            if (gripPoint == null)
            {
                CancelFly();
                return;
            }
            flyT += Mathf.Max(0f, deltaTime) / WallBatPose.FetchFlySeconds;
            var t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(flyT));
            transform.SetPositionAndRotation(
                Vector3.Lerp(flyFrom, target.position, t),
                Quaternion.Slerp(transform.rotation, target.rotation, t));
            lastHoldVelocity = Vector3.zero;
            if (flyT >= 1f)
            {
                flying = false;
                holdSide = flySide;
                BeginHold();
                lastPinchTime = Time.time;
                transform.SetPositionAndRotation(target.position, target.rotation);
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
            lastPinchTime = Time.time;
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
