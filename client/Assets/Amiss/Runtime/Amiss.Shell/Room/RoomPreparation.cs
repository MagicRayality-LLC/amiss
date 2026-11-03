using System;
using UnityEngine;

namespace Amiss.Shell.Room
{
    public sealed class RoomPreparation
    {
        int sequence;
        double minimumVolumeM3;
        bool sceneLoadFollowsSpaceSetup;

        public event Action Changed;
        public RoomPreparationStatus Status { get; private set; } = RoomPreparationStatus.Idle;
        public RoomFailureReason Failure { get; private set; }
        public string PreparationId { get; private set; } = "";
        public RoomDraft Draft { get; private set; }
        public RoomConfirmation Confirmation { get; private set; }
        public CollisionRepresentation Collision { get; private set; }
        public bool Restricted => Status == RoomPreparationStatus.Restricted;
        public bool RoomConfirmed => Status == RoomPreparationStatus.Confirmed && Confirmation != null &&
                                     Collision != null && Collision.VerifyIntegrity();
        public bool CanCreateTrial => false;

        public string Begin(double episodeMinimumVolumeM3)
        {
            sequence++;
            PreparationId = sequence.ToString("D4");
            minimumVolumeM3 = episodeMinimumVolumeM3;
            Draft = null;
            Confirmation = null;
            Collision = null;
            Failure = RoomFailureReason.None;
            sceneLoadFollowsSpaceSetup = false;
            Status = RoomPreparationStatus.Explanation;
            if (!double.IsFinite(episodeMinimumVolumeM3) || episodeMinimumVolumeM3 <= 0d)
                Restrict(RoomFailureReason.InvalidGeometry);
            Changed?.Invoke();
            return PreparationId;
        }

        public bool RequestPermission(string preparationId)
        {
            if (!Owns(preparationId, RoomPreparationStatus.Explanation))
                return false;
            Status = RoomPreparationStatus.PermissionRequested;
            Changed?.Invoke();
            return true;
        }

        public bool CompletePermission(string preparationId, bool granted)
        {
            if (!Owns(preparationId, RoomPreparationStatus.PermissionRequested))
                return false;
            if (!granted)
            {
                Restrict(RoomFailureReason.ScenePermission);
                return true;
            }
            Failure = RoomFailureReason.None;
            sceneLoadFollowsSpaceSetup = false;
            Status = RoomPreparationStatus.SceneLoading;
            Changed?.Invoke();
            return true;
        }

        public bool RequestSpaceSetup(string preparationId)
        {
            if (!Owns(preparationId, RoomPreparationStatus.SpaceSetupReady))
                return false;
            Status = RoomPreparationStatus.SpaceSetupRequested;
            Changed?.Invoke();
            return true;
        }

        public bool CompleteSpaceSetup(string preparationId, bool succeeded, bool cancelled = false)
        {
            if (!Owns(preparationId, RoomPreparationStatus.SpaceSetupRequested))
                return false;
            if (cancelled || !succeeded)
            {
                Restrict(cancelled ? RoomFailureReason.SpaceSetupCancelled : RoomFailureReason.SpaceSetupFailed);
                return true;
            }
            Failure = RoomFailureReason.None;
            sceneLoadFollowsSpaceSetup = true;
            Status = RoomPreparationStatus.SceneLoading;
            Changed?.Invoke();
            return true;
        }

        public bool CompleteSceneLoad(string preparationId, RoomDraft draft,
            RoomFailureReason loadFailure = RoomFailureReason.None)
        {
            if (!Owns(preparationId, RoomPreparationStatus.SceneLoading))
                return false;
            var failure = loadFailure == RoomFailureReason.None && draft == null
                ? RoomFailureReason.NoScene
                : loadFailure;
            if (failure != RoomFailureReason.None || draft == null)
            {
                if (!sceneLoadFollowsSpaceSetup &&
                    (failure == RoomFailureReason.NoScene || failure == RoomFailureReason.NoCurrentRoom))
                {
                    Failure = failure;
                    Status = RoomPreparationStatus.SpaceSetupReady;
                    Changed?.Invoke();
                    return true;
                }
                Restrict(failure);
                return true;
            }

            Draft = draft;
            Failure = draft.Validate(minimumVolumeM3);
            if (Failure == RoomFailureReason.InvalidGeometry)
            {
                Restrict(Failure);
                return true;
            }
            Status = RoomPreparationStatus.AwaitingConfirmation;
            Changed?.Invoke();
            return true;
        }

        public bool AddVirtualSupplements(string preparationId)
        {
            if (!Owns(preparationId, RoomPreparationStatus.AwaitingConfirmation) || Draft == null)
                return false;
            Draft = Draft.WithVirtualSupplements();
            Failure = Draft.Validate(minimumVolumeM3);
            Changed?.Invoke();
            return true;
        }

        public bool ChooseManualBoundary(string preparationId, Vector3 userPosition, Vector3 userForward)
        {
            if (!Owns(preparationId, RoomPreparationStatus.Restricted) &&
                !Owns(preparationId, RoomPreparationStatus.SpaceSetupReady) &&
                !Owns(preparationId, RoomPreparationStatus.AwaitingConfirmation))
                return false;
            var floorY = Draft != null ? Draft.Region.min.y : userPosition.y;
            var region = new Bounds(
                new Vector3(userPosition.x, floorY + 1.25f, userPosition.z), new Vector3(3f, 2.5f, 3f));
            Draft = RoomDraft.CreateManual(region, userPosition, userForward);
            Failure = Draft.Validate(minimumVolumeM3);
            Status = RoomPreparationStatus.AwaitingConfirmation;
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// Re-reads where the wearer is standing before the clear-swing check.
        /// Stepping into an open spot can pass without a new scan.
        /// </summary>
        public bool RetargetUser(Vector3 position, Vector3 forward)
        {
            if (!Owns(PreparationId, RoomPreparationStatus.AwaitingConfirmation) || Draft == null)
                return false;
            Draft = new RoomDraft(Draft.Region, Draft.Surfaces, position, forward, Draft.ClearSwingRadiusMeters);
            Failure = Draft.Validate(minimumVolumeM3);
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// A loaded room that cannot be used may be replaced by a new
        /// on-device Space Setup. This does not invent a room.
        /// </summary>
        public bool OfferReplacementScan(string preparationId)
        {
            if (!Owns(preparationId, RoomPreparationStatus.AwaitingConfirmation) &&
                !Owns(preparationId, RoomPreparationStatus.Restricted))
                return false;
            Failure = RoomFailureReason.None;
            sceneLoadFollowsSpaceSetup = false;
            Status = RoomPreparationStatus.SpaceSetupReady;
            Changed?.Invoke();
            return true;
        }

        public bool Confirm(string preparationId) => Confirm(preparationId, false);

        public bool Confirm(string preparationId, bool allowUnsafeRegion)
        {
            if (!Owns(preparationId, RoomPreparationStatus.AwaitingConfirmation) || Draft == null)
                return false;
            if (!CollisionRepresentation.TryCreate(PreparationId, Draft, minimumVolumeM3,
                    allowUnsafeRegion, out var collision, out var failure))
            {
                Failure = failure;
                Changed?.Invoke();
                return false;
            }
            Collision = collision;
            Confirmation = new RoomConfirmation(PreparationId, Draft, collision);
            Failure = RoomFailureReason.None;
            Status = RoomPreparationStatus.Confirmed;
            Changed?.Invoke();
            return true;
        }

        public void Rescan()
        {
            Begin(minimumVolumeM3 > 0d ? minimumVolumeM3 : 1d);
        }

        public void Cancel()
        {
            sequence++;
            PreparationId = "";
            Draft = null;
            Confirmation = null;
            Collision = null;
            Failure = RoomFailureReason.None;
            Status = RoomPreparationStatus.Idle;
            Changed?.Invoke();
        }

        void Restrict(RoomFailureReason reason)
        {
            Failure = reason;
            Status = RoomPreparationStatus.Restricted;
            Confirmation = null;
            Collision = null;
            Changed?.Invoke();
        }

        bool Owns(string preparationId, RoomPreparationStatus expected) =>
            !string.IsNullOrEmpty(preparationId) && preparationId == PreparationId && Status == expected;
    }
}
