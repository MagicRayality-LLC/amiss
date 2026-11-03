using System.Collections.Generic;
using UnityEngine;

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

        readonly List<GameObject> visuals = new();
        ShellServices services;
        RoomDraft shownDraft;
        bool safetyShown;

        void Start()
        {
            services = FindFirstObjectByType<ShellServices>();
        }

        void Update()
        {
            if (services == null || services.Room == null)
                return;
            var room = services.Room;
            if (room.Recovery.SafetyHold && room.Preparation.Draft != null)
            {
                if (!safetyShown || shownDraft != room.Preparation.Draft)
                    Show(room.Preparation.Draft, true);
                return;
            }
            safetyShown = false;
            if (room.Preparation.Status == RoomPreparationStatus.AwaitingConfirmation)
            {
                if (shownDraft != room.Preparation.Draft)
                    Show(room.Preparation.Draft, false);
            }
            else if (shownDraft != null)
            {
                Clear();
            }
        }

        void Show(RoomDraft draft, bool safety)
        {
            Clear();
            shownDraft = draft;
            safetyShown = safety;
            if (draft == null)
                return;
            foreach (var surface in draft.Surfaces)
            {
                var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = "DerivedSurfaceHighlight";
                var collider = visual.GetComponent<Collider>();
                if (collider != null)
                    Destroy(collider);
                visual.transform.SetParent(transform, false);
                visual.transform.SetPositionAndRotation(surface.Center, surface.Rotation);
                visual.transform.localScale = surface.Size;
                var color = safety
                    ? new Color(0.95f, 0.12f, 0.08f, 1f)
                    : surface.Provenance switch
                    {
                        BoundaryProvenance.Scanned => new Color(0.08f, 0.65f, 0.35f, 1f),
                        BoundaryProvenance.VirtualSupplement => new Color(0.95f, 0.62f, 0.08f, 1f),
                        _ => new Color(0.58f, 0.22f, 0.82f, 1f)
                    };
                var renderer = visual.GetComponent<Renderer>();
                renderer.sharedMaterial = CreateHighlightMaterial(color, renderer.sharedMaterial);
                visuals.Add(visual);
            }
        }

        /// <summary>
        /// Builds one highlight material from the lit shader the player ships.
        /// A missing shader is a build defect, not a silent fallback: it is
        /// logged as an error and the primitive's own material is used so the
        /// scan stays visible rather than vanishing.
        /// </summary>
        public static Material CreateHighlightMaterial(Color color, Material fallback = null)
        {
            var shader = Shader.Find(HighlightShaderName);
            if (shader == null)
            {
                Debug.LogError("[Amiss] room highlight shader missing from this build: " +
                               HighlightShaderName);
                return fallback != null ? new Material(fallback) : null;
            }

            var material = new Material(shader);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            return material;
        }

        void Clear()
        {
            foreach (var visual in visuals)
                if (visual != null)
                    Destroy(visual);
            visuals.Clear();
            shownDraft = null;
        }

        void OnDestroy()
        {
            Clear();
        }
    }
}
