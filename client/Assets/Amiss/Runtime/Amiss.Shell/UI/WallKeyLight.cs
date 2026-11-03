using UnityEngine;

namespace Amiss.Shell
{
    /// <summary>
    /// The one soft key light for the wall set. The settings are
    /// reference-derived presentation values: the reference image shades the
    /// set with a gentle upper-front key and a soft falloff. Shadows stay off
    /// because every wall-set renderer already opts out of casting and
    /// receiving them, and the key light only touches virtual geometry; the
    /// real room behind passthrough is unaffected.
    /// </summary>
    public static class WallKeyLight
    {
        public const string Name = "WallKeyLight";
        public const float Intensity = 1.0f;
        public const LightShadows Shadows = LightShadows.None;
        public static readonly Color Color = new Color(1f, 0.95f, 0.88f);
        public static readonly Vector3 Euler = new Vector3(45f, -20f, 0f);

        public static Light Create(Transform parent)
        {
            var go = new GameObject(Name);
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(Euler);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color;
            light.intensity = Intensity;
            light.shadows = Shadows;
            light.renderMode = LightRenderMode.Auto;
            return light;
        }
    }
}
