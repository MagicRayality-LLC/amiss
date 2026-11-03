using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amiss.Shell.Evaluation;
using Amiss.Shell.Tutor;
using Amiss.Shell.Voice;
using NUnit.Framework;

namespace Amiss.Tests
{
    /// <summary>
    /// The one voice lifecycle of design 06 sections 3 and 4: consent and
    /// permission gates, the two capture-open paths, stop-before-capture,
    /// answer origin, the bounded speech queue, and stale-callback rejection.
    /// The adapters are fakes: no network, no microphone, no audio device.
    /// </summary>
    public class VoiceControllerTests
    {
        static readonly string[] OneLine = { "The air in the room is warmer now." };

        sealed class FakeDictation : IDictationPort
        {
            public bool Configured { get; set; } = true;
            public bool Permission { get; set; } = true;
            public bool FailStart;
            public int Begins;
            public int Requests;
            public MicrophonePermissionRequest RequestOutcome =
                MicrophonePermissionRequest.PromptRaised;
            public Window Last;
            public readonly List<string> Log;

            public FakeDictation(List<string> log) => Log = log;

            public bool MicrophonePermissionGranted => Permission;

            public MicrophonePermissionRequest RequestMicrophonePermission()
            {
                Requests++;
                Log.Add("dictation.request_permission");
                return RequestOutcome;
            }

            public bool CaptureActive => Last != null && Last.Open && !Last.Cancelled;

            public IDictationSession Begin(string turnId, DictationCallbacks callbacks)
            {
                Begins++;
                Log.Add("dictation.begin");
                if (FailStart)
                    return null;
                Last = new Window(this, turnId, callbacks);
                return Last;
            }

            public sealed class Window : IDictationSession
            {
                readonly FakeDictation owner;
                readonly DictationCallbacks callbacks;

                public Window(FakeDictation owner, string turnId, DictationCallbacks callbacks)
                {
                    this.owner = owner;
                    this.callbacks = callbacks;
                    TurnId = turnId;
                    Open = true;
                }

                public string TurnId { get; }
                public bool Open { get; private set; }
                public bool Cancelled { get; private set; }
                public int Closes { get; private set; }
                public int Cancels { get; private set; }

                public void Close()
                {
                    Closes++;
                    owner.Log.Add("dictation.close");
                    Open = false;
                }

                public void Cancel()
                {
                    Cancels++;
                    owner.Log.Add("dictation.cancel");
                    Open = false;
                    Cancelled = true;
                }

                public void Activate() => callbacks.Activated();

                public void Deliver(string text) => callbacks.FinalText(text);

                public void Fail(string code) => callbacks.Failed(code);
            }
        }

        sealed class FakeSpeech : ISpeechSynthesisPort
        {
            public bool Configured { get; set; } = true;
            public readonly List<string> Spoken = new();
            public readonly List<string> Ids = new();
            public int Stops;
            public readonly List<string> Log;
            Action<string> completed;
            Action<string> failed;

            public FakeSpeech(List<string> log) => Log = log;

            public void Speak(string unitId, string text, Action<string> onCompleted,
                Action<string> onFailed)
            {
                Log.Add("speech.speak");
                Spoken.Add(text);
                Ids.Add(unitId);
                completed = onCompleted;
                failed = onFailed;
            }

            public void Stop()
            {
                Log.Add("speech.stop");
                Stops++;
                completed = null;
                failed = null;
            }

            public void CompleteLast() => completed?.Invoke(Ids.Count > 0 ? Ids[^1] : "");

            public void FailLast() => failed?.Invoke(Ids.Count > 0 ? Ids[^1] : "");
        }

        sealed class FakeTutor : ITutorTurnPort
        {
            public int Calls;
            public string Origin = "";
            public string TurnId = "";
            public string Utterance = "";
            public TutorTurnOutcome Next = TutorTurnOutcome.Spoken(OneLine);

            public Task<TutorTurnOutcome> Ask(string origin, string turnId, string utterance)
            {
                Calls++;
                Origin = origin;
                TurnId = turnId;
                Utterance = utterance;
                return Task.FromResult(Next);
            }
        }

        sealed class Harness
        {
            public readonly List<string> Log = new();
            public readonly FakeDictation Dictation;
            public readonly FakeSpeech Speech;
            public readonly FakeTutor Tutor = new();
            public double Now;
            public VoiceController Controller;

            public Harness(VoiceConsentStatus consent = VoiceConsentStatus.Accepted)
            {
                Dictation = new FakeDictation(Log);
                Speech = new FakeSpeech(Log);
                Controller = new VoiceController(Dictation, Speech, Tutor, consent, () => Now);
            }

            public void Listen()
            {
                Controller.ToggleExplicitCapture();
                Dictation.Last.Activate();
            }

            public void SpeakAValidatedReply(string utterance = "why does it feel cold?")
            {
                Listen();
                Controller.CloseCapture();
                Dictation.Last.Deliver(utterance);
            }
        }

        [Test]
        public void CaptureOpensOnlyAfterThePlatformReportsTheMicrophoneOn()
        {
            var harness = new Harness();
            var outcome = harness.Controller.ToggleExplicitCapture();

            Assert.IsTrue(outcome.Accepted, outcome.Readable);
            Assert.AreEqual(1, harness.Dictation.Begins);
            Assert.AreEqual(VoiceState.Closed, harness.Controller.State,
                "the indicator must follow actual adapter state, not the request");

            harness.Dictation.Last.Activate();
            Assert.AreEqual(VoiceState.Listening, harness.Controller.State);
        }

        [Test]
        public void ClosingCaptureStopsTheMicrophoneBeforeTranscribing()
        {
            var harness = new Harness();
            harness.Listen();

            var outcome = harness.Controller.CloseCapture();

            Assert.IsTrue(outcome.Accepted, outcome.Readable);
            Assert.AreEqual(1, harness.Dictation.Last.Closes, "Deactivate must precede transcription");
            Assert.AreEqual(VoiceState.Transcribing, harness.Controller.State);
        }

        [Test]
        public void FinalTextReachesTheTutorWithAnswerOrigin()
        {
            var harness = new Harness();
            harness.SpeakAValidatedReply("how warm is the room?");

            Assert.AreEqual(1, harness.Tutor.Calls);
            Assert.AreEqual(VoiceController.AnswerOrigin, harness.Tutor.Origin);
            Assert.AreEqual("how warm is the room?", harness.Tutor.Utterance);
            Assert.AreEqual(harness.Controller.CurrentTurn.TurnId, harness.Tutor.TurnId,
                "the request turn is the capture turn");
            Assert.AreEqual(VoiceState.PlayingInterruptible, harness.Controller.State);
            CollectionAssert.AreEqual(OneLine, harness.Speech.Spoken,
                "exactly the admitted speech text reaches the speaker");
        }

        [Test]
        public void RejectedReplyIsNeverSpoken()
        {
            var harness = new Harness();
            harness.Tutor.Next = TutorTurnOutcome.Silent("citation_invalid");

            harness.SpeakAValidatedReply();

            Assert.AreEqual(1, harness.Tutor.Calls);
            Assert.IsEmpty(harness.Speech.Spoken, "an invalid reply produces silence");
            Assert.AreEqual(VoiceState.Closed, harness.Controller.State);
            Assert.AreEqual("citation_invalid", harness.Controller.LastCode);
        }

        [Test]
        public void AdmittedReplyWithoutSpeechStaysSilent()
        {
            var harness = new Harness();
            harness.Tutor.Next = TutorTurnOutcome.Spoken(Array.Empty<string>());

            harness.SpeakAValidatedReply();

            Assert.IsEmpty(harness.Speech.Spoken);
            Assert.AreEqual(VoiceState.Closed, harness.Controller.State);
            Assert.AreEqual("no_speech", harness.Controller.LastCode);
        }

        [Test]
        public void ExplicitCaptureStopsPlaybackBeforeOpeningANewWindow()
        {
            var harness = new Harness();
            harness.SpeakAValidatedReply();
            Assert.AreEqual(VoiceState.PlayingInterruptible, harness.Controller.State);

            harness.Log.Clear();
            var outcome = harness.Controller.ToggleExplicitCapture();

            Assert.IsTrue(outcome.Accepted, outcome.Readable);
            var stop = harness.Log.IndexOf("speech.stop");
            var begin = harness.Log.IndexOf("dictation.begin");
            Assert.GreaterOrEqual(stop, 0, "TTS must be stopped");
            Assert.Greater(begin, stop, "Stop() precedes ActivateImmediately()");
            Assert.AreEqual(1, harness.Speech.Stops);
            Assert.AreEqual(2, harness.Dictation.Begins);
        }

        [Test]
        public void ConsentAbsentShowsTheNoticeAndRefusesCapture()
        {
            var harness = new Harness(VoiceConsentStatus.Absent);

            var first = harness.Controller.ToggleExplicitCapture();

            Assert.IsFalse(first.Accepted);
            Assert.AreEqual("voice_consent_required", first.Code);
            StringAssert.Contains("Raw audio is not stored", first.Readable);
            Assert.AreEqual(0, harness.Dictation.Begins, "no capture before consent");
            Assert.AreEqual(VoiceState.Closed, harness.Controller.State);
        }

        [Test]
        public void SecondPressAcceptsConsentAndOpensOneWindow()
        {
            var harness = new Harness(VoiceConsentStatus.Absent);

            harness.Controller.ToggleExplicitCapture();
            var second = harness.Controller.ToggleExplicitCapture();

            Assert.IsTrue(harness.Controller.ConsentAccepted);
            Assert.IsTrue(second.Accepted, second.Readable);
            Assert.AreEqual(1, harness.Dictation.Begins);
        }

        [Test]
        public void MissingMicPermissionIssuesOneRequestAndNoCapture()
        {
            var harness = new Harness();
            harness.Dictation.Permission = false;

            var outcome = harness.Controller.ToggleExplicitCapture();

            Assert.AreEqual(1, harness.Dictation.Requests,
                "the MIC press must raise the system prompt on a fresh install");
            Assert.AreEqual(0, harness.Dictation.Begins, "the prompt precedes any capture");
            Assert.IsTrue(harness.Controller.MicrophonePermissionRequested);
            Assert.AreEqual("voice_microphone_requested", outcome.Code);
            Assert.AreEqual(VoiceState.Closed, harness.Controller.State);
        }

        [Test]
        public void PermissionGrantAfterThePromptOpensListeningOnTheNextPress()
        {
            var harness = new Harness();
            harness.Dictation.Permission = false;
            harness.Controller.ToggleExplicitCapture();

            harness.Dictation.Permission = true;
            var outcome = harness.Controller.ToggleExplicitCapture();

            Assert.IsTrue(outcome.Accepted, outcome.Readable);
            Assert.AreEqual(1, harness.Dictation.Begins);
            Assert.AreEqual(1, harness.Dictation.Requests,
                "a granted prompt is not raised again");
            Assert.IsFalse(harness.Controller.MicrophonePermissionRequested);

            harness.Dictation.Last.Activate();
            Assert.AreEqual(VoiceState.Listening, harness.Controller.State);
        }

        [Test]
        public void MicrophoneDeniedRefusesCaptureAndUpload()
        {
            var harness = new Harness();
            harness.Dictation.Permission = false;

            var requested = harness.Controller.ToggleExplicitCapture();
            Assert.IsFalse(requested.Accepted, "a prompt request is not a capture");
            Assert.AreEqual("voice_microphone_requested", requested.Code);

            var outcome = harness.Controller.ToggleExplicitCapture();

            Assert.IsFalse(outcome.Accepted);
            Assert.AreEqual("voice_microphone_denied", outcome.Code);
            Assert.AreEqual(1, harness.Dictation.Requests,
                "a refused follow-up press does not raise a second prompt");
            Assert.AreEqual(0, harness.Dictation.Begins);
            Assert.IsFalse(harness.Controller.MicrophoneAvailable);
        }

        [Test]
        public void ConsentNoticeIssuesNoPermissionRequest()
        {
            var harness = new Harness(VoiceConsentStatus.Absent);
            harness.Dictation.Permission = false;

            var first = harness.Controller.ToggleExplicitCapture();

            Assert.AreEqual("voice_consent_required", first.Code);
            Assert.AreEqual(0, harness.Dictation.Requests,
                "the consent notice comes before the system prompt");
            Assert.AreEqual(0, harness.Dictation.Begins);
        }

        [Test]
        public void UnconfiguredBuildRefusesCaptureAndSynthesis()
        {
            var harness = new Harness();
            harness.Dictation.Configured = false;
            harness.Dictation.Permission = false;
            harness.Speech.Configured = false;

            var outcome = harness.Controller.ToggleExplicitCapture();
            Assert.IsFalse(outcome.Accepted);
            Assert.AreEqual("voice_unconfigured", outcome.Code);
            Assert.AreEqual(0, harness.Dictation.Begins);
            Assert.AreEqual(0, harness.Dictation.Requests,
                "an unconfigured build asks for no microphone permission");

            var queued = harness.Controller.EnqueueValidatedSpeech("unit-1", "text");
            Assert.IsTrue(queued.Accepted);
            Assert.IsEmpty(harness.Speech.Spoken, "no synthesis request without configuration");
            Assert.AreEqual("voice_tts_unconfigured", harness.Controller.LastCode);
        }

        [Test]
        public void MicrophoneDenialStillLeavesSynthesisAvailable()
        {
            var harness = new Harness();
            harness.Dictation.Permission = false;

            var queued = harness.Controller.EnqueueValidatedSpeech("unit-1", "text");

            Assert.IsTrue(queued.Accepted, queued.Readable);
            CollectionAssert.AreEqual(new[] { "text" }, harness.Speech.Spoken,
                "the two permission layers are independent");
        }

        [Test]
        public void RevocationStopsBothAdaptersImmediatelyAndBlocksNewWork()
        {
            var harness = new Harness();
            harness.SpeakAValidatedReply();
            harness.Log.Clear();

            harness.Controller.RevokeConsent();

            Assert.AreEqual(VoiceConsentStatus.Revoked, harness.Controller.Consent);
            Assert.GreaterOrEqual(harness.Speech.Stops, 1, "TTS stops on revocation");
            Assert.AreEqual(VoiceState.Closed, harness.Controller.State);

            var blocked = harness.Controller.ToggleExplicitCapture();
            Assert.IsFalse(blocked.Accepted);
            Assert.AreEqual("voice_consent_revoked", blocked.Code);
            Assert.AreEqual(1, harness.Dictation.Begins, "no window opens after withdrawal");
        }

        [Test]
        public void StaleCallbacksFromAnOldTurnAreDiscarded()
        {
            var harness = new Harness();
            harness.Listen();
            var old = harness.Dictation.Last;
            harness.Controller.CloseCapture();

            // A newer explicit turn invalidates the old one's callbacks.
            harness.Controller.ToggleExplicitCapture();
            harness.Dictation.Last.Activate();
            old.Deliver("stale text from the previous window");

            Assert.AreEqual(0, harness.Tutor.Calls, "an old callback cannot become the current turn");
            Assert.AreEqual(VoiceState.Listening, harness.Controller.State);

            harness.Controller.CloseCapture();
            harness.Dictation.Last.Deliver("current text");
            Assert.AreEqual(1, harness.Tutor.Calls);
            Assert.AreEqual("current text", harness.Tutor.Utterance);
        }

        [Test]
        public void EmptyTranscriptionClosesWithoutATutorCall()
        {
            var harness = new Harness();
            harness.Listen();
            harness.Controller.CloseCapture();

            harness.Dictation.Last.Deliver("   ");

            Assert.AreEqual(0, harness.Tutor.Calls);
            Assert.AreEqual(VoiceState.Closed, harness.Controller.State);
            Assert.AreEqual("voice_transcription", harness.Controller.LastCode);
        }

        [Test]
        public void TranscriptionAfterTheTenSecondDeadlineIsDropped()
        {
            var harness = new Harness();
            harness.Listen();
            harness.Controller.CloseCapture();

            harness.Now = VoiceController.TranscriptionDeadlineSeconds + 0.5;
            harness.Controller.Tick();

            Assert.AreEqual(VoiceState.Closed, harness.Controller.State);
            Assert.AreEqual("voice_transcription", harness.Controller.LastCode);
            harness.Dictation.Last.Deliver("too late");
            Assert.AreEqual(0, harness.Tutor.Calls);
        }

        [Test]
        public void QueueHoldsOneActiveAndOnePendingUnit()
        {
            var harness = new Harness();

            Assert.IsTrue(harness.Controller.EnqueueValidatedSpeech("unit-1", "first").Accepted);
            var firstUnit = harness.Controller.ActiveUnit;
            Assert.IsTrue(harness.Controller.EnqueueValidatedSpeech("unit-2", "second").Accepted);
            var third = harness.Controller.EnqueueValidatedSpeech("unit-3", "third");

            Assert.IsFalse(third.Accepted);
            Assert.AreEqual("voice_queue_full", third.Code);
            Assert.AreEqual("unit-1", harness.Controller.ActiveUnit.UnitId);
            Assert.AreEqual("unit-2", harness.Controller.PendingUnit.UnitId);
            Assert.AreEqual(1, harness.Speech.Spoken.Count, "only the active unit is loading");

            harness.Speech.CompleteLast();
            Assert.AreEqual(VoiceUnitCompletion.Completed, firstUnit.Completion);
            Assert.AreEqual("unit-2", harness.Controller.ActiveUnit?.UnitId);
            Assert.AreEqual(2, harness.Speech.Spoken.Count);
        }

        [Test]
        public void MuteStopsAudioAndKeepsTheAcceptedSubtitle()
        {
            var harness = new Harness();
            harness.SpeakAValidatedReply();
            var speaking = harness.Controller.ActiveUnit;

            harness.Controller.SetMuted(true);

            Assert.AreEqual(1, harness.Speech.Stops);
            Assert.AreEqual(VoiceState.Closed, harness.Controller.State);
            Assert.AreEqual(VoiceUnitCompletion.Suppressed, speaking.Completion,
                "mute stops playback without resuming it later");
        }

        [Test]
        public void MuteDoesNotCloseACaptureWindow()
        {
            var harness = new Harness();
            harness.Listen();

            harness.Controller.SetMuted(true);

            Assert.AreEqual(VoiceState.Listening, harness.Controller.State);
            Assert.AreEqual(0, harness.Dictation.Last.Closes);
        }

        [Test]
        public void MicrophoneAvailabilityNeedsAllThreeIndependentGates()
        {
            var harness = new Harness();
            Assert.IsTrue(harness.Controller.MicrophoneAvailable);

            harness.Dictation.Permission = false;
            Assert.IsFalse(harness.Controller.MicrophoneAvailable);
            harness.Dictation.Permission = true;

            harness.Dictation.Configured = false;
            Assert.IsFalse(harness.Controller.MicrophoneAvailable);
            harness.Dictation.Configured = true;

            harness.Controller.RevokeConsent();
            Assert.IsFalse(harness.Controller.MicrophoneAvailable);
        }

        [Test]
        public void VoiceAnswerOriginIsTheDeclaredRequestOrigin()
        {
            Assert.AreEqual(TutorConversation.AnswerOrigin, VoiceController.AnswerOrigin);
            CollectionAssert.Contains(EvaluationContract.Origins, VoiceController.AnswerOrigin);
        }

        [Test]
        public void LifecycleClosureCancelsCaptureAndDropsLateCallbacks()
        {
            var harness = new Harness();
            harness.Listen();
            var open = harness.Dictation.Last;

            harness.Controller.CloseForLifecycle("focus_lost");

            Assert.AreEqual(1, open.Cancels, "a lifecycle closure cancels the window, not just closes it");
            Assert.AreEqual(VoiceState.Closed, harness.Controller.State);
            Assert.IsFalse(harness.Controller.IsBusy);

            open.Deliver("text after focus loss");
            Assert.AreEqual(0, harness.Tutor.Calls, "a late callback cannot reopen the turn");

            var restart = harness.Controller.ToggleExplicitCapture();
            Assert.IsTrue(restart.Accepted, restart.Readable);
            Assert.AreEqual(2, harness.Dictation.Begins,
                "a fresh explicit action is required after the closure");
        }

        [Test]
        public void DictationFailureDuringCaptureClosesTheWindow()
        {
            var harness = new Harness();
            harness.Listen();

            harness.Dictation.Last.Fail("network_unavailable");

            Assert.AreEqual(VoiceState.Closed, harness.Controller.State);
            Assert.AreEqual("network_unavailable", harness.Controller.LastCode);
            Assert.AreEqual(1, harness.Dictation.Last.Cancels,
                "a failed window cannot keep the microphone open");
            Assert.AreEqual(0, harness.Tutor.Calls);
        }

        [Test]
        public void FailedCaptureStartLeavesThePanelPathWorking()
        {
            var harness = new Harness();
            harness.Dictation.FailStart = true;

            var outcome = harness.Controller.ToggleExplicitCapture();

            Assert.IsFalse(outcome.Accepted);
            Assert.AreEqual("voice_capture_failed", outcome.Code);
            Assert.AreEqual(VoiceState.Closed, harness.Controller.State);
            Assert.AreEqual(VoiceConsentStatus.Accepted, harness.Controller.Consent);
        }
    }
}
