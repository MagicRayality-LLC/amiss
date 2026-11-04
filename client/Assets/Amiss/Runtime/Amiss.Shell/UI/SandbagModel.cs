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
    /// the tool placed it and celebrates where the tool absorbed.
    ///
    /// Dispersal is the reference celebratory firework by founder decision
    /// 2026-11-04 (recorded in `private/design/next.md`): fun, not a
    /// temperature reading.
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
        public const int DispersalParticleBudget = 280;

        /// <summary>SRD 07 section 4.2 dispersal palette: soft pastel only, never fire or hot glow.</summary>
        public static readonly Color PastelTeal = new(0.36f, 0.88f, 0.90f);
        public static readonly Color PastelPink = new(0.95f, 0.65f, 0.85f);
        public static readonly Color PastelLilac = new(0.75f, 0.60f, 0.95f);

        public static bool TryMount(Transform host, out SandbagView view,
            bool hover = true, bool centerOnHost = false)
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

            if (centerOnHost)
                CenterVisual(visual, host);
            var hoverDriver = hover ? visual.AddComponent<SandbagHover>() : null;
            view = new SandbagView(visual.transform, hoverDriver);
            Debug.Log("[Amiss] sandbag mounted");
            return true;
        }

        /// <summary>
        /// Shifts the visual so its combined bounds centre sits on the host
        /// origin. The grabbable bag's host position is the hover anchor, so
        /// the model centre and the armed region must agree.
        /// </summary>
        static void CenterVisual(GameObject visual, Transform host)
        {
            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return;
            var bounds = renderers[0].bounds;
            for (var index = 1; index < renderers.Length; index++)
                bounds.Encapsulate(renderers[index].bounds);
            visual.transform.localPosition -= host.InverseTransformPoint(bounds.center);
        }

        /// <summary>
        /// Celebratory firework at the absorption point: a chrysanthemum
        /// burst with trails, a core flash, falling glitter, and pastel
        /// confetti, reusing the reference firework arts and scaled by the
        /// measured absorbed joules. Founder decision 2026-11-04 (recorded
        /// in `private/design/next.md`): this is for fun and does not claim
        /// the bag measures temperature. Pure presentation: no colliders, no
        /// impulse, cleaned up a few seconds later. Returns the transient
        /// root (null for invalid inputs) so tests can inspect the debris
        /// they must then destroy.
        /// </summary>
        public static GameObject TriggerDispersal(Vector3 positionM, double absorbedJ)
        {
            if (!float.IsFinite(positionM.x + positionM.y + positionM.z))
                return null;
            if (!double.IsFinite(absorbedJ) || absorbedJ < 0.0)
                absorbedJ = 0.0;
            var intensity = Mathf.Clamp01((float)(absorbedJ / ReferenceAbsorptionJ));

            var root = new GameObject("SandbagDispersal");
            root.transform.position = positionM;
            BuildBurst(root.transform, intensity);
            BuildCoreFlash(root.transform, intensity);
            BuildGlitter(root.transform, intensity);
            BuildConfetti(root.transform, intensity);
            DestroyObject(root, 3.5f);
            return root;
        }

        /// <summary>Main chrysanthemum burst with per-particle trails.</summary>
        static void BuildBurst(Transform parent, float intensity)
        {
            var go = new GameObject("Burst");
            go.transform.SetParent(parent, false);
            var burst = go.AddComponent<ParticleSystem>();
            var main = burst.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 0.4f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.6f, 2.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.6f, 4.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.035f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.82f, 0.35f, 1f),
                new Color(1f, 0.55f, 0.18f, 1f));
            main.gravityModifier = 0.45f;
            main.maxParticles = 170;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = burst.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, (short)(36 + 64 * intensity)),
                new ParticleSystem.Burst(0.04f, (short)(18 + 27 * intensity)),
                new ParticleSystem.Burst(0.1f, (short)(9 + 16 * intensity))
            });

            var shape = burst.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.015f;
            shape.radiusThickness = 1f;

            var overLifetime = burst.colorOverLifetime;
            overLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.95f, 0.75f), 0f),
                    new GradientColorKey(new Color(1f, 0.72f, 0.28f), 0.22f),
                    new GradientColorKey(PastelTeal, 0.55f),
                    new GradientColorKey(PastelPink, 0.78f),
                    new GradientColorKey(PastelLilac, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.95f, 0.25f),
                    new GradientAlphaKey(0.55f, 0.65f),
                    new GradientAlphaKey(0f, 1f)
                });
            overLifetime.color = gradient;

            var size = burst.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                AnimationCurve.EaseInOut(0f, 1f, 1f, 0.05f));

            var trails = burst.trails;
            trails.enabled = true;
            trails.mode = ParticleSystemTrailMode.PerParticle;
            trails.lifetime = 0.22f;
            trails.ratio = 0.85f;
            trails.minVertexDistance = 0.012f;
            trails.dieWithParticles = true;
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(1f,
                AnimationCurve.Linear(0f, 1f, 1f, 0f));
            trails.colorOverLifetime = new ParticleSystem.MinMaxGradient(gradient);
            trails.colorOverTrail = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.85f, 0.4f, 0.9f),
                PastelTeal);

            var noise = burst.noise;
            noise.enabled = true;
            noise.strength = 0.22f;
            noise.frequency = 0.45f;
            noise.scrollSpeed = 0.2f;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 2.8f;
            renderer.velocityScale = 0.12f;
            renderer.cameraVelocityScale = 0f;
            renderer.alignment = ParticleSystemRenderSpace.View;
            var material = AdditiveSprite(LoadTex("Spark_Sprite") ?? LoadTex("Streak_Sprite"));
            if (material != null)
            {
                renderer.material = material;
                renderer.trailMaterial = material;
            }
            if (Application.isPlaying)
                burst.Play(true);
        }

        /// <summary>Soft white-gold core flash at ignition.</summary>
        static void BuildCoreFlash(Transform parent, float intensity)
        {
            var go = new GameObject("CoreFlash");
            go.transform.SetParent(parent, false);
            var flash = go.AddComponent<ParticleSystem>();
            var main = flash.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 0.25f;
            main.startLifetime = 0.35f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(
                0.14f + 0.08f * intensity, 0.22f + 0.12f * intensity);
            main.startColor = new Color(1f, 0.92f, 0.7f, 0.95f);
            main.gravityModifier = 0f;
            main.maxParticles = 4;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = flash.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)2) });

            var overLifetime = flash.colorOverLifetime;
            overLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.95f, 0.8f), 0f),
                    new GradientColorKey(new Color(1f, 0.7f, 0.3f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f)
                });
            overLifetime.color = gradient;

            var size = flash.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                AnimationCurve.EaseInOut(0f, 0.6f, 1f, 1.8f));

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var material = AdditiveSprite(LoadTex("Glow_Sprite"));
            if (material != null)
                renderer.material = material;
            if (Application.isPlaying)
                flash.Play(true);
        }

        /// <summary>Falling glitter twinkles that outlive the burst.</summary>
        static void BuildGlitter(Transform parent, float intensity)
        {
            var go = new GameObject("Glitter");
            go.transform.SetParent(parent, false);
            var glitter = go.AddComponent<ParticleSystem>();
            var main = glitter.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 0.5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 2.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.006f, 0.016f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.9f, 0.5f, 1f),
                PastelTeal);
            main.gravityModifier = 0.7f;
            main.maxParticles = 60;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = glitter.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0.05f, (short)(13 + 17 * intensity)),
                new ParticleSystem.Burst(0.2f, (short)(9 + 11 * intensity)),
                new ParticleSystem.Burst(0.35f, (short)(5 + 5 * intensity))
            });

            var shape = glitter.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.08f;

            var overLifetime = glitter.colorOverLifetime;
            overLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.95f, 0.7f), 0f),
                    new GradientColorKey(PastelPink, 0.5f),
                    new GradientColorKey(PastelLilac, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.15f),
                    new GradientAlphaKey(0.8f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                });
            overLifetime.color = gradient;

            var size = glitter.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.2f),
                new Keyframe(0.2f, 1f),
                new Keyframe(0.4f, 0.3f),
                new Keyframe(0.6f, 1f),
                new Keyframe(1f, 0f)));

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var material = AdditiveSprite(LoadTex("Star_Sprite"));
            if (material != null)
                renderer.material = material;
            if (Application.isPlaying)
                glitter.Play(true);
        }

        /// <summary>Pastel confetti fanning out on a cone.</summary>
        static void BuildConfetti(Transform parent, float intensity)
        {
            var go = new GameObject("Confetti");
            go.transform.SetParent(parent, false);
            var confetti = go.AddComponent<ParticleSystem>();
            var main = confetti.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 0.35f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 2.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f, 2.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.045f);
            main.startColor = new ParticleSystem.MinMaxGradient(PastelTeal, PastelPink);
            main.gravityModifier = 0.85f;
            main.maxParticles = 14;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = confetti.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
                { new ParticleSystem.Burst(0.02f, (short)(5 + 9 * intensity)) });

            var shape = confetti.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 50f;
            shape.radius = 0.02f;

            var overLifetime = confetti.colorOverLifetime;
            overLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.85f, 0.45f), 0f),
                    new GradientColorKey(PastelTeal, 0.4f),
                    new GradientColorKey(PastelLilac, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.7f, 0.6f),
                    new GradientAlphaKey(0f, 1f)
                });
            overLifetime.color = gradient;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var material = AdditiveSprite(LoadTex("Confetti_Sprite"));
            if (material != null)
                renderer.material = material;
            if (Application.isPlaying)
                confetti.Play(true);
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
            else if (delay <= 0f)
                Object.DestroyImmediate(target);
            // EditMode has no delayed destroy: a delayed transient (the
            // dispersal root) stays alive for the test that owns it to
            // inspect and destroy. Destroying it here would make it
            // unobservable by construction.
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
