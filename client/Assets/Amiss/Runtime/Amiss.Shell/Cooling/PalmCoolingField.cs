using System;
using UnityEngine;

namespace Amiss.Shell.Cooling
{
    /// Hourglass "flux tube" of electric field lines between two palms.
    ///
    /// Performance budget (defaults, Quest 2/3):
    ///   18 core + 18 glow LineRenderers x 20 points  -> ~1.4k verts, 36 draw calls batched by material (2 materials)
    ///   4 pooled crackle arcs x 8 points, 2 palm-ring quads, 1 waist glow quad, 1 spark ParticleSystem (<= 80 particles)
    ///   Additive unlit shader, no depth write, textures <= 128 px (total ~12 KB). CPU: ~0.1 ms/frame (no allocations per frame).
    ///   Everything is disabled while Strength == 0.
    [DefaultExecutionOrder(100)]
    public class PalmCoolingField : MonoBehaviour
    {
        public PalmCoolingGesture gesture;

        [Header("Shape")]
        [Range(6, 32)] public int lineCount = 18;
        [Range(8, 40)] public int pointsPerLine = 20;
        [Tooltip("Field-line footprint radius on each palm (m). Spec: 6-8 cm.")]
        public float palmRadius = 0.07f;
        [Tooltip("Waist radius at the midpoint (m). 0.006 = 1.2 cm diameter.")]
        public float waistRadius = 0.006f;
        [Tooltip("How far the line leaves along the palm normal before bending in (fraction of distance).")]
        [Range(0.05f, 0.5f)] public float palmTangent = 0.22f;
        [Range(0.05f, 0.5f)] public float waistTangent = 0.24f;
        [Tooltip("Total twist of each line through the tube (degrees).")]
        public float twistDeg = 25f;

        [Header("Width")]
        public float coreWidth = 0.0035f;
        public float glowWidthScale = 5f;
        public AnimationCurve widthProfile = new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(0.22f, 0.55f), new Keyframe(0.5f, 0.2f), new Keyframe(0.78f, 0.55f), new Keyframe(1f, 1f));

        [Header("Colour")]
        [ColorUsage(false, true)] public Color coreColor = new Color(0.78f, 0.90f, 1f);
        [ColorUsage(false, true)] public Color tealColor = new Color(0.36f, 0.882f, 0.902f); // #5CE1E6
        [ColorUsage(false, true)] public Color violetColor = new Color(0.62f, 0.45f, 1f);
        [Range(0, 1)] public float violetShare = 0.2f;
        public float intensity = 1.4f;
        public float glowIntensity = 0.28f;
        [Tooltip("Extra waist brightness when hands are close (Closeness = 1).")]
        public float waistBoostClose = 1.6f;

        [Header("Motion")]
        public float scrollSpeed = 1.6f;
        public float jitterAmplitude = 0.0022f;
        public float jitterFrequency = 7f;
        [Range(0, 1)] public float flicker = 0.25f;
        [Tooltip("Crackle arcs per second at full strength.")]
        public float arcsPerSecond = 7f;

        [Header("Materials / textures (auto-created if empty)")]
        public Material lineMaterial;   // FieldFlow, Additive Scroll
        public Material glowMaterial;   // FieldFlow, softer
        public Material ringMaterial;   // PalmRing
        public Material sparkMaterial;  // Spark
        public Material waistMaterial;  // Glow

        [Header("Optional")]
        public AudioSource hum;          // assign a looping hum clip; none is shipped
        public float humMaxVolume = 0.35f;
        public Vector2 humPitch = new Vector2(0.85f, 1.25f);
        public bool controllerHaptics = false;
        [Range(0, 1)] public float hapticAmplitude = 0.12f;

        LineRenderer[] core, glow, arcs;
        float[] arcLife, arcAge, lineAngle, lineRadius, lineHue;
        Vector3[][] pts;
        Vector3[] arcPts;
        Transform ringL, ringR, waistGlow;
        Material ringMatL, ringMatR, waistMat;
        ParticleSystem sparks;
        Gradient grad = new Gradient();
        GradientColorKey[] ck = new GradientColorKey[3];
        GradientAlphaKey[] ak = new GradientAlphaKey[3];
        float arcAccum, sparkAccum;
        bool visible;
        static readonly int TintId = Shader.PropertyToID("_Tint");

        void Reset() { gesture = GetComponent<PalmCoolingGesture>() ?? GetComponentInParent<PalmCoolingGesture>(); }

        void Awake()
        {
            if (!gesture) gesture = GetComponent<PalmCoolingGesture>();
            EnsureMaterials();
            Build();
            SetVisible(false);
        }

        // ---------- setup ----------
        Material MakeMat(string tex, float scroll, float inten)
        {
            // The shader lives in Resources: no serialized material references
            // it, so Shader.Find alone would let the Android build strip it.
            var sh = Resources.Load<Shader>("PalmCooling/PalmCoolingAdditive");
            if (!sh) sh = Shader.Find("PalmCooling/Additive Scroll");
            if (!sh) sh = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
            var m = new Material(sh) { name = "PalmCooling_" + tex };
            var t = Resources.Load<Texture2D>("PalmCooling/" + tex);
            if (t) m.mainTexture = t;
            if (tex == "FieldFlow") m.mainTextureScale = new Vector2(3f, 1f);
            if (m.HasProperty("_ScrollSpeed")) m.SetFloat("_ScrollSpeed", scroll);
            if (m.HasProperty("_Intensity")) m.SetFloat("_Intensity", inten);
            return m;
        }

        void EnsureMaterials()
        {
            if (!lineMaterial) lineMaterial = MakeMat("FieldFlow", scrollSpeed, 1f);
            if (!glowMaterial) glowMaterial = MakeMat("FieldFlow", scrollSpeed * 0.6f, 1f);
            if (!ringMaterial) ringMaterial = MakeMat("PalmRing", 0f, 1f);
            if (!sparkMaterial) sparkMaterial = MakeMat("Spark", 0f, 1.5f);
            if (!waistMaterial) waistMaterial = MakeMat("Glow", 0f, 1f);
        }

        LineRenderer NewLine(string n, Material m, int count)
        {
            var go = new GameObject(n); go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = m; lr.positionCount = count; lr.useWorldSpace = true;
            lr.textureMode = LineTextureMode.Stretch; lr.alignment = LineAlignment.View;
            lr.numCapVertices = 0; lr.numCornerVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.receiveShadows = false;
            lr.allowOcclusionWhenDynamic = false;
            return lr;
        }

        Transform NewQuad(string n, Material m, out Material inst)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad); go.name = n;
            var col = go.GetComponent<Collider>(); if (col) Destroy(col);
            go.transform.SetParent(transform, false);
            var r = go.GetComponent<MeshRenderer>();
            inst = new Material(m); r.sharedMaterial = inst;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            return go.transform;
        }

        void Build()
        {
            var rnd = new System.Random(12345);
            core = new LineRenderer[lineCount]; glow = new LineRenderer[lineCount]; pts = new Vector3[lineCount][];
            lineAngle = new float[lineCount]; lineRadius = new float[lineCount]; lineHue = new float[lineCount];
            for (int i = 0; i < lineCount; i++)
            {
                lineAngle[i] = (i + (float)rnd.NextDouble() * 0.35f) / lineCount * Mathf.PI * 2f;
                lineRadius[i] = (i % 3 == 2) ? 0.62f : 0.9f + 0.1f * (float)rnd.NextDouble();
                lineHue[i] = (float)rnd.NextDouble();
                pts[i] = new Vector3[pointsPerLine];
                core[i] = NewLine("FieldLine_" + i, lineMaterial, pointsPerLine);
                glow[i] = NewLine("FieldGlow_" + i, glowMaterial, pointsPerLine);
                core[i].widthCurve = widthProfile; glow[i].widthCurve = widthProfile;
            }
            arcs = new LineRenderer[4]; arcLife = new float[4]; arcAge = new float[4]; arcPts = new Vector3[8];
            for (int i = 0; i < arcs.Length; i++) { arcs[i] = NewLine("Crackle_" + i, lineMaterial, 8); arcs[i].widthMultiplier = 0.0016f; arcs[i].enabled = false; arcLife[i] = 0; }
            ringL = NewQuad("PalmRing_L", ringMaterial, out ringMatL);
            ringR = NewQuad("PalmRing_R", ringMaterial, out ringMatR);
            waistGlow = NewQuad("WaistGlow", waistMaterial, out waistMat);

            var sgo = new GameObject("Sparks"); sgo.transform.SetParent(transform, false);
            sparks = sgo.AddComponent<ParticleSystem>();
            sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = sparks.main; main.loop = false; main.playOnAwake = false; main.maxParticles = 80;
            main.simulationSpace = ParticleSystemSimulationSpace.World; main.startLifetime = 0.35f; main.startSpeed = 0f;
            var em = sparks.emission; em.enabled = false;
            var sh = sparks.shape; sh.enabled = false;
            var col = sparks.colorOverLifetime; col.enabled = true;
            var g = new Gradient(); g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(tealColor, 1) },
                                             new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0, 1) });
            col.color = g;
            var sz = sparks.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 1, 1, 0.2f));
            var pr = sgo.GetComponent<ParticleSystemRenderer>(); pr.sharedMaterial = sparkMaterial;
            pr.renderMode = ParticleSystemRenderMode.Stretch; pr.velocityScale = 0.04f; pr.lengthScale = 1.2f;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sparks.Play();
        }

        void SetVisible(bool v)
        {
            if (visible == v && core != null && core[0].enabled == v) return;
            visible = v;
            for (int i = 0; i < lineCount; i++) { core[i].enabled = v; glow[i].enabled = v; }
            if (!v) foreach (var a in arcs) a.enabled = false;
            ringL.gameObject.SetActive(v); ringR.gameObject.SetActive(v); waistGlow.gameObject.SetActive(v);
            if (hum) { if (v && !hum.isPlaying) hum.Play(); else if (!v && hum.isPlaying) hum.Stop(); }
            if (!v) Haptics(0f);
        }

        // ---------- per frame ----------
        static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            float u = 1 - t; return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
        }

        static Vector3 PlaneBasis(Vector3 n, Vector3 hint)
        {
            var u = Vector3.ProjectOnPlane(hint, n);
            if (u.sqrMagnitude < 1e-6f) u = Vector3.ProjectOnPlane(Vector3.up, n);
            if (u.sqrMagnitude < 1e-6f) u = Vector3.ProjectOnPlane(Vector3.forward, n);
            return u.normalized;
        }

        void LateUpdate()
        {
            float S = gesture ? gesture.Strength : 0f;
            if (S <= 0.001f || !gesture.HasPose) { SetVisible(false); return; }
            SetVisible(true);

            var L = gesture.Left; var R = gesture.Right;
            Vector3 a = L.center, b = R.center, axis = b - a; float D = Mathf.Max(axis.magnitude, 0.01f); Vector3 dir = axis / D;
            Vector3 mid = (a + b) * 0.5f;
            float close = gesture.Closeness, t = Time.time;

            // Bases: left palm plane, right palm plane (same angular frame via left finger direction), waist plane.
            Vector3 nL = L.normal, nR = R.normal;
            Vector3 uL = PlaneBasis(nL, L.fingerDir), vL = Vector3.Cross(nL, uL);
            Vector3 uR = PlaneBasis(nR, uL), vR = Vector3.Cross(uR, nR); // mirrored so angles line up across the gap
            Vector3 uW = PlaneBasis(dir, uL), vW = Vector3.Cross(dir, uW);
            float stretchDim = Mathf.Lerp(0.45f, 1f, close);
            float discR = palmRadius * Mathf.Lerp(0.85f, 1f, S);
            float waistR = waistRadius * Mathf.Lerp(1.6f, 1f, close);
            float twist = twistDeg * Mathf.Deg2Rad;
            float jit = jitterAmplitude * (0.6f + 0.4f * S) * Mathf.Lerp(1.3f, 1f, close);

            for (int i = 0; i < lineCount; i++)
            {
                float th = lineAngle[i], rr = lineRadius[i] * discR;
                Vector3 p0 = a + (Mathf.Cos(th) * uL + Mathf.Sin(th) * vL) * rr + nL * 0.006f;
                Vector3 p3 = b + (Mathf.Cos(th + twist) * uR + Mathf.Sin(th + twist) * vR) * rr + nR * 0.006f;
                float thW = th + twist * 0.5f;
                Vector3 w = mid + (Mathf.Cos(thW) * uW + Mathf.Sin(thW) * vW) * waistR * lineRadius[i];
                Vector3 c1 = p0 + nL * (D * palmTangent);
                Vector3 c2 = w - dir * (D * waistTangent);
                Vector3 c3 = w + dir * (D * waistTangent);
                Vector3 c4 = p3 + nR * (D * palmTangent);
                var P = pts[i]; int n = P.Length;
                for (int k = 0; k < n; k++)
                {
                    float s = k / (float)(n - 1);
                    Vector3 p = s < 0.5f ? Bezier(p0, c1, c2, w, s * 2f) : Bezier(w, c3, c4, p3, s * 2f - 1f);
                    float env = Mathf.Sin(s * Mathf.PI);
                    float nx = Mathf.PerlinNoise(i * 3.1f + s * 4f, t * jitterFrequency) - 0.5f;
                    float ny = Mathf.PerlinNoise(i * 5.7f + 17f, t * jitterFrequency + s * 4f) - 0.5f;
                    P[k] = p + (uW * nx + vW * ny) * (jit * 2f * env);
                }
                core[i].SetPositions(P); glow[i].SetPositions(P);

                float fl = 1f - flicker + flicker * Mathf.PerlinNoise(t * 11f, i * 1.37f);
                float baseA = S * stretchDim * fl * intensity;
                Color edge = lineHue[i] < violetShare ? Color.Lerp(violetColor, tealColor, 0.35f) : tealColor;
                Color centre = Color.Lerp(coreColor, Color.white, close * 0.5f);
                float waistA = Mathf.Clamp01(baseA * (0.7f + waistBoostClose * close * 0.5f));
                SetGrad(core[i], edge, centre, Mathf.Clamp01(baseA * 0.8f), waistA);
                core[i].widthMultiplier = coreWidth * Mathf.Lerp(0.8f, 1.1f, close);
                SetGrad(glow[i], edge, Color.Lerp(tealColor, coreColor, 0.5f), Mathf.Clamp01(baseA * glowIntensity), Mathf.Clamp01(waistA * glowIntensity * 1.3f));
                glow[i].widthMultiplier = coreWidth * glowWidthScale;
            }

            // Palm rings
            PlaceRing(ringL, ringMatL, L, S, t, 1f);
            PlaceRing(ringR, ringMatR, R, S, t, -1f);
            // Waist glow billboard
            var cam = Camera.main;
            waistGlow.position = mid;
            if (cam) waistGlow.rotation = Quaternion.LookRotation(waistGlow.position - cam.transform.position, cam.transform.up);
            waistGlow.localScale = Vector3.one * Mathf.Lerp(0.03f, 0.055f, close);
            waistMat.SetColor(TintId, coreColor * (S * Mathf.Lerp(0.25f, 0.8f, close)));

            UpdateArcs(S, t);
            EmitSparks(S, a, b, mid, nL, nR, uL, vL, uR, vR, discR, dir);

            if (hum) { hum.volume = humMaxVolume * S; hum.pitch = Mathf.Lerp(humPitch.x, humPitch.y, S * (0.5f + 0.5f * close)); }
            Haptics(S * hapticAmplitude * (0.6f + 0.4f * close));
        }

        void SetGrad(LineRenderer lr, Color edge, Color centre, float aEdge, float aMid)
        {
            ck[0] = new GradientColorKey(edge, 0f); ck[1] = new GradientColorKey(centre, 0.5f); ck[2] = new GradientColorKey(edge, 1f);
            ak[0] = new GradientAlphaKey(aEdge, 0f); ak[1] = new GradientAlphaKey(aMid, 0.5f); ak[2] = new GradientAlphaKey(aEdge, 1f);
            grad.SetKeys(ck, ak); lr.colorGradient = grad;
        }

        void PlaceRing(Transform r, Material m, PalmSample p, float S, float t, float spin)
        {
            r.position = p.center + p.normal * 0.004f;
            // Quad faces -Z; make it face along the palm normal, then spin slowly.
            r.rotation = Quaternion.LookRotation(-p.normal, p.fingerDir) * Quaternion.Euler(0, 0, t * 40f * spin);
            r.localScale = Vector3.one * palmRadius * 2.3f;
            m.SetColor(TintId, Color.Lerp(tealColor, coreColor, 0.3f) * (S * 0.9f));
        }

        void UpdateArcs(float S, float t)
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < arcs.Length; i++)
            {
                if (!arcs[i].enabled) continue;
                arcAge[i] += dt;
                if (arcAge[i] >= arcLife[i]) { arcs[i].enabled = false; continue; }
                float f = 1f - arcAge[i] / arcLife[i];
                arcs[i].startColor = arcs[i].endColor = new Color(0.85f, 0.95f, 1f, f * S);
            }
            arcAccum += dt * arcsPerSecond * S;
            while (arcAccum >= 1f)
            {
                arcAccum -= UnityEngine.Random.Range(0.6f, 1.4f);
                int slot = Array.FindIndex(arcs, x => !x.enabled); if (slot < 0) break;
                int li = UnityEngine.Random.Range(0, lineCount); var P = pts[li];
                float t0 = UnityEngine.Random.Range(0.08f, 0.62f), span = UnityEngine.Random.Range(0.15f, 0.3f);
                for (int k = 0; k < arcPts.Length; k++)
                {
                    float s = t0 + span * k / (arcPts.Length - 1);
                    float fi = s * (P.Length - 1); int i0 = Mathf.Clamp((int)fi, 0, P.Length - 2);
                    Vector3 p = Vector3.Lerp(P[i0], P[i0 + 1], fi - i0);
                    float amp = (k == 0 || k == arcPts.Length - 1) ? 0f : 0.009f;
                    arcPts[k] = p + UnityEngine.Random.insideUnitSphere * amp;
                }
                arcs[slot].SetPositions(arcPts); arcs[slot].enabled = true;
                arcAge[slot] = 0f; arcLife[slot] = UnityEngine.Random.Range(0.06f, 0.12f);
            }
        }

        void EmitSparks(float S, Vector3 a, Vector3 b, Vector3 mid, Vector3 nL, Vector3 nR, Vector3 uL, Vector3 vL, Vector3 uR, Vector3 vR, float discR, Vector3 dir)
        {
            sparkAccum += Time.deltaTime * 45f * S;
            var ep = new ParticleSystem.EmitParams();
            while (sparkAccum >= 1f)
            {
                sparkAccum -= 1f;
                int where = UnityEngine.Random.Range(0, 5); // 0,1 left, 2,3 right, 4 waist
                float th = UnityEngine.Random.value * Mathf.PI * 2f;
                Vector3 pos, vel;
                if (where < 2) { var r = Mathf.Cos(th) * uL + Mathf.Sin(th) * vL; pos = a + r * discR * UnityEngine.Random.Range(0.5f, 1f); vel = (Vector3.Cross(nL, r) * 0.5f + r * 0.3f + nL * 0.2f) * UnityEngine.Random.Range(0.1f, 0.35f); }
                else if (where < 4) { var r = Mathf.Cos(th) * uR + Mathf.Sin(th) * vR; pos = b + r * discR * UnityEngine.Random.Range(0.5f, 1f); vel = (Vector3.Cross(nR, r) * 0.5f + r * 0.3f + nR * 0.2f) * UnityEngine.Random.Range(0.1f, 0.35f); }
                else { pos = mid + UnityEngine.Random.insideUnitSphere * 0.006f; vel = (UnityEngine.Random.onUnitSphere - Vector3.Project(UnityEngine.Random.onUnitSphere, dir)) * UnityEngine.Random.Range(0.15f, 0.4f); }
                ep.position = pos; ep.velocity = vel;
                ep.startSize = UnityEngine.Random.Range(0.004f, 0.009f);
                ep.startLifetime = UnityEngine.Random.Range(0.15f, 0.4f);
                ep.startColor = Color.Lerp(coreColor, tealColor, UnityEngine.Random.value);
                sparks.Emit(ep, 1);
            }
        }

        // ---------- optional haptics (reflection, no SDK dependency) ----------
        static Type ovrInput; static System.Reflection.MethodInfo vib; static object ctrlL, ctrlR; static bool hapticLookup;
        float lastHaptic = -1f;
        void Haptics(float amp)
        {
            if (!controllerHaptics || Mathf.Abs(amp - lastHaptic) < 0.01f) return;
            lastHaptic = amp;
            if (!hapticLookup)
            {
                hapticLookup = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { ovrInput = asm.GetType("OVRInput"); if (ovrInput != null) break; }
                if (ovrInput == null) return;
                var ct = ovrInput.GetNestedType("Controller");
                vib = ovrInput.GetMethod("SetControllerVibration", new[] { typeof(float), typeof(float), ct });
                ctrlL = Enum.Parse(ct, "LTouch"); ctrlR = Enum.Parse(ct, "RTouch");
            }
            if (vib == null) return;
            vib.Invoke(null, new object[] { 0.3f, amp, ctrlL }); vib.Invoke(null, new object[] { 0.3f, amp, ctrlR });
        }

        void OnDisable() { if (core != null) SetVisible(false); }
    }
}
