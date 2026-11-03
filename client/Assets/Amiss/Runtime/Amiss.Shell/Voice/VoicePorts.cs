using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Amiss.Shell.Voice
{
    /// <summary>
    /// The platform callbacks of one capture window. They enqueue results for
    /// the controller; none of them opens a microphone, plays text, or writes
    /// simulation state by itself (06 section 1).
    /// </summary>
    public sealed class DictationCallbacks
    {
        public Action Activated { get; set; } = () => { };
        public Action<string> PartialText { get; set; } = _ => { };
        public Action CaptureEnded { get; set; } = () => { };
        public Action<string> FinalText { get; set; } = _ => { };
        public Action<string> Failed { get; set; } = _ => { };
    }

    public enum DictationActivationMode
    {
        Immediate,
        Automatic
    }

    /// <summary>
    /// One bounded capture window. <see cref="Close"/> must stop the
    /// microphone before the final text can arrive; <see cref="Cancel"/>
    /// abandons the window so no late callback is treated as a result. Raw
    /// audio never crosses this boundary (06 section 1).
    /// </summary>
    public interface IDictationSession
    {
        string TurnId { get; }
        void Close();
        void Cancel();
    }

    /// <summary>
    /// The outcome of one in-app microphone permission request. The SDK call
    /// reports a grant through <c>HasMicPermission()</c>; it reports no denial,
    /// so a refused follow-up press is what tells the controller the person
    /// did not accept the raised prompt.
    /// </summary>
    public enum MicrophonePermissionRequest
    {
        /// <summary>The permission was already granted; no prompt was needed.</summary>
        AlreadyGranted,

        /// <summary>The system prompt was raised; the grant is not yet reported.</summary>
        PromptRaised,

        /// <summary>The platform could not raise a prompt.</summary>
        Unavailable
    }

    /// <summary>
    /// The provider-independent dictation port. The first adapter is Wit
    /// dictation; consent, transcript retention, and failure wording belong to
    /// the controller, not here.
    /// </summary>
    public interface IDictationPort
    {
        /// <summary>True when this device holds a usable speech-provider setup.</summary>
        bool Configured { get; }

        /// <summary>The current system microphone permission, never consent.</summary>
        bool MicrophonePermissionGranted { get; }

        /// <summary>
        /// Raises the system microphone prompt without opening a capture
        /// window, so a fresh install can be granted on the MIC press. The
        /// adapter's own SDK call is used; no capture, transcript, or upload
        /// starts here.
        /// </summary>
        MicrophonePermissionRequest RequestMicrophonePermission();

        /// <summary>True while the platform reports the microphone is recording.</summary>
        bool CaptureActive { get; }

        /// <summary>
        /// Opens one capture window. Returns null when the platform adapter
        /// cannot start; the window's activation callback fires only once the
        /// microphone is actually recording.
        /// </summary>
        IDictationSession Begin(string turnId, DictationCallbacks callbacks,
            DictationActivationMode mode = DictationActivationMode.Immediate);
    }

    /// <summary>
    /// The provider-independent synthesis port. It accepts whole-response
    /// validated text or the episode-owned opening goal, plays it from the approved speaker, and
    /// stops all loading and audio on demand.
    /// </summary>
    public interface ISpeechSynthesisPort
    {
        bool Configured { get; }

        void Speak(string unitId, string text, Action<string> completed, Action<string> failed);

        /// <summary>Stops loading and playback immediately; no completion follows.</summary>
        void Stop();
    }

    /// <summary>
    /// The one tutor turn seam. The shell binds it to the existing
    /// <c>TutorConversation</c>; the controller only sees whole-reply
    /// admission, never the transport. The turn is awaited, so one model
    /// request never holds the frame the controller and the Trial run on.
    /// </summary>
    public interface ITutorTurnPort
    {
        Task<TutorTurnOutcome> Ask(string origin, string turnId, string utterance);
    }

    public readonly struct TutorTurnOutcome
    {
        public TutorTurnOutcome(bool admitted, string failureCode,
            IReadOnlyList<string> acceptedSpeech)
        {
            Admitted = admitted;
            FailureCode = failureCode ?? "";
            AcceptedSpeech = acceptedSpeech ?? Array.Empty<string>();
        }

        public bool Admitted { get; }
        public string FailureCode { get; }
        public IReadOnlyList<string> AcceptedSpeech { get; }

        public static TutorTurnOutcome Silent(string failureCode) =>
            new(false, failureCode, Array.Empty<string>());

        public static TutorTurnOutcome Spoken(IReadOnlyList<string> speech) =>
            new(true, "", speech);
    }
}
