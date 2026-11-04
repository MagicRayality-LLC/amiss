using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// Deterministic initial setup. It mirrors the headless oracle's draw
    /// order and placement arithmetic exactly (six named-stream draws per
    /// ball, then a uniform position inside the pinned bounds), and adds only
    /// the 07 section 2 requirement the oracle does not carry: finite,
    /// non-overlapping balls inside the usable region, within a declared
    /// attempt bound. Exhausting the bound rejects the setup; it never
    /// reduces the ball count or overlaps balls to make a card pass.
    /// The teacher's group drift vector is added after the random draws, so
    /// zero drift reproduces the exact pre-drift spawn, and zero spread plus
    /// drift gives one ball its exact specified velocity.
    /// </summary>
    public static class BallSpawner
    {
        public static bool TrySpawn(NamedRandomStreams streams, int ballCount,
            float radiusM, float massKg, float speedScaleMPerS, Vector3 driftMPerS,
            Bounds region, IReadOnlyList<Bounds> excluded, int attemptBound,
            out BallState[] balls, out string reason)
        {
            balls = Array.Empty<BallState>();
            reason = "";
            if (streams == null)
            {
                reason = "seed_streams_missing";
                return false;
            }
            if (ballCount <= 0)
            {
                reason = "ball_count_invalid";
                return false;
            }
            if (!Room.RoomGeometry.IsFinite(driftMPerS))
            {
                reason = "drift_not_finite";
                return false;
            }
            if (!Room.RoomGeometry.IsFinite(region.center) ||
                !Room.RoomGeometry.IsFinite(region.size))
            {
                reason = "region_not_finite";
                return false;
            }

            var span = new Vector3(
                Mathf.Max(0f, region.size.x - 2f * radiusM),
                Mathf.Max(0f, region.size.y - 2f * radiusM),
                Mathf.Max(0f, region.size.z - 2f * radiusM));
            for (var axis = 0; axis < 3; axis++)
                if (span[axis] <= 0f)
                {
                    reason = "region_too_small_for_radius";
                    return false;
                }

            var placed = new List<BallState>(ballCount);
            var attempts = Mathf.Max(1, attemptBound);
            for (var index = 0; index < ballCount; index++)
            {
                var accepted = false;
                for (var attempt = 0; attempt < attempts && !accepted; attempt++)
                {
                    var draws = streams.Draw(FrozenWriterConfiguration.DefaultSeedStreamId, 6);
                    var position = new Vector3(
                        region.min.x + radiusM + (float)draws[0] * span.x,
                        region.min.y + radiusM + (float)draws[1] * span.y,
                        region.min.z + radiusM + (float)draws[2] * span.z);
                    var velocity = new Vector3(
                        ((float)draws[3] * 2f - 1f) * speedScaleMPerS + driftMPerS.x,
                        ((float)draws[4] * 2f - 1f) * speedScaleMPerS + driftMPerS.y,
                        ((float)draws[5] * 2f - 1f) * speedScaleMPerS + driftMPerS.z);

                    if (!IsClear(position, radiusM, placed, excluded))
                        continue;

                    placed.Add(new BallState(
                        "ball-" + (index + 1).ToString("D4", CultureInfo.InvariantCulture),
                        position, velocity, massKg, radiusM));
                    accepted = true;
                }
                if (!accepted)
                {
                    reason = "spawn_attempt_bound_exhausted";
                    return false;
                }
            }

            balls = placed.ToArray();
            return true;
        }

        static bool IsClear(Vector3 position, float radius, List<BallState> placed,
            IReadOnlyList<Bounds> excluded)
        {
            for (var index = 0; index < placed.Count; index++)
            {
                var separation = placed[index].PositionM - position;
                var reach = placed[index].RadiusM + radius;
                if (separation.sqrMagnitude < reach * reach)
                    return false;
            }
            if (excluded == null)
                return true;
            for (var index = 0; index < excluded.Count; index++)
            {
                var bounds = excluded[index];
                if (bounds.SqrDistance(position) < radius * radius)
                    return false;
            }
            return true;
        }
    }
}
