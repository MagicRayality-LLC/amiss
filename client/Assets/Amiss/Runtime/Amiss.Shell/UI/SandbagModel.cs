using UnityEngine;
using UnityEngine.Rendering;

namespace Amiss.Shell
{
    /// <summary>The mounted floating sandbag: its visual root and hover driver.</summary>
    public sealed class SandbagView
    {
        public SandbagView(Transform root, SandbagHover hover)
        {
            Root = root;
            Hover = hover;
        }

        public Transform Root { get; }
        public SandbagHover Hover { get; }
    }

    /// <summary>
    /// The founder-supplied cloth bag flown as the P0 slowing tool: linen
    /// body, rope drawstring, teal star inlay. Built at runtime from Resources
    /// so no scene prefab edit is needed; the same URP Lit treatment as the
    /// wall set.
    ///
    /// The bag floats: it has no collider and no rigidbody, so nothing about
    /// it can fall, collide, or push. The scientific writer owns the hover
    /// absorber region and the one absorption; this visual only hovers where
    /// the tool placed it and disperses where the tool absorbed.
    /// </summary>
    public static class SandbagModel
    {
        public const string PrefabResource = "Sandbag/Sandbag";

        /// <summary>
        /// Nominal full-scale absorption for dispersal intensity: a 1 kg ball
        /// at 1 m/s damped by the declared factor keeps a quarter of its
        /// energy, so 0.375 J is absorbed. Prototype scale reference only,
        /// labelled uncalibrated with the tool parameters.
        /// </summary>
        public const double ReferenceAbsorptionJ = 0.375;

        /// <summary>All dispersal systems together never exceed this many live particles.</summary>
        public const int DispersalParticleBudget = 160;

        /// <summary>SRD 07 section 4.2 dispersal palette: soft pastel only, never fire or hot glow.</summary>
        public static readonly Color PastelTeal = new(0.36f, 0.88f, 0.90f);
        public static readonly Color PastelPink = new(0.95f, 0.65f, 0.85f);
        public static readonly Color PastelLilac = new(0.75f, 0.60f, 0.95f);

        public static bool TryMount(Transform host, out SandbagView view)
        {
            view = null;
            if (host == null)
                return false;
            var prefab = Resources.Load<GameObject>(PrefabResource);
            if (prefab == null)
            {
                Debug.LogWarning("[Amiss] sandbag model missing");
                return false;
            }

            var visual = Object.Instantiate(prefab, host, false);
            visual.name = "Sandbag";
            // The bag floats: strip every physics body so the visual can never
            // fall, collide, or push. Absorption lives in the writer region.
            foreach (var collider in visual.GetComponentsInChildren<Collider>(true))
                DestroyObject(collider);
            foreach (var body in visual.GetComponentsInChildren<Rigidbody>(true))
                DestroyObject(body);
            if (!AssignMaterials(visual))
            {
                DestroyObject(visual);
                return false;
            }

            var hover = visual.AddComponent<SandbagHover>();
            view = new SandbagView(visual.transform, hover);
            Debug.Log("[Amiss] sandbag mounted");
            return true;
        }

        /// <summary>
        /// Soft dispersal at the absorption point: pastel stardust plus one
        /// expanding glow disc, intensity-scaled by the measured absorbed
        /// joules. No fire, no hot glow, no colliders, no impulse — pure
        /// presentation, cleaned up a few seconds later.
        /// </summary>
        public static void TriggerDispersal(Vector3 positionM, double absorbedJ)
        {
            if (!float.IsFinite(positionM.x + positionM.y + positionM.z))
                return;
            if (!double.IsFinite(absorbedJ) || absorbedJ < 0.0)
                absorbedJ = 0.0;
            var intensity = Mathf.Clamp01((float)(absorbedJ / ReferenceAbsorptionJ));

            var root = new GameObject("SandbagDispersal");
            root.transform.position = positionM;
            BuildStardust(root.transform, intensity);
            BuildGlowDisc(root.transform, intensity);
            DestroyObject(root, 2.6f);
        }

        static void BuildStardust(Transform parent, float intensity)
        {
            var go = new GameObject("Stardust");
            go.transform.SetParent(parent, false);
            var burst = go.AddComponent<ParticleSystem>();
            var main = burst.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 0.5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 0.5f + 1.5f * intensity);
            main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.04f);
            main.startColor = PastelTeal;
            main.gravityModifier = 0.35f;
            main.maxParticles = 110;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = burst.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, (short)(24 + 86 * intensity))
            });

            var shape = burst.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.02f;

            var overLifetime = burst.colorOverLifetime;
            overLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 1f, 1f), 0f),
                    new GradientColorKey(PastelTeal, 0.35f),
                    new GradientColorKey(PastelPink, 0.7f),
                    new GradientColorKey(PastelLilac, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.12f),
                    new GradientAlphaKey(0.8f, 0.55f),
                    new GradientAlphaKey(0f, 1f)
                });
            overLifetime.color = gradient;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var material = AdditiveSprite(LoadTex("Star_Sprite") ?? LoadTex("Confetti_Sprite"));
            if (material != null)
                renderer.material = material;
            if (Application.isPlaying)
                burst.Play(true);
        }

        static void BuildGlowDisc(Transform parent, float intensity)
        {
            var go = new GameObject("GlowDisc");
            go.transform.SetParent(parent, false);
            var disc = go.AddComponent<ParticleSystem>();
            var main = disc.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 0.6f;
            main.startLifetime = 0.6f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.10f + 0.08f * intensity,
                0.16f + 0.12f * intensity);
            main.startColor = new Color(0.75f, 0.95f, 1f, 0.45f);
            main.gravityModifier = 0f;
            main.maxParticles = 2;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = disc.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)1) });

            var size = disc.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                AnimationCurve.EaseInOut(0f, 0.4f, 1f, 2.6f));

            var overLifetime = disc.colorOverLifetime;
            overLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(PastelTeal, 0f) },
                new[]
                {
                    new GradientAlphaKey(0.45f, 0f),
                    new GradientAlphaKey(0f, 1f)
                });
            overLifetime.color = gradient;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var sprite = LoadTex("Glow_Sprite");
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                         ?? Shader.Find("Particles/Standard Unlit")
                         ?? Shader.Find("Sprites/Default");
            if (shader != null && sprite != null)
            {
                var material = new Material(shader);
                if (material.HasProperty("_BaseMap"))
                    material.SetTexture("_BaseMap", sprite);
                if (material.HasProperty("_MainTex"))
                    material.SetTexture("_MainTex", sprite);
                material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                renderer.material = material;
            }
            if (Application.isPlaying)
                disc.Play(true);
        }

        static Material AdditiveSprite(Texture2D sprite)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                         ?? Shader.Find("Particles/Standard Unlit")
                         ?? Shader.Find("Mobile/Particles/Additive")
                         ?? Shader.Find("Sprites/Default");
            if (shader == null || sprite == null)
                return null;
            var material = new Material(shader);
            if (material.HasProperty("_BaseMap"))
                material.SetTexture("_BaseMap", sprite);
            if (material.HasProperty("_MainTex"))
                material.SetTexture("_MainTex", sprite);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", Color.white);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.One);
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.EnableKeyword("_ALPHABLEND_ON");
            return material;
        }

        static bool AssignMaterials(GameObject visual)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning("[Amiss] sandbag lit shader missing");
                return false;
            }

            var fabric = Lit(shader, LoadTex("Fabric_Albedo"), LoadTex("Fabric_Normal"),
                0.25f, 0f, Color.white);
            var rope = Lit(shader, LoadTex("Rope_Albedo"), LoadTex("Rope_Normal"),
                0.15f, 0f, Color.white);
            // Cool teal brand inlay only; its emission is a static accent, not
            // a heat reading, and the dispersal never uses hot colors.
            var teal = Lit(shader, LoadTex("TealAccent_Albedo"), null,
                0.4f, 0.6f, PastelTeal);
            if (fabric == null || rope == null)
                return false;

            foreach (var renderer in visual.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.sharedMaterial = MaterialFor(renderer.name, fabric, rope, teal);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            return true;
        }

        static Material MaterialFor(string name, Material fabric, Material rope, Material teal)
        {
            var n = name.ToLowerInvariant();
            if (n.Contains("drawstring") || n.Contains("draw") || n.Contains("string") ||
                n.Contains("cord") || n.Contains("rope") || n.Contains("bow"))
                return rope;
            if (n.Contains("star") || n.Contains("teal") || n.Contains("accent") ||
                n.Contains("embroider"))
                return teal;
            return fabric;
        }

        static Material Lit(Shader shader, Texture2D albedo, Texture2D normal,
            float smoothness, float emission, Color tint)
        {
            if (albedo == null)
                return null;
            var material = new Material(shader);
            material.SetTexture("_BaseMap", albedo);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", 0f);
            if (normal != null)
            {
                material.SetTexture("_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
                material.SetFloat("_BumpScale", 1f);
            }
            material.SetTexture("_EmissionMap", albedo);
            material.SetColor("_EmissionColor", tint * emission);
            if (emission > 0f)
                material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return material;
        }

        static Texture2D LoadTex(string name) =>
            Resources.Load<Texture2D>("Sandbag/" + name);

        static void DestroyObject(Object target, float delay = 0f)
        {
            if (target == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(target, delay);
            else
                Object.DestroyImmediate(target);
        }
    }

    /// <summary>
    /// Visual-only hover: a gentle bob and slow yaw around the placed anchor.
    /// It moves no physics and owns no region; the writer resolves impacts.
    /// </summary>
    public sealed class SandbagHover : MonoBehaviour
    {
        Vector3 baseLocalPosition;

        void Awake() => baseLocalPosition = transform.localPosition;

        void Update()
        {
            transform.localPosition = baseLocalPosition +
                                      Vector3.up * (Mathf.Sin(Time.time * 3.77f) * 0.02f);
            transform.Rotate(Vector3.up, 15f * Time.deltaTime, Space.Self);
        }
    }
}
