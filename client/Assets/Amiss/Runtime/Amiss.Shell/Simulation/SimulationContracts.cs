using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// One mutable scientific entity inside the live runtime. It is never
    /// exposed outside the runtime: publications copy every value.
    /// </summary>
    public sealed class BallState
    {
        public BallState(string entityId, Vector3 positionM, Vector3 velocityMPerS,
            float massKg, float radiusM)
        {
            EntityId = entityId ?? "";
            PositionM = positionM;
            VelocityMPerS = velocityMPerS;
            MassKg = massKg;
            RadiusM = radiusM;
        }

        public string EntityId { get; }
        public Vector3 PositionM { get; set; }
        public Vector3 VelocityMPerS { get; set; }
        public float MassKg { get; }
        public float RadiusM { get; }
        public string Membership { get; set; } = "free";
    }

    /// <summary>Value copy of one ball in an immutable published view.</summary>
    public sealed class PublishedBall
    {
        public PublishedBall(string entityId, Vector3 positionM, Vector3 velocityMPerS,
            float massKg, float radiusM, string membership)
        {
            EntityId = entityId ?? "";
            PositionM = positionM;
            VelocityMPerS = velocityMPerS;
            MassKg = massKg;
            RadiusM = radiusM;
            Membership = membership ?? "free";
        }

        public string EntityId { get; }
        public Vector3 PositionM { get; }
        public Vector3 VelocityMPerS { get; }
        public float MassKg { get; }
        public float RadiusM { get; }
        public string Membership { get; }
    }

    /// <summary>
    /// Immutable projection of one completed Tick. Presentation, measurement,
    /// and evidence consumers read this and can never touch live state, so no
    /// render object, chart, or callback becomes a second writer.
    /// </summary>
    public sealed class TrialStateView
    {
        public TrialStateView(string viewId, string trialId, int tick,
            IReadOnlyList<PublishedBall> balls, string collisionRepresentationId,
            string collisionChecksum, string writerConfigurationId)
        {
            ViewId = viewId ?? "";
            TrialId = trialId ?? "";
            Tick = tick;
            Balls = balls ?? Array.Empty<PublishedBall>();
            CollisionRepresentationId = collisionRepresentationId ?? "";
            CollisionChecksum = collisionChecksum ?? "";
            WriterConfigurationId = writerConfigurationId ?? "";
        }

        public string ViewId { get; }
        public string TrialId { get; }
        public int Tick { get; }
        public IReadOnlyList<PublishedBall> Balls { get; }
        public string CollisionRepresentationId { get; }
        public string CollisionChecksum { get; }
        public string WriterConfigurationId { get; }

        public static TrialStateView From(string trialId, int tick, int sequence,
            IReadOnlyList<BallState> balls, string representationId, string checksum,
            string writerConfigurationId)
        {
            var copies = new PublishedBall[balls.Count];
            for (var index = 0; index < balls.Count; index++)
            {
                var ball = balls[index];
                copies[index] = new PublishedBall(ball.EntityId, ball.PositionM,
                    ball.VelocityMPerS, ball.MassKg, ball.RadiusM, ball.Membership);
            }
            return new TrialStateView(
                "view-" + trialId + "-" + tick.ToString("D8", CultureInfo.InvariantCulture) +
                "-" + sequence.ToString("D6", CultureInfo.InvariantCulture),
                trialId, tick,
                new ReadOnlyCollection<PublishedBall>(copies),
                representationId, checksum, writerConfigurationId);
        }
    }

    /// <summary>
    /// Named, purpose-separated SHA-256 counter streams. This mirrors the
    /// approved headless oracle exactly: the material is
    /// <c>expansion_id \0 layout_version \0 root_seed \0 stream \0 draw_index</c>,
    /// and the draw is the first eight digest bytes as a big-endian fraction.
    /// Adding a presentation read cannot consume a scientific draw because the
    /// counter belongs to the named stream only.
    /// </summary>
    public sealed class NamedRandomStreams
    {
        public const string ExpansionId = "stable-stream-name-v1";
        public const string LayoutVersion = "1.0.0";
        const double TwoTo64 = 18446744073709551616.0;

        readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);

        public NamedRandomStreams(ulong rootSeed)
        {
            RootSeed = rootSeed;
        }

        public ulong RootSeed { get; }

        public double DrawUniform(string streamName)
        {
            var name = streamName ?? "";
            var index = counts.TryGetValue(name, out var count) ? count : 0;
            var material = string.Join("\0", ExpansionId, LayoutVersion,
                RootSeed.ToString(CultureInfo.InvariantCulture), name,
                index.ToString(CultureInfo.InvariantCulture));
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
            counts[name] = index + 1;
            ulong value = 0;
            for (var byteIndex = 0; byteIndex < 8; byteIndex++)
                value = (value << 8) | digest[byteIndex];
            return value / TwoTo64;
        }

        public IReadOnlyList<double> Draw(string streamName, int count)
        {
            var draws = new double[Mathf.Max(0, count)];
            for (var index = 0; index < draws.Length; index++)
                draws[index] = DrawUniform(streamName);
            return draws;
        }

        public int DrawCount(string streamName) =>
            counts.TryGetValue(streamName ?? "", out var count) ? count : 0;

        public IReadOnlyDictionary<string, int> Counts() =>
            new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(counts,
                StringComparer.Ordinal));
    }

    /// <summary>Stable seed derivation from stable identifiers, never from draw order.</summary>
    public static class StableSeed
    {
        public static ulong From(string scope, string identity)
        {
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(
                Encoding.UTF8.GetBytes((scope ?? "") + "\0" + (identity ?? "")));
            ulong value = 0;
            for (var index = 0; index < 8; index++)
                value = (value << 8) | digest[index];
            return value;
        }
    }

    /// <summary>
    /// Whether a writer may be bound to a product Trial. Approved design 02
    /// section 3.1 rejects the frozen Unity rigidbody writer after the locked
    /// gate; it stays available as an archived evaluation artifact and is
    /// never bound, while the custom hard-sphere writer is the one product
    /// binding. <see cref="ScientificWriterSlot"/> enforces this at the single
    /// binding point, so no factory, test, or harness can bind a rejected
    /// configuration to a Trial.
    /// </summary>
    public enum WriterDisposition
    {
        ProductTrial,
        ArchivedOnly
    }

    /// <summary>Contact accounting of one completed fixed step.</summary>
    public readonly struct ContactStepFacts
    {
        public ContactStepFacts(int wallContacts, int pairContacts, bool iterationBoundExhausted)
        {
            WallContacts = wallContacts;
            PairContacts = pairContacts;
            IterationBoundExhausted = iterationBoundExhausted;
        }

        public int WallContacts { get; }
        public int PairContacts { get; }

        /// <summary>
        /// True when a declared contact bound ran out before every eligible
        /// contact was resolved. The step is a numerical hold, never a
        /// permissive tunnel or an energy correction.
        /// </summary>
        public bool IterationBoundExhausted { get; }
    }

    /// <summary>
    /// The one writer face. A Trial has exactly one binding of this face, and
    /// only the runtime calls <see cref="Advance"/>. The writer owns its own
    /// complete configuration record, so a replacement writer cannot inherit
    /// the rejected configuration's fields.
    /// </summary>
    public interface IScientificWriter
    {
        string WriterId { get; }
        string ConfigurationId { get; }
        WriterDisposition Disposition { get; }
        IReadOnlyList<string> ConfigurationRecord { get; }
        void Setup(SimulationTrialSetup setup);
        void Advance(int tick, float deltaTimeSeconds);
        void Teardown();
    }

    /// <summary>
    /// A writer that reports per-step contact facts. The runtime uses the
    /// iteration-bound flag to enter a numerical hold instead of publishing a
    /// step that did not resolve every declared contact.
    /// </summary>
    public interface IContactFactWriter
    {
        ContactStepFacts LastContactFacts { get; }
    }

    public sealed class SimulationTrialSetup
    {
        public SimulationTrialSetup(string trialId, IReadOnlyList<BallState> balls,
            PinnedCollision collision, FrozenWriterConfiguration configuration)
        {
            TrialId = trialId ?? "";
            Balls = balls ?? Array.Empty<BallState>();
            Collision = collision;
            Configuration = configuration;
        }

        public string TrialId { get; }
        public IReadOnlyList<BallState> Balls { get; }
        public PinnedCollision Collision { get; }
        public FrozenWriterConfiguration Configuration { get; }
    }

    public sealed class StepResult
    {
        public StepResult(int executed, int tick, bool paused)
        {
            Executed = executed;
            Tick = tick;
            Paused = paused;
        }

        public int Executed { get; }
        public int Tick { get; }
        public bool Paused { get; }
    }
}
