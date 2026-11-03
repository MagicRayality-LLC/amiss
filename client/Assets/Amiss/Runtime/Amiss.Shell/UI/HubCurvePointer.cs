using Oculus.Interaction;
using UnityEngine;

namespace Amiss.Shell
{
    /// <summary>
    /// Forwards a hit on the curved panel to the flat canvas point under that
    /// part of the picture. The compositor draws the cylinder; the canvas
    /// itself stays flat.
    /// </summary>
    public sealed class HubCurvePointer : PointableElement
    {
        Transform canvas;
        float radius;

        public void Bind(Transform canvasTransform, float curveRadius)
        {
            canvas = canvasTransform;
            radius = curveRadius;
        }

        public override void ProcessPointerEvent(PointerEvent evt)
        {
            var pose = evt.Pose;
            if (canvas != null)
                pose.position = EpisodeHub.CanvasPointForCylinderHit(canvas, pose.position, radius);
            base.ProcessPointerEvent(new PointerEvent(evt.Identifier, evt.Type, pose, evt.Data));
        }
    }
}
