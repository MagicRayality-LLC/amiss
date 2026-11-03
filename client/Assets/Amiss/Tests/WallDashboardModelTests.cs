using Amiss.Shell;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Amiss.Tests
{
    public class WallDashboardModelTests
    {
        [Test]
        public void ReferenceDashboardMountsTheSpeakerAboveTheGlass()
        {
            AssetDatabase.ImportAsset(
                "Assets/Amiss/Art/WallDashboard",
                ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);

            var chrome = new GameObject("TvChrome");
            Assert.IsTrue(WallDashboardModel.TryMount(chrome.transform, out var speaker));
            Assert.IsNotNull(speaker);
            var speakerRenderer = speaker.GetComponent<Renderer>();
            Assert.IsNotNull(speakerRenderer);
            Assert.Greater(speakerRenderer.bounds.center.y, chrome.transform.position.y + 0.25f);

            var audio = speaker.GetComponent<AudioSource>();
            Assert.IsNotNull(audio);
            Assert.AreEqual(1f, audio.spatialBlend);
            Assert.IsFalse(audio.playOnAwake);

            MeshRenderer screen = null;
            MeshRenderer grille = null;
            foreach (var renderer in chrome.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.name == "Display_Screen")
                    screen = renderer;
                if (renderer.name == "PA_Speaker_Grille")
                    grille = renderer;
            }

            Assert.IsNotNull(screen);
            Assert.IsFalse(screen.enabled);
            Assert.IsNotNull(grille);
            Assert.IsTrue(grille.enabled);
            Assert.IsNull(chrome.GetComponentInChildren<Collider>());
            Object.DestroyImmediate(chrome);
        }
    }
}
