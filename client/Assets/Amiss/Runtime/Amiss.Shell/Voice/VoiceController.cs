using System;
using System.Text;

namespace Amiss.Shell.Voice
{
    /// <summary>
    /// The sole writer of voice state (06 section 1). It owns the local
    /// cancellation identities and sequencing; it never writes Trial, stage,
    /// measurement, or persistence state. Every platform callback is checked
    /// against the current turn id and lifecycle epoch, so a late result from
    /// an old window is dropped instead of closing a newer one.
    ///
    /// The lifecycle of design 06 section 3 is
    /// <c>closed</c> to <c>listening</c> to <c>transcribing</c> to
    /// <c>waiting_model</c> to <c>playing_interruptible</c>, with consent,
    /// configuration, and microphone permission gates checked before any
    /// capture or synthesis request. The gates run in that order, so a press
    /// before consent only shows the notice and a press before configuration
    /// asks the platform for nothing. Once both pass and the permission is
    /// missing, the same press raises the system prompt at most once; no
    /// capture window opens until a later press observes the grant.
    /// </summary>
    public sealed class VoiceController
    {
        /// <summary>
        /// The consent/privacy notice shown before the first dictation or TTS
        /// request (06 section 4.1). It names the two Wit operations and the
        /// no-retention rule, and no other processor.
        /// </summary>
        public const string ConsentNotice =
            "VOICE CONSENT: speech is sent to Wit for transcription, and the " +
            "tutor's reply text is sent to Wit for synthesis. Raw audio is not " +
            "stored. Press MIC again to accept.";

        public const string ConsentVersion = "wit-voice-consent-1";

        /// <summary>The tutor origin of a voice-triggered turn (06 section 5.1).</summary>
        public const string AnswerOrigin = "answer";

        /// <summary>08/06 section 7: transcription after capture close.</summary>
        public const double TranscriptionDeadlineSeconds = 10.0;

        /// <summary>08 section 4.1: the bounded current utterance.</summary>
        public const int MaxUtteranceBytes = 8 * 1024;

        readonly IDictationPort dictation;
        readonly ISpeechSynthesisPort speech;
        readonly ITutorTurnPort tutor;
        readonly Func<double> clock;

        VoiceTurn turn;
        IDictationSession session;
        bool activationPending;
        bool noticeShown;
        double captureClosedAt;
        int sequence;
        int epoch;
        int queueEpoch = 1;
        bool muted;
        SpeechUnit active;
        SpeechUnit pending;
        VoiceConsentStatus consentStatus;
        double? acceptedAt;
        double? revokedAt;
        PermissionRequestProgress permissionRequest;

        enum PermissionRequestProgress
        {
            None,
            Requested,
            Denied
        }

        public VoiceController(IDictationPort dictation, ISpeechSynthesisPort speech,
            ITutorTurnPort tutor, VoiceConsentStatus consent = VoiceConsentStatus.Absent,
            Func<double> clock = null)
        {
            this.dictation = dictation ?? throw new ArgumentNullException(nameof(dictation));
            this.speech = speech ?? throw new ArgumentNullException(nameof(speech));
            this.tutor = tutor ?? throw new ArgumentNullException(nameof(tutor));
            consentStatus = consent;
            this.clock = clock ?? (() => 0.0);
        }

        public VoiceState State { get; private set; } = VoiceState.Closed;

        public VoiceConsentStatus Consent => consentStatus;

        public bool ConsentAccepted => consentStatus == VoiceConsentStatus.Accepted;

        /// <summary>
        /// True when the hub may offer capture: consent accepted, a provider
        /// configuration is present, and the system microphone is granted.
        /// It is never inferred from consent alone (06 section 4.1).
        /// </summary>
        public bool MicrophoneAvailable =>
            ConsentAccepted && dictation.Configured && dictation.MicrophonePermissionGranted;

        /// <summary>Synthesis needs consent and a configuration, not the microphone.</summary>
        public bool SpeechAvailable => ConsentAccepted && speech.Configured;

        /// <summary>The current system microphone permission, never consent.</summary>
        public bool MicrophonePermissionGranted => dictation.MicrophonePermissionGranted;

        /// <summary>
        /// True while a system microphone prompt is outstanding on the MIC
        /// press. A grant ends it without another press; a refused follow-up
        /// press turns it into the typed denied state (06 section 4.1).
        /// </summary>
        public bool MicrophonePermissionRequested =>
            permissionRequest == PermissionRequestProgress.Requested &&
            !dictation.MicrophonePermissionGranted;

        /// <summary>True when a speech-provider configuration exists on this device.</summary>
        public bool DictationConfigured => dictation.Configured;

        public bool Muted => muted;

        public VoiceTurn CurrentTurn => turn;

        public SpeechUnit ActiveUnit => active;

        public SpeechUnit PendingUnit => pending;

        public int Epoch => epoch;

        public int QueueEpoch => queueEpoch;

        public double? AcceptedAt => acceptedAt;

        public double? RevokedAt => revokedAt;

        /// <summary>The last typed code and its one readable line.</summary>
        public string LastCode { get; private set; } = "";

        public string LastReadable { get; private set; } = "";

        /// <summary>
        /// True while a capture window is open, opening, or speaking. The
        /// shell uses it to apply focus, safety, and unload closures without
        /// reopening a window afterwards.
        /// </summary>
        public bool IsBusy => activationPending || State != VoiceState.Closed;

        /// <summary>
        /// One explicit microphone action: open a capture window, close the
        /// open one, raise the system microphone prompt once when the
        /// permission is missing, or, before consent exists, show the notice
        /// and accept it on the second press.
        /// </summary>
        public VoiceOutcome ToggleExplicitCapture()
        {
            if (activationPending || State == VoiceState.Listening)
                return CloseCapture();

            var gate = CaptureGate();
            if (!gate.Accepted)
                return gate;

            StopForCapture();

            var opened = new VoiceTurn("voice-turn-" + (++sequence).ToString("D4"),
                VoiceTurnKind.Explicit, ++epoch);
            turn = opened;

            DictationCallbacks callbacks;
            callbacks = new DictationCallbacks
            {
                Activated = () => OnActivated(opened, opened.Epoch),
                FinalText = text => OnFinalText(opened, opened.Epoch, text),
                Failed = code => OnDictationFailed(opened, opened.Epoch, code)
            };

            IDictationSession started;
            try
            {
                started = dictation.Begin(opened.TurnId, callbacks);
            }
            catch (Exception)
            {
                started = null;
            }

            if (started == null)
            {
                session = null;
                activationPending = false;
                AdvanceEpoch();
                State = VoiceState.Closed;
                return Refuse("voice_capture_failed",
                    "The microphone could not be started; no audio was captured. " +
                    "Panel answers keep working.");
            }

            session = started;
            activationPending = true;
            // The indicator follows actual adapter state: the turn is not
            // "listening" until the platform reports the microphone on.
            State = VoiceState.Closed;
            return Accept("voice_activating", "Microphone starting.");
        }

        /// <summary>Closes the open window: microphone off first, then transcribing.</summary>
        public VoiceOutcome CloseCapture()
        {
            if (activationPending)
            {
                var cancelled = session;
                session = null;
                activationPending = false;
                SafeCancel(cancelled);
                AdvanceEpoch();
                State = VoiceState.Closed;
                return Accept("voice_capture_cancelled", "Microphone off before capture began.");
            }

            if (State != VoiceState.Listening)
                return Refuse("voice_not_listening", "No capture window is open.");

            var closed = session;
            session = null;
            try
            {
                closed?.Close();
            }
            catch (Exception)
            {
                // The adapter is already closed on every path that reaches
                // here; a platform stop failure cannot reopen the window.
            }

            State = VoiceState.Transcribing;
            captureClosedAt = clock();
            return Accept("voice_transcribing", "Microphone off; waiting for the transcript.");
        }

        /// <summary>
        /// A local lifecycle closure (focus loss, safety hold, episode unload,
        /// or consent withdrawal). Capture and playback stop immediately and
        /// late callbacks are dropped; capture never restarts by itself.
        /// </summary>
        public VoiceOutcome CloseForLifecycle(string reason)
        {
            var code = string.IsNullOrEmpty(reason) ? "voice_lifecycle_closed" : "voice_" + reason;
            CancelEverything(VoiceUnitCompletion.Interrupted);
            AdvanceEpoch();
            State = VoiceState.Closed;
            LastCode = code;
            LastReadable = "Voice closed by a local lifecycle change; the panel path continues.";
            return Accept(code, LastReadable);
        }

        /// <summary>Accepts consent and starts the bounded local record.</summary>
        public VoiceOutcome AcceptConsent()
        {
            consentStatus = VoiceConsentStatus.Accepted;
            acceptedAt = clock();
            revokedAt = null;
            noticeShown = false;
            LastCode = "voice_consent_accepted";
            LastReadable = "Voice consent accepted.";
            return Accept(LastCode, LastReadable);
        }

        /// <summary>
        /// Withdraws consent: immediate stop, cancelled capture, and a future
        /// block. The panel and the default path are unaffected (06 section 4.1).
        /// </summary>
        public VoiceOutcome RevokeConsent()
        {
            consentStatus = VoiceConsentStatus.Revoked;
            revokedAt = clock();
            CancelEverything(VoiceUnitCompletion.Interrupted);
            AdvanceEpoch();
            State = VoiceState.Closed;
            LastCode = "voice_consent_revoked";
            LastReadable = "Voice consent withdrawn; voice stays off. Panel answers keep working.";
            return Accept(LastCode, LastReadable);
        }

        /// <summary>
        /// Mute stops audible synthesis immediately and keeps the accepted
        /// subtitle. It never opens or closes a capture window and never
        /// resumes the stopped unit later (06 section 4.2).
        /// </summary>
        public void SetMuted(bool value)
        {
            if (muted == value)
                return;
            muted = value;
            if (!muted)
                return;
            if (active != null || pending != null)
                StopPlayback(VoiceUnitCompletion.Suppressed, "voice_muted");
            else if (State == VoiceState.PlayingInterruptible)
                State = VoiceState.Closed;
        }

        /// <summary>Drives the local 10-second transcription deadline.</summary>
        public VoiceOutcome Tick()
        {
            if (State != VoiceState.Transcribing)
                return VoiceOutcome.Accept("", "");
            if (clock() - captureClosedAt <= TranscriptionDeadlineSeconds)
                return VoiceOutcome.Accept("", "");
            return FailTranscription("No transcript arrived within 10 seconds of capture close; " +
                                     "no text is fabricated. The panel path continues.");
        }

        /// <summary>
        /// Queues one already validated speech unit. The queue bound is one
        /// active unit plus one admitted pending unit; a third unit is refused
        /// rather than accumulated (06 section 2). Muted units keep their
        /// subtitle and request no audible synthesis.
        /// </summary>
        public VoiceOutcome EnqueueValidatedSpeech(string unitId, string text,
            VoiceUnitKind kind = VoiceUnitKind.Answer, string turnId = null)
        {
            if (string.IsNullOrEmpty(text))
                return Refuse("empty_speech", "An empty speech unit was refused.");
            if (consentStatus != VoiceConsentStatus.Accepted)
                return Refuse("voice_consent_required",
                    "Voice is disabled without consent; the accepted text stays a subtitle.");

            var unit = new SpeechUnit(unitId, turnId ?? (turn != null ? turn.TurnId : ""),
                kind, text, queueEpoch);

            if (muted)
            {
                unit.Completion = VoiceUnitCompletion.Suppressed;
                LastCode = "voice_muted";
                LastReadable = "Muted: the accepted text stays a subtitle and no audio is requested.";
                return Accept(LastCode, LastReadable);
            }

            if (active == null)
            {
                var started = StartUnit(unit);
                return started
                    ? Accept("voice_playing", "Speech requested from the wall speaker.")
                    : Accept(LastCode, LastReadable);
            }

            if (pending == null)
            {
                pending = unit;
                return Accept("voice_queued", "One speech unit queued behind the active one.");
            }

            unit.Completion = VoiceUnitCompletion.Expired;
            return Refuse("voice_queue_full",
                "The voice queue holds one active and one pending unit; the extra unit expired.");
        }

        void OnActivated(VoiceTurn captured, int capturedEpoch)
        {
            if (!Matches(captured, capturedEpoch) || !activationPending)
                return;
            activationPending = false;
            State = VoiceState.Listening;
            LastCode = "voice_listening";
            LastReadable = "Microphone on; dictation is recording.";
        }

        async void OnFinalText(VoiceTurn captured, int capturedEpoch, string text)
        {
            if (!Matches(captured, capturedEpoch) || State != VoiceState.Transcribing)
                return;
            if (clock() - captureClosedAt > TranscriptionDeadlineSeconds)
            {
                FailTranscription("The transcript arrived after the 10-second deadline; it was dropped.");
                return;
            }

            var utterance = (text ?? "").Trim();
            if (utterance.Length == 0)
            {
                FailTranscription("No speech was recognized; no text is fabricated. The panel path continues.");
                return;
            }

            if (Encoding.UTF8.GetByteCount(utterance) > MaxUtteranceBytes)
            {
                FailTranscription("The transcript exceeds the bounded utterance size; it was not sent.");
                return;
            }

            State = VoiceState.WaitingModel;
            TutorTurnOutcome outcome;
            try
            {
                outcome = await tutor.Ask(AnswerOrigin, captured.TurnId, utterance);
            }
            catch (Exception)
            {
                outcome = TutorTurnOutcome.Silent("model_unavailable");
            }

            // A lifecycle closure, consent withdrawal, or a newer capture
            // supersedes this turn while the request is in flight, so its reply
            // is dropped rather than spoken into a newer window (06 section 7).
            if (!Matches(captured, capturedEpoch) || State != VoiceState.WaitingModel)
                return;

            if (!outcome.Admitted)
            {
                State = VoiceState.Closed;
                LastCode = string.IsNullOrEmpty(outcome.FailureCode)
                    ? "model_unavailable"
                    : outcome.FailureCode;
                LastReadable = "The tutor reply was not admitted, so nothing is spoken.";
                return;
            }

            var speech = outcome.AcceptedSpeech ?? Array.Empty<string>();
            if (speech.Count == 0)
            {
                State = VoiceState.Closed;
                LastCode = "no_speech";
                LastReadable = "The reply was admitted with no speech unit.";
                return;
            }

            for (var index = 0; index < speech.Count; index++)
                EnqueueValidatedSpeech("speech-" + captured.TurnId + "-" + (index + 1).ToString("D2"),
                    speech[index], VoiceUnitKind.Answer, captured.TurnId);

            if (active == null && pending == null)
                State = VoiceState.Closed;
        }

        void OnDictationFailed(VoiceTurn captured, int capturedEpoch, string code)
        {
            if (!Matches(captured, capturedEpoch))
                return;
            if (State != VoiceState.Transcribing && State != VoiceState.Listening)
                return;
            FailTranscription(string.IsNullOrEmpty(code) ? "voice_transcription" : code,
                "Dictation failed; no text is fabricated. The panel path continues.");
        }

        bool StartUnit(SpeechUnit unit)
        {
            active = unit;
            if (!speech.Configured)
            {
                active = null;
                unit.Completion = VoiceUnitCompletion.Suppressed;
                State = VoiceState.Closed;
                LastCode = "voice_tts_unconfigured";
                LastReadable = "Synthesis is not configured; the accepted subtitle is kept and no audio plays.";
                return false;
            }

            State = VoiceState.PlayingInterruptible;
            try
            {
                speech.Speak(unit.UnitId, unit.Text,
                    completedId => OnSpeechCompleted(unit, completedId),
                    failedId => OnSpeechFailed(unit, failedId));
            }
            catch (Exception)
            {
                OnSpeechFailed(unit, unit.UnitId);
            }

            return true;
        }

        void OnSpeechCompleted(SpeechUnit unit, string completedId)
        {
            if (!ReferenceEquals(unit, active) || unit.QueueEpoch != queueEpoch ||
                completedId != unit.UnitId)
                return;
            unit.Completion = VoiceUnitCompletion.Completed;
            active = null;
            PromotePending();
        }

        void OnSpeechFailed(SpeechUnit unit, string failedId)
        {
            if (!ReferenceEquals(unit, active) || unit.QueueEpoch != queueEpoch ||
                failedId != unit.UnitId)
                return;
            unit.Completion = VoiceUnitCompletion.Expired;
            active = null;
            LastCode = "voice_tts";
            LastReadable = "Synthesis failed; the accepted subtitle is kept and no audio plays.";
            PromotePending();
        }

        void PromotePending()
        {
            if (pending == null)
            {
                State = VoiceState.Closed;
                return;
            }

            var next = pending;
            pending = null;
            StartUnit(next);
        }

        /// <summary>
        /// Explicit capture stops old synthesis before the new window opens:
        /// loading and audio stop first, the affected units are interrupted,
        /// and the queue epoch advances so no late callback completes them
        /// (06 section 3.1).
        /// </summary>
        void StopForCapture()
        {
            if (active == null && pending == null && State != VoiceState.PlayingInterruptible)
                return;
            StopPlayback(VoiceUnitCompletion.Interrupted, "voice_interrupted");
        }

        void StopPlayback(VoiceUnitCompletion completion, string code)
        {
            try
            {
                speech.Stop();
            }
            catch (Exception)
            {
                // The local stop is authoritative; a platform stop failure
                // cannot keep a unit pending.
            }

            if (active != null)
                active.Completion = completion;
            if (pending != null)
                pending.Completion = completion;
            active = null;
            pending = null;
            queueEpoch++;
            State = VoiceState.Closed;
            LastCode = code;
            LastReadable = "Playback stopped.";
        }

        void CancelEverything(VoiceUnitCompletion completion)
        {
            var cancelled = session;
            session = null;
            activationPending = false;
            SafeCancel(cancelled);
            if (active != null || pending != null)
            {
                StopPlayback(completion, LastCode.Length > 0 ? LastCode : "voice_stopped");
                return;
            }

            try
            {
                speech.Stop();
            }
            catch (Exception)
            {
            }

            queueEpoch++;
            State = VoiceState.Closed;
        }

        static void SafeCancel(IDictationSession window)
        {
            try
            {
                window?.Cancel();
            }
            catch (Exception)
            {
            }
        }

        VoiceOutcome CaptureGate()
        {
            if (consentStatus == VoiceConsentStatus.Revoked)
                return Refuse("voice_consent_revoked",
                    "Voice stays off after consent withdrawal; panel answers keep working.");
            if (consentStatus != VoiceConsentStatus.Accepted)
            {
                if (!noticeShown)
                {
                    noticeShown = true;
                    LastCode = "voice_consent_required";
                    LastReadable = ConsentNotice;
                    return Refuse(LastCode, LastReadable);
                }

                AcceptConsent();
            }

            if (!dictation.Configured)
                return Refuse("voice_unconfigured",
                    "Voice is not configured on this device; no audio is captured or sent. " +
                    "Panel answers keep working.");

            if (!dictation.MicrophonePermissionGranted)
            {
                if (permissionRequest == PermissionRequestProgress.Requested)
                {
                    // The prompt was raised and no grant follows: the person
                    // did not accept it. Keep the refusal typed; never raise a
                    // second system prompt (06 section 4.1).
                    permissionRequest = PermissionRequestProgress.Denied;
                    return Refuse("voice_microphone_denied",
                        "Microphone permission is not granted; no audio is captured. " +
                        "Panel answers keep working.");
                }

                if (permissionRequest == PermissionRequestProgress.Denied)
                {
                    return Refuse("voice_microphone_denied",
                        "Microphone permission is not granted; no audio is captured. " +
                        "Panel answers keep working.");
                }

                permissionRequest = PermissionRequestProgress.Requested;
                MicrophonePermissionRequest request;
                try
                {
                    request = dictation.RequestMicrophonePermission();
                }
                catch (Exception)
                {
                    request = MicrophonePermissionRequest.Unavailable;
                }

                if (request == MicrophonePermissionRequest.AlreadyGranted)
                {
                    permissionRequest = PermissionRequestProgress.None;
                }
                else if (request == MicrophonePermissionRequest.PromptRaised)
                {
                    // The gate does not pass: no capture window opens and
                    // nothing is uploaded. The typed state tells the hub that
                    // a system prompt is outstanding.
                    return Refuse("voice_microphone_requested",
                        "Microphone permission requested; accept the system prompt, " +
                        "then press MIC again.");
                }
                else
                {
                    permissionRequest = PermissionRequestProgress.Denied;
                    return Refuse("voice_microphone_denied",
                        "Microphone permission could not be requested; no audio is captured. " +
                        "Panel answers keep working.");
                }
            }
            else
            {
                permissionRequest = PermissionRequestProgress.None;
            }

            return VoiceOutcome.Accept("voice_gates_passed", "");
        }

        VoiceOutcome FailTranscription(string readable) =>
            FailTranscription("voice_transcription", readable);

        VoiceOutcome FailTranscription(string code, string readable)
        {
            var open = session;
            session = null;
            activationPending = false;
            SafeCancel(open);
            State = VoiceState.Closed;
            LastCode = code;
            LastReadable = readable;
            AdvanceEpoch();
            return VoiceOutcome.Refuse(code, readable);
        }

        bool Matches(VoiceTurn captured, int capturedEpoch) =>
            captured != null && ReferenceEquals(captured, turn) && capturedEpoch == epoch;

        void AdvanceEpoch() => epoch++;

        VoiceOutcome Accept(string code, string readable)
        {
            LastCode = code;
            LastReadable = readable;
            return VoiceOutcome.Accept(code, readable);
        }

        VoiceOutcome Refuse(string code, string readable)
        {
            LastCode = code;
            LastReadable = readable;
            return VoiceOutcome.Refuse(code, readable);
        }
    }
}
