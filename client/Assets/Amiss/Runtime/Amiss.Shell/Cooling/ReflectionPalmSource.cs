using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace Amiss.Shell.Cooling
{
    /// Reads a Meta OVRHand + OVRSkeleton through reflection, so this compiles with or
    /// without the Meta XR SDK installed. Works with both legacy (Hand_*) and OpenXR
    /// (XRHand_*) bone ids by matching name suffixes, exactly like HandBoneUtil.FindBone.
    /// Put it on (or under) the OVRHandPrefab of each hand and set "side".
    public class ReflectionPalmSource : PalmHandSource
    {
        [Tooltip("Component named OVRHand. Auto-found in parents/children if empty.")]
        public Component ovrHand;
        [Tooltip("Component named OVRSkeleton. Auto-found if empty.")]
        public Component ovrSkeleton;
        [Tooltip("Flip if the debug gizmo shows the normal coming out of the back of the hand.")]
        public bool invertNormal = false;
        public bool requireHighConfidence = true;
        public bool drawGizmo = true;

        Transform wrist, indexK, middleK, pinkyK, tIndex, tMiddle, tRing, tPinky, tThumb;
        PropertyInfo pTracked, pValid, pHighConf, pSkInit, pBones;
        float nextLookup;
        PalmSample last;

        void Reset() { AutoFind(); }
        void Awake() { AutoFind(); }

        void AutoFind()
        {
            if (ovrHand == null) ovrHand = FindByType("OVRHand");
            if (ovrSkeleton == null) ovrSkeleton = FindByType("OVRSkeleton");
            if (ovrHand != null)
            {
                var t = ovrHand.GetType();
                pTracked = t.GetProperty("IsTracked");
                pValid = t.GetProperty("IsDataValid");
                pHighConf = t.GetProperty("IsDataHighConfidence");
            }
            if (ovrSkeleton != null)
            {
                var t = ovrSkeleton.GetType();
                pSkInit = t.GetProperty("IsInitialized");
                pBones = t.GetProperty("Bones");
            }
        }

        Component FindByType(string typeName)
        {
            foreach (var c in GetComponentsInParent<Component>(true)) if (c && c.GetType().Name == typeName) return c;
            foreach (var c in GetComponentsInChildren<Component>(true)) if (c && c.GetType().Name == typeName) return c;
            return null;
        }

        static bool B(PropertyInfo p, object o) => p != null && o != null && p.GetValue(o) is bool b && b;

        bool LookupBones()
        {
            if (!B(pSkInit, ovrSkeleton) || pBones == null) return false;
            if (!(pBones.GetValue(ovrSkeleton) is IEnumerable bones)) return false;
            wrist = indexK = middleK = pinkyK = tIndex = tMiddle = tRing = tPinky = tThumb = null;
            foreach (var bone in bones)
            {
                if (bone == null) continue;
                var bt = bone.GetType();
                string id = bt.GetProperty("Id")?.GetValue(bone)?.ToString() ?? "";
                var tr = bt.GetProperty("Transform")?.GetValue(bone) as Transform;
                if (tr == null) continue;
                if (id.EndsWith("WristRoot") || id.EndsWith("XRHand_Wrist")) wrist = tr;
                else if (id.EndsWith("Index1") || id.EndsWith("IndexProximal")) indexK = tr;
                else if (id.EndsWith("Middle1") || id.EndsWith("MiddleProximal")) middleK = tr;
                else if (id.EndsWith("Pinky1") || id.EndsWith("LittleProximal")) pinkyK = tr;
                else if (id.EndsWith("IndexTip")) tIndex = tr;
                else if (id.EndsWith("MiddleTip")) tMiddle = tr;
                else if (id.EndsWith("RingTip")) tRing = tr;
                else if (id.EndsWith("PinkyTip") || id.EndsWith("LittleTip")) tPinky = tr;
                else if (id.EndsWith("ThumbTip")) tThumb = tr;
            }
            return wrist && indexK && middleK && pinkyK && tIndex && tMiddle && tRing && tPinky && tThumb;
        }

        public override bool TrySample(out PalmSample s)
        {
            s = default;
            if (ovrHand == null || ovrSkeleton == null) { if (Time.time > nextLookup) { nextLookup = Time.time + 1f; AutoFind(); } return false; }
            if (!B(pTracked, ovrHand) || !B(pValid, ovrHand)) return false;
            if (requireHighConfidence && pHighConf != null && !B(pHighConf, ovrHand)) return false;
            if (wrist == null || tThumb == null) { if (Time.time < nextLookup || !LookupBones()) { nextLookup = Time.time + 0.5f; return false; } }

            Vector3 w = wrist.position, m = middleK.position;
            s.center = Vector3.Lerp(w, m, 0.55f);
            s.fingerDir = (m - w).normalized;
            Vector3 n = Vector3.Cross(indexK.position - w, pinkyK.position - w).normalized;
            // Right hand: (index x pinky) points out of the palm; left hand is mirrored.
            if (side == Side.Left) n = -n;
            if (invertNormal) n = -n;
            s.normal = n;
            s.openness = ComputeOpenness(s.center, tIndex.position, tMiddle.position, tRing.position, tPinky.position, tThumb.position);
            s.tracked = true;
            last = s;
            return true;
        }

        void OnDrawGizmosSelected()
        {
            if (!drawGizmo || !last.tracked) return;
            Gizmos.color = Color.cyan; Gizmos.DrawLine(last.center, last.center + last.normal * 0.1f);
            Gizmos.DrawWireSphere(last.center, 0.01f);
        }
    }
}
