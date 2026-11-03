using System;
using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Amiss.Shell.Input
{
    public sealed class OvrToolInputAdapter : MonoBehaviour
    {
        struct PoseSample
        {
            public Pose Pose;
            public float Time;
        }

        readonly Dictionary<(InputModality, InputSide), PoseSample> previous = new();
        readonly Dictionary<(InputModality, InputSide), long> sequences = new();
        InputNormalizer normalizer;
        SwingActivityClassifier classifier;
        int targetTick;

        public event Action<NormalizedToolCommand> ToolSample;
        public event Action<SwingTransition> SwingChanged;

        /// <summary>
        /// Every adjudicated sample: the accepted normalized command with its
        /// validity evidence, or the stable reason it was rejected. This is the
        /// only path a tool may consume; the raw sample stays in the adapter and
        /// its bounded diagnostic.
        /// </summary>
        public event Action<InputNormalizationResult, int> SampleAdjudicated;

        public InputDiagnosticLog Diagnostics { get; } = new();

        /// <summary>
        /// The installed profile. The composition root runs its Awake before
        /// this component's Start and hands the bat the profile it reads then,
        /// so the candidate is also the pre-Start value; Start installs the
        /// same profile with fresh normalizer state.
        /// </summary>
        public InputLimitProfile Profile { get; private set; } =
            InputLimitProfile.CandidateUnmeasured();

        public int CurrentTick => targetTick;

#if DEVELOPMENT_BUILD
        public event Action<RawInputSample, InputNormalizationResult> SampleProcessed;
#endif

        void Start()
        {
            // The hackathon build adopts one declared, explicitly uncalibrated
            // candidate profile so valid hand/controller samples reach the bat.
            // This is a recorded deviation from 05 section 5.2 until the Quest
            // Pro fixture measurement replaces the candidate.
            SetProfile(InputLimitProfile.CandidateUnmeasured());
#if DEVELOPMENT_BUILD
            gameObject.AddComponent<InputEvidenceController>();
#endif
        }

        public void SetProfile(InputLimitProfile profile)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            normalizer = new InputNormalizer(profile);
            classifier = new SwingActivityClassifier(profile);
            previous.Clear();
            sequences.Clear();
        }

        void Update()
        {
            targetTick++;
            var hands = InputSourceSelector.SelectBest(
                FindObjectsByType<HandRef>(FindObjectsSortMode.None),
                hand => Side(hand.Handedness), HandQuality);
            foreach (var hand in hands.Values)
                SampleHand(hand);
            var controllers = InputSourceSelector.SelectBest(
                FindObjectsByType<ControllerRef>(FindObjectsSortMode.None),
                controller => Side(controller.Handedness), ControllerQuality);
            foreach (var controller in controllers.Values)
                SampleController(controller);
        }

        static int HandQuality(HandRef hand)
        {
            if (!hand.IsConnected)
                return 0;
            if (!hand.IsTrackedDataValid || !hand.GetPointerPose(out _))
                return 1;
            return hand.IsHighConfidence ? 3 : 2;
        }

        static int ControllerQuality(ControllerRef controller)
        {
            if (!controller.IsConnected)
                return 0;
            return controller.IsPoseValid && controller.TryGetPointerPose(out _) ? 2 : 1;
        }

        void SampleHand(HandRef hand)
        {
            var side = Side(hand.Handedness);
            if (side == InputSide.Unknown)
                return;
            if (!hand.IsConnected || !hand.IsTrackedDataValid || !hand.GetPointerPose(out var pose))
            {
                Submit(new RawInputSample(InputModality.Hand, side, NextSequence(InputModality.Hand, side),
                    targetTick - 1, targetTick, default, Vector3.zero, Vector3.zero, 0f));
                return;
            }
            var velocity = Velocity(InputModality.Hand, side, pose);
            Submit(new RawInputSample(InputModality.Hand, side, NextSequence(InputModality.Hand, side),
                targetTick - 1, targetTick, pose, velocity.linear, velocity.angular,
                hand.IsHighConfidence ? 1f : 0.5f));
        }

        void SampleController(ControllerRef controller)
        {
            var side = Side(controller.Handedness);
            if (side == InputSide.Unknown)
                return;
            if (!controller.IsConnected || !controller.IsPoseValid || !controller.TryGetPointerPose(out var pose))
            {
                Submit(new RawInputSample(InputModality.Controller, side,
                    NextSequence(InputModality.Controller, side), targetTick - 1, targetTick,
                    default, Vector3.zero, Vector3.zero, 0f));
                return;
            }
            var velocity = Velocity(InputModality.Controller, side, pose);
            Submit(new RawInputSample(InputModality.Controller, side,
                NextSequence(InputModality.Controller, side), targetTick - 1, targetTick,
                pose, velocity.linear, velocity.angular, 1f));
        }

        void Submit(RawInputSample sample)
        {
            var normalized = normalizer.Normalize(sample);
#if DEVELOPMENT_BUILD
            SampleProcessed?.Invoke(sample, normalized);
#endif
            SampleAdjudicated?.Invoke(normalized, sample.TargetTick);
            if (!normalized.Accepted)
            {
                Diagnostics.Record(normalized.InvalidReason);
                var closure = classifier.Abort(sample.TargetTick);
                if (closure.Previous != closure.Current)
                    SwingChanged?.Invoke(closure);
                return;
            }
            ToolSample?.Invoke(normalized.Command);
            var transition = classifier.Update(normalized.Command);
            if (transition.Previous != transition.Current)
                SwingChanged?.Invoke(transition);
        }

        long NextSequence(InputModality modality, InputSide side)
        {
            var key = (modality, side);
            sequences.TryGetValue(key, out var next);
            next++;
            sequences[key] = next;
            return next;
        }

        (Vector3 linear, Vector3 angular) Velocity(InputModality modality, InputSide side, Pose pose)
        {
            var key = (modality, side);
            var now = Time.unscaledTime;
            if (!previous.TryGetValue(key, out var last) || now <= last.Time)
            {
                previous[key] = new PoseSample { Pose = pose, Time = now };
                return (Vector3.zero, Vector3.zero);
            }
            var dt = now - last.Time;
            var linear = (pose.position - last.Pose.position) / dt;
            var delta = pose.rotation * Quaternion.Inverse(last.Pose.rotation);
            delta.ToAngleAxis(out var angle, out var axis);
            if (angle > 180f)
                angle -= 360f;
            var angular = float.IsFinite(angle) && float.IsFinite(axis.x)
                ? axis * (angle * Mathf.Deg2Rad / dt)
                : Vector3.zero;
            previous[key] = new PoseSample { Pose = pose, Time = now };
            return (linear, angular);
        }

        static InputSide Side(Handedness handedness) => handedness switch
        {
            Handedness.Left => InputSide.Left,
            Handedness.Right => InputSide.Right,
            _ => InputSide.Unknown
        };
    }
}
