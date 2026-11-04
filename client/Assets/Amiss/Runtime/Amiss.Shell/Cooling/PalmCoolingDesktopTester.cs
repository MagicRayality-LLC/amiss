using UnityEngine;

namespace Amiss.Shell.Cooling
{
    /// Desktop test mode: drives two TransformPalmSource stand-ins with mouse + keyboard
    /// so the gesture and effect can be tuned in the Editor without a headset.
    ///   Mouse wheel / W,S : hands closer / apart (8-90 cm)
    ///   Hold RMB + drag   : rotate the whole pair (yaw/pitch)
    ///   Q / E             : tilt left palm away from facing (tests the 35 deg threshold)
    ///   A / D             : move the pair left / right;  R / F : up / down
    ///   1 / 2             : toggle left / right hand open (fist)
    ///   Space             : toggle tracking of both hands
    public class PalmCoolingDesktopTester : MonoBehaviour
    {
        public TransformPalmSource left, right;
        public float distance = 0.30f;
        public float tiltDeg = 0f;
        public bool showHelp = true;
        float yaw, pitch;

        void Update()
        {
            if (!left || !right) return;
#if ENABLE_LEGACY_INPUT_MANAGER
            float dt = Time.deltaTime;
            distance += Input.mouseScrollDelta.y * 0.02f;
            if (Input.GetKey(KeyCode.W)) distance -= 0.25f * dt;
            if (Input.GetKey(KeyCode.S)) distance += 0.25f * dt;
            distance = Mathf.Clamp(distance, 0.04f, 0.95f);
            if (Input.GetMouseButton(1)) { yaw += Input.GetAxis("Mouse X") * 3f; pitch -= Input.GetAxis("Mouse Y") * 3f; }
            if (Input.GetKey(KeyCode.Q)) tiltDeg += 40f * dt;
            if (Input.GetKey(KeyCode.E)) tiltDeg -= 40f * dt;
            Vector3 mv = new Vector3((Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0),
                                     (Input.GetKey(KeyCode.R) ? 1 : 0) - (Input.GetKey(KeyCode.F) ? 1 : 0), 0) * 0.3f * dt;
            transform.position += transform.rotation * mv;
            if (Input.GetKeyDown(KeyCode.Alpha1)) left.openness = left.openness > 0.5f ? 0.1f : 1f;
            if (Input.GetKeyDown(KeyCode.Alpha2)) right.openness = right.openness > 0.5f ? 0.1f : 1f;
            if (Input.GetKeyDown(KeyCode.Space)) left.tracked = right.tracked = !left.tracked;
#endif

            transform.rotation = Quaternion.Euler(pitch, yaw, 0);
            // Left palm at -X facing +X, right palm at +X facing -X, fingers up.
            left.transform.localPosition = new Vector3(-distance * 0.5f, 0, 0);
            left.transform.localRotation = Quaternion.LookRotation(Vector3.right, Vector3.up) * Quaternion.Euler(tiltDeg, 0, 0);
            right.transform.localPosition = new Vector3(distance * 0.5f, 0, 0);
            right.transform.localRotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
        }

        void OnGUI()
        {
            if (!showHelp) return;
            var g = GetComponentInParent<PalmCoolingGesture>() ?? FindObjectOfType<PalmCoolingGesture>();
            string st = g ? $"active={g.IsActive} strength={g.Strength:0.00} dist={g.Distance * 100f:0}cm close={g.Closeness:0.00}" : "no gesture";
            GUI.Label(new Rect(10, 10, 700, 60), "PalmCooling test: wheel/W,S distance  RMB rotate  Q/E tilt  A,D,R,F move  1/2 fist  Space tracking\n" + st);
        }
    }
}
