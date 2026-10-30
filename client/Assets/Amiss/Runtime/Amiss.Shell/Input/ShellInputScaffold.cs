using UnityEngine;

namespace Amiss.Shell
{
    /// <summary>
    /// Controller-driven library navigation for the shell build. Emits only
    /// semantic commands (move selection, confirm, back) into the session;
    /// it never writes physics, panel state, or episode data directly. This
    /// scaffold exists for the library smoke build; the equivalent-input
    /// integration (hand and controller parity, validity checks, swing
    /// classification) replaces it in its owning build step.
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

            if (OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger, OVRInput.Controller.RTouch))
                Confirm();

            if (OVRInput.GetDown(OVRInput.RawButton.B, OVRInput.Controller.RTouch) ||
                OVRInput.GetDown(OVRInput.RawButton.LThumbstick, OVRInput.Controller.LTouch) ||
                OVRInput.GetDown(OVRInput.RawButton.RThumbstick, OVRInput.Controller.RTouch))
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
                    else
                        session.RequestStart();
                    break;
                case ShellSessionState.StartReady:
                    session.RequestStart();
                    break;
            }
        }

        void Back()
        {
            var session = services.Session;
            if (session.State != ShellSessionState.Library)
                session.ReturnToLibrary();
        }
    }
}