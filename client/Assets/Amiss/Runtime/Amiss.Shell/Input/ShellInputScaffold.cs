using UnityEngine;

namespace Amiss.Shell
{
    /// <summary>
    /// Controller-driven library and room-preparation commands. Emits only
    /// semantic local commands into the session and room owner; it never
    /// writes physics or episode data directly. Equivalent hand/controller
    /// physical input, validity checks, and swing classification remain with
    /// their owning build step.
    /// </summary>
    public sealed class ShellInputScaffold : MonoBehaviour
    {
        const float MoveThreshold = 0.6f;

        ShellServices services;
        int moveCooldown;

        void Start()
        {
            services = FindFirstObjectByType<ShellServices>();
        }

        void Update()
        {
            if (services == null)
                return;
            var session = services.Session;

            var horizontal =
                OVRInput.Get(OVRInput.RawAxis2D.LThumbstick, OVRInput.Controller.LTouch).x +
                OVRInput.Get(OVRInput.RawAxis2D.RThumbstick, OVRInput.Controller.RTouch).x;

            if (moveCooldown > 0)
                moveCooldown--;
            if (Mathf.Abs(horizontal) > MoveThreshold && moveCooldown == 0 &&
                session.State == ShellSessionState.Library)
            {
                moveCooldown = 14;
                var direction = horizontal > 0f ? 1 : -1;
                MoveSelection(direction);
            }

            if (OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger, OVRInput.Controller.RTouch) ||
                OVRInput.GetDown(OVRInput.RawButton.A, OVRInput.Controller.RTouch))
                Confirm();
            if (OVRInput.GetDown(OVRInput.RawButton.X, OVRInput.Controller.LTouch))
                services.Room?.ManualOrSupplement();
            if (OVRInput.GetDown(OVRInput.RawButton.Y, OVRInput.Controller.LTouch))
                services.Room?.Rescan();
            if (OVRInput.GetDown(OVRInput.RawButton.RThumbstick, OVRInput.Controller.RTouch))
                services.Room?.RelocatePanel();
            if (OVRInput.GetDown(OVRInput.RawButton.B, OVRInput.Controller.RTouch))
                Back();
        }

        void MoveSelection(int direction)
        {
            var session = services.Session;
            if (session.Cards.Count == 0)
                return;
            var next = session.State == ShellSessionState.Library && session.SelectedIndex < 0
                ? (direction > 0 ? 0 : session.Cards.Count - 1)
                : Mathf.Clamp(session.SelectedIndex + direction, 0, session.Cards.Count - 1);
            if (next != session.SelectedIndex)
                session.SelectCard(next);
        }

        void Confirm()
        {
            var session = services.Session;
            switch (session.State)
            {
                case ShellSessionState.Library:
                    if (session.SelectedIndex < 0 && session.Cards.Count > 0)
                        session.SelectCard(0);
                    break;
                case ShellSessionState.StartReady:
                    session.RequestStart();
                    services.Room?.BeginSelectedEpisode();
                    break;
                case ShellSessionState.RoomPreparation:
                case ShellSessionState.RoomConfirmed:
                case ShellSessionState.SetupAccepted:
                case ShellSessionState.Restricted:
                    services.Room?.PrimaryAction();
                    break;
            }
        }

        void Back()
        {
            if (services.Room != null &&
                services.Room.Preparation.Status == Room.RoomPreparationStatus.SpaceSetupRequested)
            {
                services.Room.CancelSpaceSetup();
                return;
            }
            if (services.Session.State != ShellSessionState.Library)
                services.Room?.ExitToLibrary();
        }
    }
}