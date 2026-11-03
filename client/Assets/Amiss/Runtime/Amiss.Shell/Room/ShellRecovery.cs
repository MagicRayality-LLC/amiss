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
        public PanelMountMode MountMode { get; }
        public PanelPlacementSource Source { get; }
        public string Reason { get; }
        public string WallProvenance { get; }

        public PanelPlacement(Vector3 position, Quaternion rotation, Vector2 panelEnvelope, int revision,
            PanelPlacementStatus status = PanelPlacementStatus.Placed,
            PanelMountMode mountMode = PanelMountMode.ForwardFallback,
            PanelPlacementSource source = PanelPlacementSource.None,
            string reason = "", string wallProvenance = "")
        {
            Position = position;
            Rotation = RoomGeometry.Normalize(rotation);
            PanelEnvelope = panelEnvelope;
            Revision = revision;
            Status = status;
            MountMode = mountMode;
            Source = source;
            Reason = reason ?? "";
            WallProvenance = wallProvenance ?? "";
        }

        public PanelPlacement WithStatus(PanelPlacementStatus status) => new(Position, Rotation,
            PanelEnvelope, Revision, status, MountMode, Source, Reason, WallProvenance);

        public PanelPlacement WithReason(string reason) => new(Position, Rotation,
            PanelEnvelope, Revision, Status, MountMode, Source, reason, WallProvenance);

        public Bounds PanelBounds => RoomGeometry.RotatedBounds(Position,
            new Vector3(PanelEnvelope.x + 2f * PanelMount.Bezel,
                PanelEnvelope.y + 2f * PanelMount.Bezel,
                PanelMount.BodyDepth), Rotation);

        public Bounds RootBounds
        {
            get
            {
                var bounds = PanelBounds;
                var above = PanelEnvelope.y * 0.5f + PanelMount.Bezel + PanelMount.SpeakerGap +
                            PanelMount.SpeakerHeight * 0.5f;
                bounds.Encapsulate(RoomGeometry.RotatedBounds(
                    Position + Rotation * Vector3.up * above,
                    new Vector3(PanelMount.SpeakerWidth, PanelMount.SpeakerHeight,
                        PanelMount.SpeakerDepth), Rotation));
                return bounds;
            }
        }
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
            panel = PanelMount.Select(currentConfirmation, panel?.Revision + 1 ?? 0,
                panel == null ? PanelPlacementSource.AutoNearestWall : PanelPlacementSource.Recovery);
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

        /// <summary>
        /// A stopped trial is no longer the accepted setup, but its pinned
        /// collision representation stays available: the same confirmed room
        /// can start another trial without accepting the setup again.
        /// </summary>
        public void TrialStopped()
        {
            if (!SetupAccepted && AcceptedTrialId.Length == 0)
                return;
            SetupAccepted = false;
            AcceptedTrialId = "";
            Changed?.Invoke();
        }

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
            panel = panel.WithStatus(PanelPlacementStatus.RecoveryNeeded);
            Changed?.Invoke();
        }

        public PanelPlacement LocatePanel() => panel;

        public bool TryRemountPanel(Vector3 releasePosition, Quaternion releaseRotation, out string reason)
        {
            if (currentConfirmation == null || panel == null)
            {
                reason = "room_not_confirmed";
                return false;
            }
            if (PanelMount.TryRemount(currentConfirmation, releasePosition, panel.Revision + 1,
                    out var mounted, out reason))
            {
                panel = mounted;
                Changed?.Invoke();
                return true;
            }
            panel = panel.WithReason(string.IsNullOrEmpty(reason)
                ? PanelMount.ReleaseNotOnWall
                : reason);
            Changed?.Invoke();
            return false;
        }

        public bool RelocatePanel(Vector3 candidatePosition, Quaternion candidateRotation) =>
            TryRemountPanel(candidatePosition, candidateRotation, out _);

        public bool ResetPanelToNearestWall()
        {
            if (currentConfirmation == null || panel == null)
                return false;
            var next = PanelMount.Select(currentConfirmation, panel.Revision + 1,
                PanelPlacementSource.ResetNearestWall);
            if (next == null || next.MountMode != PanelMountMode.WallMounted)
            {
                panel = panel.WithReason(PanelMount.WallUnavailable);
                Changed?.Invoke();
                return false;
            }
            panel = next;
            Changed?.Invoke();
            return true;
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
            if (candidate.MountMode == PanelMountMode.WallMounted && candidate.WallProvenance == "")
                return false;
            return true;
        }
    }
}
