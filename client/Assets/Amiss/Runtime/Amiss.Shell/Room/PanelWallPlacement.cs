using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEngine;

namespace Amiss.Shell.Room
{
    public enum PanelMountMode
    {
        ForwardFallback,
        WallMounted
    }

    public enum PanelPlacementSource
    {
        None,
        AutoNearestWall,
        UserRemount,
        ResetNearestWall,
        Recovery
    }

    public sealed class WallSurface
    {
        public string SurfaceId { get; }
        public BoundaryProvenance Provenance { get; }
        public Vector3 Center { get; }
        public Vector3 Normal { get; }
        public Vector3 Right { get; }
        public float Width { get; }
        public float Height { get; }

        public WallSurface(string surfaceId, BoundaryProvenance provenance, Vector3 center,
            Vector3 normal, Vector3 right, float width, float height)
        {
            SurfaceId = surfaceId ?? "";
            Provenance = provenance;
            Center = center;
            Normal = normal.sqrMagnitude > 0.000001f ? normal.normalized : Vector3.forward;
            var projected = Vector3.ProjectOnPlane(right, Vector3.up);
            Right = projected.sqrMagnitude > 0.000001f
                ? projected.normalized
                : Vector3.Cross(Vector3.up, Normal).normalized;
            Width = Mathf.Max(0f, width);
            Height = Mathf.Max(0f, height);
        }

        public bool CarriesPanel =>
            Provenance == BoundaryProvenance.Scanned ||
            Provenance == BoundaryProvenance.VirtualSupplement;
        public string ProvenanceLabel => Provenance == BoundaryProvenance.VirtualSupplement
            ? "virtual supplement"
            : "scanned";
        public Bounds WorldBounds => RoomGeometry.RotatedBounds(Center, new Vector3(Width, Height, 0.02f),
            Quaternion.LookRotation(Normal, Vector3.up));

        public static WallSurface FromSurface(CollisionSurface surface)
        {
            if (surface == null || surface.Role != CollisionSurfaceRole.Wall ||
                surface.Primary != PrimaryRepresentation.Plane ||
                surface.Size.x <= 0f || surface.Size.y <= 0f)
                return null;
            var up = surface.Rotation * Vector3.up;
            if (Mathf.Abs(Vector3.Dot(surface.Normal, up)) > 0.7f)
                return null;
            return new WallSurface(surface.SurfaceId, surface.Provenance, surface.Center,
                surface.Normal, surface.Rotation * Vector3.right, surface.Size.x, surface.Size.y);
        }

        public static IReadOnlyList<WallSurface> FromSurfaces(IEnumerable<CollisionSurface> surfaces)
        {
            var walls = (surfaces ?? Array.Empty<CollisionSurface>())
                .Select(FromSurface)
                .Where(wall => wall != null && wall.CarriesPanel)
                .ToArray();
            return new ReadOnlyCollection<WallSurface>(walls);
        }
    }

    /// <summary>
    /// Declared layout envelope and wall selection for the one wall-mounted
    /// LED-TV instrument. The screen is 16:9; the envelope includes the matte
    /// bezel, and the set includes the knife switch hanging beside it, so the
    /// whole set fits the chosen wall. Wall candidates come from the confirmed
    /// room's scanned wall planes (and visible virtual supplements), never from
    /// a free-standing pose.
    /// </summary>
    public static class PanelMount
    {
        public const float ScreenWidth = 1.45f;
        public const float ScreenHeight = 0.82f;
        public const float Bezel = 0.03f;
        public const float BodyDepth = 0.06f;
        public const float BracketStandoff = 0.12f;
        public const float MountMargin = 0.10f;
        public const float MountCenterHeight = 1.35f;
        public const float DistanceBand = 0.25f;
        public const float SnapDistance = 0.40f;
        public const float ReferenceGlassHeight = 0.60f;
        public const float SpeakerWidth = 0.25f * ScreenHeight / ReferenceGlassHeight;
        public const float SpeakerHeight = 0.15f * ScreenHeight / ReferenceGlassHeight;
        public const float SpeakerDepth = 0.12f * ScreenHeight / ReferenceGlassHeight;
        public const float SpeakerGap = 0.10f * ScreenHeight / ReferenceGlassHeight;
        public const string WallUnavailable = "wall_unavailable";
        public const string ReleaseNotOnWall = "release_not_on_usable_wall";

        public static readonly Vector2 ScreenEnvelope = new Vector2(ScreenWidth, ScreenHeight);

        public static float ScreenHalfWidth => (ScreenWidth + 2f * Bezel) * 0.5f;
        public static float ScreenHalfHeight => (ScreenHeight + 2f * Bezel) * 0.5f;

        /// <summary>
        /// How far the knife switch reaches from the television centre on its
        /// own side: its declared lateral offset plus the plate's half-width.
        /// </summary>
        public static float SwitchReach =>
            KnifeSwitchPose.LateralMeters + KnifeSwitchPose.PlateHalfWidthMeters;

        /// <summary>Wall a mounted edge needs on the television's bezel side.</summary>
        public static float ScreenSideReserve => ScreenHalfWidth + MountMargin;

        /// <summary>
        /// Wall a mounted edge needs on the switch side. The switch stands
        /// further out than the bezel, so that side reserves its reach.
        /// </summary>
        public static float SwitchSideReserve => SwitchReach + MountMargin;

        /// <summary>
        /// Width of the set from the switch's outer edge to the far bezel edge,
        /// measured through the television centre.
        /// </summary>
        public static float SetWidth => ScreenHalfWidth + SwitchReach;

        public static PanelPlacement Select(RoomConfirmation confirmation, int revision,
            PanelPlacementSource source)
        {
            if (confirmation == null)
                return null;
            WallSurface best = null;
            var bestScore = float.MaxValue;
            var bestDistance = float.MaxValue;
            PanelPlacement bestPlacement = null;
            foreach (var wall in confirmation.Walls)
            {
                if (!TryMountPose(wall, confirmation, out var position, out var rotation))
                    continue;
                var distance = Mathf.Abs(Vector3.Dot(confirmation.ConfirmedUserPosition - wall.Center,
                    wall.Normal));
                var towardWall = Vector3.ProjectOnPlane(wall.Center - confirmation.ConfirmedUserPosition,
                    Vector3.up);
                var forward = towardWall.sqrMagnitude > 0.000001f &&
                              Vector3.Dot(towardWall.normalized, confirmation.ConfirmedUserForward) > 0.5f;
                var score = distance - (forward ? DistanceBand : 0f);
                if (best != null && score > bestScore + 0.0001f)
                    continue;
                if (best != null && Mathf.Abs(score - bestScore) <= 0.0001f && distance >= bestDistance)
                    continue;
                var candidate = new PanelPlacement(position, rotation, ScreenEnvelope, revision,
                    PanelPlacementStatus.Placed, PanelMountMode.WallMounted, source, "",
                    wall.ProvenanceLabel);
                if (!ShellRecovery.ValidatePanel(candidate, confirmation))
                    continue;
                best = wall;
                bestScore = score;
                bestDistance = distance;
                bestPlacement = candidate;
            }
            return bestPlacement ??
                   ForwardFallback(confirmation, revision, source, WallUnavailable);
        }

        public static PanelPlacement ForwardFallback(RoomConfirmation confirmation, int revision,
            PanelPlacementSource source, string reason)
        {
            if (confirmation == null)
                return null;
            var forward = confirmation.ConfirmedUserForward;
            var position = confirmation.ConfirmedUserPosition + forward * 1.5f + Vector3.up * 1.3f;
            var rotation = Quaternion.LookRotation(forward, Vector3.up);
            return new PanelPlacement(position, rotation, ScreenEnvelope, revision,
                PanelPlacementStatus.Placed, PanelMountMode.ForwardFallback, source,
                string.IsNullOrEmpty(reason) ? WallUnavailable : reason, "");
        }

        public static bool TryRemount(RoomConfirmation confirmation, Vector3 releasePosition,
            int revision, out PanelPlacement placement, out string reason)
        {
            placement = null;
            reason = confirmation == null ? "room_not_confirmed" : ReleaseNotOnWall;
            if (confirmation == null)
                return false;
            var bestDistance = float.MaxValue;
            foreach (var wall in confirmation.Walls)
            {
                if (!TryMountPose(wall, confirmation, out var position, out var rotation))
                    continue;
                var planeDistance = Mathf.Abs(Vector3.Dot(releasePosition - wall.Center, wall.Normal));
                if (planeDistance > SnapDistance)
                    continue;
                var lateral = Vector3.Dot(releasePosition - wall.Center, wall.Right);
                var vertical = releasePosition.y - wall.Center.y;
                if (Mathf.Abs(lateral) > wall.Width * 0.5f + SnapDistance ||
                    Mathf.Abs(vertical) > wall.Height * 0.5f + SnapDistance)
                    continue;
                if (planeDistance >= bestDistance)
                    continue;
                var candidate = new PanelPlacement(position, rotation, ScreenEnvelope, revision,
                    PanelPlacementStatus.Placed, PanelMountMode.WallMounted,
                    PanelPlacementSource.UserRemount, "", wall.ProvenanceLabel);
                if (!ShellRecovery.ValidatePanel(candidate, confirmation))
                    continue;
                bestDistance = planeDistance;
                placement = candidate;
            }
            return placement != null;
        }

        public static bool ContainsGrabSurface(PanelPlacement placement, Vector3 worldPoint)
        {
            if (placement == null)
                return false;
            var local = Quaternion.Inverse(placement.Rotation) * (worldPoint - placement.Position);
            return GrabSurface.Contains(local);
        }

        static bool TryMountPose(WallSurface wall, RoomConfirmation confirmation,
            out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (wall == null || confirmation == null ||
                wall.Width < SetWidth + 2f * MountMargin ||
                wall.Height < ScreenHeight + 2f * Bezel + 2f * MountMargin)
                return false;
            var user = confirmation.ConfirmedUserPosition;
            var face = Vector3.Dot(user - wall.Center, wall.Normal) >= 0f
                ? wall.Normal
                : -wall.Normal;
            var right = Vector3.Cross(Vector3.up, face).normalized;
            var halfHeight = ScreenHalfHeight;
            // The television turns to face the room, so its own right axis is
            // this frame's -right and the switch hangs on the negative side.
            // That side therefore reserves the switch's reach, while the other
            // side reserves the bezel: either clamp keeps the entire set on the
            // wall with the declared margin at both wall edges.
            var lateral = Mathf.Clamp(Vector3.Dot(user - wall.Center, right),
                -(wall.Width * 0.5f - SwitchSideReserve),
                wall.Width * 0.5f - ScreenSideReserve);
            var floorY = confirmation.Region.min.y;
            var minY = Mathf.Max(wall.Center.y - wall.Height * 0.5f + halfHeight + MountMargin,
                floorY + halfHeight);
            var maxY = wall.Center.y + wall.Height * 0.5f - halfHeight - MountMargin;
            if (maxY < minY)
                return false;
            var center = wall.Center + right * lateral;
            center.y = Mathf.Clamp(floorY + MountCenterHeight, minY, maxY);
            position = center + face * BracketStandoff;
            rotation = Quaternion.LookRotation(-face, Vector3.up);
            return RoomGeometry.IsFinite(position) && RoomGeometry.IsFinite(rotation);
        }
    }

    /// <summary>
    /// Grab regions on the TV body in the instrument's local frame. The
    /// self-lit screen is excluded so panel ray/poke controls keep working;
    /// only the bezel ring and the bracket below the screen accept a grab.
    /// </summary>
    public static class GrabSurface
    {
        public const float Reach = 0.14f;

        public static bool Contains(Vector3 local)
        {
            var halfWidth = (PanelMount.ScreenWidth + 2f * PanelMount.Bezel) * 0.5f;
            var halfHeight = (PanelMount.ScreenHeight + 2f * PanelMount.Bezel) * 0.5f;
            var depth = PanelMount.BodyDepth + PanelMount.BracketStandoff;
            if (Mathf.Abs(local.z) > depth + Reach)
                return false;
            if (IsOnScreen(local))
                return false;
            var onBezel = Mathf.Abs(local.x) <= halfWidth + Reach &&
                          Mathf.Abs(local.y) <= halfHeight + Reach &&
                          (Mathf.Abs(local.x) >= PanelMount.ScreenWidth * 0.5f - 0.01f ||
                           Mathf.Abs(local.y) >= PanelMount.ScreenHeight * 0.5f - 0.01f);
            if (onBezel)
                return true;
            var belowScreen = local.y < -halfHeight && local.y >= -halfHeight - 0.12f - Reach &&
                              Mathf.Abs(local.x) <= 0.25f + Reach;
            return belowScreen;
        }

        public static bool IsOnScreen(Vector3 local) =>
            Mathf.Abs(local.x) < PanelMount.ScreenWidth * 0.5f &&
            Mathf.Abs(local.y) < PanelMount.ScreenHeight * 0.5f &&
            local.z <= PanelMount.BodyDepth * 0.5f + Reach;

        public static Pose Follow(Pose pointer, Pose grabOffset) => new Pose(
            pointer.position + pointer.rotation * grabOffset.position,
            pointer.rotation * grabOffset.rotation);

        public static Pose Offset(Pose pointer, Pose panel) => new Pose(
            Quaternion.Inverse(pointer.rotation) * (panel.position - pointer.position),
            Quaternion.Inverse(pointer.rotation) * panel.rotation);
    }
}
