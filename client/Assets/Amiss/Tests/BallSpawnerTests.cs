using System.Collections.Generic;
using Amiss.Shell.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// Teacher distribution knobs on the spawner (founder decision 2026-11-04,
    /// option A): count, uniform mass, speed scale, and one group drift vector.
    /// Zero drift reproduces the exact pre-drift spawn because the drift is
    /// added after the named-stream draws, mirroring
    /// <c>HeadlessBallWriter.setup</c>; zero spread plus drift gives one ball
    /// its exact specified velocity.
    /// </summary>
    public class BallSpawnerTests
    {
        static readonly Bounds Region =
            new(new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f));

        static BallState[] Spawn(ulong seed, int count, float speed, Vector3 drift)
        {
            Assert.IsTrue(BallSpawner.TrySpawn(new NamedRandomStreams(seed), count,
                FrozenWriterConfiguration.BallRadiusM, FrozenWriterConfiguration.BallMassKg,
                speed, drift, Region, new List<Bounds>(),
                FrozenWriterConfiguration.SpawnAttemptBound,
                out var balls, out var reason), reason);
            return balls;
        }

        [Test]
        public void ZeroDriftKeepsEveryVelocityInsideTheSpeedScale()
        {
            var balls = Spawn(11, 8, 1f, Vector3.zero);
            Assert.AreEqual(8, balls.Length);
            foreach (var ball in balls)
            {
                Assert.AreEqual(FrozenWriterConfiguration.BallMassKg, ball.MassKg);
                Assert.LessOrEqual(Mathf.Abs(ball.VelocityMPerS.x), 1f);
                Assert.LessOrEqual(Mathf.Abs(ball.VelocityMPerS.y), 1f);
                Assert.LessOrEqual(Mathf.Abs(ball.VelocityMPerS.z), 1f);
            }
        }

        [Test]
        public void DriftShiftsEveryVelocityExactly()
        {
            // Tolerance, not exactness: the runtime may fuse the spawn's
            // multiply-add into one rounding while the expected side here
            // rounds twice, so the two paths can differ by a float ulp.
            const float tolerance = 1e-6f;
            var drift = new Vector3(1.5f, -2f, 0.25f);
            var still = Spawn(11, 6, 1f, Vector3.zero);
            var moved = Spawn(11, 6, 1f, drift);
            Assert.AreEqual(still.Length, moved.Length);
            for (var index = 0; index < still.Length; index++)
            {
                Assert.AreEqual(still[index].PositionM, moved[index].PositionM,
                    "ball " + index + " position must not move with drift");
                var expected = still[index].VelocityMPerS + drift;
                var actual = moved[index].VelocityMPerS;
                Assert.AreEqual(expected.x, actual.x, tolerance, "ball " + index + " vx");
                Assert.AreEqual(expected.y, actual.y, tolerance, "ball " + index + " vy");
                Assert.AreEqual(expected.z, actual.z, tolerance, "ball " + index + " vz");
            }
        }

        [Test]
        public void SingleBallTakesAnExactVelocity()
        {
            var drift = new Vector3(1f, 2f, -0.5f);
            var balls = Spawn(11, 1, 0f, drift);
            Assert.AreEqual(1, balls.Length);
            Assert.AreEqual(drift, balls[0].VelocityMPerS);
        }

        [Test]
        public void NonFiniteDriftIsRefused()
        {
            Assert.IsFalse(BallSpawner.TrySpawn(new NamedRandomStreams(11), 4,
                FrozenWriterConfiguration.BallRadiusM, FrozenWriterConfiguration.BallMassKg,
                1f, new Vector3(float.NaN, 0f, 0f), Region, new List<Bounds>(),
                FrozenWriterConfiguration.SpawnAttemptBound,
                out _, out var reason));
            Assert.AreEqual("drift_not_finite", reason);
        }

        [Test]
        public void InvalidCountIsRefused()
        {
            Assert.IsFalse(BallSpawner.TrySpawn(new NamedRandomStreams(11), 0,
                FrozenWriterConfiguration.BallRadiusM, FrozenWriterConfiguration.BallMassKg,
                1f, Vector3.zero, Region, new List<Bounds>(),
                FrozenWriterConfiguration.SpawnAttemptBound,
                out _, out var reason));
            Assert.AreEqual("ball_count_invalid", reason);
        }
    }
}
