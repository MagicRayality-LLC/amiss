using Amiss.Shell.Room;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.Rendering;

namespace Amiss.Shell
{
    /// <summary>
    /// How the wearer reached the switch. The hand pinch and the controller
    /// trigger are proximity tests; the ray and poke arrive as Interaction SDK
    /// pointer selects on the plate.
    /// </summary>
    public enum KnifeSwitchInputKind
    {
        None,
        HandPinch,
        ControllerTrigger,
        Ray,
        Poke
    }

    /// <summary>What a press did, decided before any session or visual change.</summary>
    public enum KnifeSwitchOutcome
    {
        None,
        Held,
        VisualOnly,
        Started,
        Stopped
    }

    /// <summary>
    /// One gesture yields one switch transition. The close path latched on the
    /// closed blade; a toggle needs the same latch so the Select that stops a
    /// running trial cannot be followed by a start in the same gesture. The
    /// owner releases the latch once every activation input is up.
    /// </summary>
    public sealed class KnifeSwitchGesture
    {
        public bool Latched { get; private set; }

        public KnifeSwitchOutcome Press(KnifeSwitchInputKind kind, ShellSessionState session,
            bool closed, bool roomPresent, bool runtimeTrialRunning)
        {
            if (Latched)
                return KnifeSwitchOutcome.None;
            var outcome = WallKnifeSwitch.Decide(kind, session, closed, roomPresent,
                runtimeTrialRunning);
            if (outcome != KnifeSwitchOutcome.None)
                Latched = true;
            return outcome;
        }

        public void Release() => Latched = false;
    }

    /// <summary>
    /// Where the knife switch sits relative to the television. The television
    /// forward points into the wall when the set is wall-mounted. The model's
    /// own forward is the slate back plate, so that axis points into the wall
    /// and the red face points into the room. The switch is on the viewer's right.
    /// </summary>
    public static class KnifeSwitchPose
    {
        public const float PlateHalfWidthMeters = 0.10f;
        // Reference-derived presentation values: the switch plate clears the
        // bezel by about 0.13 m, and its center reads about 0.04 m above the
        // television center.
        public const float GapFromBezelMeters = 0.13f;
        public const float CenterHeightAboveTvCenterMeters = 0.04f;
        public const float WallClearanceMeters = 0.02f;

        public static float LateralMeters =>
            PanelMount.ScreenWidth * 0.5f + PanelMount.Bezel + PlateHalfWidthMeters + GapFromBezelMeters;

        public static Pose Beside(Vector3 televisionPosition, Quaternion televisionRotation, bool onWall)
        {
            var intoWall = televisionRotation * Vector3.forward;
            var right = televisionRotation * Vector3.right;
            var up = televisionRotation * Vector3.up;
            var depth = onWall ? PanelMount.BracketStandoff - WallClearanceMeters : 0f;
            var position = televisionPosition + right * LateralMeters +
                           up * CenterHeightAboveTvCenterMeters + intoWall * depth;
            var rotation = Quaternion.LookRotation(intoWall, Vector3.up);
            return new Pose(position, rotation);
        }
    }

    /// <summary>
    /// The red knife switch on the wall to the right of the television.
    /// A pinch, a controller trigger, or an Interaction SDK ray or poke select
    /// on the plate closes the blades and starts the run when the room is
    /// confirmed. While the run is going, the same activation opens the blades
    /// and ends the trial.
    /// </summary>
    public sealed class WallKnifeSwitch : MonoBehaviour
    {
        public const float PinchRadiusMeters = 0.18f;
        // The plate is 0.20 m by 0.30 m, so its half diagonal is 0.18 m. This
        // radius reaches the whole plate from its center, plus the offset of a
        // pinching hand's pointer pose from the surface it grabs.
        public const float PlateRadiusMeters = 0.26f;
        // BladePivot's local +Z is the blade axis, so a local X rotation swings
        // the blade in the plate's depth plane. At -90 degrees the axis points
        // along the plate toward the ON jaw: the blade then runs from the hinge
        // pin at local y = -0.085 to the jaw slot 0.200 m above it, the tip
        // landing between KS_JawProng_LA and KS_JawProng_LB. The authored rest
        // pose takes that same local rotation to -125 degrees, which is the open
        // switch 35 degrees out of the plate. Zero is not on this arc: it points
        // the blade straight into the wall, which is what the earlier build did.
        public const float AngleOff = -125f;
        public const float AngleOn = -90f;
        public const float TargetHeightMeters = 0.30f;
        public const float PlateThicknessMeters = 0.03f;
        // The base plate runs from the model origin to 0.022 m into the room;
        // the ray and poke surface sits on that room-facing face.
        public const float PlateFrontMeters = -0.022f;

        // The blade group (blades, tips, crossbar, handle, stem) was authored as
        // one rigid translation away from its own pivot: every child sits 0.0488
        // m off the hinge pin line and 0.1282 m too far along the blade axis, so
        // the blade starts 0.129 m past the pin and its tip overshoots the ON jaw
        // slot by the same amount. Measured from the FBX (Blender import) against
        // the hinge pin at local y = -0.085, z = -0.050 and the jaw slot at
        // y = 0.115, z = -0.050. The correction below restores the authored
        // mechanism to the back plate, which keeps its pose.
        public static readonly Vector3 BladeGroupCorrection = new Vector3(0f, 0.0488f, -0.1282f);

        // The room face of each label box is the mesh's -Z face; on it u runs
        // toward local -X, which reads "ON" and "OFF" reversed from the room.
        // Both labels are mapped the same way, so one u flip on each label
        // material puts the lettering back without touching the pose.
        public static readonly Vector2 LabelBaseMapScale = new Vector2(-1f, 1f);
        public static readonly Vector2 LabelBaseMapOffset = new Vector2(1f, 0f);

        const string ResourcePath = "KnifeSwitch/KnifeSwitch";

        Transform blade;
        Renderer led;
        Transform handle;
        bool closed;
        bool shown;
        float angle = AngleOff;
        float target = AngleOff;
        bool pinchWasDown;
        int pointerHolds;
        bool built;
        ShellServices services;
        readonly KnifeSwitchGesture gesture = new();

        void Awake()
        {
            Build();
            gameObject.SetActive(false);
            shown = false;
        }

        /// <summary>
        /// Loads the model, its materials, and the ray and poke targets. Awake
        /// runs it once; the edit-mode tests call it without a play session.
        /// </summary>
        public void Build()
        {
            if (built)
                return;
            built = true;
            services = FindFirstObjectByType<ShellServices>();
            BuildVisual();
        }

        /// <summary>
        /// The blade is closed exactly while a trial runs, whichever path
        /// started it: a press on the switch, or the hub's START TRIAL. The
        /// pose follows the session, so a trial that ends by any path opens
        /// the blades. T11 poses: closed is the blade on the ON contacts.
        /// </summary>
        public static bool RendersClosed(ShellSessionState session) =>
            EpisodeLaunchGate.TrialRunning(session);

        void Update()
        {
            if (RendersClosed(CurrentSession()))
            {
                if (!closed)
                    CloseVisual();
            }
            else if (closed)
            {
                OpenVisual();
            }
            if (blade != null)
            {
                angle = Mathf.MoveTowards(angle, target, 420f * Time.deltaTime);
                blade.localRotation = Quaternion.Euler(angle, 0f, 0f);
            }
            if (!shown)
                return;
            var input = PinchDownNearHandle();
            if (input != KnifeSwitchInputKind.None)
                Activate(input);
            if (!AnyActivationDown())
                gesture.Release();
        }

        public void SetVisible(bool visible)
        {
            shown = visible;
            if (!visible)
            {
                // A hiding wall set receives no Unselect; a latched gesture
                // would otherwise swallow the next press after it returns.
                gesture.Release();
                pointerHolds = 0;
            }
            if (gameObject.activeSelf != visible)
                gameObject.SetActive(visible);
        }

        public void Follow(Transform television, bool onWall)
        {
            if (television == null)
                return;
            var pose = KnifeSwitchPose.Beside(television.position, television.rotation, onWall);
            transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        /// <summary>
        /// One activation is one transition. A running trial opens the blades
        /// and ends through the room's stop seam; a confirmed room starts it;
        /// a refused press changes nothing. The gesture latch makes duplicate
        /// selects from one activation idempotent.
        /// </summary>
        public void Activate(KnifeSwitchInputKind kind)
        {
            if (kind == KnifeSwitchInputKind.None)
                return;
            if (gesture.Latched)
            {
                Debug.Log("[Amiss] knife switch press=" + kind +
                          " transition=refused reason=latched");
                return;
            }

            var room = services != null ? services.Room : null;
            var runtimeTrialRunning = room != null && room.HasRunningTrial;
            var outcome = gesture.Press(kind, CurrentSession(), closed, room != null,
                runtimeTrialRunning);
            var transition = "refused";
            var reason = "";
            switch (outcome)
            {
                case KnifeSwitchOutcome.Started:
                    room.AcceptSetup();
                    if (EpisodeLaunchGate.TrialRunning(CurrentSession()))
                    {
                        CloseVisual();
                        transition = "started";
                    }
                    else
                    {
                        reason = string.IsNullOrEmpty(room.StartBlock)
                            ? "not_started"
                            : room.StartBlock;
                    }
                    break;
                case KnifeSwitchOutcome.Stopped:
                    if (room.StopRunningTrial("knife_switch_open"))
                    {
                        OpenVisual();
                        transition = "stopped";
                    }
                    else
                    {
                        reason = "trial_stop_refused";
                    }
                    break;
                case KnifeSwitchOutcome.VisualOnly:
                    OpenVisual();
                    reason = "not_running";
                    break;
                default:
                    reason = EpisodeLaunchGate.TrialRunning(CurrentSession())
                        ? "no_runtime_trial"
                        : room == null ? "room_missing" : "room_not_confirmed";
                    break;
            }
            Debug.Log("[Amiss] knife switch press=" + kind + " transition=" + transition +
                      (reason.Length > 0 ? " reason=" + reason : ""));
        }

        public static KnifeSwitchOutcome Decide(KnifeSwitchInputKind kind, ShellSessionState session,
            bool closed, bool roomPresent, bool runtimeTrialRunning)
        {
            if (kind == KnifeSwitchInputKind.None)
                return KnifeSwitchOutcome.None;
            if (EpisodeLaunchGate.TrialRunning(session))
                return EpisodeLaunchGate.CanStopTrial(session, runtimeTrialRunning)
                    ? KnifeSwitchOutcome.Stopped
                    : KnifeSwitchOutcome.Held;
            if (closed)
                return KnifeSwitchOutcome.VisualOnly;
            if (EpisodeLaunchGate.KnifeSwitchStartsTrial(session, closed))
                return roomPresent ? KnifeSwitchOutcome.Started : KnifeSwitchOutcome.Held;
            return KnifeSwitchOutcome.Held;
        }

        void CloseVisual()
        {
            if (closed)
                return;
            closed = true;
            target = AngleOn;
            if (led != null)
            {
                var material = led.material;
                material.EnableKeyword("_EMISSION");
                if (material.HasProperty("_EmissionColor"))
                    material.SetColor("_EmissionColor", new Color(1f, 0.2f, 0.05f) * 4f);
            }
            var hub = FindFirstObjectByType<EpisodeHub>();
            if (hub != null)
                hub.SetOpen(false);
            Debug.Log("[Amiss] knife switch closed");
        }

        void OpenVisual()
        {
            if (!closed)
                return;
            closed = false;
            target = AngleOff;
            if (led != null)
            {
                var material = led.material;
                material.DisableKeyword("_EMISSION");
                if (material.HasProperty("_EmissionColor"))
                    material.SetColor("_EmissionColor", Color.black);
            }
            Debug.Log("[Amiss] knife switch opened");
        }

        /// <summary>One more or one fewer ray or poke pointer holding the plate.</summary>
        public void PointerHeld(bool held)
        {
            pointerHolds = Mathf.Max(0, pointerHolds + (held ? 1 : -1));
        }

        bool AnyActivationDown() => pinchWasDown || pointerHolds > 0;

        ShellSessionState CurrentSession() =>
            services != null && services.Session != null
                ? services.Session.State
                : ShellSessionState.Library;

        KnifeSwitchInputKind PinchDownNearHandle()
        {
            var input = KnifeSwitchInputKind.None;
            var hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
            for (var i = 0; i < hands.Length; i++)
            {
                var hand = hands[i];
                if (hand == null || !hand.IsTracked || !hand.IsPointerPoseValid)
                    continue;
                if (!hand.GetFingerIsPinching(OVRHand.HandFinger.Index))
                    continue;
                if (!pinchWasDown && Near(hand.PointerPose.position))
                    input = KnifeSwitchInputKind.HandPinch;
            }
            if (!pinchWasDown &&
                (OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger) ||
                 OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger)))
            {
                var rig = FindFirstObjectByType<OVRCameraRig>();
                var tracking = rig != null ? rig.trackingSpace : null;
                if (tracking != null)
                {
                    if (OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger) &&
                        Near(tracking.TransformPoint(OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch))))
                        input = KnifeSwitchInputKind.ControllerTrigger;
                    if (OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger) &&
                        Near(tracking.TransformPoint(OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch))))
                        input = KnifeSwitchInputKind.ControllerTrigger;
                }
            }
            pinchWasDown = HandsPinching(hands) ||
                OVRInput.Get(OVRInput.RawButton.LIndexTrigger) ||
                OVRInput.Get(OVRInput.RawButton.RIndexTrigger);
            return input;
        }

        static bool HandsPinching(OVRHand[] hands)
        {
            for (var i = 0; i < hands.Length; i++)
            {
                var hand = hands[i];
                if (hand != null && hand.IsTracked && hand.GetFingerIsPinching(OVRHand.HandFinger.Index))
                    return true;
            }
            return false;
        }

        bool Near(Vector3 worldPoint) =>
            WithinReach(worldPoint, transform.position, handle != null ? handle.position : transform.position);

        public static bool WithinReach(Vector3 point, Vector3 plateCenter, Vector3 handleCenter) =>
            (point - handleCenter).sqrMagnitude <= PinchRadiusMeters * PinchRadiusMeters ||
            (point - plateCenter).sqrMagnitude <= PlateRadiusMeters * PlateRadiusMeters;

        void BuildVisual()
        {
            var prefab = Resources.Load<GameObject>(ResourcePath);
            if (prefab == null)
            {
                Debug.LogWarning("[Amiss] knife switch model missing");
                return;
            }
            var visual = Instantiate(prefab, transform, false);
            visual.name = "KnifeSwitch";
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader != null)
                AssignMaterials(visual, shader);
            NormalizeHeight(visual);
            foreach (var child in visual.GetComponentsInChildren<Transform>(true))
            {
                if (blade == null && child.name == "BladePivot")
                    blade = child;
                if (handle == null && child.name.Contains("Handle") && !child.name.Contains("Stem"))
                    handle = child;
                if (led == null && child.name.Contains("LED"))
                    led = child.GetComponent<Renderer>();
            }
            if (blade != null)
            {
                CorrectBladeGroup(blade);
                blade.localRotation = Quaternion.Euler(AngleOff, 0f, 0f);
            }
            BuildInteraction(visual.transform);
            Debug.Log("[Amiss] knife switch built height=" + Height(visual).ToString("F3"));
        }

        /// <summary>
        /// Puts the blade group back on its own pivot without moving the back
        /// plate. Every child of the pivot carries the same authoring offset, so
        /// one correction on the subtree restores the authored mechanism.
        /// </summary>
        static void CorrectBladeGroup(Transform pivot)
        {
            foreach (var part in pivot.GetComponentsInChildren<Transform>(true))
            {
                if (part != pivot)
                    part.localPosition += BladeGroupCorrection;
            }
        }

        /// <summary>
        /// The switch carries no collider and no interactable of its own, so the
        /// hub's Interaction SDK ray and poke pattern is what turns a point and
        /// a trigger, or a fingertip press, into the same start a pinch makes.
        /// The plate itself is a flat plane: one surface, shared by both.
        /// </summary>
        void BuildInteraction(Transform visual)
        {
            var plate = new GameObject("KnifeSwitchTarget");
            plate.transform.SetParent(visual, false);
            plate.transform.localPosition = new Vector3(0f, 0f, PlateFrontMeters);
            plate.transform.localRotation = Quaternion.identity;
            plate.transform.localScale = Vector3.one;
            var plane = plate.AddComponent<PlaneSurface>();
            plane.InjectNormalFacing(PlaneSurface.NormalFacing.Backward);
            plane.InjectDoubleSided(true);
            var clipper = plate.AddComponent<BoundsClipper>();
            clipper.Position = Vector3.zero;
            clipper.Size = new Vector3(KnifeSwitchPose.PlateHalfWidthMeters * 2f, TargetHeightMeters,
                PlateThicknessMeters);
            var clipped = plate.AddComponent<ClippedPlaneSurface>();
            clipped.InjectAllClippedPlaneSurface(plane, new IBoundsClipper[] { clipper });

            var ray = new GameObject("KnifeSwitchRay");
            ray.transform.SetParent(plate.transform, false);
            var rayInteractable = ray.AddComponent<RayInteractable>();
            var rayPointer = ray.AddComponent<KnifeSwitchPointer>();
            rayPointer.Bind(this, KnifeSwitchInputKind.Ray);
            rayInteractable.InjectSurface(clipped);
            rayInteractable.InjectOptionalSelectSurface(clipped);
            rayInteractable.InjectOptionalPointableElement(rayPointer);

            var poke = new GameObject("KnifeSwitchPoke");
            poke.transform.SetParent(plate.transform, false);
            var pokeInteractable = poke.AddComponent<PokeInteractable>();
            var pokePointer = poke.AddComponent<KnifeSwitchPointer>();
            pokePointer.Bind(this, KnifeSwitchInputKind.Poke);
            pokeInteractable.InjectSurfacePatch(clipped);
            pokeInteractable.InjectOptionalPointableElement(pokePointer);
        }

        static void NormalizeHeight(GameObject visual)
        {
            var height = Height(visual);
            if (height < 0.001f || (height > 0.2f && height < 0.45f))
                return;
            visual.transform.localScale *= TargetHeightMeters / height;
        }

        static float Height(GameObject visual)
        {
            var found = false;
            var bounds = new Bounds(visual.transform.position, Vector3.zero);
            foreach (var renderer in visual.GetComponentsInChildren<Renderer>(true))
            {
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                    bounds.Encapsulate(renderer.bounds);
            }
            return found ? bounds.size.y : 0f;
        }

        static void AssignMaterials(GameObject visual, Shader shader)
        {
            var red = Lit(shader, Load("RedPaint_Albedo"), Load("RedPaint_Normal"),
                new Color(0.72f, 0.08f, 0.06f), 0.08f, 0.35f, 0f);
            var slate = Lit(shader, Load("Slate_Albedo"), Load("Slate_Normal"),
                new Color(0.06f, 0.065f, 0.075f), 0.15f, 0.25f, 0f);
            var copper = Lit(shader, Load("Copper_Albedo"), Load("Copper_Normal"),
                new Color(0.78f, 0.45f, 0.16f), 0.85f, 0.45f, 0f);
            var brass = Lit(shader, Load("Brass_Albedo"), Load("Brass_Normal"),
                new Color(0.62f, 0.48f, 0.22f), 0.8f, 0.45f, 0f);
            var bakelite = Lit(shader, Load("Bakelite_Albedo"), Load("Bakelite_Normal"),
                new Color(0.12f, 0.08f, 0.06f), 0f, 0.2f, 0f);
            var labelOn = Lit(shader, Load("Label_ON"), null, Color.white, 0f, 0.3f, 0f);
            var labelOff = Lit(shader, Load("Label_OFF"), null, Color.white, 0f, 0.3f, 0f);
            FlipLabelText(labelOn);
            FlipLabelText(labelOff);
            var lamp = Lit(shader, Load("LED_Emission"), null, new Color(0.15f, 0.04f, 0.03f), 0f, 0.2f, 0f);
            foreach (var renderer in visual.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.sharedMaterial = MaterialFor(renderer.name, red, slate, copper, brass, bakelite,
                    labelOn, labelOff, lamp);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
        }

        static Material MaterialFor(string name, Material red, Material slate, Material copper, Material brass,
            Material bakelite, Material labelOn, Material labelOff, Material lamp)
        {
            var n = name.ToLowerInvariant();
            if (n.Contains("blade") || n.Contains("tip"))
                return copper;
            if (n.Contains("hinge") || n.Contains("pin") || n.Contains("jaw") || n.Contains("prong"))
                return brass;
            if (n.Contains("crossbar") || n.Contains("stem") || n.Contains("bakelite"))
                return bakelite;
            if (n.Contains("label_on") || n.Contains("labelon"))
                return labelOn;
            if (n.Contains("label_off") || n.Contains("labeloff"))
                return labelOff;
            if (n.Contains("led"))
                return lamp;
            if (n.Contains("handle") || n.Contains("rail") || n.Contains("lip") || n.Contains("badge") ||
                n.Contains("red"))
                return red;
            return slate;
        }

        public static void FlipLabelText(Material material)
        {
            material.SetTextureScale("_BaseMap", LabelBaseMapScale);
            material.SetTextureOffset("_BaseMap", LabelBaseMapOffset);
        }

        static Material Lit(Shader shader, Texture2D albedo, Texture2D normal, Color color, float metallic,
            float smoothness, float emission)
        {
            var material = new Material(shader);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            if (albedo != null)
            {
                material.SetTexture("_BaseMap", albedo);
                material.SetColor("_BaseColor", Color.white);
            }
            if (normal != null)
            {
                material.SetTexture("_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
                material.SetFloat("_BumpScale", 1f);
            }
            if (emission > 0f && albedo != null)
            {
                material.SetTexture("_EmissionMap", albedo);
                material.SetColor("_EmissionColor", color * emission);
                material.EnableKeyword("_EMISSION");
            }
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return material;
        }

        static Texture2D Load(string name) => Resources.Load<Texture2D>("KnifeSwitch/" + name);
    }

    /// <summary>
    /// Turns an Interaction SDK ray or poke select on the switch plate into the
    /// activation a pinch makes, so the switch answers the input the hub
    /// already answers.
    /// </summary>
    public sealed class KnifeSwitchPointer : PointableElement
    {
        WallKnifeSwitch target;
        KnifeSwitchInputKind kind;
        bool held;

        public void Bind(WallKnifeSwitch switchTarget, KnifeSwitchInputKind input)
        {
            target = switchTarget;
            kind = input;
        }

        public override void ProcessPointerEvent(PointerEvent evt)
        {
            if (target != null)
            {
                if (evt.Type == PointerEventType.Select)
                {
                    if (!held)
                    {
                        held = true;
                        target.PointerHeld(true);
                    }
                    target.Activate(kind);
                }
                else if ((evt.Type == PointerEventType.Unselect ||
                          evt.Type == PointerEventType.Cancel) && held)
                {
                    held = false;
                    target.PointerHeld(false);
                }
            }
            base.ProcessPointerEvent(evt);
        }
    }
}
