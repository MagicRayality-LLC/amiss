using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEngine;

namespace Amiss.Shell.Room
{
    public enum PanelPlacementStatus
    {
        Unplaced,
        Placed,
        RecoveryNeeded
    }

    public sealed class PanelPlacement
    {
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector2 PanelEnvelope { get; }
        public int Revision { get; }
        public PanelPlacementStatus Status { get; }

        public PanelPlacement(Vector3 position, Quaternion rotation, Vector2 panelEnvelope, int revision,
            PanelPlacementStatus status = PanelPlacementStatus.Placed)
        {
            Position = position;
            Rotation = RoomGeometry.Normalize(rotation);
            PanelEnvelope = panelEnvelope;
            Revision = revision;
            Status = status;
        }

        public Bounds PanelBounds => RoomGeometry.RotatedBounds(Position,
            new Vector3(PanelEnvelope.x, PanelEnvelope.y, 0.08f), Rotation);
        public Bounds RootBounds => RoomGeometry.RotatedBounds(Position + Vector3.up * 0.12f,
            new Vector3(PanelEnvelope.x, PanelEnvelope.y + 0.30f, 0.16f), Rotation);
    }

    public sealed class ShellRecovery
    {
        readonly Dictionary<string, CollisionPin> pins = new(StringComparer.Ordinal);
        RoomConfirmation currentConfirmation;
        CollisionRepresentation currentCollision;
        PanelPlacement panel;

        public event Action Changed;
        public bool Focused { get; private set; } = true;
        public bool SafetyValid { get; private set; } = true;
        public bool PauseRequested { get; private set; }
        public bool ResumeRequired { get; private set; }
        public bool SafetyHold => !SafetyValid;
        public bool SceneUpdateAvailable { get; private set; }
        public bool PreparationInProgress { get; private set; }
        public string HoldReason { get; private set; } = "";
        public IReadOnlyDictionary<string, CollisionPin> HistoricalPins =>
            new ReadOnlyDictionary<string, CollisionPin>(pins);
        public RoomConfirmation CurrentConfirmation => currentConfirmation;
        public CollisionRepresentation CurrentCollision => currentCollision;
        public PanelPlacement Panel => panel;
        public bool SetupAccepted { get; private set; }
        public string AcceptedTrialId { get; private set; } = "";

        public bool ConfirmRoom(RoomPreparation preparation)
        {
            if (preparation == null || !preparation.RoomConfirmed)
                return false;
            currentConfirmation = preparation.Confirmation;
            currentCollision = preparation.Collision;
            SetupAccepted = false;
            AcceptedTrialId = "";
            SafetyValid = true;
            HoldReason = "";
            PreparationInProgress = false;
            if (panel == null)
                panel = CreateInitialCandidate(currentConfirmation);
            if (!ValidatePanel(panel, currentConfirmation))
                panel = new PanelPlacement(panel.Position, panel.Rotation, panel.PanelEnvelope,
                    panel.Revision, PanelPlacementStatus.RecoveryNeeded);
            Changed?.Invoke();
            return true;
        }

        public CollisionPin AcceptTrialSetup(string trialId, string confirmationChecksum)
        {
            if (string.IsNullOrWhiteSpace(trialId) || currentConfirmation == null || currentCollision == null ||
                confirmationChecksum != currentConfirmation.CollisionChecksum ||
                confirmationChecksum != currentCollision.Checksum || !currentCollision.VerifyIntegrity() || !SafetyValid)
                return null;
            if (pins.TryGetValue(trialId, out var existing))
                return existing.CollisionChecksum == confirmationChecksum ? existing : null;
            var pin = new CollisionPin(trialId, currentCollision);
            pins.Add(trialId, pin);
            SetupAccepted = true;
            AcceptedTrialId = trialId;
            Changed?.Invoke();
            return pin;
        }

        public bool CanCreateTrial(string trialId) =>
            pins.TryGetValue(trialId, out var pin) && pin.Representation.VerifyIntegrity() && SafetyValid;

        public void BeginFutureRescan()
        {
            PreparationInProgress = true;
            SceneUpdateAvailable = false;
            if (pins.Count > 0)
            {
                PauseRequested = true;
                ResumeRequired = true;
                HoldReason = "room_preparation_future_trial";
            }
            Changed?.Invoke();
        }

        public void SceneUpdated()
        {
            SceneUpdateAvailable = true;
            Changed?.Invoke();
        }

        public void FocusLost()
        {
            Focused = false;
            PauseRequested = true;
            ResumeRequired = true;
            if (SafetyValid)
                HoldReason = "application_focus";
            Changed?.Invoke();
        }

        public void FocusReturned()
        {
            Focused = true;
            if (PauseRequested)
                ResumeRequired = true;
            Changed?.Invoke();
        }

        public void InvalidateSafety(string reason = "safety_boundary")
        {
            SafetyValid = false;
            PauseRequested = true;
            ResumeRequired = true;
            HoldReason = string.IsNullOrWhiteSpace(reason) ? "safety_boundary" : reason;
            Changed?.Invoke();
        }

        public bool ReconfirmSafety(string collisionChecksum)
        {
            if (currentCollision == null || collisionChecksum != currentCollision.Checksum ||
                !currentCollision.VerifyIntegrity())
                return false;
            SafetyValid = true;
            HoldReason = PauseRequested ? "explicit_resume_required" : "";
            ResumeRequired = PauseRequested;
            Changed?.Invoke();
            return true;
        }

        public bool RequestResume()
        {
            if (!Focused || !SafetyValid || !ResumeRequired)
                return false;
            PauseRequested = false;
            ResumeRequired = false;
            HoldReason = "";
            Changed?.Invoke();
            return true;
        }

        public void RelocalizationChanged(bool worldLockActive)
        {
            if (worldLockActive || panel == null)
                return;
            panel = new PanelPlacement(panel.Position, panel.Rotation, panel.PanelEnvelope, panel.Revision,
                PanelPlacementStatus.RecoveryNeeded);
            Changed?.Invoke();
        }

        public PanelPlacement LocatePanel() => panel;

        public bool RelocatePanel(Vector3 candidatePosition, Quaternion candidateRotation)
        {
            if (currentConfirmation == null)
                return false;
            var revision = panel?.Revision + 1 ?? 0;
            var candidate = new PanelPlacement(candidatePosition, candidateRotation,
                new Vector2(1.20f, 0.80f), revision);
            if (!ValidatePanel(candidate, currentConfirmation))
            {
                if (panel != null)
                    panel = new PanelPlacement(panel.Position, panel.Rotation, panel.PanelEnvelope,
                        panel.Revision, PanelPlacementStatus.RecoveryNeeded);
                Changed?.Invoke();
                return false;
            }
            panel = candidate;
            Changed?.Invoke();
            return true;
        }

        public static PanelPlacement CreateInitialCandidate(RoomConfirmation confirmation)
        {
            if (confirmation == null)
                return null;
            var position = confirmation.ConfirmedUserPosition + confirmation.ConfirmedUserForward * 1.5f +
                           Vector3.up * 1.3f;
            return new PanelPlacement(position, Quaternion.LookRotation(confirmation.ConfirmedUserForward, Vector3.up),
                new Vector2(1.20f, 0.80f), 0);
        }

        public static bool ValidatePanel(PanelPlacement candidate, RoomConfirmation confirmation)
        {
            if (candidate == null || confirmation == null ||
                !RoomGeometry.IsFinite(candidate.Position) || !RoomGeometry.IsFinite(candidate.Rotation) ||
                candidate.PanelEnvelope.x <= 0f || candidate.PanelEnvelope.y <= 0f)
                return false;
            var swing = new Bounds(confirmation.ConfirmedUserPosition + Vector3.up * 0.9f,
                Vector3.one * (confirmation.ClearSwingRadiusMeters * 2f));
            if (candidate.RootBounds.Intersects(swing) ||
                confirmation.ExcludedObstacles.Any(obstacle => candidate.RootBounds.Intersects(obstacle)))
                return false;
            return true;
        }
    }
}
