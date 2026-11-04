using UnityEngine;
using UnityEngine.Events;

namespace Amiss.Shell.Cooling
{
    /// Detects "both hands open, palms facing each other" and exposes a smoothed Strength 0..1.
    ///
    /// Enter (all must hold for enterHoldTime):
    ///   both hands tracked + high confidence, openness >= openEnter (0.70),
    ///   each palm normal within facingEnterDeg (35 deg) of the direction to the other palm,
    ///   normals anti-parallel within antiParallelEnterDeg (40 deg),
    ///   palm distance in [minDistance 0.08, maxDistance 0.80] m.
    /// Exit (any for exitHoldTime): openness < openExit (0.50), facing > facingExitDeg (48),
    ///   anti-parallel > antiParallelExitDeg (55), distance outside [0.06, 0.88] m, or tracking lost > lostGrace.
    public class PalmCoolingGesture : MonoBehaviour
    {
        public PalmHandSource leftHand;
        public PalmHandSource rightHand;

        [Header("Openness (0 fist .. 1 flat)")]
        [Range(0, 1)] public float openEnter = 0.70f;
        [Range(0, 1)] public float openExit = 0.50f;
        [Header("Facing (degrees)")]
        public float facingEnterDeg = 35f;
        public float facingExitDeg = 48f;
        public float antiParallelEnterDeg = 40f;
        public float antiParallelExitDeg = 55f;
        [Header("Distance (metres)")]
        public float minDistance = 0.08f;
        public float maxDistance = 0.80f;
        public float distanceExitMargin = 0.08f;
        [Header("Timing")]
        public float enterHoldTime = 0.10f;
        public float exitHoldTime = 0.12f;
        public float lostGrace = 0.20f;
        public float fadeInTime = 0.25f;
        public float fadeOutTime = 0.30f;
        [Tooltip("Pose smoothing time constant (s). 0 = raw.")]
        public float poseSmoothing = 0.045f;

        public UnityEvent OnFieldStart = new UnityEvent();
        public UnityEvent OnFieldEnd = new UnityEvent();

        // ---- outputs ----
        public bool IsActive { get; private set; }
        /// Smoothed 0..1, fades in/out; scaled by alignment and openness while active.
        public float Strength { get; private set; }
        /// 0 at maxDistance .. 1 at minDistance.
        public float Closeness { get; private set; }
        public float Distance { get; private set; }
        public PalmSample Left => sL;
        public PalmSample Right => sR;
        /// Last valid (smoothed) palms are kept while fading out so the effect can finish.
        public bool HasPose { get; private set; }

        PalmSample sL, sR;
        float enterTimer, exitTimer, lostTimer, targetStrength;

        void Update() => Step(Time.deltaTime);

        /// <summary>Advances the detector by one deterministic frame.</summary>
        public void Step(float deltaSeconds)
        {
            bool okL = leftHand && leftHand.TrySample(out var rawL) ? (sL = Smooth(sL, rawL, HasPose, deltaSeconds)).tracked : false;
            bool okR = rightHand && rightHand.TrySample(out var rawR) ? (sR = Smooth(sR, rawR, HasPose, deltaSeconds)).tracked : false;
            float dt = deltaSeconds;

            bool both = okL && okR;
            if (both) { HasPose = true; lostTimer = 0f; } else lostTimer += dt;

            float align = 0f, open = 0f;
            bool enterOk = false, exitBad = false;
            if (both)
            {
                Vector3 d = sR.center - sL.center;
                Distance = d.magnitude;
                Vector3 dir = Distance > 1e-4f ? d / Distance : Vector3.right;
                float fL = Vector3.Angle(sL.normal, dir);
                float fR = Vector3.Angle(sR.normal, -dir);
                float anti = Vector3.Angle(sL.normal, -sR.normal);
                open = Mathf.Min(sL.openness, sR.openness);
                float worstFacing = Mathf.Max(fL, fR);
                enterOk = open >= openEnter && worstFacing <= facingEnterDeg && anti <= antiParallelEnterDeg &&
                          Distance >= minDistance && Distance <= maxDistance;
                exitBad = open < openExit || worstFacing > facingExitDeg || anti > antiParallelExitDeg ||
                          Distance < minDistance - distanceExitMargin * 0.25f || Distance > maxDistance + distanceExitMargin;
                align = Mathf.Clamp01(1f - worstFacing / facingExitDeg) * Mathf.Clamp01(1f - anti / (antiParallelExitDeg * 1.4f));
                Closeness = Mathf.Clamp01(Mathf.InverseLerp(maxDistance, minDistance, Distance));
            }
            else exitBad = lostTimer > lostGrace;

            if (!IsActive)
            {
                enterTimer = enterOk ? enterTimer + dt : 0f;
                if (enterTimer >= enterHoldTime) { IsActive = true; exitTimer = 0f; OnFieldStart.Invoke(); }
            }
            else
            {
                exitTimer = exitBad ? exitTimer + dt : 0f;
                if (exitTimer >= exitHoldTime) { IsActive = false; enterTimer = 0f; OnFieldEnd.Invoke(); }
            }

            if (IsActive && both)
                targetStrength = Mathf.Lerp(0.35f, 1f, align) * Mathf.Lerp(0.6f, 1f, Mathf.InverseLerp(openExit, 1f, open));
            else if (!IsActive) targetStrength = 0f;

            float rate = targetStrength > Strength ? 1f / Mathf.Max(0.01f, fadeInTime) : 1f / Mathf.Max(0.01f, fadeOutTime);
            Strength = Mathf.MoveTowards(Strength, targetStrength, rate * dt);
        }

        PalmSample Smooth(PalmSample prev, PalmSample raw, bool hasPrev, float deltaSeconds)
        {
            if (!raw.tracked || !hasPrev || !prev.tracked || poseSmoothing <= 0f) return raw;
            float k = 1f - Mathf.Exp(-deltaSeconds / poseSmoothing);
            raw.center = Vector3.Lerp(prev.center, raw.center, k);
            raw.normal = Vector3.Slerp(prev.normal, raw.normal, k).normalized;
            raw.fingerDir = Vector3.Slerp(prev.fingerDir, raw.fingerDir, k).normalized;
            raw.openness = Mathf.Lerp(prev.openness, raw.openness, k);
            return raw;
        }
    }
}
