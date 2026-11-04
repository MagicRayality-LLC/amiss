using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Amiss.Shell;
using Amiss.Shell.Input;
using Amiss.Shell.Room;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Amiss.Tests
{
    public class QuestRoomWiringTests
    {
        [Test]
        public void GeneratedSceneHasOneDeviceOnlyRoomOwner()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Amiss/Shell.unity", OpenSceneMode.Single);
            var mruks = Object.FindObjectsByType<MRUK>(FindObjectsSortMode.None);
            Assert.AreEqual(1, mruks.Length);
            Assert.AreEqual(MRUK.SceneDataSource.Device, mruks[0].SceneSettings.DataSource);
            Assert.IsFalse(mruks[0].SceneSettings.LoadSceneOnStartup);
            Assert.IsFalse(mruks[0].EnableWorldLock);
            Assert.AreEqual(1, Object.FindObjectsByType<MetaRoomAdapter>(FindObjectsSortMode.None).Length);
            Assert.AreEqual(1, Object.FindObjectsByType<RoomPreparationController>(FindObjectsSortMode.None).Length);
            Assert.AreEqual(1, Object.FindObjectsByType<InstrumentRoot>(FindObjectsSortMode.None).Length);
        }

        [Test]
        public void CameraRigDoesNotRequestScenePermissionAtStartup()
        {
            EditorSceneManager.OpenScene("Assets/Amiss/Shell.unity", OpenSceneMode.Single);
            var manager = Object.FindFirstObjectByType<OVRManager>();
            Assert.IsNotNull(manager);
            var serialized = new SerializedObject(manager);
            Assert.IsFalse(serialized.FindProperty("requestScenePermissionOnStartup").boolValue);
            Assert.AreEqual(OVRManager.TrackingOrigin.FloorLevel, manager.trackingOriginType);
            Assert.IsTrue(manager.isInsightPassthroughEnabled);
            Assert.IsNotNull(Object.FindFirstObjectByType<OVRPassthroughLayer>());
        }

        [Test]
        public void GeneratedSceneHasOneSoftKeyLight()
        {
            EditorSceneManager.OpenScene("Assets/Amiss/Shell.unity", OpenSceneMode.Single);
            var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            Assert.AreEqual(1, lights.Length);
            var key = lights[0];
            Assert.AreEqual(WallKeyLight.Name, key.name);
            Assert.AreEqual(LightType.Directional, key.type);
            Assert.AreEqual(WallKeyLight.Shadows, key.shadows);
            Assert.AreEqual(WallKeyLight.Intensity, key.intensity, 0.0001f);
            Assert.AreEqual(WallKeyLight.Color.r, key.color.r, 0.0001f);
            Assert.AreEqual(WallKeyLight.Color.g, key.color.g, 0.0001f);
            Assert.AreEqual(WallKeyLight.Color.b, key.color.b, 0.0001f);
            Assert.AreEqual(WallKeyLight.Color.a, key.color.a, 0.0001f);
            Assert.Less(Quaternion.Angle(Quaternion.Euler(WallKeyLight.Euler),
                key.transform.localRotation), 0.001f);
            Assert.AreEqual(LightmapBakeType.Realtime, key.lightmapBakeType);
        }

        [Test]
        public void SceneSupportAndPermissionAreDeclaredWithoutCameraAccess()
        {
            var config = OVRProjectConfig.CachedProjectConfig;
            Assert.AreEqual(OVRProjectConfig.FeatureSupport.Required, config.sceneSupport);
            var manifest = File.ReadAllText("Assets/Plugins/Android/AndroidManifest.xml");
            StringAssert.Contains(OVRPermissionsRequester.ScenePermission, manifest);
            StringAssert.DoesNotContain("HEADSET_CAMERA", manifest);
        }

        [Test]
        public void InitialLibraryPanelFacesTheUser()
        {
            EditorSceneManager.OpenScene("Assets/Amiss/Shell.unity", OpenSceneMode.Single);
            var instrument = Object.FindFirstObjectByType<InstrumentRoot>();
            var rig = Object.FindFirstObjectByType<OVRCameraRig>();
            var directionToUser = rig.transform.position - instrument.transform.position;
            directionToUser.y = 0f;
            directionToUser.Normalize();
            Assert.Greater(Vector3.Dot(-instrument.transform.forward, directionToUser), 0.99f);
        }

        [Test]
        public void UnconfirmedLibraryPlacesOnceInFrontOfATrackedEye()
        {
            var panel = new GameObject("LibraryPanel");
            var instrument = panel.AddComponent<InstrumentRoot>();
            var eyeGo = new GameObject("Eye");
            var eye = eyeGo.transform;
            var forward = Quaternion.Euler(0f, 90f, 0f);
            eye.SetPositionAndRotation(new Vector3(4f, 1.7f, -2f), forward);

            Assert.IsTrue(instrument.TryPlaceUnconfirmedLibrary(eye, roomConfirmed: false));
            Assert.Less(Vector3.Distance(new Vector3(5.5f, 1.45f, -2f), panel.transform.position), 0.00001f);
            Assert.Greater(Vector3.Dot(panel.transform.forward, forward * Vector3.forward), 0.999f);

            eye.position = new Vector3(0f, 1.6f, 0f);
            Assert.IsFalse(instrument.TryPlaceUnconfirmedLibrary(eye, roomConfirmed: false));
            Assert.Less(Vector3.Distance(new Vector3(5.5f, 1.45f, -2f), panel.transform.position), 0.00001f);

            Object.DestroyImmediate(eyeGo);
            Object.DestroyImmediate(panel);
        }

        [Test]
        public void UnconfirmedLibraryWaitsOutAnUntrackedEyeThenPlacesOnce()
        {
            var panel = new GameObject("LibraryPanel");
            var instrument = panel.AddComponent<InstrumentRoot>();
            var eyeGo = new GameObject("Eye");
            var eye = eyeGo.transform;
            eye.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            for (var frame = 0; frame < InstrumentRoot.LibraryPlaceFrameBudget - 1; frame++)
                Assert.IsFalse(instrument.TryPlaceUnconfirmedLibrary(eye, roomConfirmed: false));
            Assert.AreEqual(Vector3.zero, panel.transform.position);

            Assert.IsTrue(instrument.TryPlaceUnconfirmedLibrary(eye, roomConfirmed: false));
            Assert.Less(Vector3.Distance(new Vector3(0f, -0.25f, 1.5f), panel.transform.position), 0.00001f);
            Assert.IsFalse(instrument.TryPlaceUnconfirmedLibrary(eye, roomConfirmed: false));

            Object.DestroyImmediate(eyeGo);
            Object.DestroyImmediate(panel);
        }

        [Test]
        public void ConfirmedOrGrabbedLibraryIsNotMovedInFrontOfTheEye()
        {
            var panel = new GameObject("LibraryPanel");
            var instrument = panel.AddComponent<InstrumentRoot>();
            var eyeGo = new GameObject("Eye");
            var eye = eyeGo.transform;
            eye.SetPositionAndRotation(new Vector3(1f, 1.6f, 2f), Quaternion.identity);

            Assert.IsFalse(instrument.TryPlaceUnconfirmedLibrary(eye, roomConfirmed: true));
            Assert.AreEqual(Vector3.zero, panel.transform.position);

            instrument.BeginGrab();
            Assert.IsFalse(instrument.TryPlaceUnconfirmedLibrary(eye, roomConfirmed: false));
            Assert.AreEqual(Vector3.zero, panel.transform.position);
            instrument.EndGrab();
            Assert.IsTrue(instrument.TryPlaceUnconfirmedLibrary(eye, roomConfirmed: false));

            Object.DestroyImmediate(eyeGo);
            Object.DestroyImmediate(panel);
        }

        [Test]
        public void InitialPanelPlacementUsesTheCurrentEyePose()
        {
            var panel = new GameObject("InitialPanel");
            var forward = Quaternion.Euler(0f, 90f, 0f) * Vector3.forward;
            InstrumentRoot.PlaceInitialPanel(panel.transform, new Vector3(4f, 1.7f, -2f), forward);
            Assert.Less(Vector3.Distance(new Vector3(5.5f, 1.45f, -2f), panel.transform.position), 0.00001f);
            Assert.Greater(Vector3.Dot(panel.transform.forward, forward), 0.999f);
            Object.DestroyImmediate(panel);
        }

        [Test]
        public void RightHandRecenterSnapsTheStartupHudToTheCenterEye()
        {
            var hub = new GameObject("StartupHud").transform;
            var eye = new Vector3(0.42f, 1.62f, -0.18f);
            var gaze = Quaternion.Euler(-12f, 28f, 9f) * Vector3.forward;
            var rightHand = eye + new Vector3(0.28f, -0.35f, 0.22f);

            Assert.IsFalse(EpisodeHub.TrySnapToCenterEye(hub, new Vector3(eye.x, 0.1f, eye.z), gaze));
            Assert.AreEqual(Vector3.zero, hub.position);

            Assert.IsTrue(EpisodeHub.TrySnapToCenterEye(hub, eye, gaze));
            Assert.AreEqual(0f, EpisodeHub.LateralMeters(eye, gaze, hub.position), 0.0001f);
            var flat = new Vector3(gaze.x, 0f, gaze.z).normalized;
            var expected = eye + flat * 1.5f;
            expected.y = eye.y - 0.25f;
            Assert.Less(Vector3.Distance(expected, hub.position), 0.0001f);
            Assert.Greater(Vector3.Dot(hub.forward, flat), 0.999f);
            Assert.Greater(Vector3.Distance(hub.position, rightHand), 0.5f);
            Assert.IsTrue(EpisodeHub.RecenterWindowOpen(10, 10));
            Assert.IsTrue(EpisodeHub.RecenterWindowOpen(10, 11));
            Assert.IsFalse(EpisodeHub.RecenterWindowOpen(10, 12));
            Assert.IsFalse(EpisodeHub.RecenterWindowOpen(-1, 11));

            Object.DestroyImmediate(hub.gameObject);
        }

        [Test]
        public void QuickActionsProvideRayPokeAndBothPhysicalModalities()
        {
            EditorSceneManager.OpenScene("Assets/Amiss/Shell.unity", OpenSceneMode.Single);
            Assert.AreEqual(1, Object.FindObjectsByType<RayInteractable>(FindObjectsSortMode.None).Length);
            Assert.AreEqual(1, Object.FindObjectsByType<PokeInteractable>(FindObjectsSortMode.None).Length);
            Assert.AreEqual(2, Object.FindObjectsByType<PointableCanvas>(FindObjectsSortMode.None).Length);
            Assert.AreEqual(1, Object.FindObjectsByType<PointableCanvasModule>(FindObjectsSortMode.None).Length);
            Assert.GreaterOrEqual(Object.FindObjectsByType<HandRef>(FindObjectsSortMode.None).Length, 2);
            Assert.GreaterOrEqual(Object.FindObjectsByType<ControllerRef>(FindObjectsSortMode.None).Length, 2);
            Assert.AreEqual(1, Object.FindObjectsByType<OvrToolInputAdapter>(FindObjectsSortMode.None).Length);
        }

        [Test]
        public void CanvasIsWorldSpaceTmpAtDeclaredScale()
        {
            EditorSceneManager.OpenScene("Assets/Amiss/Shell.unity", OpenSceneMode.Single);
            var canvas = Object.FindFirstObjectByType<Canvas>();
            Assert.AreEqual(RenderMode.WorldSpace, canvas.renderMode);
            Assert.AreEqual(new Vector2(1450, 820), canvas.GetComponent<RectTransform>().sizeDelta);
            Assert.AreEqual(Vector3.one * 0.001f, canvas.GetComponent<RectTransform>().localScale);
            Assert.IsNull(canvas.GetComponent<CanvasScaler>());
            Assert.IsNotNull(TMP_Settings.instance);
            Assert.IsNotNull(TMP_Settings.defaultFontAsset);
            Assert.AreEqual(AtlasPopulationMode.Static, TMP_Settings.defaultFontAsset.atlasPopulationMode);
            Assert.Greater(TMP_Settings.defaultFontAsset.atlasTextures.Length, 0);
            Assert.IsNull(canvas.GetComponent<UnityEngine.UI.Text>());
            var overlay = canvas.GetComponent<OVROverlayCanvas>();
            Assert.IsNotNull(overlay);
            Assert.AreEqual(OVROverlayCanvas.CompositionMode.PunchAHole, overlay.compositionMode);
            Assert.IsTrue(overlay.overlayEnabled);
        }

        [Test]
        public void HubOverlayUsesTheReferenceCurve()
        {
            var go = new GameObject("HubCanvas", typeof(RectTransform), typeof(Canvas));
            var overlay = go.AddComponent<OVROverlayCanvas>();
            overlay.compositionMode = OVROverlayCanvas.CompositionMode.PunchAHole;
            EpisodeHub.CurveOverlay(overlay);
            Assert.AreEqual(OVROverlayCanvas.CanvasShape.Curved, overlay.shape);
            Assert.AreEqual(EpisodeHub.CurveRadiusMeters, overlay.curveRadius);
            Assert.IsFalse(overlay.enable3D);
            Assert.AreEqual(OVROverlayCanvas.CompositionMode.PunchAHole, overlay.compositionMode);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void CylinderHitLandsOnTheCanvasPointThatDrewIt()
        {
            var canvas = new GameObject("canvas").transform;
            canvas.SetPositionAndRotation(new Vector3(2f, 1f, -3f), Quaternion.Euler(0f, 35f, 0f));
            const float arc = -0.752f;
            const float height = 0.11f;
            var angle = arc / EpisodeHub.CurveRadiusMeters;
            var local = new Vector3(
                Mathf.Sin(angle) * EpisodeHub.CurveRadiusMeters,
                height,
                Mathf.Cos(angle) * EpisodeHub.CurveRadiusMeters - EpisodeHub.CurveRadiusMeters);
            var hit = canvas.position + canvas.right * local.x + canvas.up * local.y + canvas.forward * local.z;
            var flat = EpisodeHub.CanvasPointForCylinderHit(canvas, hit, EpisodeHub.CurveRadiusMeters);
            var expected = canvas.position + canvas.right * arc + canvas.up * height;
            Assert.Less(Vector3.Distance(expected, flat), 0.0001f);
            Object.DestroyImmediate(canvas.gameObject);
        }

        [Test]
        public void HubPressesFollowTheCurvedPanel()
        {
            EditorSceneManager.OpenScene("Assets/Amiss/Shell.unity", OpenSceneMode.Single);
            var hub = new GameObject("EpisodeHub").AddComponent<EpisodeHub>();
            LogAssert.Expect(LogType.Log, new Regex(@"\[Amiss\] hub interaction ray=True poke=True curve=1\.75"));
            typeof(EpisodeHub).GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(hub, null);

            var canvas = hub.GetComponentInChildren<Canvas>();
            var overlay = canvas.GetComponent<OVROverlayCanvas>();
            Assert.AreEqual(OVROverlayCanvas.CanvasShape.Curved, overlay.shape);
            Assert.AreEqual(EpisodeHub.CurveRadiusMeters, overlay.curveRadius);
            Assert.AreEqual(OVROverlayCanvas.CompositionMode.PunchAHole, overlay.compositionMode);

            var rayObject = GameObject.Find("HubISDK_RayCanvasInteraction");
            var pokeObject = GameObject.Find("HubISDK_PokeCanvasInteraction");
            Assert.IsNotNull(rayObject);
            Assert.IsNotNull(pokeObject);
            var walls = hub.transform.Find("HubCanvas/Panel/Walls");
            Assert.IsNotNull(walls);
            Assert.IsNotNull(walls.GetComponent<Button>());
            Assert.AreEqual("WALLS ON", walls.GetComponentInChildren<TMP_Text>().text);
            var surface = rayObject.GetComponent<RayInteractable>().Surface as ClippedCylinderSurface;
            Assert.IsNotNull(surface);
            Assert.AreSame(surface, pokeObject.GetComponent<PokeInteractable>().SurfacePatch);
            Assert.IsInstanceOf<HubCurvePointer>(rayObject.GetComponent<RayInteractable>().PointableElement);
            Assert.IsInstanceOf<HubCurvePointer>(pokeObject.GetComponent<PokeInteractable>().PointableElement);
            Assert.AreEqual(CylinderSurface.NormalFacing.In, surface.Cylinder.GetComponent<CylinderSurface>().Facing);
            Assert.AreEqual(EpisodeHub.CurveRadiusMeters, surface.Cylinder.Radius, 0.0001f);
            Assert.AreEqual(EpisodeHub.OverlaySourceLayer, canvas.gameObject.layer);
            foreach (var graphic in canvas.GetComponentsInChildren<Graphic>(true))
                Assert.AreEqual(EpisodeHub.OverlaySourceLayer, graphic.gameObject.layer,
                    graphic.name + " stays on the source layer");

            var eyeObject = new GameObject("Eye");
            var eye = eyeObject.AddComponent<Camera>();
            var listener = eyeObject.GetComponent<AudioListener>();
            if (listener != null)
                Object.DestroyImmediate(listener);
            eye.cullingMask = ~0;
            EpisodeHub.ExcludeFlatSource(eye);
            Assert.Zero(eye.cullingMask & (1 << EpisodeHub.OverlaySourceLayer));
            Object.DestroyImmediate(eyeObject);

            Object.DestroyImmediate(hub.gameObject);
        }
    }
}
