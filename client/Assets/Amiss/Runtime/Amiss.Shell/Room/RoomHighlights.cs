using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Amiss.Shell.Room
{
    public sealed class RoomHighlights : MonoBehaviour
    {
        /// <summary>
        /// The shader the highlight material is built from. A player build
        /// keeps a shader only when an asset references it; the imported
        /// instrument models carry the lit pipeline shader, while the unlit
        /// pipeline shader is referenced by nothing and is stripped, which is
        /// why the scan highlights drew nothing on device.
        /// </summary>
        public const string HighlightShaderName = "Universal Render Pipeline/Lit";

        /// <summary>
        /// Resources path of the transparent template material. The asset is
        /// the build input that keeps the transparent fragment variant: a
        /// material created only at runtime is not an input, so a player
        /// build can strip the variant and fall back to the opaque one,
        /// which forces the output alpha to one.
        /// </summary>
        public const string HighlightMaterialResource =
            "RoomHighlight/RoomHighlightTransparent";

        /// <summary>Opacity of the scan and safety highlights.</summary>
        public const float PresentationAlpha = 0.3f;

        /// <summary>Colour and opacity of the walls shown after room entry.</summary>
        public static readonly Color VirtualWallColor = new Color(0.35f, 0.75f, 1f);
        public const float VirtualWallAlpha = 0.5f;

        enum PresentationMode
        {
            None,
            ScanHighlights,
            VirtualWalls,
            SafetyHighlights
        }

        readonly List<GameObject> visuals = new();
        ShellServices services;
        RoomDraft shownDraft;
        PresentationMode shownMode;

        /// <summary>Number of highlight or virtual-wall surfaces currently in the room.</summary>
        public int ShownSurfaceCount => visuals.Count;

        void Start()
        {
            services = FindFirstObjectByType<ShellServices>();
        }

        void Update()
        {
            if (services == null || services.Room == null)
                return;
            var room = services.Room;
            Sync(room.Preparation, room.Recovery,
                room.Adapter == null || room.Adapter.WorldLockActive,
                room.VirtualWallsEnabled);
        }

        /// <summary>
        /// Shows provenance colours before confirmation and, when enabled,
        /// light-blue, half-transparent virtual walls after confirmation. A
        /// safety hold replaces either view with the red safety highlights.
        /// Every view is withheld while MRUK world lock is inactive: the draft
        /// poses are frozen at load, so without a live lock they can sit at a
        /// stale or offset place and enclose the wearer.
        /// </summary>
        public void Sync(RoomPreparation preparation, ShellRecovery recovery, bool worldLockActive,
            bool virtualWallsEnabled = true)
        {
            var draft = preparation != null ? preparation.Draft : null;
            var safety = recovery != null && recovery.SafetyHold;
            var mode = PresentationMode.None;
            if (worldLockActive && draft != null)
            {
                if (safety)
                    mode = PresentationMode.SafetyHighlights;
                else if (preparation.Status == RoomPreparationStatus.AwaitingConfirmation)
                    mode = PresentationMode.ScanHighlights;
                else if (virtualWallsEnabled && preparation.Status == RoomPreparationStatus.Confirmed)
                    mode = PresentationMode.VirtualWalls;
            }

            if (mode == PresentationMode.None)
            {
                if (shownMode != PresentationMode.None)
                    Clear();
                return;
            }
            if (shownDraft != draft || shownMode != mode)
                Show(draft, mode);
        }

        void Show(RoomDraft draft, PresentationMode mode)
        {
            Clear();
            shownDraft = draft;
            shownMode = mode;
            foreach (var surface in draft.Surfaces)
            {
                if (mode == PresentationMode.VirtualWalls && surface.Role != CollisionSurfaceRole.Wall)
                    continue;
                var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = mode == PresentationMode.VirtualWalls
                    ? "VirtualRoomWall"
                    : "DerivedSurfaceHighlight";
                var collider = visual.GetComponent<Collider>();
                if (collider != null)
                    Remove(collider);
                visual.transform.SetParent(transform, false);
                visual.transform.SetPositionAndRotation(surface.Center, surface.Rotation);
                visual.transform.localScale = surface.Size;
                var color = mode switch
                {
                    PresentationMode.SafetyHighlights => new Color(0.95f, 0.12f, 0.08f),
                    PresentationMode.VirtualWalls => VirtualWallColor,
                    _ => surface.Provenance switch
                    {
                        BoundaryProvenance.Scanned => new Color(0.08f, 0.65f, 0.35f),
                        BoundaryProvenance.VirtualSupplement => new Color(0.95f, 0.62f, 0.08f),
                        _ => new Color(0.58f, 0.22f, 0.82f)
                    }
                };
                var alpha = mode == PresentationMode.VirtualWalls
                    ? VirtualWallAlpha
                    : PresentationAlpha;
                var renderer = visual.GetComponent<Renderer>();
                renderer.sharedMaterial = CreateHighlightMaterial(color, renderer.sharedMaterial, alpha);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                visuals.Add(visual);
            }
        }

        /// <summary>
        /// Builds one room-surface material by cloning the shipped transparent
        /// template, so the transparent fragment variant is the one the
        /// build kept. The surface is transparent, writes no depth, and
        /// draws after every opaque surface, so passthrough and the wearer's
        /// content stay visible through it. A missing template or shader is
        /// a build defect, not a silent fallback: it is logged as an error
        /// and the primitive's own material is used so the scan stays
        /// visible rather than vanishing.
        /// </summary>
        public static Material CreateHighlightMaterial(Color color, Material fallback = null,
            float alpha = PresentationAlpha)
        {
            var template = Resources.Load<Material>(HighlightMaterialResource);
            if (template == null)
            {
                Debug.LogError("[Amiss] room highlight template missing from this build: " +
                               HighlightMaterialResource);
                template = CreateTransparentTemplate();
                if (template == null)
                {
                    Debug.LogError("[Amiss] room highlight shader missing from this build: " +
                                   HighlightShaderName);
                    return fallback != null ? new Material(fallback) : null;
                }
            }

            color.a = alpha;
            var material = new Material(template);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            return material;
        }

        /// <summary>
        /// Builds the exact transparent URP/Lit setup the shipped template
        /// carries. The editor uses it once to serialize the template asset;
        /// runtime uses it only when that asset is missing.
        /// </summary>
        public static Material CreateTransparentTemplate()
        {
            var shader = Shader.Find(HighlightShaderName);
            if (shader == null)
                return null;

            var material = new Material(shader);
            material.SetOverrideTag("RenderType", "Transparent");
            if (material.HasProperty("_Surface"))
                material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend"))
                material.SetFloat("_Blend", 0f);
            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend",
                    (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_SrcBlendAlpha"))
                material.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            if (material.HasProperty("_DstBlendAlpha"))
                material.SetFloat("_DstBlendAlpha",
                    (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 0f);
            if (material.HasProperty("_AlphaClip"))
                material.SetFloat("_AlphaClip", 0f);
            // URP prefers premultiplied alpha when specular highlights are
            // preserved; that would override the straight SrcAlpha blend set
            // above and enable _ALPHAPREMULTIPLY_ON. Straight alpha keeps the
            // requested opacity over passthrough.
            if (material.HasProperty("_BlendModePreserveSpecular"))
                material.SetFloat("_BlendModePreserveSpecular", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.DisableKeyword("_ALPHAMODULATE_ON");
            material.SetShaderPassEnabled("DepthOnly", false);
            material.renderQueue = (int)RenderQueue.Transparent;
            return material;
        }

        void Clear()
        {
            foreach (var visual in visuals)
                if (visual != null)
                    Remove(visual);
            visuals.Clear();
            shownDraft = null;
            shownMode = PresentationMode.None;
        }

        static void Remove(Object target)
        {
            if (target == null)
                return;
            if (Application.isPlaying)
                Destroy(target);
            else
                DestroyImmediate(target);
        }

        void OnDestroy()
        {
            Clear();
        }
    }
}
