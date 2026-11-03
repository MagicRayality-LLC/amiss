using System;

namespace Amiss.Shell.Voice
{
    /// <summary>
    /// The public voice projection of design 06 section 3. Listening takes
    /// precedence while playback is also active, so the microphone indicator
    /// never hides actual capture; playback remains visible through PlaybackActive.
    /// </summary>
    public enum VoiceState
    {
        Closed,
        Listening,
        Transcribing,
        WaitingModel,
        PlayingInterruptible
    }

    /// <summary>
    /// The bounded local consent record of design 06 section 4.1. Consent is
    /// never inferred from the system microphone permission, and the system
    /// permission is never inferred from consent.
    /// </summary>
    public enum VoiceConsentStatus
    {
        Absent,
        Accepted,
        Revoked
    }

    /// <summary>The activation kind of a turn (06 section 2).</summary>
    public enum VoiceTurnKind
    {
        Explicit,
        Automatic,
        Answer
    }

    public enum VoiceUnitKind
    {
        OpeningGoal,
        Answer,
        Proactive,
        Safety
    }

    /// <summary>The completion values of a speech unit (06 section 2).</summary>
    public enum VoiceUnitCompletion
    {
        Pending,
        Completed,
        Interrupted,
        Expired,
        Suppressed
    }

    /// <summary>
    /// A typed, key-free voice result. <see cref="Code"/> is the stable
    /// machine value; <see cref="Readable"/> is the one panel line. Neither
    /// carries audio, a transcript history, or a credential.
    /// </summary>
    public readonly struct VoiceOutcome
    {
        VoiceOutcome(bool accepted, string code, string readable)
        {
            Accepted = accepted;
            Code = code ?? "";
            Readable = readable ?? "";
        }

        public bool Accepted { get; }
        public string Code { get; }
        public string Readable { get; }

        public static VoiceOutcome Accept(string code, string readable) =>
            new(true, code, readable);

        public static VoiceOutcome Refuse(string code, string readable) =>
            new(false, code, readable);
    }

    /// <summary>
    /// One immutable voice turn identity (06 section 2): turn id, activation
    /// kind, and the local lifecycle epoch the callbacks must still match.
    /// </summary>
    public sealed class VoiceTurn
    {
        public VoiceTurn(string turnId, VoiceTurnKind kind, int epoch)
        {
            TurnId = turnId ?? "";
            Kind = kind;
            Epoch = epoch;
        }

        public string TurnId { get; }
        public VoiceTurnKind Kind { get; }
        public int Epoch { get; }
    }

    /// <summary>
    /// One speech unit. Text is either whole-response-validated model text or
    /// the episode-owned fixed opening goal; the controller is the only writer of
    /// <see cref="Completion"/>.
    /// </summary>
    public sealed class SpeechUnit
    {
        public SpeechUnit(string unitId, string turnId, VoiceUnitKind kind, string text, int queueEpoch)
        {
            UnitId = unitId ?? "";
            TurnId = turnId ?? "";
            Kind = kind;
            Text = text ?? "";
            QueueEpoch = queueEpoch;
            Completion = VoiceUnitCompletion.Pending;
        }

        public string UnitId { get; }
        public string TurnId { get; }
        public VoiceUnitKind Kind { get; }
        public string Text { get; }
        public int QueueEpoch { get; }
        public VoiceUnitCompletion Completion { get; internal set; }
    }
}
