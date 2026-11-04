using UnityEngine;

namespace Amiss.Shell.Cooling
{
    /// Palm stand-in for desktop testing (or for driving the effect from any transform).
    /// The transform's forward (+Z) is the palm normal, up (+Y) is the finger direction.
    public class TransformPalmSource : PalmHandSource
    {
        [Range(0, 1)] public float openness = 1f;
        public bool tracked = true;

        public override bool TrySample(out PalmSample s)
        {
            s = new PalmSample
            {
                tracked = tracked && isActiveAndEnabled,
                center = transform.position,
                normal = transform.forward,
                fingerDir = transform.up,
                openness = openness
            };
            return s.tracked;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.36f, 0.88f, 0.9f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.08f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(0.08f, 0.09f, 0.01f));
        }
    }
}
