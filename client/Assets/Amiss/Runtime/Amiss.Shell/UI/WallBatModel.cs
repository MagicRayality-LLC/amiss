using UnityEngine;
using UnityEngine.Rendering;

namespace Amiss.Shell
{
    /// <summary>
    /// The founder-supplied wooden bat: maple barrel, dark grip tape, subtle
    /// teal accent ring. Built at runtime from Resources so no scene prefab
    /// edit is needed; the same URP Lit treatment as the wall set.
    /// </summary>
    public static class WallBatModel
    {
        public const string PrefabResource = "BaseballBat/BaseballBat";

        public static bool TryMount(Transform host, out Transform gripPoint, out Transform tipPoint)
        {
            gripPoint = null;
            tipPoint = null;
            if (host == null)
                return false;
            var prefab = Resources.Load<GameObject>(PrefabResource);
            if (prefab == null)
            {
                Debug.LogWarning("[Amiss] baseball bat model missing");
                return false;
            }

            var visual = Object.Instantiate(prefab, host, false);
            visual.name = "BaseballBat";
            StripColliders(visual);
            if (!AssignMaterials(visual))
            {
                DestroyObject(visual);
                return false;
            }

            foreach (var child in visual.GetComponentsInChildren<Transform>(true))
            {
                if (gripPoint == null && child.name == "GripPoint")
                    gripPoint = child;
                if (tipPoint == null && child.name == "TipPoint")
                    tipPoint = child;
            }
            if (gripPoint == null)
            {
                Debug.LogWarning("[Amiss] baseball bat grip point missing");
                DestroyObject(visual);
                return false;
            }
            Debug.Log("[Amiss] baseball bat mounted");
            return true;
        }

        static bool AssignMaterials(GameObject visual)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning("[Amiss] baseball bat lit shader missing");
                return false;
            }

            var wood = Lit(shader, LoadTex("Wood_Albedo"), LoadTex("Wood_Normal"),
                0.35f, 0f, 0f, new Color(0.62f, 0.42f, 0.22f));
            var grip = Lit(shader, LoadTex("Grip_Albedo"), LoadTex("Grip_Normal"),
                0.2f, 0f, 0f, new Color(0.08f, 0.08f, 0.09f));
            var teal = Lit(shader, LoadTex("TealAccent_Albedo"), null,
                0.35f, 0.45f, 1.2f, new Color(0.361f, 0.882f, 0.902f));
            if (wood == null || grip == null)
                return false;

            foreach (var renderer in visual.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.sharedMaterial = MaterialFor(renderer.name, wood, grip, teal);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            return true;
        }

        static Material MaterialFor(string name, Material wood, Material grip, Material teal)
        {
            var n = name.ToLowerInvariant();
            if (n.Contains("grip"))
                return grip;
            if (n.Contains("teal") || n.Contains("ring") || n.Contains("accent"))
                return teal;
            return wood;
        }

        static Material Lit(Shader shader, Texture2D albedo, Texture2D normal, float smoothness,
            float metallic, float emission, Color tint)
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
            material.SetColor("_EmissionColor", tint * emission);
            if (emission > 0f)
                material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return material;
        }

        static Texture2D LoadTex(string name) =>
            Resources.Load<Texture2D>("BaseballBat/" + name);

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
