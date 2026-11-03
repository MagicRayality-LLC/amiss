using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Amiss.EditorTools
{
    public static class AmissProbe
    {
        public static void ConfigureAndBuild()
        {
            var config = OVRProjectConfig.CachedProjectConfig;
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
            config.systemLoadingScreenBackground = OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough;
            OVRProjectConfig.CommitProjectConfig(config);
            var openXr = UnityEngine.XR.OpenXR.OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            openXr.renderMode = UnityEngine.XR.OpenXR.OpenXRSettings.RenderMode.SinglePassInstanced;
            EditorUtility.SetDirty(openXr);
            var pipeline = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>(
                "Assets/Amiss/Settings/AmissURP.asset");
            var serializedPipeline = new SerializedObject(pipeline);
            serializedPipeline.FindProperty("m_AllowPostProcessAlphaOutput").boolValue = false;
            serializedPipeline.ApplyModifiedPropertiesWithoutUndo();
            PlayerSettings.preserveFramebufferAlpha = false;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var mainCamera = GameObject.Find("Main Camera");
            if (mainCamera != null)
                Object.DestroyImmediate(mainCamera);
            var light = GameObject.Find("Directional Light");
            if (light != null)
                Object.DestroyImmediate(light);

            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab");
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
            var manager = rig.GetComponent<OVRManager>();
            manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            manager.isInsightPassthroughEnabled = false;
            var serializedManager = new SerializedObject(manager);
            serializedManager.FindProperty("requestScenePermissionOnStartup").boolValue = false;
            serializedManager.ApplyModifiedPropertiesWithoutUndo();
            var passthrough = rig.GetComponent<OVRPassthroughLayer>();
            if (passthrough != null)
                Object.DestroyImmediate(passthrough);
            var eye = rig.transform.Find("TrackingSpace/CenterEyeAnchor").GetComponent<Camera>();
            eye.clearFlags = CameraClearFlags.SolidColor;
            eye.backgroundColor = new Color(0.1f, 0f, 0.2f, 1f);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "ProbeCube";
            cube.transform.SetParent(eye.transform, false);
            cube.transform.localPosition = new Vector3(1f, 0f, 1.5f);
            cube.transform.localRotation = Quaternion.Euler(20f, 35f, 0f);
            cube.transform.localScale = Vector3.one * 0.5f;
            var material = new Material(Shader.Find("Unlit/Color"));
            material.color = new Color(0.8f, 0f, 0.8f, 1f);
            cube.GetComponent<Renderer>().sharedMaterial = material;

            var canvasGo = new GameObject("ProbeCanvas", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.Image));
            canvasGo.transform.SetParent(eye.transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = canvasGo.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(1200f, 800f);
            canvasRect.localScale = Vector3.one * 0.001f;
            canvasRect.localPosition = new Vector3(0f, -0.25f, 1.5f);
            canvasRect.localRotation = Quaternion.identity;
            canvasGo.GetComponent<UnityEngine.UI.Image>().color = new Color(0.05f, 0.06f, 0.08f, 1f);
            var statusGo = new GameObject("Status", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            statusGo.transform.SetParent(canvasGo.transform, false);
            var status = statusGo.GetComponent<TMPro.TextMeshProUGUI>();
            status.text = "SPACE SCAN: NOT STARTED";
            status.fontSize = 64f;
            status.alignment = TMPro.TextAlignmentOptions.Center;
            status.color = Color.white;
            status.raycastTarget = false;
            var statusRect = statusGo.GetComponent<RectTransform>();
            statusRect.anchorMin = Vector2.zero;
            statusRect.anchorMax = Vector2.one;
            statusRect.offsetMin = Vector2.zero;
            statusRect.offsetMax = Vector2.zero;

            var diagnostics = new GameObject("ProbeDiagnostics");
            diagnostics.AddComponent<Amiss.Shell.AmissProbeRuntime>();

            var leftHand = AddHand(rig, "LeftHandAnchor", false);
            var rightHand = AddHand(rig, "RightHandAnchor", true);
            AddExitButton(canvasGo, leftHand, rightHand);
            AddSpaceSetupSelfCheck(status, eye);

            const string scenePath = "Assets/Amiss/Probe.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            OVRManifestPreprocessor.GenerateOrUpdateAndroidManifest(true);
            AmissProjectSetup.ScrubBuildMachineAddress();
            var report = BuildPipeline.BuildPlayer(
                new[] { new EditorBuildSettingsScene(scenePath, true) },
                "Builds/Amiss-debug.apk", BuildTarget.Android, BuildOptions.Development);
            Debug.Log($"[Amiss] Probe build result: {report.summary.result}, errors={report.summary.totalErrors}");
            if (report.summary.result != BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }

        /// <summary>
        /// Adds the stock OVRHandPrefab under the named hand anchor so the
        /// probe HUD can be touched with hand tracking only. The stock prefab
        /// is left-handed; for the right anchor both the hand and skeleton
        /// types are flipped to right.
        /// </summary>
        public static GameObject AddHand(GameObject rig, string anchorName, bool right)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/com.meta.xr.sdk.core/Prefabs/OVRHandPrefab.prefab");
            if (prefab == null)
                throw new System.Exception("OVRHandPrefab prefab not found");
            var anchor = rig.transform.Find("TrackingSpace/" + anchorName);
            if (anchor == null)
                throw new System.Exception($"Hand anchor TrackingSpace/{anchorName} not found");
            var hand = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            hand.name = right ? "OVRHandRight" : "OVRHandLeft";
            hand.transform.SetParent(anchor, false);
            if (right)
            {
                var serializedHand = new SerializedObject(hand.GetComponent<OVRHand>());
                serializedHand.FindProperty("HandType").intValue = (int)OVRHand.Hand.HandRight;
                serializedHand.ApplyModifiedPropertiesWithoutUndo();
                var serializedSkeleton = new SerializedObject(hand.GetComponent<OVRSkeleton>());
                serializedSkeleton.FindProperty("_skeletonType").intValue =
                    (int)OVRSkeleton.SkeletonType.HandRight;
                serializedSkeleton.ApplyModifiedPropertiesWithoutUndo();
            }
            return hand;
        }

        /// <summary>
        /// Adds the hand-only quit control to the probe HUD canvas: a red
        /// plate the wearer touches with an index fingertip for one second to
        /// leave the app without a controller.
        /// </summary>
        public static RectTransform AddExitButton(
            GameObject canvasGo, GameObject leftHand, GameObject rightHand)
        {
            var buttonGo = new GameObject("ExitButton", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            buttonGo.transform.SetParent(canvasGo.transform, false);
            var image = buttonGo.GetComponent<UnityEngine.UI.Image>();
            image.color = new Color(0.55f, 0.08f, 0.08f, 1f);
            var rect = buttonGo.GetComponent<RectTransform>();
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(480f, 150f);
            rect.anchoredPosition = new Vector2(300f, -285f);

            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            labelGo.transform.SetParent(buttonGo.transform, false);
            var label = labelGo.GetComponent<TMPro.TextMeshProUGUI>();
            label.text = "TOUCH TO QUIT";
            label.fontSize = 56f;
            label.alignment = TMPro.TextAlignmentOptions.Center;
            label.color = Color.white;
            label.raycastTarget = false;
            var labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var exit = canvasGo.AddComponent<Amiss.Shell.HandExitButton>();
            exit.Target = rect;
            exit.TargetImage = image;
            exit.Label = label;
            exit.LeftHandSkeleton = leftHand.GetComponent<OVRSkeleton>();
            exit.RightHandSkeleton = rightHand.GetComponent<OVRSkeleton>();
            return rect;
        }

        /// <summary>
        /// Adds the scan-on-demand self-check to the probe HUD: an MRUK
        /// instance in device-data mode (no startup auto-load), the production
        /// room adapter, and the narrating self-check driver that runs the
        /// permission, current-room containment, and Space Setup sequence on
        /// launch.
        /// </summary>
        public static void AddSpaceSetupSelfCheck(TMPro.TextMeshProUGUI status, Camera eye)
        {
            var mrukGo = new GameObject("MRUK");
            var mruk = mrukGo.AddComponent<Meta.XR.MRUtilityKit.MRUK>();
            mruk.SceneSettings = new Meta.XR.MRUtilityKit.MRUK.MRUKSettings();
            mruk.SceneSettings.DataSource = Meta.XR.MRUtilityKit.MRUK.SceneDataSource.Device;
            mruk.SceneSettings.LoadSceneOnStartup = false;
            mruk.EnableWorldLock = true;

            var adapterGo = new GameObject("MetaRoomAdapter");
            var adapter = adapterGo.AddComponent<Amiss.Shell.Room.MetaRoomAdapter>();

            var setupGo = new GameObject("ProbeSpaceSetup");
            var selfCheck = setupGo.AddComponent<Amiss.Shell.ProbeSpaceSetup>();
            selfCheck.Status = status;
            selfCheck.Room = adapter;
            selfCheck.Eye = eye != null ? eye.transform : null;
        }
    }
}
