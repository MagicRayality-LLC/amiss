using System.Collections;
using Amiss.Shell;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Amiss.PlayModeTests
{
    public class InteractionSceneSmokeTests
    {
        [UnityTest]
        public IEnumerator GeneratedSceneStartsWithCleanInteractionWiring()
        {
            yield return SceneManager.LoadSceneAsync("Shell", LoadSceneMode.Single);
            for (var frame = 0; frame < 10; frame++)
                yield return null;

            Assert.AreEqual(2, Object.FindObjectsByType<RayInteractable>(FindObjectsSortMode.None).Length);
            Assert.AreEqual(2, Object.FindObjectsByType<PokeInteractable>(FindObjectsSortMode.None).Length);
            Assert.GreaterOrEqual(Object.FindObjectsByType<HandRef>(FindObjectsSortMode.None).Length, 2);
            Assert.GreaterOrEqual(Object.FindObjectsByType<ControllerRef>(FindObjectsSortMode.None).Length, 2);
            Assert.IsNotNull(Object.FindFirstObjectByType<ShellServices>().Panel);
            Assert.Greater(Object.FindObjectsByType<TMP_Text>(FindObjectsSortMode.None).Length, 0);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
