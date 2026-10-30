using Amiss.Shell.Room;
using UnityEngine;

namespace Amiss.Shell
{
    public sealed class InstrumentRoot : MonoBehaviour
    {
        ShellServices services;
        GameObject speaker;
        int appliedRevision = -1;
        PanelPlacementStatus appliedStatus = PanelPlacementStatus.Unplaced;

        void Start()
        {
            services = FindFirstObjectByType<ShellServices>();
            BuildSpeaker();
        }

        void LateUpdate()
        {
            var panel = services != null && services.Room != null ? services.Room.Recovery.Panel : null;
            if (panel == null || panel.Revision == appliedRevision && panel.Status == appliedStatus)
                return;
            if (panel.Status == PanelPlacementStatus.Placed)
            {
                transform.SetPositionAndRotation(panel.Position, panel.Rotation);
                appliedRevision = panel.Revision;
                appliedStatus = panel.Status;
            }
        }

        void BuildSpeaker()
        {
            if (speaker != null)
                return;
            var canvas = GetComponentInChildren<Canvas>();
            if (canvas != null && canvas.GetComponent<BoxCollider>() == null)
            {
                var collision = canvas.gameObject.AddComponent<BoxCollider>();
                var rect = canvas.GetComponent<RectTransform>();
                collision.size = new Vector3(rect.rect.width, rect.rect.height, 8f);
                collision.isTrigger = true;
            }

            speaker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            speaker.name = "WoodenSpeaker";
            speaker.transform.SetParent(transform, false);
            speaker.transform.localScale = new Vector3(0.36f, 0.12f, 0.10f);
            var speakerCollider = speaker.GetComponent<Collider>();
            if (speakerCollider != null)
                Destroy(speakerCollider);
            var renderer = speaker.GetComponent<Renderer>();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.color = new Color(0.28f, 0.14f, 0.055f, 1f);
            renderer.sharedMaterial = material;
            var audio = speaker.AddComponent<AudioSource>();
            audio.spatialBlend = 1f;
            audio.playOnAwake = false;
            audio.dopplerLevel = 0f;

            var panelCollider = canvas != null ? canvas.GetComponent<Collider>() : null;
            if (panelCollider != null)
            {
                var panelBounds = panelCollider.bounds;
                var speakerBounds = renderer.bounds;
                speaker.transform.position = new Vector3(panelBounds.center.x,
                    panelBounds.max.y + speakerBounds.extents.y + 0.03f, panelBounds.center.z);
            }
            else
            {
                speaker.transform.localPosition = new Vector3(0f, 0.52f, 0f);
            }
        }
    }
}
