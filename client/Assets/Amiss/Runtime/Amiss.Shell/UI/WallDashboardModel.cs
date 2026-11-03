using Amiss.Shell.Room;
using UnityEngine;
using UnityEngine.Rendering;

namespace Amiss.Shell
{
    /// <summary>
    /// The founder-supplied wall display: a matte bezel, teal hairline and
    /// status lamp, and a powder-coat PA speaker with a circular grille.
    /// The glass is scaled to the 16:9 dashboard and the placeholder screen
    /// mesh is hidden so the live panel is the glass. The speaker stays
    /// above that glass, inside the same root.
    /// </summary>
    public static class WallDashboardModel
    {
        public const string PrefabResource = "WallDashboard/WallDashboard";

        public static bool TryMount(Transform chromeRoot, out Transform speaker)
        {
            speaker = null;
            if (chromeRoot == null)
                return false;
            var prefab = Resources.Load<GameObject>(PrefabResource);
            if (prefab == null)
            {
                Debug.LogWarning("[Amiss] wall dashboard model missing");
                return false;
            }

            var visual = Object.Instantiate(prefab, chromeRoot, false);
            visual.name = "WallDashboard";
            StripColliders(visual);
            if (!AssignMaterials(visual))
            {
                DestroyObject(visual);
                return false;
            }

            var screen = FindNamed(visual.transform, "Display_Screen");
            var screenRenderer = screen != null ? screen.GetComponent<MeshRenderer>() : null;
            if (screenRenderer == null || !FitGlass(visual.transform, screenRenderer))
            {
                DestroyObject(visual);
                return false;
            }

            screenRenderer.enabled = false;
            var speakerTransform = FindNamed(visual.transform, "PA_Speaker_Body");
            if (speakerTransform == null)
                speakerTransform = FindNamed(visual.transform, "PA_Speaker");
            if (speakerTransform == null)
                speakerTransform = visual.transform;
            var audio = speakerTransform.gameObject.AddComponent<AudioSource>();
            audio.spatialBlend = 1f;
            audio.playOnAwake = false;
            audio.dopplerLevel = 0f;
            audio.rolloffMode = AudioRolloffMode.Logarithmic;
            audio.minDistance = 0.4f;
            audio.maxDistance = 8f;
            speaker = speakerTransform;
            Debug.Log("[Amiss] wall dashboard mounted scale=" +
                      visual.transform.localScale.x.ToString("F3"));
            return true;
        }

        static bool FitGlass(Transform visual, Renderer screen)
        {
            visual.localPosition = Vector3.zero;
            visual.localRotation = Quaternion.identity;
            visual.localScale = Vector3.one;
            if (!TryBounds(visual.parent, screen, out var bounds))
                return false;
            visual.localRotation = UprightRotation(bounds.size);
            if (!TryBounds(visual.parent, screen, out bounds))
                return false;
            if (bounds.size.y < 0.2f || bounds.size.y > 3f)
                return false;
            visual.localScale = Vector3.one * (PanelMount.ScreenHeight / bounds.size.y);
            if (!CenterOnGlass(visual, screen))
                return false;

            var bezel = FindNamed(visual, "Display_Bezel");
            var bezelRenderer = bezel != null ? bezel.GetComponent<Renderer>() : null;
            if (bezelRenderer != null &&
                TryBounds(visual.parent, bezelRenderer, out var bezelBounds) &&
                bezelBounds.center.z < -0.001f)
                TurnAboutGlass(visual, Quaternion.Euler(0f, 180f, 0f));

            var body = FindNamed(visual, "PA_Speaker_Body");
            var bodyRenderer = body != null ? body.GetComponent<Renderer>() : null;
            if (bodyRenderer != null &&
                TryBounds(visual.parent, bodyRenderer, out var bodyBounds) &&
                bodyBounds.center.y < 0f)
                TurnAboutGlass(visual, Quaternion.Euler(0f, 0f, 180f));

            return TryBounds(visual.parent, screen, out bounds) &&
                   Mathf.Abs(bounds.size.y - PanelMount.ScreenHeight) < 0.05f &&
                   bounds.size.x > bounds.size.y;
        }

        /// <summary>
        /// Rotates the whole display about the parent origin. The glass is
        /// already centered there, so it stays put while the speaker and the
        /// bezel swap sides.
        /// </summary>
        static void TurnAboutGlass(Transform visual, Quaternion turn)
        {
            visual.localPosition = turn * visual.localPosition;
            visual.localRotation = turn * visual.localRotation;
        }

        static bool CenterOnGlass(Transform visual, Renderer screen)
        {
            if (!TryBounds(visual.parent, screen, out var bounds))
                return false;
            visual.localPosition -= bounds.center;
            return true;
        }

        /// <summary>
        /// Turns the glass so its wide axis is X, its tall axis is Y, and
        /// its thickness is Z. Later flips correct facing and speaker side.
        /// </summary>
        static Quaternion UprightRotation(Vector3 size)
        {
            var wide = LargestAxis(size);
            var thick = SmallestAxis(size);
            var high = 3 - wide - thick;
            if (wide == 0 && high == 1 && thick == 2)
                return Quaternion.identity;
            if (wide == 0 && high == 2 && thick == 1)
                return Quaternion.Euler(-90f, 0f, 0f);
            if (wide == 1 && high == 0 && thick == 2)
                return Quaternion.Euler(0f, 0f, 90f);
            if (wide == 1 && high == 2 && thick == 0)
                return Quaternion.Euler(90f, 0f, 90f);
            if (wide == 2 && high == 1 && thick == 0)
                return Quaternion.Euler(0f, 90f, 0f);
            if (wide == 2 && high == 0 && thick == 1)
                return Quaternion.Euler(-90f, 90f, 0f);
            return Quaternion.identity;
        }

        static int LargestAxis(Vector3 size)
        {
            if (size.y >= size.x && size.y >= size.z)
                return 1;
            return size.z > size.x ? 2 : 0;
        }

        static int SmallestAxis(Vector3 size)
        {
            if (size.y <= size.x && size.y <= size.z)
                return 1;
            return size.z < size.x ? 2 : 0;
        }

        static bool TryBounds(Transform space, Renderer renderer, out Bounds bounds)
        {
            bounds = default;
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (space == null || mesh == null)
                return false;
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            var center = mesh.bounds.center;
            var extents = mesh.bounds.extents;
            for (var i = 0; i < 8; i++)
            {
                var corner = center + Vector3.Scale(extents, new Vector3(
                    (i & 1) == 0 ? -1f : 1f,
                    (i & 2) == 0 ? -1f : 1f,
                    (i & 4) == 0 ? -1f : 1f));
                var local = space.InverseTransformPoint(renderer.transform.TransformPoint(corner));
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }
            bounds = new Bounds((min + max) * 0.5f, max - min);
            return true;
        }

        static bool AssignMaterials(GameObject visual)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning("[Amiss] wall dashboard lit shader missing");
                return false;
            }

            var bezel = Lit(shader, LoadTex("Bezel_Albedo"), LoadTex("Bezel_Normal"),
                0.25f, 0f, 0.45f, Color.white);
            var body = Lit(shader, LoadTex("PA_Body_Albedo"), LoadTex("PA_Body_Normal"),
                0.42f, 0.05f, 0.55f, Color.white);
            var grille = Lit(shader, LoadTex("PA_Grille_Albedo"), LoadTex("PA_Grille_Normal"),
                0.35f, 0.65f, 0.4f, Color.white);
            var teal = Lit(shader, LoadTex("Teal_Emission"), null,
                0.2f, 0f, 2.2f, new Color(0.361f, 0.882f, 0.902f));
            var led = Lit(shader, LoadTex("LED_Emission"), null,
                0.15f, 0f, 4f, new Color(0.361f, 0.882f, 0.902f));
            if (bezel == null || body == null || grille == null)
                return false;

            foreach (var renderer in visual.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.sharedMaterial = MaterialFor(renderer.name, bezel, body, grille, teal, led);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            return true;
        }

        static Material MaterialFor(string name, Material bezel, Material body, Material grille,
            Material teal, Material led)
        {
            var n = name.ToLowerInvariant();
            if (n.Contains("led"))
                return led;
            if (n.Contains("hairline") || n.Contains("teal") || n.Contains("accent"))
                return teal;
            if (n.Contains("grille") || n.Contains("grill"))
                return grille;
            if (n.Contains("bezel") || n.Contains("screen") || n.Contains("display"))
                return bezel;
            return body;
        }

        static Material Lit(Shader shader, Texture2D albedo, Texture2D normal, float smoothness,
            float metallic, float emission, Color emissionTint)
        {
            if (albedo == null)
                return null;
            var material = new Material(shader);
            material.SetTexture("_BaseMap", albedo);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", metallic);
            if (normal != null)
            {
                material.SetTexture("_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
                material.SetFloat("_BumpScale", 1f);
            }
            material.SetTexture("_EmissionMap", albedo);
            material.SetColor("_EmissionColor", emissionTint * emission);
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return material;
        }

        static Texture2D LoadTex(string name) =>
            Resources.Load<Texture2D>("WallDashboard/" + name);

        static Transform FindNamed(Transform root, string name)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform.name == name)
                    return transform;
            }
            return null;
        }

        static void StripColliders(GameObject visual)
        {
            foreach (var collider in visual.GetComponentsInChildren<Collider>(true))
                DestroyObject(collider);
        }

        static void DestroyObject(Object target)
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
