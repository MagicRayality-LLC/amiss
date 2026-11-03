using Amiss.Shell.Input;
using Amiss.Shell.Room;
using UnityEngine;

namespace Amiss.Shell.UI
{
    /// <summary>
    /// Direct bezel/bracket movement for the wall-mounted TV. Hand pinch or
    /// controller grip grabs the TV body outside the self-lit screen, moves it
    /// presentation-only, and on release asks the room owner to accept a
    /// usable-wall pose. It never writes physics or Trial state.
    /// </summary>
    public sealed class PanelGrabController : MonoBehaviour
    {
        public InstrumentRoot Target;
        public RoomPreparationController Room;

        bool held;
        Pose grabOffset;
        InputModality heldModality;
        InputSide heldSide;

        void Start()
        {
            if (Target == null)
                Target = FindFirstObjectByType<InstrumentRoot>();
            if (Room == null)
                Room = FindFirstObjectByType<RoomPreparationController>();
        }

        void Update()
        {
            if (Target == null || Room == null)
                return;
            if (held)
            {
                if (TryPointer(heldModality, heldSide, out var pose, out var heldNow) && heldNow)
                    Target.UpdateGrab(GrabSurface.Follow(pose, grabOffset));
                else
                    Release();
                return;
            }
            if (!Room.CanMovePanel || Target.IsGrabbed)
                return;
            foreach (var hand in FindObjectsByType<OVRHand>(FindObjectsSortMode.None))
            {
                var side = SideOf(hand);
                if (side == InputSide.Unknown)
                    continue;
                if (TryPointer(InputModality.Hand, side, out var pose, out var pressed) && pressed &&
                    TryGrab(InputModality.Hand, side, pose))
                    return;
            }
            foreach (var side in new[] { InputSide.Left, InputSide.Right })
            {
                if (TryPointer(InputModality.Controller, side, out var pose, out var pressed) && pressed &&
                    TryGrab(InputModality.Controller, side, pose))
                    return;
            }
        }

        bool TryGrab(InputModality modality, InputSide side, Pose pose)
        {
            var local = Quaternion.Inverse(Target.transform.rotation) * (pose.position - Target.transform.position);
            if (!GrabSurface.Contains(local))
                return false;
            grabOffset = GrabSurface.Offset(pose,
                new Pose(Target.transform.position, Target.transform.rotation));
            heldModality = modality;
            heldSide = side;
            held = true;
            Target.BeginGrab();
            return true;
        }

        void Release()
        {
            held = false;
            Target.EndGrab();
            Room.TryRemountPanel(Target.transform.position, Target.transform.rotation);
        }

        bool TryPointer(InputModality modality, InputSide side, out Pose pose, out bool pressed)
        {
            pose = default;
            pressed = false;
            if (modality == InputModality.Hand)
            {
                foreach (var hand in FindObjectsByType<OVRHand>(FindObjectsSortMode.None))
                {
                    if (SideOf(hand) != side || !hand.IsTracked || !hand.IsPointerPoseValid)
                        continue;
                    pose = new Pose(hand.PointerPose.position, hand.PointerPose.rotation);
                    pressed = hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                    return true;
                }
                return false;
            }
            var controller = side == InputSide.Left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
            var button = side == InputSide.Left ? OVRInput.RawButton.LHandTrigger : OVRInput.RawButton.RHandTrigger;
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig == null || !OVRInput.IsControllerConnected(controller))
                return false;
            var localPosition = OVRInput.GetLocalControllerPosition(controller);
            var localRotation = OVRInput.GetLocalControllerRotation(controller);
            var tracking = rig.trackingSpace;
            pose = new Pose(tracking.TransformPoint(localPosition), tracking.rotation * localRotation);
            pressed = OVRInput.Get(button, controller);
            return true;
        }

        static InputSide SideOf(OVRHand hand)
        {
            var current = hand != null ? hand.transform : null;
            while (current != null)
            {
                if (current.name.Contains("Left"))
                    return InputSide.Left;
                if (current.name.Contains("Right"))
                    return InputSide.Right;
                current = current.parent;
            }
            return InputSide.Unknown;
        }
    }
}
