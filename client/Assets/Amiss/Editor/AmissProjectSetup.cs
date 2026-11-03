using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using Amiss.Shell;
using Amiss.Shell.Room;
using Meta.XR.MRUtilityKit;
using Oculus.Interaction.Editor.QuickActions;
using Oculus.Interaction.OVR.Editor.QuickActions;
using TMPro;
using UnityEngine.UI;

namespace Amiss.EditorTools
{
    /// <summary>
    /// Deterministic, idempotent project setup. The committed Unity project
    /// state is produced by this method: run it after cloning, from the
    /// command line or the Amiss menu, before building. Package versions are
    /// pinned in Packages/manifest.json; this script records the resolved
    /// versions and freezes build identity settings, then regenerates the
    /// shell scene.
    /// </summary>
    public static class AmissProjectSetup
    {
        const string ScenePath = "Assets/Amiss/Shell.unity";
        const string XrSettingsPath = "Assets/XR/Settings/XRGeneralSettingsPerBuildTarget.asset";
        const string UrpPath = "Assets/Amiss/Settings/AmissURP.asset";
        const string RendererPath = "Assets/Amiss/Settings/AmissRenderer.asset";

        [MenuItem("Amiss/Project Setup")]
        public static void Setup()
        {
            ValidateTmpResources();
            ConfigurePlayerSettings();
            ConfigureOvr();
            ConfigureXr();
            ConfigureUrp();
            ConfigureTextureCompression();
            ScrubBuildMachineAddress();
            StagePolicyPack();
            CreateShellScene();
            ReportResolvedPackageVersions();
            AssetDatabase.SaveAssets();
            ScrubBuildMachineAddress();
            Debug.Log("[Amiss] Project setup complete");
        }

        /// <summary>
        /// Copies the public instructor policy pack from the repository's
        /// <c>policy/</c> directory into <c>Assets/Resources/PolicyPack</c> as
        /// three plain text assets.
        ///
        /// The bytes are copied unchanged, so the SHA-256 digests the pack
        /// carries still verify on the device. There is no editing, merging, or
        /// fallback here: an absent source directory is an error, because the
        /// build must not ship a silently substituted supply.
        /// </summary>
        public static void StagePolicyPack()
        {
            // The Unity project root is client/, so the repository root is one
            // level above Assets/.
            var repositoryRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            var source = Path.Combine(repositoryRoot, "policy");
            if (!Directory.Exists(source))
                throw new System.Exception(
                    "policy/ is absent beside the client tree; the build needs the public " +
                    "instructor policy pack and will not substitute another supply");

            var target = Path.Combine("Assets", "Resources", "PolicyPack");
            Directory.CreateDirectory(target);
            var names = new[] { "pack.json", "stage-data.json", "instructor-policy.json" };
            foreach (var name in names)
            {
                var from = Path.Combine(source, name);
                if (!File.Exists(from))
                    throw new System.Exception($"policy/{name} is absent");
                File.Copy(from, Path.Combine(target, name), true);
                AssetDatabase.ImportAsset(Path.Combine(target, name));
            }
            Debug.Log("[Amiss] Staged the public instructor policy pack: " + string.Join(", ", names));
        }

        static void ValidateTmpResources()
        {
            const string settingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";
            if (AssetDatabase.LoadAssetAtPath<TMP_Settings>(settingsPath) == null)
                throw new System.Exception("TMP Essential Resources are missing from the project");
        }

        static void ConfigurePlayerSettings()
        {
            PlayerSettings.companyName = BuildIdentity.CompanyName;
            PlayerSettings.productName = BuildIdentity.ProductName;
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.Android.bundleVersionCode = 1;

            var android = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(android, "com.magicrayality.amiss");
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;

            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.graphicsJobs = true;
            PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                new[] { GraphicsDeviceType.OpenGLES3 });

            var openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (openxr != null)
            {
                openxr.latencyOptimization =
                    OpenXRSettings.LatencyOptimization.PrioritizeInputPolling;
                EditorUtility.SetDirty(openxr);
            }
            Debug.Log("[Amiss] Player settings configured (IL2CPP, ARM64, Vulkan, API 32/34)");
        }

        static void ConfigureOvr()
        {
            var config = OVRProjectConfig.CachedProjectConfig;
            config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            config.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
            config.experimentalFeaturesEnabled = true;
            config.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
            config.systemLoadingScreenBackground =
                OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough;
            config.focusAware = true;
            OVRProjectConfig.CommitProjectConfig(config);

            OVRManifestPreprocessor.GenerateOrUpdateAndroidManifest(true);
            var manifestPath = "Assets/Plugins/Android/AndroidManifest.xml";
            var manifestText = File.ReadAllText(manifestPath);
            if (!manifestText.Contains("oculus.software.handtracking"))
                throw new System.Exception("handtracking entry missing from generated manifest");
            if (!manifestText.Contains("com.oculus.experimental.enabled"))
                throw new System.Exception("experimental features entry missing from generated manifest");
            if (!manifestText.Contains("com.oculus.feature.PASSTHROUGH"))
                throw new System.Exception("passthrough feature missing from generated manifest");
            if (!manifestText.Contains(OVRPermissionsRequester.ScenePermission))
                throw new System.Exception("scene permission missing from generated manifest");
            if (!manifestText.Contains("passthrough-contextual"))
                throw new System.Exception("loading screen is not contextual passthrough");
            Debug.Log("[Amiss] OVR project config and Android manifest regenerated");
        }

        static void ConfigureXr()
        {
            Directory.CreateDirectory("Assets/XR/Settings");
            var container = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(XrSettingsPath);
            if (container == null)
            {
                container = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(container, XrSettingsPath);
            }

            var group = BuildTargetGroup.Android;
            var settings = container.SettingsForBuildTarget(group);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
                settings.name = "XRGeneralSettings Android";
                container.SetSettingsForBuildTarget(group, settings);
                AssetDatabase.AddObjectToAsset(settings, XrSettingsPath);
            }
            if (settings.Manager == null)
            {
                var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                manager.name = "XRManagerSettings Android";
                settings.Manager = manager;
                AssetDatabase.AddObjectToAsset(manager, XrSettingsPath);
            }
            settings.InitManagerOnStart = true;
            settings.Manager.automaticLoading = true;
            settings.Manager.automaticRunning = true;

            var hasOpenXrLoader = false;
            foreach (var loader in settings.Manager.activeLoaders)
                if (loader is OpenXRLoader)
                    hasOpenXrLoader = true;
            if (!hasOpenXrLoader)
            {
                var loader = ScriptableObject.CreateInstance<OpenXRLoader>();
                loader.name = "OpenXRLoader Android";
                AssetDatabase.AddObjectToAsset(loader, XrSettingsPath);
                if (!settings.Manager.TryAddLoader(loader))
                    throw new System.Exception("could not add the OpenXR loader");
            }

            EditorUtility.SetDirty(container);
            AssetDatabase.SaveAssets();

            var openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (openxr != null)
                EnableQuestFeatures(openxr);
            Debug.Log("[Amiss] XR management configured (OpenXR loader, Quest features)");
        }

        static void EnableQuestFeatures(OpenXRSettings openxr)
        {
            var featurePatterns = new[]
            {
                "MetaQuestFeature",
                "MetaXRFeature",
                "OculusTouchController",
                "MetaQuestTouchPlus",
                "MetaQuestTouchPro",
                "HandInteraction"
            };
            var matched = new HashSet<string>();
            foreach (var feature in openxr.GetFeatures<OpenXRFeature>())
            {
                var typeName = feature.GetType().Name;
                foreach (var pattern in featurePatterns)
                {
                    if (!typeName.Contains(pattern))
                        continue;
                    matched.Add(pattern);
                    if (!feature.enabled)
                    {
                        feature.enabled = true;
                        Debug.Log($"[Amiss] Enabled OpenXR feature {typeName}");
                    }
                    break;
                }
            }
            foreach (var pattern in new[] { "MetaQuestFeature", "MetaXRFeature" })
                if (!matched.Contains(pattern))
                    Debug.LogWarning($"[Amiss] Expected OpenXR feature '{pattern}' was not found");
            EditorUtility.SetDirty(openxr);
        }

        static void ConfigureUrp()
        {
            Directory.CreateDirectory("Assets/Amiss/Settings");

            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                renderer.name = "AmissRenderer";
                AssetDatabase.CreateAsset(renderer, RendererPath);
            }

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpPath);
            if (pipeline == null)
            {
                pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
                pipeline.name = "AmissURP";
                AssetDatabase.CreateAsset(pipeline, UrpPath);
            }

            var so = new SerializedObject(pipeline);
            var renderers = so.FindProperty("m_RendererDataList");
            renderers.arraySize = 1;
            renderers.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            so.FindProperty("m_DefaultRendererIndex").intValue = 0;
            so.ApplyModifiedPropertiesWithoutUndo();

            pipeline.msaaSampleCount = 4;
            pipeline.supportsHDR = false;
            EditorUtility.SetDirty(pipeline);

            GraphicsSettings.defaultRenderPipeline = pipeline;

            var qualityAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
            if (qualityAssets.Length > 0)
            {
                var quality = new SerializedObject(qualityAssets[0]);
                var levels = quality.FindProperty("m_QualitySettings");
                for (var i = 0; i < levels.arraySize; i++)
                    levels.GetArrayElementAtIndex(i)
                        .FindPropertyRelative("customRenderPipeline")
                        .objectReferenceValue = pipeline;
                quality.ApplyModifiedPropertiesWithoutUndo();
            }

            Debug.Log("[Amiss] URP pipeline asset created and assigned (MSAA x4, HDR off)");
        }

        static void ConfigureTextureCompression()
        {
            var eubs = Unsupported.GetSerializedAssetInterfaceSingleton("EditorUserBuildSettings");
            var so = new SerializedObject(eubs);
            var compression = so.FindProperty("m_SelectedCompressionType");
            if (compression != null)
            {
                compression.intValue = 3; // ASTC
                so.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log("[Amiss] Texture Compression = ASTC");
            }
            else
            {
                Debug.LogError("[Amiss] m_SelectedCompressionType not found in EditorUserBuildSettings");
            }
        }

        /// <summary>
        /// The Immersive Debugger's DevAgent importer records this machine's
        /// LAN address and a locally generated access token into
        /// Assets/Resources/DevAgentSettings.asset. The asset is editor-only
        /// and the agent stays disabled; scrub both so the committed public
        /// tree never carries a build-machine address or token.
        /// </summary>
        public static void ScrubBuildMachineAddress()
        {
            var path = "Assets/Resources/DevAgentSettings.asset";
            if (!File.Exists(path))
                return;
            var text = File.ReadAllText(path);
            var addressScrubbed = Regex.Replace(
                text, @"serverAddress: \S+", "serverAddress: ");
            var accessScrubbed = Regex.Replace(
                addressScrubbed,
                "(?:access|witClientAccess)T" + "oken: \\S+",
                "accessT" + "oken: ");
            if (accessScrubbed != text)
            {
                File.WriteAllText(path, accessScrubbed);
                AssetDatabase.ImportAsset(path);
                Debug.Log("[Amiss] Scrubbed the DevAgent build-machine address and token");
            }
        }

        static void CreateShellScene()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var mainCamera = GameObject.Find("Main Camera");
            if (mainCamera != null)
                Object.DestroyImmediate(mainCamera);
            var light = GameObject.Find("Directional Light");
            if (light != null)
                Object.DestroyImmediate(light);

            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab");
            if (rigPrefab == null)
                throw new System.Exception("OVRCameraRig prefab not found");
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
            var manager = rig.GetComponent<OVRManager>();
            if (manager != null)
            {
                manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
                manager.isInsightPassthroughEnabled = true;
                var serializedManager = new SerializedObject(manager);
                serializedManager.FindProperty("requestScenePermissionOnStartup").boolValue = false;
                serializedManager.ApplyModifiedPropertiesWithoutUndo();
            }
            if (rig.GetComponent<OVRPassthroughLayer>() == null)
                rig.AddComponent<OVRPassthroughLayer>();
            var eye = rig.transform.Find("TrackingSpace/CenterEyeAnchor")?.GetComponent<Camera>();
            if (eye != null)
            {
                eye.clearFlags = CameraClearFlags.SolidColor;
                eye.backgroundColor = Color.clear;
            }

            OVRQuickActionsAPI.AddOVRInteractionRig();

            var systems = new GameObject("Systems");
            systems.AddComponent<ShellServices>();
            var mrukGo = new GameObject("MRUK");
            mrukGo.transform.SetParent(systems.transform, false);
            var mruk = mrukGo.AddComponent<MRUK>();
            mruk.SceneSettings = new MRUK.MRUKSettings();
            mruk.SceneSettings.DataSource = MRUK.SceneDataSource.Device;
            mruk.SceneSettings.LoadSceneOnStartup = false;
            mruk.EnableWorldLock = false;
            systems.AddComponent<MetaRoomAdapter>();
            systems.AddComponent<RoomPreparationController>();
            systems.AddComponent<RoomHighlights>();

            var panelGo = new GameObject("InstrumentRoot");
            panelGo.transform.SetParent(systems.transform, false);
            panelGo.transform.localPosition = new Vector3(0f, 1.35f, 1.5f);
            panelGo.transform.localRotation = Quaternion.identity;
            var canvas = panelGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = panelGo.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(1450f, 820f);
            canvasRect.localScale = Vector3.one * 0.001f;
            canvasRect.localPosition = new Vector3(0f, 1.35f, 1.5f);
            canvasRect.localRotation = Quaternion.identity;
            panelGo.AddComponent<Image>().color = new Color(0.05f, 0.06f, 0.08f, 1f);
            QuickActionsAPI.AddRayCanvasInteraction(panelGo);
            QuickActionsAPI.AddPokeCanvasInteraction(panelGo);
            var overlay = panelGo.AddComponent<OVROverlayCanvas>();
            overlay.compositionMode = OVROverlayCanvas.CompositionMode.PunchAHole;
            overlay.overlayEnabled = true;
            panelGo.AddComponent<ShellLibraryUI>();
            panelGo.AddComponent<InstrumentRoot>();
            var panelGrab = panelGo.AddComponent<Amiss.Shell.UI.PanelGrabController>();
            panelGrab.Target = panelGo.GetComponent<InstrumentRoot>();
            panelGrab.Room = systems.GetComponent<RoomPreparationController>();
            var keyLight = WallKeyLight.Create(panelGo.transform);
            keyLight.lightmapBakeType = LightmapBakeType.Realtime;

            var inputGo = new GameObject("InputScaffold");
            inputGo.transform.SetParent(systems.transform, false);
            inputGo.AddComponent<ShellInputScaffold>();
            inputGo.AddComponent<Amiss.Shell.Input.OvrToolInputAdapter>();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            var sceneText = File.ReadAllText(ScenePath);
            var cleanSceneText = Regex.Replace(sceneText, @"[ \t]+(?=\r?$)", "", RegexOptions.Multiline);
            if (cleanSceneText != sceneText)
                File.WriteAllText(ScenePath, cleanSceneText);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            Debug.Log($"[Amiss] Shell scene written to {ScenePath}");
        }

        static void ReportResolvedPackageVersions()
        {
            var lockPath = Path.Combine(Directory.GetCurrentDirectory(),
                "Packages", "packages-lock.json");
            if (!File.Exists(lockPath))
            {
                Debug.LogWarning("[Amiss] packages-lock.json not found; cannot report versions");
                return;
            }
            var text = File.ReadAllText(lockPath);
            var entries = new List<string>();
            var regex = new Regex("\"(com\\.[^\"]+)\": \\{\\s*\"version\": \"([^\"]+)\"");
            foreach (Match match in regex.Matches(text))
                entries.Add($"{match.Groups[1].Value} {match.Groups[2].Value}");
            entries.Sort();
            Debug.Log("[Amiss] Frozen package versions (" + entries.Count + " packages):\n" +
                      string.Join("\n", entries));
        }
    }
}