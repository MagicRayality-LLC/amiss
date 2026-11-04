using UnityEngine;

namespace Amiss.Shell.Cooling
{
    /// Builds the palm cooling rig on the live interaction rig at runtime.
    ///
    /// The hands are prefab instances that arrive with the scene, so their
    /// OVRHand components exist before the shell composes its tools. Nothing
    /// in the generated scene authored the cooling objects, which meant the
    /// field existed in code but never in a build; this creates the gesture,
    /// the field art, and the tool as children of the composition root, then
    /// ShellServices binds the tool to the simulation host.
    public static class PalmCoolingInstaller
    {
        /// <summary>
        /// Installs the rig once under <paramref name="parent"/>. Returns the
        /// tool, or null when both hands are not present in the scene.
        /// </summary>
        public static PalmCoolingTool Install(Transform parent)
        {
            var existing = parent != null ? parent.GetComponentInChildren<PalmCoolingTool>(true) : null;
            if (existing != null) return existing;

            OVRHand left = null, right = null;
            foreach (var hand in Object.FindObjectsByType<OVRHand>(FindObjectsInactive.Include))
            {
                var side = SideOf(hand);
                if (side < 0) continue;
                if (side == 0 && left == null) left = hand;
                else if (side == 1 && right == null) right = hand;
            }
            if (left == null || right == null)
            {
                Debug.Log("[Amiss] palm cooling not installed: left and right OVRHand were not found");
                return null;
            }

            var leftSource = Source(left, PalmHandSource.Side.Left);
            var rightSource = Source(right, PalmHandSource.Side.Right);

            var root = new GameObject("PalmCooling");
            root.transform.SetParent(parent, false);

            var gesture = root.AddComponent<PalmCoolingGesture>();
            gesture.leftHand = leftSource;
            gesture.rightHand = rightSource;

            var field = root.AddComponent<PalmCoolingField>();
            field.gesture = gesture;

            var tool = root.AddComponent<PalmCoolingTool>();
            tool.gesture = gesture;
            tool.field = field;

            Debug.Log("[Amiss] palm cooling armed on both hands");
            return tool;
        }

        static MetaPalmSource Source(OVRHand hand, PalmHandSource.Side side)
        {
            var source = hand.GetComponent<MetaPalmSource>();
            if (source == null) source = hand.gameObject.AddComponent<MetaPalmSource>();
            source.ovrHand = hand;
            source.side = side;
            return source;
        }

        static int SideOf(OVRHand hand)
        {
            var current = hand != null ? hand.transform : null;
            while (current != null)
            {
                if (current.name.Contains("Left")) return 0;
                if (current.name.Contains("Right")) return 1;
                current = current.parent;
            }
            return -1;
        }
    }
}
