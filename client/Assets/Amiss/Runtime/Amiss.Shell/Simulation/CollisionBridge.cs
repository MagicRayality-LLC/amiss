using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The one collision representation a Trial consumes. Approved design 02
    /// section 3.2 makes it immutable for the life of a Trial: the shell owns
    /// the versioned representation and its integrity data, and this bridge
    /// only validates and pins it. A rescan builds a new representation for a
    /// new Trial; it never rewrites this object.
    /// </summary>
    public sealed class PinnedCollision
    {
        internal PinnedCollision(CollisionRepresentation representation,
            IReadOnlyList<CollisionSurface> surfaces)
        {
            Representation = representation;
            RepresentationId = representation.RepresentationId;
            FormatVersion = representation.FormatVersion;
            CoordinateFrameId = representation.CoordinateFrameId;
            CoordinateFrameVersion = representation.CoordinateFrameVersion;
            ProducerContractVersion = representation.ProducerContractVersion;
            Checksum = representation.Checksum;
            SizeClass = representation.SizeClass;
            BoundaryLabel = representation.BoundaryLabel;
            Region = representation.Region;
            EffectiveVolumeM3 = representation.EffectiveVolumeM3;
            DeductionMethod = representation.DeductionMethod;
            Surfaces = surfaces;
        }

        public CollisionRepresentation Representation { get; }
        public string RepresentationId { get; }
        public string FormatVersion { get; }
        public string CoordinateFrameId { get; }
        public string CoordinateFrameVersion { get; }
        public string ProducerContractVersion { get; }
        public string Checksum { get; }
        public string SizeClass { get; }
        public string BoundaryLabel { get; }
        public Bounds Region { get; }
        public float EffectiveVolumeM3 { get; }
        public string DeductionMethod { get; }

        /// <summary>Surfaces in deterministic traversal order (stable surface id).</summary>
        public IReadOnlyList<CollisionSurface> Surfaces { get; }

        public int SurfaceCount => Surfaces.Count;

        public IReadOnlyList<Bounds> Obstacles => Surfaces
            .Where(surface => surface.Role == CollisionSurfaceRole.Obstacle)
            .Select(surface => surface.WorldBounds)
            .ToArray();

        public bool VerifyIntegrity() => Representation != null && Representation.VerifyIntegrity();
    }

    /// <summary>
    /// Validates a confirmed room's pinned collision representation and hands
    /// the runtime the one representation it may consume. It reuses the shell
    /// representation and its existing SHA-256 checksum scheme; no second
    /// checksum algorithm exists here.
    /// </summary>
    public static class CollisionBridge
    {
        public static bool TryBind(RoomConfirmation confirmation, CollisionPin pin,
            out PinnedCollision pinned, out TrialStartRefusal refusal)
        {
            pinned = null;
            refusal = TrialStartRefusal.None;

            if (confirmation == null)
            {
                refusal = TrialStartRefusal.RoomNotConfirmed;
                return false;
            }
            if (pin == null || pin.Representation == null)
            {
                refusal = TrialStartRefusal.CollisionPinMissing;
                return false;
            }
            if (string.IsNullOrEmpty(pin.TrialId) ||
                pin.CollisionChecksum != confirmation.CollisionChecksum)
            {
                refusal = TrialStartRefusal.CollisionPinMismatch;
                return false;
            }

            var representation = pin.Representation;
            if (representation.RepresentationId != pin.CollisionRepresentationId)
            {
                refusal = TrialStartRefusal.CollisionPinMismatch;
                return false;
            }
            if (representation.FormatVersion != CollisionRepresentation.CurrentFormatVersion)
            {
                refusal = TrialStartRefusal.CollisionUnsupported;
                return false;
            }
            if (!representation.VerifyIntegrity())
            {
                refusal = TrialStartRefusal.CollisionIntegrityFailed;
                return false;
            }

            // 02 section 3.2 runtime checks: finite geometry, stable unique
            // ids, one primary representation per surface, declared bounds.
            var surfaces = representation.Surfaces
                .OrderBy(surface => surface.SurfaceId, StringComparer.Ordinal)
                .ToArray();
            if (surfaces.Length == 0 ||
                !RoomGeometry.IsFinite(representation.Region.center) ||
                !RoomGeometry.IsFinite(representation.Region.size) ||
                representation.Region.size.x <= 0f || representation.Region.size.y <= 0f ||
                representation.Region.size.z <= 0f ||
                !RoomGeometry.IsFinite(representation.EffectiveVolumeM3) ||
                representation.EffectiveVolumeM3 <= 0f)
            {
                refusal = TrialStartRefusal.CollisionIntegrityFailed;
                return false;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var surface in surfaces)
            {
                if (string.IsNullOrWhiteSpace(surface.SurfaceId) || !ids.Add(surface.SurfaceId) ||
                    !RoomGeometry.IsFinite(surface.Center) ||
                    !RoomGeometry.IsFinite(surface.Size) ||
                    !RoomGeometry.IsFinite(surface.Rotation) ||
                    !RoomGeometry.IsFinite(surface.Normal) ||
                    surface.Size.x <= 0f || surface.Size.y <= 0f || surface.Size.z <= 0f ||
                    !Enum.IsDefined(typeof(PrimaryRepresentation), surface.Primary) ||
                    surface.Primary == PrimaryRepresentation.Plane &&
                    surface.Normal.sqrMagnitude < 0.5f)
                {
                    refusal = TrialStartRefusal.CollisionIntegrityFailed;
                    return false;
                }
            }

            pinned = new PinnedCollision(representation,
                new ReadOnlyCollection<CollisionSurface>(surfaces));
            return true;
        }

        /// <summary>
        /// Static colliders for the pinned representation, in the same stable
        /// order. They are created once per Trial and never rebuilt by a
        /// rescan, furniture update, or relocalization.
        /// </summary>
        public static GameObject InstallStatics(PinnedCollision collision, Transform parent)
        {
            var root = new GameObject("CollisionStatics");
            root.transform.SetParent(parent, false);
            foreach (var surface in collision.Surfaces)
            {
                var surfaceObject = new GameObject(surface.SurfaceId);
                surfaceObject.transform.SetParent(root.transform, false);
                surfaceObject.transform.SetPositionAndRotation(surface.Center, surface.Rotation);
                var collider = surfaceObject.AddComponent<BoxCollider>();
                collider.size = surface.Size;
            }
            return root;
        }
    }
}
