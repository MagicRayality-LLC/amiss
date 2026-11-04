using System;
using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell
{
    /// <summary>Where the loose cloth bag currently is.</summary>
    public enum ClothBagState
    {
        Hidden,
        Docked,
        Held,
        Fetching,
        Thrown,
        Floating
    }

    /// <summary>
    /// Dock, hold, throw, and float math for the loose cloth bag, in meters.
    /// Pure functions so the edit-mode suite can pin them without a headset.
    /// </summary>
    public static class ClothBagPose
    {
        /// <summary>
        /// Bag centre in TV-local space, beside the bat in front of the set.
        /// The glass faces local -Z, so a negative Z keeps the bag on the
        /// room side where the wearer can see and reach it.
        /// </summary>
        public static readonly Vector3 DockLocalPosition = new Vector3(-0.30f, -0.52f, -0.14f);

        /// <summary>Upright dock orientation, only turned by the television's own pose.</summary>
        public static readonly Vector3 DockLocalEuler = new Vector3(0f, 0f, 0f);

        /// <summary>Pinch or fist within this of the bag centre grabs it directly.</summary>
        public const float CloseGrabRadiusMeters = 0.24f;

        /// <summary>Far pinch-fetch works within this range.</summary>
        public const float FetchRangeMeters = 5f;

        /// <summary>The pointing cone for a far fetch, in degrees.</summary>
        public const float FetchHalfAngleDegrees = 12f;

        /// <summary>How long a fetched bag takes to fly to the hand.</summary>
        public const float FetchFlySeconds = 0.28f;

        /// <summary>Release speed that counts as a throw rather than a drop.</summary>
        public const float ThrowStartSpeedMeters = 1.2f;

        /// <summary>Measured hand speed is clamped to this, so a noisy frame cannot rocket the bag.</summary>
        public const float MaxThrowSpeedMeters = 8f;

        /// <summary>Downward acceleration of the thrown bag until it stops itself.</summary>
        public const float ThrowGravityMPerS2 = 9.81f;

        /// <summary>A throw always travels this long before it may stop at its apex.</summary>
        public const float MinThrowSeconds = 0.12f;

        /// <summary>Longest a throw flies before it stops and floats.</summary>
        public const float MaxThrowSeconds = 1.4f;

        /// <summary>Furthest a thrown bag may travel from the release point.</summary>
        public const float MaxThrowDistanceMeters = 2.5f;

        /// <summary>Exponential damping applied to a slow release.</summary>
        public const float GlideDamping = 6f;

        /// <summary>A release below this speed floats where it was let go.</summary>
        public const float GlideStopSpeedMeters = 0.15f;

        /// <summary>A pinch dropout shorter than this keeps the hold.</summary>
        public const float PinchGraceSeconds = 0.08f;

        /// <summary>
        /// A freshly spawned bag ignores the live hand for this long, so the
        /// replacement created by a grab cannot be grabbed by the same closed
        /// hand in the same frame and cascade into another replacement.
        /// </summary>
        public const float SpawnGrabDelaySeconds = 0.25f;

        /// <summary>
        /// Floor for the centre distance between two still-floating bags, in
        /// meters. The supply uses the larger of this floor and the two
        /// visuals' measured extents. Two bags closer than the resulting
        /// distance push each other apart; the visual is never a physics body,
        /// so the separation is presentation geometry only.
        /// </summary>
        public const float MinBagSeparationMeters = 0.22f;

        /// <summary>
        /// Returns the symmetric push-apart deltas for two bag centres closer
        /// than <paramref name="minimum"/>. Coincident centres fall back to a
        /// deterministic horizontal axis, so the result never depends on
        /// floating-point direction noise.
        /// </summary>
        public static bool TrySeparate(Vector3 first, Vector3 second, float minimum,
            out Vector3 firstDelta, out Vector3 secondDelta)
        {
            firstDelta = default;
            secondDelta = default;
            if (!IsFinite(first) || !IsFinite(second) || minimum <= 0f)
                return false;
            var offset = second - first;
            var distance = offset.magnitude;
            if (distance >= minimum)
                return false;
            var direction = distance > 1e-5f ? offset / distance : Vector3.right;
            var push = (minimum - distance) * 0.5f;
            firstDelta = -direction * push;
            secondDelta = direction * push;
            return true;
        }

        public static Pose DockPose(Pose television) => new Pose(
            television.position + television.rotation * DockLocalPosition,
            television.rotation * Quaternion.Euler(DockLocalEuler));

        public static Vector3 ClampThrowVelocity(Vector3 velocity) =>
            Vector3.ClampMagnitude(velocity, MaxThrowSpeedMeters);

        public static bool IsThrow(Vector3 releaseVelocity) =>
            releaseVelocity.magnitude >= ThrowStartSpeedMeters;

        public static Vector3 DampGlide(Vector3 velocity, float deltaTime) =>
            velocity * Mathf.Exp(-GlideDamping * Mathf.Max(0f, deltaTime));

        /// <summary>
        /// One ballistic step of a thrown bag. The caller checks
        /// <see cref="ShouldStopThrow"/> afterwards; the stop itself is the
        /// founder's "stop itself automatically and float there still".
        /// </summary>
        public static void StepThrow(ref Vector3 position, ref Vector3 velocity,
            float deltaTime)
        {
            var step = Mathf.Max(0f, deltaTime);
            velocity.y -= ThrowGravityMPerS2 * step;
            position += velocity * step;
        }

        /// <summary>
        /// True when the thrown bag stops itself: at the top of its arc after
        /// the minimum flight, after the longest flight, at the distance cap,
        /// or when the motion stopped being finite.
        /// </summary>
        public static bool ShouldStopThrow(Vector3 position, Vector3 velocity,
            Vector3 releasePosition, float elapsedSeconds)
        {
            if (!IsFinite(position))
                return true;
            if (elapsedSeconds >= MaxThrowSeconds)
                return true;
            if (Vector3.Distance(position, releasePosition) >= MaxThrowDistanceMeters)
                return true;
            return elapsedSeconds >= MinThrowSeconds && velocity.y <= 0f;
        }

        /// <summary>
        /// True when an open tracked hand must release the bag now: only after
        /// the bag's shorter grace window past the last grip frame.
        /// </summary>
        public static bool ShouldRelease(float now, float lastGripTime) =>
            now - lastGripTime >= PinchGraceSeconds;

        public static bool AimsAt(Vector3 handPosition, Vector3 handForward, Vector3 target)
        {
            var to = target - handPosition;
            var distance = to.magnitude;
            if (distance < 0.3f || distance > FetchRangeMeters)
                return false;
            return Vector3.Angle(handForward, to / distance) <= FetchHalfAngleDegrees;
        }

        public static float AimAngle(Vector3 handPosition, Vector3 handForward,
            Vector3 target)
        {
            var to = target - handPosition;
            if (to.sqrMagnitude < 1e-10f)
                return 0f;
            return Vector3.Angle(handForward, to.normalized);
        }

        public static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }

    /// <summary>
    /// The loose cloth bag docked under the television. It is the wearer-facing
    /// body of the P0 slowing tool: hands only, a pinch or closed fist near it
    /// grabs it, and the same gesture while pointing at it from across the room
    /// fetches it to the hand, exactly like the bat. Releasing it with measured
    /// hand speed throws it; the throw stops itself at the top of its arc and
    /// the bag floats still there. A slow release floats it where it was let go.
    ///
    /// The visual owns no physics and writes no Trial state. The bag center is
    /// the hover anchor the sandbag tool arms once it floats; the scientific
    /// writer still applies and measures the one absorption of 07 section 4.2.
    /// A <see cref="ClothBagSupply"/> keeps the dock stocked: a grab spawns a
    /// fresh docked bag, up to 100 bags in the room, and a consumed bag is
    /// removed with the reference firework.
    /// </summary>
    public sealed class WallClothBag : MonoBehaviour
    {
        Transform dockedTo;
        Transform trackingSpace;
        float separationRadius;
        bool built;
        bool shown;
        bool held;
        bool fetching;
        bool thrown;
        bool gliding;
        bool floating;
        bool docked = true;
        int holdSide = -1;
        int fetchSide = -1;
        float lastGripTime;
        float fetchT;
        Vector3 fetchFrom;
        float throwElapsed;
        Vector3 throwVelocity;
        Vector3 throwStart;
        Vector3 glideVelocity;
        Vector3 heldVelocity;
        Vector3 floatingPosition;
        int floatSerial;
        float floatTime;
        float grabAllowedTime;
        static OVRHand[] cachedHands;
        static int cachedHandsFrame = -1;
        readonly Dictionary<int, bool> wasGripping = new();

        /// <summary>Fired once when the bag passes into a hold (grab or fetch arrival).</summary>
        public event Action Grabbed;

        void Awake()
        {
            Build();
            grabAllowedTime = Time.time + ClothBagPose.SpawnGrabDelaySeconds;
            gameObject.SetActive(false);
            shown = false;
        }

        void LateUpdate()
        {
            if (!shown || !built)
                return;
            var hands = TrackedHands();
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
                var hasFrame = TryGetHandFrame(hand, out var pinchMid, out var fistPoint,
                    out var fistClosed);
                var gripping = pinching || fistClosed;
                var edge = gripping && !IsGripping(side);
                wasGripping[side] = gripping;
                var anchor = WallBatPose.GripAnchor(indexPinching, pinchMid, fistPoint);
                var grabPoint = !pinching && fistClosed && hasFrame
                    ? fistPoint
                    : pointer.position;
                if (held && side == holdSide)
                {
                    if (gripping)
                    {
                        lastGripTime = Time.time;
                        if (hasFrame || pointerValid)
                            HoldTick(hasFrame ? anchor : pointer.position, Time.deltaTime);
                    }
                    else if (!ClothBagPose.ShouldRelease(Time.time, lastGripTime))
                    {
                        if (hasFrame || pointerValid)
                            HoldTick(hasFrame ? anchor : pointer.position, Time.deltaTime);
                    }
                    else
                    {
                        ReleaseHold();
                    }
                }
                else if (fetching && side == fetchSide)
                {
                    if (gripping && (hasFrame || pointerValid))
                        FetchTickTo(hasFrame ? anchor : pointer.position, Time.deltaTime);
                    else if (!gripping)
                        CancelFetch();
                }
                else if (!held && !fetching && edge &&
                         Time.time >= grabAllowedTime)
                {
                    if ((hasFrame || pointerValid) && TryCloseGrab(grabPoint))
                    {
                        holdSide = side;
                        lastGripTime = Time.time;
                        HoldTick(hasFrame ? anchor : pointer.position, 0f);
                    }
                    else if (pointerValid &&
                             TryFetch(pointer.position, pointer.forward))
                    {
                        fetchSide = side;
                    }
                }
            }
            if (thrown)
                ThrowTick(Time.deltaTime);
            else if (gliding)
                GlideTick(Time.deltaTime);
        }

        /// <summary>
        /// One hand lookup per frame for every bag in the supply. A room at the
        /// 100-bag cap would otherwise search the scene 100 times a frame.
        /// </summary>
        static OVRHand[] TrackedHands()
        {
            if (cachedHands == null || cachedHandsFrame != Time.frameCount)
            {
                cachedHands = FindObjectsByType<OVRHand>(FindObjectsInactive.Exclude);
                cachedHandsFrame = Time.frameCount;
            }
            return cachedHands;
        }

        /// <summary>
        /// Resolves the live pinch frame: the pinching fingers' midpoint, the
        /// palm as the fist point, and a three-of-four curled-finger fist.
        /// </summary>
        bool TryGetHandFrame(OVRHand hand, out Vector3 pinchMid, out Vector3 fistPoint,
            out bool fistClosed)
        {
            pinchMid = default;
            fistPoint = default;
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
            pinchMid = (indexTip + thumbTip) * 0.5f;
            fistPoint = Joint(OVRSkeleton.BoneId.XRHand_Palm);
            fistClosed = WallBatPose.IsClosedFist(fistPoint, indexTip, middleTip,
                ringTip, pinkyTip, data.RootScale > 0f ? data.RootScale : hand.HandScale);
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
        /// Loads the visual once, centered on the host so the host position is
        /// the bag centre the hover region uses. Awake runs it; the edit-mode
        /// suite calls it without a play session.
        /// </summary>
        public void Build()
        {
            if (built)
                return;
            built = true;
            if (!SandbagModel.TryMount(transform, out var view, hover: false,
                    centerOnHost: true) || view?.Root == null)
                return;
            var renderers = view.Root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return;
            var bounds = renderers[0].bounds;
            for (var index = 1; index < renderers.Length; index++)
                bounds.Encapsulate(renderers[index].bounds);
            separationRadius = Mathf.Max(bounds.extents.x,
                Mathf.Max(bounds.extents.y, bounds.extents.z));
        }

        /// <summary>
        /// The visual's largest half-extent, used so two floating bags push
        /// apart before they overlap. Falls back to the declared floor.
        /// </summary>
        public float SeparationRadiusMeters => Mathf.Max(separationRadius,
            ClothBagPose.MinBagSeparationMeters * 0.5f);

        public ClothBagState State
        {
            get
            {
                if (!shown)
                    return ClothBagState.Hidden;
                if (held)
                    return ClothBagState.Held;
                if (fetching)
                    return ClothBagState.Fetching;
                if (thrown)
                    return ClothBagState.Thrown;
                if (floating)
                    return ClothBagState.Floating;
                return docked ? ClothBagState.Docked : ClothBagState.Floating;
            }
        }

        public bool IsFloating => shown && floating;
        public bool IsDocked => docked && !held && !fetching && !thrown && !floating;
        public Vector3 FloatingPosition => floatingPosition;

        /// <summary>
        /// Increases each time the bag settles to a still float, so the tool
        /// controller arms the hover region exactly once per float.
        /// </summary>
        public int FloatSerial => floatSerial;

        /// <summary>When this bag settled to its most recent float, across all bags.</summary>
        public float FloatTime => floatTime;

        public void SetVisible(bool visible)
        {
            if (!visible)
            {
                held = false;
                fetching = false;
                thrown = false;
                gliding = false;
                floating = false;
                holdSide = -1;
                fetchSide = -1;
                glideVelocity = Vector3.zero;
                throwVelocity = Vector3.zero;
                heldVelocity = Vector3.zero;
                docked = true;
                wasGripping.Clear();
                PlaceDocked();
            }
            shown = visible;
            if (gameObject.activeSelf != visible)
                gameObject.SetActive(visible);
        }

        /// <summary>
        /// Keeps an untouched bag docked under the television. A bag the wearer
        /// grabbed, threw, or floated stays where it is.
        /// </summary>
        public void Follow(Transform television)
        {
            if (television != null)
                dockedTo = television;
            if (television == null || !docked || held || fetching || thrown ||
                gliding || floating || !built)
                return;
            PlaceDocked();
        }

        void PlaceDocked()
        {
            if (dockedTo == null)
                return;
            var pose = ClothBagPose.DockPose(
                new Pose(dockedTo.position, dockedTo.rotation));
            transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        public bool TryCloseGrab(Vector3 handPosition)
        {
            if (held || fetching)
                return false;
            if (Vector3.Distance(handPosition, transform.position) >
                ClothBagPose.CloseGrabRadiusMeters)
                return false;
            return BeginHold();
        }

        public bool TryFetch(Vector3 handPosition, Vector3 handForward)
        {
            if (held || fetching)
                return false;
            var forward = handForward.normalized;
            if (!ClothBagPose.AimsAt(handPosition, forward, transform.position))
                return false;
            // The bat docks beside the bag. When both are candidates, the one
            // closer to the aim ray wins so one pinch never fetches both.
            var bat = FindFirstObjectByType<WallBat>();
            if (bat != null && bat.IsDocked)
            {
                var batAngle = ClothBagPose.AimAngle(handPosition, forward,
                    bat.transform.position);
                var bagAngle = ClothBagPose.AimAngle(handPosition, forward,
                    transform.position);
                if (batAngle < bagAngle)
                    return false;
            }
            fetching = true;
            fetchSide = -1;
            fetchFrom = transform.position;
            fetchT = 0f;
            floating = false;
            thrown = false;
            gliding = false;
            docked = false;
            glideVelocity = Vector3.zero;
            throwVelocity = Vector3.zero;
            return true;
        }

        public void HoldTick(Vector3 anchor, float deltaTime)
        {
            var pose = HoldPosition(anchor);
            var step = Mathf.Max(0f, deltaTime);
            if (step > 1e-5f)
                heldVelocity = ClothBagPose.IsFinite(pose) && ClothBagPose.IsFinite(
                    transform.position)
                    ? (pose - transform.position) / step
                    : Vector3.zero;
            transform.position = pose;
        }

        static Vector3 HoldPosition(Vector3 anchor) => anchor;

        public void FetchTickTo(Vector3 target, float deltaTime)
        {
            fetchT += Mathf.Max(0f, deltaTime) / ClothBagPose.FetchFlySeconds;
            var t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(fetchT));
            transform.position = Vector3.Lerp(fetchFrom, target, t);
            heldVelocity = Vector3.zero;
            if (fetchT >= 1f)
            {
                fetching = false;
                BeginHold();
                transform.position = target;
            }
        }

        /// <summary>
        /// Ends a hold: measured hand speed throws the bag, a slow release
        /// glides to a stop, and both end floating still. Called from the
        /// hand loop and by the edit-mode suite.
        /// </summary>
        public void ReleaseHold()
        {
            if (!held)
                return;
            held = false;
            holdSide = -1;
            docked = false;
            var velocity = ClothBagPose.IsFinite(heldVelocity)
                ? ClothBagPose.ClampThrowVelocity(heldVelocity)
                : Vector3.zero;
            heldVelocity = Vector3.zero;
            if (ClothBagPose.IsThrow(velocity))
            {
                thrown = true;
                gliding = false;
                throwVelocity = velocity;
                throwStart = transform.position;
                throwElapsed = 0f;
                Debug.Log("[Amiss] cloth bag thrown speed=" +
                          velocity.magnitude.ToString("F2"));
                return;
            }
            if (velocity.magnitude >= ClothBagPose.GlideStopSpeedMeters)
            {
                gliding = true;
                glideVelocity = velocity;
                return;
            }
            glideVelocity = Vector3.zero;
            FloatNow();
        }

        public void ThrowTick(float deltaTime)
        {
            if (!thrown)
                return;
            var step = Mathf.Max(0f, deltaTime);
            var position = transform.position;
            throwElapsed += step;
            ClothBagPose.StepThrow(ref position, ref throwVelocity, step);
            transform.position = position;
            if (ClothBagPose.ShouldStopThrow(position, throwVelocity, throwStart,
                    throwElapsed))
                FloatNow();
        }

        public void GlideTick(float deltaTime)
        {
            if (!gliding)
                return;
            glideVelocity = ClothBagPose.DampGlide(glideVelocity, deltaTime);
            if (glideVelocity.magnitude < ClothBagPose.GlideStopSpeedMeters)
            {
                glideVelocity = Vector3.zero;
                FloatNow();
                return;
            }
            transform.position += glideVelocity * Mathf.Max(0f, deltaTime);
        }

        void FloatNow()
        {
            thrown = false;
            gliding = false;
            fetching = false;
            fetchSide = -1;
            docked = false;
            floating = true;
            floatingPosition = transform.position;
            throwVelocity = Vector3.zero;
            glideVelocity = Vector3.zero;
            floatSerial++;
            floatTime = Time.realtimeSinceStartup;
            Debug.Log("[Amiss] cloth bag floating t=" +
                      Time.realtimeSinceStartup.ToString("F2") + " pos=" +
                      floatingPosition.ToString("F2"));
        }

        /// <summary>
        /// Floats the bag at a requested anchor, used by a stage that orders a
        /// bag instead of the wearer throwing one.
        /// </summary>
        public void PlaceAt(Vector3 position)
        {
            if (!built || !ClothBagPose.IsFinite(position))
                return;
            transform.position = position;
            held = false;
            holdSide = -1;
            heldVelocity = Vector3.zero;
            FloatNow();
        }

        /// <summary>
        /// Moves a still-floating bag by a presentation-only offset, used when
        /// two floating bags overlap and push each other aside. The stored
        /// float anchor moves with the visual so the tool can re-arm the hover
        /// region at the new centre. Any other state ignores the nudge.
        /// </summary>
        public bool NudgeFloating(Vector3 delta)
        {
            if (!shown || !floating || !ClothBagPose.IsFinite(delta))
                return false;
            floatingPosition += delta;
            transform.position = floatingPosition;
            return true;
        }

        /// <summary>
        /// Restocks this bag under the television. The supply's consume path
        /// uses it only when no supply owns the bag, so replenishment has no
        /// inventory, cooldown, or score (07 section 4.2).
        /// </summary>
        public void RespawnToDock()
        {
            if (!shown)
                return;
            held = false;
            fetching = false;
            thrown = false;
            gliding = false;
            floating = false;
            docked = true;
            holdSide = -1;
            fetchSide = -1;
            throwVelocity = Vector3.zero;
            glideVelocity = Vector3.zero;
            heldVelocity = Vector3.zero;
            PlaceDocked();
            Debug.Log("[Amiss] cloth bag respawned");
        }

        bool BeginHold()
        {
            held = true;
            fetching = false;
            thrown = false;
            gliding = false;
            floating = false;
            docked = false;
            fetchSide = -1;
            glideVelocity = Vector3.zero;
            throwVelocity = Vector3.zero;
            heldVelocity = Vector3.zero;
            lastGripTime = Time.time;
            Debug.Log("[Amiss] cloth bag grabbed");
            Grabbed?.Invoke();
            return true;
        }

        void CancelFetch()
        {
            fetching = false;
            fetchSide = -1;
            docked = false;
            FloatNow();
        }

        bool IsGripping(int side) =>
            wasGripping.TryGetValue(side, out var heldDown) && heldDown;

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
