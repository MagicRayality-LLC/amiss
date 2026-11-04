using System;
using System.Text;
using UnityEngine;

namespace Amiss.Shell.Voice
{
    /// <summary>
    /// The sole writer of voice state (06 section 1). It owns the local
    /// cancellation identities and sequencing; it never writes Trial, stage,
    /// measurement, or persistence state. Every platform callback is checked
    /// against the current turn id and lifecycle epoch, so a late result from
    /// an old window is dropped instead of closing a newer one.
    ///
    /// Capture, tutor waiting, and playback are composite states: automatic
    /// capture remains active during opening and answer TTS, and current-turn
    /// speech stops playback without closing that capture. Consent,
    /// configuration, and microphone permission gates still precede every
    /// capture or synthesis request. A first MIC press shows consent; a later
    /// press accepts it. Permission prompts never themselves open capture.
    /// </summary>
    public sealed class VoiceController
    {
        /// <summary>
        /// The consent/privacy notice shown before the first dictation or TTS
        /// request (06 section 4.1). It names the two Wit operations and the
        /// no-retention rule, and no other processor.
        /// </summary>
        public const string ConsentNotice =
            "VOICE CONSENT: during an enabled Trial, speech is sent to Wit for " +
            "transcription, including speech during playback. The opening goal and " +
            "tutor reply text are sent to Wit for synthesis. Raw audio is not " +
            "stored. Press MIC again to accept.";

        public const string ConsentVersion = "wit-voice-consent-1";

        /// <summary>The tutor origin of a voice-triggered turn (06 section 5.1).</summary>
        public const string AnswerOrigin = "answer";

        /// <summary>08/06 section 7: transcription after capture close.</summary>
        public const double TranscriptionDeadlineSeconds = 10.0;

        /// <summary>
        /// The platform must confirm an opened window within this long. Without
        /// it a turn whose start-listening event never arrives sits in
        /// activation forever: no timeout fires, no rearm follows, and every
        /// later question is dropped as belonging to no open turn.
        /// </summary>
        public const double ActivationDeadlineSeconds = 10.0;

        /// <summary>08 section 4.1: the bounded current utterance.</summary>
        public const int MaxUtteranceBytes = 8 * 1024;

        readonly IDictationPort dictation;
        readonly ISpeechSynthesisPort speech;
        readonly ITutorTurnPort tutor;
        readonly Func<double> clock;

        VoiceTurn turn;
        IDictationSession session;
        bool activationPending;
        double activationStartedAt;
        bool captureEnded;
        bool noticeShown;
        double captureClosedAt;
        int sequence;
        int epoch;
        int queueEpoch = 1;
        bool muted;
        bool voiceEnabled;
        bool trialActive;
        bool lifecycleBlocked;
        bool rearmPending;
        int openingSequence;
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
            voiceEnabled = ConsentAccepted;
            this.clock = clock ?? (() => 0.0);
        }

        public VoiceState State { get; private set; } = VoiceState.Closed;

        public VoiceConsentStatus Consent => consentStatus;

        public bool ConsentAccepted => consentStatus == VoiceConsentStatus.Accepted;

        public bool VoiceEnabled => voiceEnabled;

        public bool TrialActive => trialActive;

        public bool CaptureActive => activationPending || session != null;

        public bool PlaybackActive => active != null || pending != null;

        /// <summary>
        /// True when the hub may offer capture: consent accepted, a provider
        /// configuration is present, and the system microphone is granted.
        /// It is never inferred from consent alone (06 section 4.1).
        /// </summary>
        public bool MicrophoneAvailable =>
            voiceEnabled && ConsentAccepted && dictation.Configured &&
            dictation.MicrophonePermissionGranted;

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

        public VoiceOutcome StartTrialEpisode(string openingGoal)
        {
            trialActive = true;
            lifecycleBlocked = false;
            rearmPending = false;
            CancelEverything(VoiceUnitCompletion.Interrupted);
            AdvanceEpoch();
            State = VoiceState.Closed;

            if (voiceEnabled)
            {
                EnqueueValidatedSpeech("opening-" + (++openingSequence).ToString("D4"),
                    openingGoal, VoiceUnitKind.OpeningGoal);
                EnsureAutomaticCapture();
            }

            return Accept(voiceEnabled ? "voice_trial_started" : "voice_disabled",
                voiceEnabled
                    ? "Trial voice started with the local opening goal."
                    : "Voice is off; the local opening goal stays as a subtitle.");
        }

        public VoiceOutcome EndTrialEpisode()
        {
            trialActive = false;
            rearmPending = false;
            CancelEverything(VoiceUnitCompletion.Interrupted);
            AdvanceEpoch();
            State = VoiceState.Closed;
            return Accept("voice_trial_closed", "Trial voice closed; no capture remains.");
        }

        public VoiceOutcome ToggleVoice()
        {
            if (voiceEnabled && !dictation.MicrophonePermissionGranted)
                voiceEnabled = false;
            if (!voiceEnabled)
            {
                var gate = CaptureGate();
                if (!ConsentAccepted)
                    return gate;
                voiceEnabled = true;
                lifecycleBlocked = false;
                if (!gate.Accepted)
                    return gate;
                if (trialActive)
                    return ToggleExplicitCapture();
                return Accept("voice_enabled", "Voice enabled; listening starts with the Trial.");
            }

            if (PlaybackActive)
            {
                StopForCapture();
                if (!CaptureActive && trialActive)
                    return ToggleExplicitCapture();
                return Accept("voice_interrupted", "Playback stopped; the current capture continues.");
            }

            voiceEnabled = false;
            rearmPending = false;
            CancelEverything(VoiceUnitCompletion.Interrupted);
            AdvanceEpoch();
            State = VoiceState.Closed;
            return Accept("voice_disabled", "Voice off; capture and playback stopped.");
        }

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

            return BeginCapture(VoiceTurnKind.Explicit, DictationActivationMode.Immediate, true);
        }

        void EnsureAutomaticCapture()
        {
            if (!voiceEnabled || !trialActive || lifecycleBlocked || !ConsentAccepted ||
                !dictation.Configured || !dictation.MicrophonePermissionGranted ||
                activationPending || session != null || State == VoiceState.Transcribing ||
                State == VoiceState.WaitingModel)
                return;
            BeginCapture(VoiceTurnKind.Automatic, DictationActivationMode.Automatic, false);
        }

        VoiceOutcome BeginCapture(VoiceTurnKind kind, DictationActivationMode mode,
            bool stopPlayback)
        {
            if (stopPlayback)
                StopForCapture();

            var opened = new VoiceTurn("voice-turn-" + (++sequence).ToString("D4"),
                kind, ++epoch);
            turn = opened;
            var callbacks = new DictationCallbacks
            {
                Activated = () => OnActivated(opened, opened.Epoch),
                PartialText = text => OnPartialText(opened, opened.Epoch, text),
                CaptureEnded = () => OnCaptureEnded(opened, opened.Epoch),
                RequestEnded = () => OnRequestEnded(opened, opened.Epoch),
                FinalText = text => OnFinalText(opened, opened.Epoch, text),
                Failed = code => OnDictationFailed(opened, opened.Epoch, code)
            };

            // The pending flag is raised before the platform call: a reused
            // dictation service raises its start-listening event synchronously
            // inside Begin, and OnActivated drops that event when the flag is
            // not yet set, leaving the turn permanently pending.
            activationPending = true;
            activationStartedAt = clock();
            IDictationSession started;
            try
            {
                started = dictation.Begin(opened.TurnId, callbacks, mode);
            }
            catch (Exception)
            {
                started = null;
            }

            rearmPending = false;
            captureEnded = false;
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
            // A synchronous activation inside Begin already cleared the flag
            // and set Listening; keep that state instead of overwriting it.
            if (activationPending)
                State = active != null ? VoiceState.PlayingInterruptible : VoiceState.Closed;
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
            Debug.Log("[Amiss] voice lifecycle close reason=" + reason + " state=" + State);
            var code = string.IsNullOrEmpty(reason) ? "voice_lifecycle_closed" : "voice_" + reason;
            voiceEnabled = false;
            rearmPending = false;
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
            voiceEnabled = false;
            rearmPending = false;
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

        /// <summary>Drives the local transcription and activation deadlines.</summary>
        public VoiceOutcome Tick()
        {
            var outcome = VoiceOutcome.Accept("", "");
            if (State == VoiceState.Transcribing &&
                clock() - captureClosedAt > TranscriptionDeadlineSeconds)
            {
                outcome = FailTranscription("No transcript arrived within 10 seconds of capture close; " +
                    "no text is fabricated. The panel path continues.");
            }

            if (activationPending &&
                clock() - activationStartedAt > ActivationDeadlineSeconds)
            {
                Debug.Log("[Amiss] voice activation timed out; reopening capture");
                var open = session;
                session = null;
                activationPending = false;
                SafeCancel(open);
                State = VoiceState.Closed;
                LastCode = "voice_activation_timeout";
                LastReadable = "The microphone did not start in time; listening again.";
                rearmPending = voiceEnabled && trialActive && !lifecycleBlocked;
                AdvanceEpoch();
            }

            if (rearmPending && session == null && !activationPending &&
                State != VoiceState.Transcribing && State != VoiceState.WaitingModel)
                EnsureAutomaticCapture();
            return outcome;
        }

        /// <summary>
        /// Queues one validated speech unit or fixed local opening goal. The queue bound is one
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

            var unit = new SpeechUnit(unitId,
                kind == VoiceUnitKind.OpeningGoal
                    ? ""
                    : turnId ?? (turn != null ? turn.TurnId : ""),
                kind, text, queueEpoch);

            if (muted)
            {
                unit.Completion = VoiceUnitCompletion.Suppressed;
                LastCode = "voice_muted";
                LastReadable = "Muted: the accepted text stays a subtitle and no audio is requested.";
                EnsureAutomaticCapture();
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

        void OnPartialText(VoiceTurn captured, int capturedEpoch, string text)
        {
            if (!Matches(captured, capturedEpoch) || State != VoiceState.Listening ||
                active == null)
                return;
            if (!HasCompleteWord(text))
            {
                Debug.Log("[Amiss] voice partial ignored: no complete word");
                return;
            }
            StopPlayback(VoiceUnitCompletion.Interrupted, "voice_interrupted");
        }

        void OnCaptureEnded(VoiceTurn captured, int capturedEpoch)
        {
            if (!Matches(captured, capturedEpoch) ||
                (State != VoiceState.Listening && !activationPending))
                return;
            session = null;
            activationPending = false;
            captureEnded = true;
            captureClosedAt = clock();
            State = VoiceState.Transcribing;
        }

        /// <summary>
        /// The platform request is over, so no final text can still arrive
        /// for the previous capture. When capture already ended this is the
        /// silent turn: it closes without a tutor call, without fabricated
        /// text, and without waiting out the transcription deadline, then
        /// rearms on a later frame. When capture is still open, the terminal
        /// arrived early (reordered platform events, or a stale terminal from
        /// the previous request landing on this window): the microphone stops
        /// and the turn waits for the transcript under the deadline instead
        /// of stranding listening on a dead request. A transcript that still
        /// arrives answers; otherwise the deadline rearms. Either way capture
        /// never strands listening on a dead request.
        /// </summary>
        void OnRequestEnded(VoiceTurn captured, int capturedEpoch)
        {
            if (!Matches(captured, capturedEpoch))
                return;
            if (!captureEnded)
            {
                if (State != VoiceState.Listening && !activationPending)
                    return;
                Debug.Log("[Amiss] voice request ended during capture turn=" + captured.TurnId);
                OnCaptureEnded(captured, capturedEpoch);
                return;
            }
            if (State != VoiceState.Transcribing)
                return;
            Debug.Log("[Amiss] voice request ended with no transcript turn=" + captured.TurnId);
            session = null;
            activationPending = false;
            State = VoiceState.Closed;
            LastCode = "voice_no_speech";
            LastReadable = "No speech was recognized; listening again.";
            rearmPending = voiceEnabled && trialActive && !lifecycleBlocked;
            AdvanceEpoch();
        }

        async void OnFinalText(VoiceTurn captured, int capturedEpoch, string text)
        {
            Debug.Log("[Amiss] voice final text enter turn=" +
                (captured != null ? captured.TurnId : "null") + " epoch=" + capturedEpoch +
                " state=" + State + " text=\"" + (text ?? "") + "\"");
            if (!Matches(captured, capturedEpoch))
            {
                Debug.Log("[Amiss] voice final dropped: turn superseded");
                return;
            }
            if (State == VoiceState.Listening || activationPending)
                OnCaptureEnded(captured, capturedEpoch);
            if (State != VoiceState.Transcribing)
            {
                Debug.Log("[Amiss] voice final dropped: state=" + State);
                return;
            }
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
            Debug.Log("[Amiss] voice transcript turn=" + captured.TurnId +
                " text=\"" + utterance + "\"");
            TutorTurnOutcome outcome;
            try
            {
                outcome = await tutor.Ask(AnswerOrigin, captured.TurnId, utterance);
            }
            catch (Exception error)
            {
                Debug.Log("[Amiss] tutor ask exception " + error.GetType().Name);
                outcome = TutorTurnOutcome.Silent("model_unavailable");
            }

            // A lifecycle closure, consent withdrawal, or a newer capture
            // supersedes this turn while the request is in flight, so its reply
            // is dropped rather than spoken into a newer window (06 section 7).
            if (!Matches(captured, capturedEpoch) || State != VoiceState.WaitingModel)
            {
                Debug.Log("[Amiss] tutor reply dropped; the turn was superseded");
                return;
            }

            if (!outcome.Admitted)
            {
                State = VoiceState.Closed;
                LastCode = string.IsNullOrEmpty(outcome.FailureCode)
                    ? "model_unavailable"
                    : outcome.FailureCode;
                LastReadable = "The tutor reply was not admitted, so nothing is spoken.";
                Debug.Log("[Amiss] tutor reply refused code=" + LastCode);
                rearmPending = voiceEnabled && trialActive && !lifecycleBlocked;
                return;
            }

            var speech = outcome.AcceptedSpeech ?? Array.Empty<string>();
            if (speech.Count == 0)
            {
                State = VoiceState.Closed;
                LastCode = "no_speech";
                LastReadable = "The reply was admitted with no speech unit.";
                Debug.Log("[Amiss] tutor reply admitted with no speech unit");
                rearmPending = voiceEnabled && trialActive && !lifecycleBlocked;
                return;
            }

            for (var index = 0; index < speech.Count; index++)
                EnqueueValidatedSpeech("speech-" + captured.TurnId + "-" + (index + 1).ToString("D2"),
                    speech[index], VoiceUnitKind.Answer, captured.TurnId);

            if (active == null && pending == null)
            {
                State = VoiceState.Closed;
                rearmPending = voiceEnabled && trialActive && !lifecycleBlocked;
            }
        }

        void OnDictationFailed(VoiceTurn captured, int capturedEpoch, string code)
        {
            if (!Matches(captured, capturedEpoch))
                return;
            if (State != VoiceState.Transcribing && State != VoiceState.Listening &&
                !activationPending)
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
                State = session != null && !activationPending
                    ? VoiceState.Listening
                    : VoiceState.Closed;
                rearmPending = voiceEnabled && trialActive && !lifecycleBlocked;
                LastCode = "voice_tts_unconfigured";
                LastReadable = "Synthesis is not configured; the accepted subtitle is kept and no audio plays.";
                return false;
            }

            State = session != null && !activationPending
                ? VoiceState.Listening
                : VoiceState.PlayingInterruptible;
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

            EnsureAutomaticCapture();
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
            Debug.Log("[Amiss] speech synthesis failed unit=" + unit.UnitId);
            LastReadable = "Synthesis failed; the accepted subtitle is kept and no audio plays.";
            PromotePending();
        }

        void PromotePending()
        {
            if (pending == null)
            {
                State = session != null && !activationPending
                    ? VoiceState.Listening
                    : VoiceState.Closed;
                rearmPending = voiceEnabled && trialActive && !lifecycleBlocked;
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
            State = session != null && !activationPending
                ? VoiceState.Listening
                : VoiceState.Closed;
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
            Debug.Log("[Amiss] voice transcription failed code=" + code);
            var open = session;
            session = null;
            activationPending = false;
            SafeCancel(open);
            State = VoiceState.Closed;
            LastCode = code;
            LastReadable = readable;
            rearmPending = voiceEnabled && trialActive && !lifecycleBlocked;
            AdvanceEpoch();
            return VoiceOutcome.Refuse(code, readable);
        }

        /// <summary>
        /// Barge-in evidence is at least one complete word, not any sound: an
        /// interim fragment, a breath, or room noise stops nothing.
        /// </summary>
        static bool HasCompleteWord(string text)
        {
            var run = 0;
            foreach (var character in text ?? "")
            {
                if (char.IsLetterOrDigit(character))
                {
                    run++;
                    if (run >= 2)
                        return true;
                }
                else if (run > 0)
                {
                    run = 0;
                }
            }
            return false;
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
