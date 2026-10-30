using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell.Room
{
    public sealed class RoomHighlights : MonoBehaviour
    {
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
                var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                material.color = safety
                    ? new Color(0.95f, 0.12f, 0.08f, 1f)
                    : surface.Provenance switch
                    {
                        BoundaryProvenance.Scanned => new Color(0.08f, 0.65f, 0.35f, 1f),
                        BoundaryProvenance.VirtualSupplement => new Color(0.95f, 0.62f, 0.08f, 1f),
                        _ => new Color(0.58f, 0.22f, 0.82f, 1f)
                    };
                visual.GetComponent<Renderer>().sharedMaterial = material;
                visuals.Add(visual);
            }
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
