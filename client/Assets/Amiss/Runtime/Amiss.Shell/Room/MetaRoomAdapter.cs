using System;
using System.Linq;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace Amiss.Shell.Room
{
    public sealed class RoomLoadResult
    {
        public RoomDraft Draft { get; }
        public RoomFailureReason Failure { get; }

        public RoomLoadResult(RoomDraft draft, RoomFailureReason failure)
        {
            Draft = draft;
            Failure = failure;
        }
    }

    public sealed class MetaRoomAdapter : MonoBehaviour
    {
        public const string SpatialPermission = OVRPermissionsRequester.ScenePermission;
        public event Action SceneUpdated;
        bool permissionRequestInFlight;
        MRUK Mruk => MRUK.Instance;

        void Awake()
        {
            if (Mruk == null)
                return;
            Mruk.SceneSettings.DataSource = MRUK.SceneDataSource.Device;
            Mruk.SceneSettings.LoadSceneOnStartup = false;
            Mruk.EnableWorldLock = true;
            Mruk.RoomUpdatedEvent.AddListener(OnRoomUpdated);
        }

        void OnDestroy()
        {
            if (Mruk != null)
                Mruk.RoomUpdatedEvent.RemoveListener(OnRoomUpdated);
        }

        public bool WorldLockActive => Mruk != null && Mruk.IsWorldLockActive;

        public bool HasSpatialPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return UnityEngine.Android.Permission.HasUserAuthorizedPermission(SpatialPermission);
#else
            return false;
#endif
        }

        public void RequestSpatialPermission(Action<bool> completed)
        {
            if (permissionRequestInFlight || completed == null)
                return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (HasSpatialPermission())
            {
                completed(true);
                return;
            }
            permissionRequestInFlight = true;
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += permission => Finish(permission == SpatialPermission);
            callbacks.PermissionDenied += permission => Finish(false);
            UnityEngine.Android.Permission.RequestUserPermission(SpatialPermission, callbacks);
#else
            completed(false);
#endif

            void Finish(bool granted)
            {
                permissionRequestInFlight = false;
                completed(granted);
            }
        }

        public async Task<bool> RequestSpaceSetup()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                return await OVRScene.RequestSpaceSetup();
            }
            catch (Exception)
            {
                return false;
            }
#else
            await Task.Yield();
            return false;
#endif
        }

        public async Task<RoomLoadResult> LoadDeviceScene(Vector3 userPosition, Vector3 userForward)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Mruk == null)
                return new RoomLoadResult(null, RoomFailureReason.SceneLoadFailed);
            MRUK.LoadDeviceResult result;
            try
            {
                result = await Mruk.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: false);
            }
            catch (Exception)
            {
                return new RoomLoadResult(null, RoomFailureReason.SceneLoadFailed);
            }
            if (result != MRUK.LoadDeviceResult.Success)
                return new RoomLoadResult(null, MapFailure(result));
            var room = Mruk.GetCurrentRoom();
            if (room == null)
                return new RoomLoadResult(null, RoomFailureReason.NoScene);
            var draft = DeriveDraft(room, userPosition, userForward);
            return new RoomLoadResult(draft, draft == null ? RoomFailureReason.InvalidGeometry : RoomFailureReason.None);
#else
            await Task.Yield();
            return new RoomLoadResult(null, RoomFailureReason.NoScene);
#endif
        }

        public static RoomDraft DeriveDraft(MRUKRoom room, Vector3 userPosition, Vector3 userForward)
        {
            if (room == null)
                return null;
            var ordered = room.Anchors
                .Where(anchor => anchor != null)
                .OrderBy(anchor => anchor.Anchor.Uuid.ToString("N"), StringComparer.Ordinal)
                .ThenBy(anchor => anchor.Label.ToString(), StringComparer.Ordinal)
                .ThenBy(anchor => anchor.transform.position.x)
                .ThenBy(anchor => anchor.transform.position.y)
                .ThenBy(anchor => anchor.transform.position.z)
                .ToArray();
            var surfaces = ordered.Select((anchor, index) => DeriveSurface(anchor, index))
                .Where(surface => surface != null).ToArray();
            if (surfaces.Length == 0)
                return null;
            return new RoomDraft(room.GetRoomBounds(), surfaces, userPosition, userForward, 0.9f);
        }

        static CollisionSurface DeriveSurface(MRUKAnchor anchor, int index)
        {
            const MRUKAnchor.SceneLabels structural =
                MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.WALL_FACE |
                MRUKAnchor.SceneLabels.CEILING | MRUKAnchor.SceneLabels.INNER_WALL_FACE;
            var label = anchor.Label;
            var isStructural = anchor.HasAnyLabel(structural);
            var isObstacle = !isStructural && !anchor.HasAnyLabel(
                MRUKAnchor.SceneLabels.GLOBAL_MESH | MRUKAnchor.SceneLabels.DOOR_FRAME |
                MRUKAnchor.SceneLabels.WINDOW_FRAME | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE);
            if (!isStructural && !isObstacle)
                return null;

            var role = anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR)
                ? CollisionSurfaceRole.Floor
                : anchor.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING)
                    ? CollisionSurfaceRole.Ceiling
                    : isStructural ? CollisionSurfaceRole.Wall : CollisionSurfaceRole.Obstacle;
            var id = "surface-" + (index + 1).ToString("D4");
            var semantic = label.ToString();
            if (anchor.PlaneRect.HasValue && isStructural)
            {
                var rect = anchor.PlaneRect.Value;
                var scale = anchor.transform.lossyScale;
                return new CollisionSurface(id, semantic, PrimaryRepresentation.Plane,
                    BoundaryProvenance.Scanned, role,
                    anchor.transform.TransformPoint(rect.center),
                    new Vector3(rect.width * Mathf.Abs(scale.x), rect.height * Mathf.Abs(scale.y), 0.02f),
                    anchor.transform.rotation, anchor.transform.forward,
                    "MRUK plane simplified to one static plane");
            }
            if (anchor.VolumeBounds.HasValue)
            {
                var bounds = anchor.VolumeBounds.Value;
                var scale = anchor.transform.lossyScale;
                var size = Vector3.Scale(bounds.size,
                    new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                return new CollisionSurface(id, semantic, PrimaryRepresentation.BoundingVolume,
                    BoundaryProvenance.Scanned, role, anchor.transform.TransformPoint(bounds.center), size,
                    anchor.transform.rotation, Vector3.up,
                    "MRUK volume simplified to one static bounding volume");
            }
            return null;
        }

        static RoomFailureReason MapFailure(MRUK.LoadDeviceResult result)
        {
            switch (result)
            {
                case MRUK.LoadDeviceResult.NoScenePermission:
                case MRUK.LoadDeviceResult.FailurePermissionInsufficient:
                    return RoomFailureReason.ScenePermission;
                case MRUK.LoadDeviceResult.NoRoomsFound:
                    return RoomFailureReason.NoScene;
                case MRUK.LoadDeviceResult.FailureDataIsInvalid:
                    return RoomFailureReason.InvalidGeometry;
                default:
                    return RoomFailureReason.SceneLoadFailed;
            }
        }

        void OnRoomUpdated(MRUKRoom room)
        {
            SceneUpdated?.Invoke();
        }
    }
}
