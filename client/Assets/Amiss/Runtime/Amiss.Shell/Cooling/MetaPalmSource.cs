using UnityEngine;

namespace Amiss.Shell.Cooling
{
    /// Reads one Meta hand's palm from OVRHand's skeleton pose data.
    ///
    /// The interaction rig's live hand prefabs carry OVRHand but no
    /// OVRSkeleton, so joints come from IOVRSkeletonDataProvider exactly as
    /// WallBat reads its joint frame. Put this on the OVRHand GameObject
    /// (or a child); it finds the OVRHand in its parents or children.
    public class MetaPalmSource : PalmHandSource
    {
        [Tooltip("Component named OVRHand. Auto-found in parents/children if empty.")]
        public OVRHand ovrHand;
        [Tooltip("Flip if the palm normal comes out of the back of the hand.")]
        public bool invertNormal = false;
        public bool requireHighConfidence = true;

        Transform trackingSpace;
        float nextLookup;

        void Reset() { AutoFind(); }
        void Awake() { AutoFind(); }

        void AutoFind()
        {
            if (ovrHand == null) ovrHand = GetComponentInParent<OVRHand>();
            if (ovrHand == null) ovrHand = GetComponentInChildren<OVRHand>();
        }

        public override bool TrySample(out PalmSample sample)
        {
            sample = default;
            if (ovrHand == null && Time.time > nextLookup)
            {
                nextLookup = Time.time + 1f;
                AutoFind();
            }
            if (ovrHand == null || !ovrHand.isActiveAndEnabled) return false;
            if (!ovrHand.IsTracked || !ovrHand.IsDataValid) return false;
            if (requireHighConfidence && !ovrHand.IsDataHighConfidence) return false;
            if (!(ovrHand is OVRSkeleton.IOVRSkeletonDataProvider provider)) return false;

            var data = provider.GetSkeletonPoseData();
            if (!data.IsDataValid || data.BoneTranslations == null ||
                data.BoneTranslations.Length <= (int)OVRSkeleton.BoneId.XRHand_LittleTip)
                return false;

            var space = TrackingSpace();
            if (space == null) return false;

            Vector3 Joint(OVRSkeleton.BoneId id) =>
                space.TransformPoint(data.BoneTranslations[(int)id].FromFlippedZVector3f());

            sample = PalmGeometry.FromJoints(side,
                Joint(OVRSkeleton.BoneId.XRHand_Wrist),
                Joint(OVRSkeleton.BoneId.XRHand_Palm),
                Joint(OVRSkeleton.BoneId.XRHand_IndexProximal),
                Joint(OVRSkeleton.BoneId.XRHand_MiddleProximal),
                Joint(OVRSkeleton.BoneId.XRHand_LittleProximal),
                Joint(OVRSkeleton.BoneId.XRHand_IndexTip),
                Joint(OVRSkeleton.BoneId.XRHand_MiddleTip),
                Joint(OVRSkeleton.BoneId.XRHand_RingTip),
                Joint(OVRSkeleton.BoneId.XRHand_LittleTip),
                Joint(OVRSkeleton.BoneId.XRHand_ThumbTip),
                invertNormal);
            return sample.tracked;
        }

        Transform TrackingSpace()
        {
            if (trackingSpace != null) return trackingSpace;
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null && rig.trackingSpace != null)
            {
                trackingSpace = rig.trackingSpace;
                return trackingSpace;
            }
            var current = transform;
            while (current != null)
            {
                if (current.name == "TrackingSpace")
                {
                    trackingSpace = current;
                    return trackingSpace;
                }
                current = current.parent;
            }
            return null;
        }
    }

    /// Joint-to-palm math, kept pure so the edit-mode suite can pin the
    /// handedness-dependent normal without a headset.
    public static class PalmGeometry
    {
        /// <summary>
        /// Builds one tracked sample. The palm normal comes from the knuckle
        /// plane: <c>cross(index - wrist, pinky - wrist)</c> points out of the
        /// back of the left hand and out of the palm of the right hand, so the
        /// right side is negated. With palms facing each other this yields
        /// normals pointing across the gap.
        /// </summary>
        public static PalmSample FromJoints(PalmHandSource.Side side,
            Vector3 wrist, Vector3 palm,
            Vector3 indexProximal, Vector3 middleProximal, Vector3 littleProximal,
            Vector3 indexTip, Vector3 middleTip, Vector3 ringTip, Vector3 pinkyTip,
            Vector3 thumbTip, bool invertNormal = false)
        {
            var normal = Vector3.Cross(indexProximal - wrist, littleProximal - wrist);
            if (normal.sqrMagnitude < 1e-10f)
                normal = Vector3.Cross(middleProximal - wrist, thumbTip - wrist);
            normal = normal.sqrMagnitude > 1e-10f ? normal.normalized : Vector3.up;
            if (side == PalmHandSource.Side.Right) normal = -normal;
            if (invertNormal) normal = -normal;

            var finger = middleProximal - wrist;
            var fingerDir = finger.sqrMagnitude > 1e-10f ? finger.normalized : Vector3.up;

            return new PalmSample
            {
                tracked = true,
                center = palm,
                normal = normal,
                fingerDir = fingerDir,
                openness = PalmHandSource.ComputeOpenness(
                    palm, indexTip, middleTip, ringTip, pinkyTip, thumbTip)
            };
        }
    }
}
