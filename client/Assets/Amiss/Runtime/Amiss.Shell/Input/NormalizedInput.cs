using System;
using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell.Input
{
    public enum InputModality
    {
        Hand,
        Controller
    }

    public enum InputSide
    {
        Unknown,
        Left,
        Right
    }

    public static class InputSourceSelector
    {
        public static IReadOnlyDictionary<InputSide, T> SelectBest<T>(IEnumerable<T> sources,
            Func<T, InputSide> side, Func<T, int> quality)
        {
            var selected = new Dictionary<InputSide, T>();
            foreach (var source in sources)
            {
                var sourceSide = side(source);
                if (sourceSide == InputSide.Unknown)
                    continue;
                if (!selected.TryGetValue(sourceSide, out var previous) ||
                    quality(source) > quality(previous))
                    selected[sourceSide] = source;
            }
            return selected;
        }
    }

    public enum InvalidInputReason
    {
        None,
        ProfileNotLocked,
        Stale,
        Duplicate,
        NonFinite,
        OutOfOrder,
        OverRate,
        OverSpeed,
        LowConfidence,
        SystemGesture
    }

    public sealed class InputLimitProfile
    {
        public string ProfileVersion { get; }
        public bool Locked { get; }
        public int MaxSampleAgeTicks { get; }
        public int MaxSamplesPerTick { get; }
        public float MinimumConfidence { get; }
        public float MaximumLinearSpeed { get; }
        public float MaximumAngularSpeed { get; }
        public float SwingEnterSpeed { get; }
        public float SwingExitSpeed { get; }
        public int EnterTicks { get; }
        public int ExitTicks { get; }

        public InputLimitProfile(
            string profileVersion,
            bool locked,
            int maxSampleAgeTicks,
            int maxSamplesPerTick,
            float minimumConfidence,
            float maximumLinearSpeed,
            float maximumAngularSpeed,
            float swingEnterSpeed,
            float swingExitSpeed,
            int enterTicks,
            int exitTicks)
        {
            if (string.IsNullOrWhiteSpace(profileVersion))
                throw new ArgumentException("A profile version is required", nameof(profileVersion));
            if (locked && (maxSampleAgeTicks < 0 || maxSamplesPerTick <= 0 ||
                           minimumConfidence < 0f || minimumConfidence > 1f ||
                           maximumLinearSpeed <= 0f || maximumAngularSpeed <= 0f ||
                           swingEnterSpeed <= 0f || swingExitSpeed < 0f ||
                           swingExitSpeed >= swingEnterSpeed || enterTicks <= 0 || exitTicks <= 0))
                throw new ArgumentException("Locked profile limits are invalid");
            ProfileVersion = profileVersion;
            Locked = locked;
            MaxSampleAgeTicks = maxSampleAgeTicks;
            MaxSamplesPerTick = maxSamplesPerTick;
            MinimumConfidence = minimumConfidence;
            MaximumLinearSpeed = maximumLinearSpeed;
            MaximumAngularSpeed = maximumAngularSpeed;
            SwingEnterSpeed = swingEnterSpeed;
            SwingExitSpeed = swingExitSpeed;
            EnterTicks = enterTicks;
            ExitTicks = exitTicks;
        }

        public static InputLimitProfile Unmeasured() => new(
            "declared-unmeasured", false, 0, 0, 0f, 0f, 0f, 0f, 0f, 0, 0);

        /// <summary>
        /// The one declared candidate the hackathon build installs so valid
        /// hand and controller samples reach the bat. Its values are
        /// <c>uncalibrated_declared_not_measured</c>: they are copied from the
        /// maintained edit-mode fixtures (<c>BatContactTests.cs</c>), not from a
        /// device run, and the Quest Pro fixture measurement of 05 section 9
        /// replaces them. The profile is labelled so no readout can present
        /// these numbers as measured limits.
        /// </summary>
        public static InputLimitProfile CandidateUnmeasured() => new(
            "quest-pro-candidate-unmeasured", true,
            maxSampleAgeTicks: 2, maxSamplesPerTick: 4, minimumConfidence: 0.5f,
            maximumLinearSpeed: 40f, maximumAngularSpeed: 20f,
            swingEnterSpeed: 3f, swingExitSpeed: 1f, enterTicks: 1, exitTicks: 1);
    }

    public readonly struct RawInputSample
    {
        public InputModality Modality { get; }
        public InputSide Side { get; }
        public long Sequence { get; }
        public int SourceTick { get; }
        public int TargetTick { get; }
        public Pose Pose { get; }
        public Vector3 LinearVelocity { get; }
        public Vector3 AngularVelocity { get; }
        public float TrackingConfidence { get; }
        public bool SystemGesture { get; }

        public RawInputSample(
            InputModality modality,
            InputSide side,
            long sequence,
            int sourceTick,
            int targetTick,
            Pose pose,
            Vector3 linearVelocity,
            Vector3 angularVelocity,
            float trackingConfidence,
            bool systemGesture = false)
        {
            Modality = modality;
            Side = side;
            Sequence = sequence;
            SourceTick = sourceTick;
            TargetTick = targetTick;
            Pose = pose;
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
            TrackingConfidence = trackingConfidence;
            SystemGesture = systemGesture;
        }
    }

    public readonly struct NormalizedToolCommand
    {
        public int TargetTick { get; }
        public InputSide Side { get; }
        public Pose Pose { get; }
        public Vector3 LinearVelocity { get; }
        public Vector3 AngularVelocity { get; }

        public NormalizedToolCommand(
            int targetTick,
            InputSide side,
            Pose pose,
            Vector3 linearVelocity,
            Vector3 angularVelocity)
        {
            TargetTick = targetTick;
            Side = side;
            Pose = pose;
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
        }
    }

    public readonly struct InputNormalizationResult
    {
        public bool Accepted { get; }
        public InvalidInputReason InvalidReason { get; }
        public NormalizedToolCommand Command { get; }

        public InputNormalizationResult(bool accepted, InvalidInputReason invalidReason,
            NormalizedToolCommand command = default)
        {
            Accepted = accepted;
            InvalidReason = invalidReason;
            Command = command;
        }
    }

    public sealed class InputNormalizer
    {
        struct ChannelState
        {
            public long Sequence;
            public int SourceTick;
            public int SamplesAtSourceTick;
        }

        readonly Dictionary<(InputModality, InputSide), ChannelState> channels = new();

        public InputLimitProfile Profile { get; }

        public InputNormalizer(InputLimitProfile profile)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        public InputNormalizationResult Normalize(RawInputSample sample)
        {
            if (!Finite(sample.Pose.position) || !Finite(sample.Pose.rotation) ||
                !Finite(sample.LinearVelocity) || !Finite(sample.AngularVelocity) ||
                !float.IsFinite(sample.TrackingConfidence))
                return Reject(InvalidInputReason.NonFinite);
            if (sample.SystemGesture)
                return Reject(InvalidInputReason.SystemGesture);
            if (sample.TrackingConfidence < Profile.MinimumConfidence)
                return Reject(InvalidInputReason.LowConfidence);
            if (!Profile.Locked)
                return Reject(InvalidInputReason.ProfileNotLocked);
            if (sample.TargetTick < sample.SourceTick || sample.TargetTick - sample.SourceTick > Profile.MaxSampleAgeTicks)
                return Reject(InvalidInputReason.Stale);
            if (sample.LinearVelocity.magnitude > Profile.MaximumLinearSpeed ||
                sample.AngularVelocity.magnitude > Profile.MaximumAngularSpeed)
                return Reject(InvalidInputReason.OverSpeed);

            var key = (sample.Modality, sample.Side);
            if (channels.TryGetValue(key, out var previous))
            {
                if (sample.Sequence == previous.Sequence)
                    return Reject(InvalidInputReason.Duplicate);
                if (sample.Sequence < previous.Sequence || sample.SourceTick < previous.SourceTick)
                    return Reject(InvalidInputReason.OutOfOrder);
                var count = sample.SourceTick == previous.SourceTick ? previous.SamplesAtSourceTick + 1 : 1;
                if (count > Profile.MaxSamplesPerTick)
                    return Reject(InvalidInputReason.OverRate);
                channels[key] = new ChannelState
                {
                    Sequence = sample.Sequence,
                    SourceTick = sample.SourceTick,
                    SamplesAtSourceTick = count
                };
            }
            else
            {
                channels[key] = new ChannelState
                {
                    Sequence = sample.Sequence,
                    SourceTick = sample.SourceTick,
                    SamplesAtSourceTick = 1
                };
            }

            return new InputNormalizationResult(true, InvalidInputReason.None,
                new NormalizedToolCommand(sample.TargetTick, sample.Side, sample.Pose,
                    sample.LinearVelocity, sample.AngularVelocity));
        }

        static InputNormalizationResult Reject(InvalidInputReason reason) => new(false, reason);

        static bool Finite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        static bool Finite(Quaternion value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) &&
            float.IsFinite(value.z) && float.IsFinite(value.w);
    }

    public sealed class InputDiagnosticLog
    {
        readonly int[] counts = new int[Enum.GetValues(typeof(InvalidInputReason)).Length];

        public int TotalInvalid { get; private set; }

        public void Record(InvalidInputReason reason)
        {
            if (reason == InvalidInputReason.None)
                return;
            counts[(int)reason]++;
            TotalInvalid++;
        }

        public int Count(InvalidInputReason reason) => counts[(int)reason];
    }

    public enum SwingActivity
    {
        Inactive,
        Candidate,
        Active,
        Exiting
    }

    public readonly struct SwingTransition
    {
        public int TargetTick { get; }
        public SwingActivity Previous { get; }
        public SwingActivity Current { get; }
        public bool InvalidClosure { get; }

        public SwingTransition(int targetTick, SwingActivity previous, SwingActivity current,
            bool invalidClosure = false)
        {
            TargetTick = targetTick;
            Previous = previous;
            Current = current;
            InvalidClosure = invalidClosure;
        }
    }

    public sealed class SwingActivityClassifier
    {
        readonly InputLimitProfile profile;
        int heldTicks;

        public SwingActivityClassifier(InputLimitProfile profile)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        public SwingActivity Activity { get; private set; } = SwingActivity.Inactive;

        public SwingTransition Update(NormalizedToolCommand command)
        {
            var previous = Activity;
            var speed = command.LinearVelocity.magnitude;
            switch (Activity)
            {
                case SwingActivity.Inactive:
                    if (speed >= profile.SwingEnterSpeed)
                    {
                        Activity = SwingActivity.Candidate;
                        heldTicks = 1;
                        if (heldTicks >= profile.EnterTicks)
                            Activity = SwingActivity.Active;
                    }
                    break;
                case SwingActivity.Candidate:
                    if (speed < profile.SwingEnterSpeed)
                    {
                        Activity = SwingActivity.Inactive;
                        heldTicks = 0;
                    }
                    else if (++heldTicks >= profile.EnterTicks)
                    {
                        Activity = SwingActivity.Active;
                        heldTicks = 0;
                    }
                    break;
                case SwingActivity.Active:
                    if (speed <= profile.SwingExitSpeed)
                    {
                        Activity = SwingActivity.Exiting;
                        heldTicks = 1;
                        if (heldTicks >= profile.ExitTicks)
                            Activity = SwingActivity.Inactive;
                    }
                    break;
                case SwingActivity.Exiting:
                    if (speed > profile.SwingExitSpeed)
                    {
                        Activity = SwingActivity.Active;
                        heldTicks = 0;
                    }
                    else if (++heldTicks >= profile.ExitTicks)
                    {
                        Activity = SwingActivity.Inactive;
                        heldTicks = 0;
                    }
                    break;
            }
            return new SwingTransition(command.TargetTick, previous, Activity);
        }

        public SwingTransition Abort(int targetTick)
        {
            var previous = Activity;
            Activity = SwingActivity.Inactive;
            heldTicks = 0;
            return new SwingTransition(targetTick, previous, Activity, true);
        }
    }

    public sealed class NormalizedInputReplayResult
    {
        public List<NormalizedToolCommand> Commands { get; } = new();
        public List<SwingTransition> Transitions { get; } = new();
        public InputDiagnosticLog Diagnostics { get; } = new();
    }

    public static class NormalizedInputReplay
    {
        public static NormalizedInputReplayResult Run(IEnumerable<RawInputSample> samples,
            InputLimitProfile profile)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            var normalizer = new InputNormalizer(profile);
            var classifier = new SwingActivityClassifier(profile);
            var result = new NormalizedInputReplayResult();
            foreach (var sample in samples)
            {
                var normalized = normalizer.Normalize(sample);
                if (!normalized.Accepted)
                {
                    result.Diagnostics.Record(normalized.InvalidReason);
                    result.Transitions.Add(classifier.Abort(sample.TargetTick));
                    continue;
                }
                result.Commands.Add(normalized.Command);
                result.Transitions.Add(classifier.Update(normalized.Command));
            }
            return result;
        }
    }
}
