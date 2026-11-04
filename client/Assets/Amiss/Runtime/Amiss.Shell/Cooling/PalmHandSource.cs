using UnityEngine;

namespace Amiss.Shell.Cooling
{
    /// One hand's palm, in world space, sampled this frame.
    public struct PalmSample
    {
        public bool tracked;          // hand tracked AND high-confidence
        public Vector3 center;        // palm centre (between wrist and middle-finger knuckle)
        public Vector3 normal;        // unit vector out of the palm (the side you'd push with)
        public Vector3 fingerDir;     // unit vector wrist -> middle knuckle
        public float openness;        // 0 = fist / pinch, 1 = flat open hand
    }

    /// Base class for anything that can supply a palm (Meta hands, test transforms, ...).
    public abstract class PalmHandSource : MonoBehaviour
    {
        public enum Side { Left, Right }
        public Side side = Side.Left;
        public abstract bool TrySample(out PalmSample sample);

        /// Shared openness metric. tips = Index/Middle/Ring/Pinky tips, thumbTip, indexTip.
        /// A finger counts as extended when its tip is >= ~8 cm from the palm centre (pinky 6.5 cm).
        /// Thumb-index gap < 3.5 cm means "pinching" and openness is forced down.
        public static float ComputeOpenness(Vector3 palm, Vector3 index, Vector3 middle, Vector3 ring, Vector3 pinky, Vector3 thumb)
        {
            float e(Vector3 tip, float closed, float open) =>
                Mathf.Clamp01(Mathf.InverseLerp(closed, open, Vector3.Distance(tip, palm)));
            float fingers = (e(index, 0.045f, 0.080f) + e(middle, 0.045f, 0.085f) +
                             e(ring, 0.040f, 0.078f) + e(pinky, 0.035f, 0.065f)) * 0.25f;
            float pinchGap = Mathf.Clamp01(Mathf.InverseLerp(0.020f, 0.045f, Vector3.Distance(thumb, index)));
            return fingers * Mathf.Lerp(0.3f, 1f, pinchGap);
        }
    }
}
