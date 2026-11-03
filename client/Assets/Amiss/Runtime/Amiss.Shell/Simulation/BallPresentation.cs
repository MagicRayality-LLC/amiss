using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// Presentation-only spheres. They read immutable published views and
    /// interpolate between two of them; they hold no rigidbody, collider, or
    /// callback into scientific state, so a render object can never become a
    /// second writer.
    /// </summary>
    public sealed class BallPresentation
    {
        /// <summary>
        /// The shader the ball material is built from. A player build keeps a
        /// shader only when an asset references it, so the balls reuse the one
        /// the imported instrument models already carry.
        /// </summary>
        public const string BallShaderName = "Universal Render Pipeline/Lit";

        static readonly Color BallColor = new(0.95f, 0.72f, 0.25f, 1f);

        GameObject root;
        Mesh mesh;
        Material material;
        int ballCount = -1;

        public int BallCount => ballCount < 0 ? 0 : ballCount;
        public bool IsBuilt => root != null;
        public GameObject Root => root;

        public void Sync(TrialStateView previous, TrialStateView current, float alpha)
        {
            if (current == null || current.Balls.Count == 0)
            {
                Clear();
                return;
            }
            if (root == null || current.Balls.Count != ballCount)
                Build(current);

            var blend = Mathf.Clamp01(alpha);
            for (var index = 0; index < ballCount && index < current.Balls.Count; index++)
            {
                var now = current.Balls[index].PositionM;
                var visual = root.transform.GetChild(index);
                if (previous != null && index < previous.Balls.Count)
                    visual.position = Vector3.Lerp(previous.Balls[index].PositionM, now, blend);
                else
                    visual.position = now;
            }
        }

        void Build(TrialStateView view)
        {
            Clear();
            ballCount = view.Balls.Count;
            var diameter = Mathf.Max(0.01f, view.Balls[0].RadiusM * 2f);

            var template = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            template.name = "AmissBallTemplate";
            mesh = template.GetComponent<MeshFilter>().sharedMesh;
            material = CreateBallMaterial(template.GetComponent<MeshRenderer>().sharedMaterial);
            DestroyNow(template);

            root = new GameObject("AmissBallPresentation");
            for (var index = 0; index < view.Balls.Count; index++)
            {
                var ball = new GameObject(view.Balls[index].EntityId);
                ball.transform.SetParent(root.transform, false);
                ball.transform.localScale = Vector3.one * diameter;
                ball.transform.position = view.Balls[index].PositionM;
                var filter = ball.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var renderer = ball.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            Debug.Log("[Amiss] balls built count=" + ballCount +
                      " diameter=" + diameter.ToString("F3") +
                      " shader=" + (material != null && material.shader != null
                          ? material.shader.name
                          : "null"));
        }

        /// <summary>
        /// Builds the one ball material. A runtime <see cref="Shader.Find"/> by
        /// name reaches a shader in a player build only when an asset keeps it;
        /// the imported instrument models carry their lit material, while the
        /// unlit pipeline shader is referenced by nothing and is stripped. A
        /// ball that fell back to the primitive's built-in material drew
        /// nothing under this pipeline, which is why a running Trial showed no
        /// balls on device. The emission keeps the ball readable in a room lit
        /// by one wall light.
        /// </summary>
        static Material CreateBallMaterial(Material primitiveMaterial)
        {
            var shader = Shader.Find(BallShaderName);
            if (shader == null)
            {
                Debug.LogError("[Amiss] ball shader missing from this build: " + BallShaderName);
                return primitiveMaterial != null ? new Material(primitiveMaterial) : null;
            }

            var created = new Material(shader);
            if (created.HasProperty("_BaseColor"))
                created.SetColor("_BaseColor", BallColor);
            if (created.HasProperty("_Color"))
                created.SetColor("_Color", BallColor);
            if (created.HasProperty("_EmissionColor"))
            {
                created.EnableKeyword("_EMISSION");
                created.SetColor("_EmissionColor", BallColor);
            }
            return created;
        }

        public void Clear()
        {
            if (root != null)
            {
                DestroyNow(root);
                root = null;
            }
            if (material != null)
            {
                DestroyNow(material);
                material = null;
            }
            mesh = null;
            ballCount = -1;
        }

        /// <summary>
        /// Play mode defers destruction to the end of the frame; edit mode has
        /// no frame to defer to, and its test and harness consumers would
        /// otherwise log an error on every rebuild.
        /// </summary>
        static void DestroyNow(Object target)
        {
            if (target == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(target);
            else
                Object.DestroyImmediate(target);
        }
    }
}
