using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Amiss.Shell.Input
{
    [Serializable]
    public sealed class InputReasonEvidence
    {
        public string reason;
        public int count;
    }

    [Serializable]
    public sealed class InputChannelEvidence
    {
        public string modality;
        public string side;
        public int observed_samples;
        public int accepted_samples;
        public int maximum_sample_age_ticks;
        public int maximum_samples_per_source_tick;
        public int speed_sample_count;
        public float mean_linear_speed;
        public float maximum_linear_speed;
        public float mean_angular_speed;
        public float maximum_angular_speed;
    }

    [Serializable]
    public sealed class InputEvidenceEntry
    {
        public string scenario_id;
        public string outcome;
        public string source;
        public string profile_version;
        public bool profile_locked;
        public float elapsed_seconds;
        public float seconds_to_first_accepted;
        public List<InputReasonEvidence> invalid_reasons = new();
        public List<InputChannelEvidence> channels = new();
    }

    [Serializable]
    public sealed class InputEvidenceManifest
    {
        public const string CurrentVersion = "1.0.0";
        public string evidence_manifest_version = CurrentVersion;
        public string application_version;
        public string unity_version;
        public string framework_api_version;
        public List<InputEvidenceEntry> entries = new();

        public static InputEvidenceManifest Create() => new()
        {
            application_version = Application.version,
            unity_version = Application.unityVersion,
            framework_api_version = Contracts.FrameworkApiVersions.FrameworkApi
        };

        public string ToJson() => JsonUtility.ToJson(this, true);

        public string WriteAtomic(string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "input-evidence.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, ToJson());
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
            return path;
        }
    }

    public sealed class InputEvidenceAccumulator
    {
        sealed class ChannelAggregate
        {
            public int Observed;
            public int Accepted;
            public int MaximumAge;
            public int MaximumRate;
            public int RateTick = int.MinValue;
            public int RateCount;
            public int SpeedCount;
            public double LinearSum;
            public double AngularSum;
            public float MaximumLinear;
            public float MaximumAngular;
        }

        readonly Dictionary<(InputModality, InputSide), ChannelAggregate> channels = new();
        readonly Dictionary<InvalidInputReason, int> invalid = new();
        float startedAt;
        float firstAcceptedAt = -1f;

        public void Begin(float now)
        {
            startedAt = now;
            firstAcceptedAt = -1f;
            channels.Clear();
            invalid.Clear();
        }

        public void Observe(RawInputSample sample, InputNormalizationResult result, float now)
        {
            var key = (sample.Modality, sample.Side);
            if (!channels.TryGetValue(key, out var channel))
            {
                channel = new ChannelAggregate();
                channels.Add(key, channel);
            }

            channel.Observed++;
            channel.MaximumAge = Math.Max(channel.MaximumAge, sample.TargetTick - sample.SourceTick);
            if (channel.RateTick != sample.SourceTick)
            {
                channel.RateTick = sample.SourceTick;
                channel.RateCount = 0;
            }
            channel.RateCount++;
            channel.MaximumRate = Math.Max(channel.MaximumRate, channel.RateCount);

            if (result.Accepted)
            {
                channel.Accepted++;
                if (firstAcceptedAt < 0f)
                    firstAcceptedAt = now;
            }
            else
            {
                invalid[result.InvalidReason] = invalid.GetValueOrDefault(result.InvalidReason) + 1;
            }

            var linear = sample.LinearVelocity.magnitude;
            var angular = sample.AngularVelocity.magnitude;
            if (sample.TrackingConfidence <= 0f || !float.IsFinite(linear) || !float.IsFinite(angular))
                return;
            channel.SpeedCount++;
            channel.LinearSum += linear;
            channel.AngularSum += angular;
            channel.MaximumLinear = Math.Max(channel.MaximumLinear, linear);
            channel.MaximumAngular = Math.Max(channel.MaximumAngular, angular);
        }

        public InputEvidenceEntry Complete(string scenarioId, string outcome, string source,
            InputLimitProfile profile, float now)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            var entry = new InputEvidenceEntry
            {
                scenario_id = Clean(scenarioId),
                outcome = Clean(outcome),
                source = Clean(source),
                profile_version = profile.ProfileVersion,
                profile_locked = profile.Locked,
                elapsed_seconds = Math.Max(0f, now - startedAt),
                seconds_to_first_accepted = firstAcceptedAt < 0f ? -1f : Math.Max(0f, firstAcceptedAt - startedAt)
            };
            foreach (var pair in invalid.OrderBy(pair => pair.Key))
                entry.invalid_reasons.Add(new InputReasonEvidence
                {
                    reason = pair.Key.ToString(),
                    count = pair.Value
                });
            foreach (var pair in channels.OrderBy(pair => pair.Key.Item1).ThenBy(pair => pair.Key.Item2))
            {
                var value = pair.Value;
                entry.channels.Add(new InputChannelEvidence
                {
                    modality = pair.Key.Item1.ToString(),
                    side = pair.Key.Item2.ToString(),
                    observed_samples = value.Observed,
                    accepted_samples = value.Accepted,
                    maximum_sample_age_ticks = value.MaximumAge,
                    maximum_samples_per_source_tick = value.MaximumRate,
                    speed_sample_count = value.SpeedCount,
                    mean_linear_speed = value.SpeedCount == 0 ? 0f : (float)(value.LinearSum / value.SpeedCount),
                    maximum_linear_speed = value.MaximumLinear,
                    mean_angular_speed = value.SpeedCount == 0 ? 0f : (float)(value.AngularSum / value.SpeedCount),
                    maximum_angular_speed = value.MaximumAngular
                });
            }
            return entry;
        }

        static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return clean.Length <= 160 ? clean : clean.Substring(0, 160);
        }
    }
}
