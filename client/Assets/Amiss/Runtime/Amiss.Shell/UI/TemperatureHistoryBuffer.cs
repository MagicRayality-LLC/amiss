using System;
using Amiss.Shell.Simulation;

namespace Amiss.Shell.UI
{
    /// <summary>One published sample of the Trial's unrounded model temperature.</summary>
    public readonly struct TemperatureHistorySample
    {
        public TemperatureHistorySample(int tick, double elapsedSeconds, double kelvin)
        {
            Tick = tick;
            ElapsedSeconds = elapsedSeconds;
            Kelvin = kelvin;
        }

        /// <summary>The Trial Tick the producer published this sample at.</summary>
        public int Tick { get; }

        /// <summary>Seconds since the start of the same Trial, from the producer's window.</summary>
        public double ElapsedSeconds { get; }

        /// <summary>The producer's unrounded <c>T_kin</c>; this buffer never rounds it.</summary>
        public double Kelvin { get; }
    }

    /// <summary>
    /// The bounded temperature-over-time history of one Trial, for the wall
    /// glass chart. It copies the producer's own published <c>T_kin</c> and
    /// never samples, converts, or rounds a temperature itself.
    ///
    /// The feed path allocates nothing: one preallocated ring of samples plus
    /// the two running extremes. A sample from a different Trial id clears the
    /// ring first, so a drawn line never connects two Trials. Feeding is
    /// idempotent within the declared cadence, so the render frame rate cannot
    /// add samples of its own.
    /// </summary>
    public sealed class TemperatureHistoryBuffer
    {
        // Presentation choices, not measurement constants: the glass holds ten
        // minutes of trial time at two samples per second of trial time, and the
        // newest sample replaces the oldest once the ring is full.
        public const int Capacity = 1200;
        public const double SampleCadenceSeconds = 0.5;

        readonly TemperatureHistorySample[] samples = new TemperatureHistorySample[Capacity];
        int start;
        int count;
        string trialId = "";
        double lastElapsedSeconds = double.NegativeInfinity;
        long revision;

        public int Count => count;
        public bool IsEmpty => count == 0;
        public string TrialId => trialId;

        /// <summary>Increments once per accepted sample, so a view repaints only on new data.</summary>
        public long Revision => revision;

        /// <summary>Lowest buffered kelvin; zero while the ring is empty.</summary>
        public double MinKelvin { get; private set; }

        /// <summary>Highest buffered kelvin; zero while the ring is empty.</summary>
        public double MaxKelvin { get; private set; }

        public double LastElapsedSeconds => count == 0 ? 0.0 : SampleAt(count - 1).ElapsedSeconds;

        /// <summary>Samples refused for a non-finite value or an invalid window.</summary>
        public long RejectedCount { get; private set; }

        /// <summary>
        /// Copies one published readout into the ring. A null, trial-less,
        /// not-applicable, or non-finite readout adds nothing, so the graph
        /// never invents a point. True when the ring took a new sample.
        /// </summary>
        public bool Feed(TemperatureReadoutSnapshot readout)
        {
            var measurement = readout?.Measurement;
            if (measurement == null || !measurement.Applicable || measurement.TK == null)
                return false;
            var kelvin = measurement.TK.Value;
            var window = measurement.Window;
            if (!double.IsFinite(kelvin) || window == null)
            {
                RejectedCount++;
                return false;
            }

            if (!string.Equals(trialId, readout.TrialId, StringComparison.Ordinal))
                Reset(readout.TrialId);

            var elapsed = window.DurationSeconds;
            if (!double.IsFinite(elapsed) || elapsed < 0.0)
            {
                RejectedCount++;
                return false;
            }
            if (count > 0 && elapsed < lastElapsedSeconds)
            {
                // The producer's clock only runs forward inside one Trial, and
                // the Trial id is not unique across restarts: a published time
                // before the last sample is a new Trial, so it starts a segment.
                Reset(readout.TrialId);
            }
            if (count > 0 && elapsed <= lastElapsedSeconds)
                return false;
            if (count > 0 && elapsed - lastElapsedSeconds < SampleCadenceSeconds)
                return false;

            Append(new TemperatureHistorySample(window.EndTick, elapsed, kelvin));
            return true;
        }

        /// <summary>Empties the ring; a new Trial id also empties it on the next feed.</summary>
        public void Reset(string newTrialId = null)
        {
            start = 0;
            count = 0;
            trialId = newTrialId ?? "";
            lastElapsedSeconds = double.NegativeInfinity;
            MinKelvin = 0.0;
            MaxKelvin = 0.0;
            revision++;
        }

        /// <summary>Oldest-first sample at <paramref name="index"/>.</summary>
        public TemperatureHistorySample SampleAt(int index)
        {
            if (index < 0 || index >= count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return samples[(start + index) % Capacity];
        }

        void Append(TemperatureHistorySample sample)
        {
            if (count == Capacity)
            {
                var evicted = samples[start];
                start = (start + 1) % Capacity;
                count--;
                if (count == 0)
                {
                    MinKelvin = 0.0;
                    MaxKelvin = 0.0;
                }
                else if (evicted.Kelvin <= MinKelvin || evicted.Kelvin >= MaxKelvin)
                {
                    RecomputeExtremes();
                }
            }

            samples[(start + count) % Capacity] = sample;
            count++;
            if (count == 1)
            {
                MinKelvin = sample.Kelvin;
                MaxKelvin = sample.Kelvin;
            }
            else
            {
                if (sample.Kelvin < MinKelvin)
                    MinKelvin = sample.Kelvin;
                if (sample.Kelvin > MaxKelvin)
                    MaxKelvin = sample.Kelvin;
            }
            lastElapsedSeconds = sample.ElapsedSeconds;
            revision++;
        }

        /// <summary>
        /// Runs only when the evicted sample held an extreme, so the common
        /// append stays constant time and no allocation is made either way.
        /// </summary>
        void RecomputeExtremes()
        {
            var min = double.PositiveInfinity;
            var max = double.NegativeInfinity;
            for (var index = 0; index < count; index++)
            {
                var value = SampleAt(index).Kelvin;
                if (value < min)
                    min = value;
                if (value > max)
                    max = value;
            }
            MinKelvin = min;
            MaxKelvin = max;
        }
    }
}
