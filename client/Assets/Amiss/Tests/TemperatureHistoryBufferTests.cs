using System;
using System.Collections.Generic;
using Amiss.Shell.Simulation;
using Amiss.Shell.UI;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// The wall glass chart history: one bounded ring per Trial, fed only from
    /// the producer's published unrounded <c>T_kin</c>. The graph never draws a
    /// number the producer did not publish, and never joins two Trials.
    /// </summary>
    public class TemperatureHistoryBufferTests
    {
        [Test]
        public void RingHoldsTheDeclaredCapacityAndDropsTheOldest()
        {
            var buffer = new TemperatureHistoryBuffer();
            for (var index = 0; index < TemperatureHistoryBuffer.Capacity + 5; index++)
                Assert.IsTrue(buffer.Feed(Readout("trial-a", index * 10, 1.1f)),
                    "sample " + index);

            Assert.AreEqual(TemperatureHistoryBuffer.Capacity, buffer.Count);
            Assert.AreEqual(5 * 10, buffer.SampleAt(0).Tick, "oldest five samples were dropped");
            Assert.AreEqual((TemperatureHistoryBuffer.Capacity + 4) * 10,
                buffer.SampleAt(buffer.Count - 1).Tick);
        }

        [Test]
        public void SamplesFollowTheDeclaredCadence()
        {
            var buffer = new TemperatureHistoryBuffer();
            Assert.IsTrue(buffer.Feed(Readout("trial-a", 0, 1.1f)));
            Assert.IsFalse(buffer.Feed(Readout("trial-a", 5, 1.1f)),
                "a sample inside the declared cadence adds nothing");
            Assert.AreEqual(1, buffer.Count);

            Assert.IsTrue(buffer.Feed(Readout("trial-a", 10, 1.1f)),
                "one sample per declared cadence of trial time");
            Assert.AreEqual(2, buffer.Count);
            Assert.IsFalse(buffer.Feed(Readout("trial-a", 10, 1.1f)),
                "the same published readout is not a second sample");
            Assert.AreEqual(2, buffer.Count);
        }

        [Test]
        public void NewTrialStartsANewSegment()
        {
            var buffer = new TemperatureHistoryBuffer();
            Assert.IsTrue(buffer.Feed(Readout("trial-a", 10, 1.0f)));
            Assert.IsTrue(buffer.Feed(Readout("trial-a", 20, 1.1f)));
            Assert.AreEqual(2, buffer.Count);

            Assert.IsTrue(buffer.Feed(Readout("trial-b", 10, 1.2f)));
            Assert.AreEqual(1, buffer.Count, "no sample of the previous Trial survives");
            Assert.AreEqual("trial-b", buffer.TrialId);
            Assert.AreEqual(buffer.SampleAt(0).Kelvin, buffer.MinKelvin);
            Assert.AreEqual(buffer.SampleAt(0).Kelvin, buffer.MaxKelvin);
        }

        [Test]
        public void RestartedTrialUnderTheSameIdStartsANewSegment()
        {
            var buffer = new TemperatureHistoryBuffer();
            Assert.IsTrue(buffer.Feed(Readout("trial-local-0001", 100, 1.0f)));
            Assert.IsTrue(buffer.Feed(Readout("trial-local-0001", 110, 1.1f)));
            Assert.AreEqual(2, buffer.Count);

            Assert.IsTrue(buffer.Feed(Readout("trial-local-0001", 20, 1.2f)),
                "the published clock went back to the start of a Trial");
            Assert.AreEqual(1, buffer.Count,
                "a restarted Trial does not reuse the previous Trial's samples");
            Assert.AreEqual(buffer.SampleAt(0).Kelvin, buffer.MinKelvin);
            Assert.AreEqual(buffer.SampleAt(0).Kelvin, buffer.MaxKelvin);
        }

        [Test]
        public void NonFiniteAndNotApplicableReadoutsAddNoSample()
        {
            var buffer = new TemperatureHistoryBuffer();
            Assert.IsTrue(buffer.Feed(Readout("trial-a", 10, 1.1f)));

            Assert.IsFalse(buffer.Feed(HandMeasured("trial-a", 20, double.NaN)));
            Assert.IsFalse(buffer.Feed(HandMeasured("trial-a", 30, double.PositiveInfinity)));
            Assert.IsFalse(buffer.Feed(NotApplicable("trial-a", 40)));
            Assert.IsFalse(buffer.Feed(null));

            Assert.AreEqual(1, buffer.Count);
            Assert.AreEqual(2, buffer.RejectedCount);
        }

        [Test]
        public void ExtremesTrackTheBufferAndFollowEviction()
        {
            var buffer = new TemperatureHistoryBuffer();
            var low = Readout("trial-a", 0, 1.0f);
            var high = Readout("trial-a", 10, 1.2f);
            Assert.IsTrue(buffer.Feed(low));
            Assert.IsTrue(buffer.Feed(high));
            Assert.IsTrue(buffer.Feed(Readout("trial-a", 20, 1.1f)));

            Assert.AreEqual(low.Measurement.TK.Value, buffer.MinKelvin, 1e-9);
            Assert.AreEqual(high.Measurement.TK.Value, buffer.MaxKelvin, 1e-9);

            for (var index = 3; index < TemperatureHistoryBuffer.Capacity + 3; index++)
                Assert.IsTrue(buffer.Feed(Readout("trial-a", index * 10, 1.1f)));

            Assert.AreEqual(TemperatureHistoryBuffer.Capacity, buffer.Count);
            Assert.AreEqual(300.0, buffer.MinKelvin, 1e-3,
                "the evicted extreme no longer bounds the axis");
            Assert.AreEqual(300.0, buffer.MaxKelvin, 1e-3);
        }

        static TemperatureReadoutSnapshot Readout(string trialId, int tick, float speed)
        {
            var measurement = TemperatureMeasurementFace.Measure(
                "measurement-" + tick, ScalePointBalls(speed),
                new MeasurementWindow("window-" + tick, 0, tick,
                    tick * FrozenWriterConfiguration.FixedDeltaTimeSeconds, tick + 1),
                initialTK: 300.0);
            return new TemperatureReadoutSnapshot(trialId, tick, "view-" + tick, measurement,
                new TemperatureEnergyLedger(1.0, 1.0, 1.0, 0.0), 0);
        }

        static TemperatureReadoutSnapshot HandMeasured(string trialId, int tick, double kelvin) =>
            new(trialId, tick, "view-" + tick,
                new TemperatureMeasurement("measurement-" + tick,
                    new MeasurementWindow("window-" + tick, 0, tick,
                        tick * FrozenWriterConfiguration.FixedDeltaTimeSeconds, tick + 1),
                    TemperatureMeasurement.ValidStatus, 12, 12, 0, 1.0, 1.0, 0.0, 0.0,
                    Array.Empty<double>(), 0.0, 0.0, kelvin, 300.0, false,
                    new TemperatureDisplayTuple(300, 27, 80, 1.0), "3N_free", 10, "1.0.0",
                    "1.0.0"),
                new TemperatureEnergyLedger(1.0, 1.0, 1.0, 0.0), 0);

        static TemperatureReadoutSnapshot NotApplicable(string trialId, int tick)
        {
            var balls = new List<PublishedBall>
            {
                new("ball-000", Vector3.zero, Vector3.one, 1f, 0.05f, "free")
            };
            var measurement = TemperatureMeasurementFace.Measure("measurement-na", balls,
                new MeasurementWindow("window-na", 0, tick,
                    tick * FrozenWriterConfiguration.FixedDeltaTimeSeconds, tick + 1),
                initialTK: null);
            return new TemperatureReadoutSnapshot(trialId, tick, "view-na", measurement,
                new TemperatureEnergyLedger(1.0, 1.0, 1.0, 0.0), 0);
        }

        static IReadOnlyList<PublishedBall> ScalePointBalls(float speed)
        {
            // Equal masses in opposite velocity pairs: the center of mass is
            // zero, so v_rms is the requested speed and T follows it.
            var directions = new[]
            {
                new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
                new Vector3(0f, 0f, 1f)
            };
            var balls = new List<PublishedBall>();
            for (var index = 0; index < 12; index++)
            {
                var sign = index % 2 == 0 ? 1f : -1f;
                balls.Add(new PublishedBall("ball-" + index.ToString("D2"), Vector3.zero,
                    directions[(index / 2) % 3] * (sign * speed), 1f, 0.05f, "free"));
            }
            return balls;
        }
    }
}
