using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Amiss.Shell
{
    public sealed class AmissProbeRuntime : MonoBehaviour
    {
        IEnumerator Start()
        {
            yield return new WaitForSeconds(2f);
            var output = new StringBuilder();
            output.AppendLine("[AmissProbe] runtime");
            var cameras = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            output.AppendLine($"cameras={cameras.Length};xrEnabled={XRSettings.enabled};pipeline={RenderPipelineManager.currentPipeline}");
            foreach (var camera in cameras)
            {
                output.AppendLine($"camera={camera.name};enabled={camera.enabled};active={camera.gameObject.activeInHierarchy};" +
                    $"tag={camera.tag};mask={camera.cullingMask};target={camera.targetTexture};" +
                    $"clear={camera.clearFlags};background={camera.backgroundColor};" +
                    $"pos={camera.transform.position};forward={camera.transform.forward};" +
                    $"near={camera.nearClipPlane};far={camera.farClipPlane};ortho={camera.orthographic};" +
                    $"projection={camera.projectionMatrix};worldToCamera={camera.worldToCameraMatrix}");
            }
            var renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            foreach (var renderer in renderers)
            {
                output.AppendLine($"renderer={renderer.name};enabled={renderer.enabled};active={renderer.gameObject.activeInHierarchy};" +
                    $"visible={renderer.isVisible};layer={renderer.gameObject.layer};pos={renderer.transform.position};bounds={renderer.bounds};" +
                    $"shader={(renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null ? renderer.sharedMaterial.shader.name : "null")}");
            }
            var canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            foreach (var canvas in canvases)
                output.AppendLine($"canvas={canvas.name};enabled={canvas.enabled};active={canvas.gameObject.activeInHierarchy};" +
                    $"layer={canvas.gameObject.layer};pos={canvas.transform.position};scale={canvas.transform.lossyScale};mode={canvas.renderMode}");
            Debug.Log(output.ToString());
        }
    }
}
