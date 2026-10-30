using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Amiss.Contracts;
using UnityEngine;

namespace Amiss.Shell.Room
{
    public enum RoomPreparationStatus
    {
        Idle,
        Explanation,
        PermissionRequested,
        SpaceSetupReady,
        SpaceSetupRequested,
        SceneLoading,
        AwaitingConfirmation,
        Confirmed,
        Restricted
    }

    public enum RoomFailureReason
    {
        None,
        ScenePermission,
        SpaceSetupCancelled,
        SpaceSetupFailed,
        NoScene,
        SceneLoadFailed,
        InvalidGeometry,
        DisconnectedRegion,
        UnsafeRegion,
        BelowMinimum,
        ConfirmationRequired,
        SetupRequired,
        SafetyInvalid
    }

    public enum BoundaryProvenance
    {
        Scanned,
        VirtualSupplement,
        ManualVirtual
    }

    public enum PrimaryRepresentation
    {
        Plane,
        BoundingVolume
    }

    public enum CollisionSurfaceRole
    {
        Floor,
        Wall,
        Ceiling,
        Obstacle
    }

    public sealed class CollisionSurface
    {
        public string SurfaceId { get; }
        public string SemanticLabel { get; }
        public PrimaryRepresentation Primary { get; }
        public BoundaryProvenance Provenance { get; }
        public CollisionSurfaceRole Role { get; }
        public Vector3 Center { get; }
        public Vector3 Size { get; }
        public Quaternion Rotation { get; }
        public Vector3 Normal { get; }
        public string Approximation { get; }

        public CollisionSurface(
            string surfaceId,
            string semanticLabel,
            PrimaryRepresentation primary,
            BoundaryProvenance provenance,
            CollisionSurfaceRole role,
            Vector3 center,
            Vector3 size,
            Quaternion rotation,
            Vector3 normal,
            string approximation)
        {
            SurfaceId = surfaceId ?? "";
            SemanticLabel = semanticLabel ?? "";
            Primary = primary;
            Provenance = provenance;
            Role = role;
            Center = center;
            Size = size;
            Rotation = RoomGeometry.Normalize(rotation);
            Normal = normal.sqrMagnitude > 0.000001f ? normal.normalized : Vector3.zero;
            Approximation = approximation ?? "";
        }

        public Bounds WorldBounds => RoomGeometry.RotatedBounds(Center, Size, Rotation);
    }

    public sealed class RoomDraft
    {
        public Bounds Region { get; }
        public float EffectiveVolumeM3 { get; }
        public string DeductionMethod { get; }
        public IReadOnlyList<CollisionSurface> Surfaces { get; }
        public Vector3 ConfirmedUserPosition { get; }
        public Vector3 ConfirmedUserForward { get; }
        public float ClearSwingRadiusMeters { get; }

        public RoomDraft(
            Bounds region,
            IReadOnlyList<CollisionSurface> surfaces,
            Vector3 confirmedUserPosition,
            Vector3 confirmedUserForward,
            float clearSwingRadiusMeters)
        {
            Region = region;
            Surfaces = new ReadOnlyCollection<CollisionSurface>((surfaces ?? Array.Empty<CollisionSurface>()).ToArray());
            ConfirmedUserPosition = confirmedUserPosition;
            var forward = confirmedUserForward;
            forward.y = 0f;
            ConfirmedUserForward = forward.sqrMagnitude > 0.000001f ? forward.normalized : Vector3.forward;
            ClearSwingRadiusMeters = clearSwingRadiusMeters;
            EffectiveVolumeM3 = CalculateEffectiveVolume(region, Surfaces);
            DeductionMethod = "region bounding volume minus overlapping obstacle bounding volumes";
        }

        public RoomDraft WithVirtualSupplements()
        {
            var surfaces = Surfaces.ToList();
            var existing = new HashSet<string>(surfaces.Select(s => s.SurfaceId), StringComparer.Ordinal);
            foreach (var supplement in CreateBoundarySurfaces(
                         Region, BoundaryProvenance.VirtualSupplement, "supplement"))
                if (existing.Add(supplement.SurfaceId))
                    surfaces.Add(supplement);
            return new RoomDraft(Region, surfaces, ConfirmedUserPosition, ConfirmedUserForward,
                ClearSwingRadiusMeters);
        }

        public static RoomDraft CreateManual(Bounds region, Vector3 userPosition, Vector3 userForward)
        {
            return new RoomDraft(region, CreateBoundarySurfaces(region, BoundaryProvenance.ManualVirtual, "manual"),
                userPosition, userForward, 0.9f);
        }

        public string BoundaryLabel
        {
            get
            {
                if (Surfaces.Count > 0 && Surfaces.All(s => s.Provenance == BoundaryProvenance.ManualVirtual))
                    return "virtual boundaries — not the scanned room";
                if (Surfaces.Any(s => s.Provenance != BoundaryProvenance.Scanned))
                    return "scanned room with virtual supplements";
                return "confirmed scanned room";
            }
        }

        public RoomFailureReason Validate(double minimumVolumeM3)
        {
            if (!RoomGeometry.IsFinite(Region.center) || !RoomGeometry.IsFinite(Region.size) ||
                Region.size.x <= 0.01f || Region.size.y <= 0.01f || Region.size.z <= 0.01f ||
                !RoomGeometry.IsFinite(EffectiveVolumeM3) || EffectiveVolumeM3 <= 0f ||
                EffectiveVolumeM3 > Region.size.x * Region.size.y * Region.size.z + 0.001f ||
                Surfaces.Count == 0 || !RoomGeometry.IsFinite(ConfirmedUserPosition) ||
                !RoomGeometry.IsFinite(ClearSwingRadiusMeters) || ClearSwingRadiusMeters <= 0f)
                return RoomFailureReason.InvalidGeometry;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var surface in Surfaces)
            {
                if (string.IsNullOrWhiteSpace(surface.SurfaceId) || !ids.Add(surface.SurfaceId) ||
                    !RoomGeometry.IsFinite(surface.Center) || !RoomGeometry.IsFinite(surface.Size) ||
                    !RoomGeometry.IsFinite(surface.Rotation) || !RoomGeometry.IsFinite(surface.Normal) ||
                    surface.Size.x <= 0f || surface.Size.y <= 0f || surface.Size.z <= 0f ||
                    surface.Primary == PrimaryRepresentation.Plane && surface.Normal.sqrMagnitude < 0.5f)
                    return RoomFailureReason.InvalidGeometry;
            }

            if (!Surfaces.Any(s => s.Role == CollisionSurfaceRole.Floor) ||
                Surfaces.Count(s => s.Role == CollisionSurfaceRole.Wall) < 3)
                return RoomFailureReason.DisconnectedRegion;

            if (EffectiveVolumeM3 < minimumVolumeM3)
                return RoomFailureReason.BelowMinimum;

            var swing = new Bounds(ConfirmedUserPosition + Vector3.up * 0.9f,
                Vector3.one * (ClearSwingRadiusMeters * 2f));
            if (!RoomGeometry.Contains(Region, swing) ||
                Surfaces.Any(s => s.Role == CollisionSurfaceRole.Obstacle && s.WorldBounds.Intersects(swing)))
                return RoomFailureReason.UnsafeRegion;

            return RoomFailureReason.None;
        }

        static float CalculateEffectiveVolume(Bounds region, IReadOnlyList<CollisionSurface> surfaces)
        {
            var volume = region.size.x * region.size.y * region.size.z;
            foreach (var obstacle in surfaces.Where(s => s.Role == CollisionSurfaceRole.Obstacle))
            {
                if (!RoomGeometry.IsFinite(obstacle.Center) || !RoomGeometry.IsFinite(obstacle.Size) ||
                    !obstacle.WorldBounds.Intersects(region))
                    continue;
                var intersection = RoomGeometry.Intersection(region, obstacle.WorldBounds);
                volume -= intersection.size.x * intersection.size.y * intersection.size.z;
            }
            return Mathf.Max(0f, volume);
        }

        static IReadOnlyList<CollisionSurface> CreateBoundarySurfaces(
            Bounds region, BoundaryProvenance provenance, string idPrefix)
        {
            const float thickness = 0.02f;
            var c = region.center;
            var s = region.size;
            return new[]
            {
                new CollisionSurface(idPrefix + "-floor", "FLOOR", PrimaryRepresentation.Plane, provenance,
                    CollisionSurfaceRole.Floor, new Vector3(c.x, region.min.y, c.z), new Vector3(s.x, thickness, s.z),
                    Quaternion.identity, Vector3.up, "simplified static floor plane"),
                new CollisionSurface(idPrefix + "-ceiling", "CEILING", PrimaryRepresentation.Plane, provenance,
                    CollisionSurfaceRole.Ceiling, new Vector3(c.x, region.max.y, c.z), new Vector3(s.x, thickness, s.z),
                    Quaternion.identity, Vector3.down, "simplified static ceiling plane"),
                new CollisionSurface(idPrefix + "-wall-left", "WALL_FACE", PrimaryRepresentation.Plane, provenance,
                    CollisionSurfaceRole.Wall, new Vector3(region.min.x, c.y, c.z), new Vector3(thickness, s.y, s.z),
                    Quaternion.identity, Vector3.right, "simplified static wall plane"),
                new CollisionSurface(idPrefix + "-wall-right", "WALL_FACE", PrimaryRepresentation.Plane, provenance,
                    CollisionSurfaceRole.Wall, new Vector3(region.max.x, c.y, c.z), new Vector3(thickness, s.y, s.z),
                    Quaternion.identity, Vector3.left, "simplified static wall plane"),
                new CollisionSurface(idPrefix + "-wall-back", "WALL_FACE", PrimaryRepresentation.Plane, provenance,
                    CollisionSurfaceRole.Wall, new Vector3(c.x, c.y, region.min.z), new Vector3(s.x, s.y, thickness),
                    Quaternion.identity, Vector3.forward, "simplified static wall plane"),
                new CollisionSurface(idPrefix + "-wall-front", "WALL_FACE", PrimaryRepresentation.Plane, provenance,
                    CollisionSurfaceRole.Wall, new Vector3(c.x, c.y, region.max.z), new Vector3(s.x, s.y, thickness),
                    Quaternion.identity, Vector3.back, "simplified static wall plane")
            };
        }
    }

    public sealed class CollisionRepresentation
    {
        public const string CurrentFormatVersion = "1.0.0";

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
        public IReadOnlyList<CollisionSurface> Surfaces { get; }

        CollisionRepresentation(string representationId, RoomDraft draft, string checksum)
        {
            RepresentationId = representationId;
            FormatVersion = CurrentFormatVersion;
            CoordinateFrameId = "mruk-world-lock";
            CoordinateFrameVersion = "1";
            ProducerContractVersion = FrameworkApiVersions.FrameworkApi;
            Checksum = checksum;
            SizeClass = draft.Surfaces.Count <= 64 ? "small" : draft.Surfaces.Count <= 256 ? "medium" : "large";
            BoundaryLabel = draft.BoundaryLabel;
            Region = draft.Region;
            EffectiveVolumeM3 = draft.EffectiveVolumeM3;
            DeductionMethod = draft.DeductionMethod;
            Surfaces = new ReadOnlyCollection<CollisionSurface>(draft.Surfaces
                .OrderBy(s => s.SurfaceId, StringComparer.Ordinal).ToArray());
        }

        public static bool TryCreate(string preparationId, RoomDraft draft, double minimumVolumeM3,
            out CollisionRepresentation representation, out RoomFailureReason failure)
        {
            representation = null;
            failure = draft?.Validate(minimumVolumeM3) ?? RoomFailureReason.InvalidGeometry;
            if (failure != RoomFailureReason.None || string.IsNullOrWhiteSpace(preparationId))
                return false;
            var id = "collision-" + preparationId;
            var checksum = ComputeChecksum(id, draft);
            representation = new CollisionRepresentation(id, draft, checksum);
            return true;
        }

        public bool VerifyIntegrity()
        {
            if (FormatVersion != CurrentFormatVersion || CoordinateFrameId != "mruk-world-lock" ||
                ProducerContractVersion != FrameworkApiVersions.FrameworkApi)
                return false;
            var draft = new RoomDraft(Region, Surfaces, Region.center, Vector3.forward, 0.9f);
            return string.Equals(Checksum, ComputeChecksum(RepresentationId, draft), StringComparison.Ordinal);
        }

        internal static string ComputeChecksum(string representationId, RoomDraft draft)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                Write(writer, "amiss-collision-representation");
                Write(writer, CurrentFormatVersion);
                Write(writer, representationId);
                Write(writer, "mruk-world-lock");
                Write(writer, "1");
                Write(writer, FrameworkApiVersions.FrameworkApi);
                Write(writer, draft.BoundaryLabel);
                Write(writer, draft.DeductionMethod);
                Write(writer, draft.EffectiveVolumeM3);
                Write(writer, draft.Region.center);
                Write(writer, draft.Region.size);
                var ordered = draft.Surfaces.OrderBy(s => s.SurfaceId, StringComparer.Ordinal).ToArray();
                writer.Write(ordered.Length);
                foreach (var surface in ordered)
                {
                    Write(writer, surface.SurfaceId);
                    Write(writer, surface.SemanticLabel);
                    writer.Write((int)surface.Primary);
                    writer.Write((int)surface.Provenance);
                    writer.Write((int)surface.Role);
                    Write(writer, surface.Center);
                    Write(writer, surface.Size);
                    Write(writer, surface.Rotation);
                    Write(writer, surface.Normal);
                    Write(writer, surface.Approximation);
                }
            }
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
        }

        static void Write(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        static void Write(BinaryWriter writer, float value)
        {
            writer.Write(value == 0f ? 0f : value);
        }

        static void Write(BinaryWriter writer, Vector3 value)
        {
            Write(writer, value.x);
            Write(writer, value.y);
            Write(writer, value.z);
        }

        static void Write(BinaryWriter writer, Quaternion value)
        {
            Write(writer, value.x);
            Write(writer, value.y);
            Write(writer, value.z);
            Write(writer, value.w);
        }
    }

    public sealed class RoomConfirmation
    {
        public string PreparationId { get; }
        public string ConfirmationId { get; }
        public string CollisionChecksum { get; }
        public string BoundaryLabel { get; }
        public Bounds Region { get; }
        public float EffectiveVolumeM3 { get; }
        public string DeductionMethod { get; }
        public Vector3 ConfirmedUserPosition { get; }
        public Vector3 ConfirmedUserForward { get; }
        public float ClearSwingRadiusMeters { get; }
        public IReadOnlyList<Bounds> ExcludedObstacles { get; }

        internal RoomConfirmation(string preparationId, RoomDraft draft, CollisionRepresentation collision)
        {
            PreparationId = preparationId;
            ConfirmationId = "confirmation-" + preparationId;
            CollisionChecksum = collision.Checksum;
            BoundaryLabel = draft.BoundaryLabel;
            Region = draft.Region;
            EffectiveVolumeM3 = draft.EffectiveVolumeM3;
            DeductionMethod = draft.DeductionMethod;
            ConfirmedUserPosition = draft.ConfirmedUserPosition;
            ConfirmedUserForward = draft.ConfirmedUserForward;
            ClearSwingRadiusMeters = draft.ClearSwingRadiusMeters;
            ExcludedObstacles = new ReadOnlyCollection<Bounds>(draft.Surfaces
                .Where(s => s.Role == CollisionSurfaceRole.Obstacle)
                .Select(s => s.WorldBounds).ToArray());
        }
    }

    public sealed class CollisionPin
    {
        public string TrialId { get; }
        public string CollisionRepresentationId { get; }
        public string CollisionChecksum { get; }
        public CollisionRepresentation Representation { get; }

        public CollisionPin(string trialId, CollisionRepresentation representation)
        {
            TrialId = trialId ?? throw new ArgumentNullException(nameof(trialId));
            Representation = representation ?? throw new ArgumentNullException(nameof(representation));
            CollisionRepresentationId = representation.RepresentationId;
            CollisionChecksum = representation.Checksum;
        }
    }

    public static class RoomGeometry
    {
        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        public static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

        public static Quaternion Normalize(Quaternion value) =>
            value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w > 0.000001f
                ? value.normalized
                : Quaternion.identity;

        public static Bounds RotatedBounds(Vector3 center, Vector3 size, Quaternion rotation)
        {
            var extents = size * 0.5f;
            var bounds = new Bounds(center, Vector3.zero);
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
                bounds.Encapsulate(center + rotation * Vector3.Scale(extents, new Vector3(x, y, z)));
            return bounds;
        }

        public static bool Contains(Bounds outer, Bounds inner) =>
            inner.min.x >= outer.min.x && inner.max.x <= outer.max.x &&
            inner.min.y >= outer.min.y && inner.max.y <= outer.max.y &&
            inner.min.z >= outer.min.z && inner.max.z <= outer.max.z;

        public static Bounds Intersection(Bounds a, Bounds b)
        {
            var min = Vector3.Max(a.min, b.min);
            var max = Vector3.Min(a.max, b.max);
            return new Bounds((min + max) * 0.5f, Vector3.Max(Vector3.zero, max - min));
        }

        public static string Meters(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
