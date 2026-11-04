using Amiss.Shell.Cooling;
using UnityEditor;
using UnityEngine;

namespace Amiss.EditorTools
{
    public static class PalmCoolingMenu
    {
        [MenuItem("Tools/Amiss/Palm Cooling/Add To Rig")]
        public static void AddToRig()
        {
            var left = FindHandTarget("Left");
            var right = FindHandTarget("Right");
            if (!left || !right)
            {
                EditorUtility.DisplayDialog("Palm Cooling",
                    "Couldn't find OVRHand / Left/RightHandAnchor in the open scene.\n" +
                    "Add an OVRCameraRig with hand prefabs first, or use 'Add Desktop Test Rig'.", "OK");
                return;
            }
            var l = left.GetComponent<ReflectionPalmSource>() ?? Undo.AddComponent<ReflectionPalmSource>(left.gameObject);
            var r = right.GetComponent<ReflectionPalmSource>() ?? Undo.AddComponent<ReflectionPalmSource>(right.gameObject);
            l.side = PalmHandSource.Side.Left;
            r.side = PalmHandSource.Side.Right;
            var root = CreateRoot(l, r);
            Selection.activeGameObject = root;
            Debug.Log("[Amiss] Palm cooling added. Hold both hands open, palms facing, 8-80 cm apart. Balls inside the tube slow down.");
        }

        [MenuItem("Tools/Amiss/Palm Cooling/Add Desktop Test Rig")]
        public static void AddDesktopTest()
        {
            var cam = Camera.main;
            var pair = new GameObject("PalmCooling_DesktopPalms");
            Undo.RegisterCreatedObjectUndo(pair, "PalmCooling test");
            pair.transform.position = cam ? cam.transform.position + cam.transform.forward * 0.6f : new Vector3(0, 1.2f, 0.6f);
            TransformPalmSource Make(string n, PalmHandSource.Side s)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = n;
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.transform.SetParent(pair.transform, false);
                go.transform.localScale = new Vector3(0.08f, 0.09f, 0.012f);
                var src = go.AddComponent<TransformPalmSource>();
                src.side = s;
                return src;
            }
            var l = Make("Palm_L", PalmHandSource.Side.Left);
            var r = Make("Palm_R", PalmHandSource.Side.Right);
            var tester = pair.AddComponent<PalmCoolingDesktopTester>();
            tester.left = l;
            tester.right = r;
            var root = CreateRoot(l, r);
            Selection.activeGameObject = root;
        }

        static GameObject CreateRoot(PalmHandSource l, PalmHandSource r)
        {
            var root = new GameObject("PalmCooling");
            Undo.RegisterCreatedObjectUndo(root, "PalmCooling");
            var g = root.AddComponent<PalmCoolingGesture>();
            g.leftHand = l;
            g.rightHand = r;
            var fx = root.AddComponent<PalmCoolingField>();
            fx.gesture = g;
            var hum = root.AddComponent<AudioSource>();
            hum.loop = true;
            hum.playOnAwake = false;
            hum.spatialBlend = 1f;
            hum.volume = 0f;
            fx.hum = hum;
            root.AddComponent<PalmCoolingTool>().gesture = g;
            var tool = root.GetComponent<PalmCoolingTool>();
            tool.field = fx;
            EditorUtility.SetDirty(fx);
            return root;
        }

        static Transform FindHandTarget(string side)
        {
            foreach (var c in Object.FindObjectsOfType<Component>(true))
            {
                if (c == null || c.GetType().Name != "OVRHand") continue;
                var p = c.transform;
                bool match = false;
                for (var q = p; q; q = q.parent)
                    if (q.name.StartsWith(side)) { match = true; break; }
                if (match) return p;
            }
            foreach (var go in Object.FindObjectsOfType<Transform>(true))
                if (go.name == side + "HandAnchor") return go;
            return null;
        }
    }
}
