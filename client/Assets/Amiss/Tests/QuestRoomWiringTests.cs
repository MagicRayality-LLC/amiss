using System.IO;
using System.Linq;
using Amiss.Shell;
using Amiss.Shell.Room;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

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
            Assert.IsTrue(mruks[0].EnableWorldLock);
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
    }
}
