using System.Collections;
using System.Collections.Generic;
using Amiss.Shell.Room;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;

namespace Amiss.Shell
{
    /// <summary>
    /// Probe HUD self-check for the scan-on-demand flow: on launch it verifies
    /// the spatial permission, loads the device Scene Model, and checks whether
    /// the wearer stands inside the current room. Only when the scan is missing
    /// entirely or this location is outside every stored room does it start the
    /// system Space Setup flow by itself; after the flow returns it reloads and
    /// reports. Every step narrates into the HUD status text and the device
    /// status file, including the raw MRUK result, the world-lock state after a
    /// bounded settle, and the current-room containment, so a failure names its
    /// cause instead of a mapped label. Post-scan world-lock or data failures
    /// stop restricted - they never relaunch Space Setup.
    /// </summary>
    public sealed class ProbeSpaceSetup : MonoBehaviour
    {
        public TextMeshProUGUI Status;
        public MetaRoomAdapter Room;
        public Transform Eye;

        const float WorldLockSettleSeconds = 3f;
        string _logPath;

        void Start()
        {
            _logPath = Application.persistentDataPath + "/probe-status.txt";
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            yield return null;
            if (Status == null || Room == null || Eye == null)
                yield break;

            SetStatus("SPACE SCAN: CHECKING PERMISSION...");
            var permissionDone = false;
            var permissionGranted = false;
            Room.RequestSpatialPermission(granted =>
            {
                permissionGranted = granted;
                permissionDone = true;
            });
            while (!permissionDone)
                yield return null;
            if (!permissionGranted)
            {
                SetStatus("SPACE SCAN: PERMISSION DENIED - allow spatial data in Settings");
                yield break;
            }

            yield return AnchorCensus();

            SetStatus("SPACE SCAN: READING DEVICE SCENE DATA...");
            yield return LoadScene();
            LogLine("initial load: " + _detail);
            if (_ok)
            {
                SetStatus($"SPACE SCAN: COMPLETE HERE - {_anchors} ANCHORS");
                yield break;
            }

            var canScan = _raw == MRUK.LoadDeviceResult.NoRoomsFound || _outsideRoom;
            if (!canScan)
            {
                SetStatus($"SPACE SCAN: RESTRICTED - {_detail}");
                yield break;
            }

            var reason = _raw == MRUK.LoadDeviceResult.NoRoomsFound
                ? "SPACE SCAN: NONE ON DEVICE"
                : "SPACE SCAN: NO ROOM AT THIS LOCATION";
            for (var countdown = 5; countdown > 0; countdown--)
            {
                SetStatus($"{reason} - SPACE SETUP OPENS IN {countdown}s\n{_detail}");
                yield return new WaitForSeconds(1f);
            }
            SetStatus($"{reason} - STARTING SPACE SETUP...");
            var setup = Room.RequestSpaceSetup();
            while (!setup.IsCompleted)
                yield return null;
            if (!setup.Result)
            {
                SetStatus("SPACE SCAN: OS REFUSED SPACE SETUP (returned false)");
                yield break;
            }

            SetStatus("SPACE SCAN: RE-CHECKING AFTER SPACE SETUP...");
            yield return LoadScene();
            LogLine("post-setup load: " + _detail);
            if (_ok)
                SetStatus($"SPACE SCAN: JUST SCANNED - ROOM FOUND, {_anchors} ANCHORS");
            else
                SetStatus($"SPACE SCAN: SETUP RETURNED BUT RE-CHECK FAILED - {_detail}");
        }

        MRUK.LoadDeviceResult _raw;
        string _detail = "no load";
        bool _ok;
        bool _outsideRoom;
        int _anchors;

        IEnumerator LoadScene()
        {
            _ok = false;
            _outsideRoom = false;
            _anchors = 0;
            _detail = "mruk=null";
            var mruk = MRUK.Instance;
            if (mruk == null)
                yield break;
            var hasScene = false;
            var hasSceneTask = MRUK.HasSceneModel();
            while (!hasSceneTask.IsCompleted)
                yield return null;
            if (!hasSceneTask.IsFaulted)
                hasScene = hasSceneTask.Result;
            mruk.EnableWorldLock = true;
            var task = mruk.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: false);
            while (!task.IsCompleted)
                yield return null;
            if (task.IsFaulted)
            {
                _detail = $"EX={task.Exception?.GetBaseException().GetType().Name ?? "unknown"}";
                mruk.EnableWorldLock = false;
                yield break;
            }
            _raw = task.Result;
            var settle = 0f;
            while (!mruk.IsWorldLockActive && settle < WorldLockSettleSeconds)
            {
                settle += Time.unscaledDeltaTime;
                yield return null;
            }
            var worldLock = mruk.IsWorldLockActive;
            var room = mruk.GetCurrentRoom();
            var inRoom = room != null && room.IsPositionInRoom(Eye.position, false);
            _anchors = room != null ? room.Anchors.Count : 0;
            _outsideRoom = room != null && !inRoom;
            _detail = $"RAW={_raw} WL={(worldLock ? "on" : "off")}({settle:0.0}s) " +
                      $"ROOM={(room != null ? "found" : "null")} IN={(inRoom ? "yes" : "no")} " +
                      $"HASSCENE={(hasScene ? "yes" : "no")}";
            _ok = _raw == MRUK.LoadDeviceResult.Success && worldLock && inRoom;
            mruk.EnableWorldLock = false;
        }

        IEnumerator AnchorCensus()
        {
            var all = CountAnchors(null);
            var layout = CountAnchors(typeof(OVRRoomLayout));
            var mesh = CountAnchors(typeof(OVRRoomMesh));
            var labels = CountAnchors(typeof(OVRSemanticLabels));
            while (!all.IsCompleted || !layout.IsCompleted || !mesh.IsCompleted || !labels.IsCompleted)
                yield return null;
            LogLine($"census: all={Result(all)} layout={Result(layout)} " +
                    $"mesh={Result(mesh)} labels={Result(labels)}");

            static string Result(System.Threading.Tasks.Task<int> task) =>
                task.IsFaulted ? "ex" : task.Result.ToString();
        }

        static System.Threading.Tasks.Task<int> CountAnchors(System.Type component)
        {
            return CountCore();

            async System.Threading.Tasks.Task<int> CountCore()
            {
                var anchors = new List<OVRAnchor>();
                var options = new OVRAnchor.FetchOptions();
                if (component != null)
                    options.SingleComponentType = component;
                var result = await OVRAnchor.FetchAnchorsAsync(anchors, options);
                return result.Success ? anchors.Count : -1;
            }
        }

        void SetStatus(string message)
        {
            Status.text = message;
            Debug.Log($"[Amiss] ProbeSpaceSetup: {message}");
            AppendLog(message);
        }

        void LogLine(string message)
        {
            Debug.Log($"[Amiss] ProbeSpaceSetup: {message}");
            AppendLog(message);
        }

        void AppendLog(string message)
        {
            if (string.IsNullOrEmpty(_logPath))
                return;
            try
            {
                System.IO.File.AppendAllText(_logPath,
                    System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + " " +
                    message.Replace("\n", " | ") + "\n");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Amiss] ProbeSpaceSetup status write failed: {ex.GetType().Name}");
            }
        }
    }
}
