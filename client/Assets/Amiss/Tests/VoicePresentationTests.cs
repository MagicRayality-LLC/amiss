using System;
using System.Threading.Tasks;
using Amiss.Shell;
using Amiss.Shell.Ports;
using Amiss.Shell.UI;
using Amiss.Shell.Voice;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// The voice presentation boundary: model-authored text reaches the one
    /// subtitle slot as literal text, the hub microphone row reports the real
    /// voice gates, the microphone control is no longer a placeholder, and the
    /// Android manifest declares the permissions both live paths need.
    /// </summary>
    public class VoicePresentationTests
    {
        [Test]
        public void ModelSubtitleTextReachesTheSlotAsLiteralText()
        {
            var escaped = CorePanelController.EscapeRichText("<b>not bold</b>");

            StringAssert.StartsWith("<noparse>", escaped);
            StringAssert.EndsWith("</noparse>", escaped);
            StringAssert.Contains("<b>not bold</b>", escaped,
                "the model text is carried through unchanged, so TMP cannot swallow it");

            var panel = Panel(null);
            panel.ShowSubtitle("<b>not bold</b>");
            StringAssert.Contains("<b>not bold</b>", panel.SubtitleText);
        }

        [Test]
        public void ModelAuthoredClosingTagCannotEscapeTheLiteralBlock()
        {
            var escaped = CorePanelController.EscapeRichText("look </noparse><size=80>big</size>");

            Assert.AreEqual(1, Count(escaped, "</noparse>"),
                "only the wrapper's own closing tag remains, so the rest stays literal");
        }

        [Test]
        public void HubMicRowReadsTheRealVoiceGates()
        {
            Assert.AreEqual("MIC UNAVAILABLE", EpisodeHub.MicLabel(null));

            var noConsent = new VoiceController(new StubDictation(), new StubSpeech(),
                new StubTutor(), VoiceConsentStatus.Absent);
            Assert.AreEqual("MIC OFF", EpisodeHub.MicLabel(Panel(noConsent)));

            var denied = new VoiceController(new StubDictation { Permission = false },
                new StubSpeech(), new StubTutor(), VoiceConsentStatus.Accepted);
            Assert.AreEqual("MIC NO PERM", EpisodeHub.MicLabel(Panel(denied)));

            var unconfigured = new VoiceController(new StubDictation { Configured = false },
                new StubSpeech(), new StubTutor(), VoiceConsentStatus.Accepted);
            Assert.AreEqual("MIC NO TOKEN", EpisodeHub.MicLabel(Panel(unconfigured)));

            var ready = new VoiceController(new StubDictation(), new StubSpeech(),
                new StubTutor(), VoiceConsentStatus.Accepted);
            Assert.AreEqual("MIC READY", EpisodeHub.MicLabel(Panel(ready)));
        }

        [Test]
        public void HubMicRowShowsThePermissionRequestThenTheGrantedState()
        {
            var dictation = new StubDictation { Permission = false };
            var voice = new VoiceController(dictation, new StubSpeech(),
                new StubTutor(), VoiceConsentStatus.Accepted);
            var panel = Panel(voice);

            Assert.AreEqual("MIC NO PERM",
                EpisodeHub.MicLabel(panel, voice.MicrophonePermissionRequested));

            Assert.IsFalse(panel.Apply(new PanelCommand(PanelCommandKind.ToggleMicrophone)),
                "a raised prompt is not an open capture");
            Assert.AreEqual(1, dictation.Requests);
            Assert.IsTrue(voice.MicrophonePermissionRequested);
            Assert.AreEqual("MIC PERM PENDING",
                EpisodeHub.MicLabel(panel, voice.MicrophonePermissionRequested));

            dictation.Permission = true;
            Assert.IsFalse(voice.MicrophonePermissionRequested);
            Assert.AreEqual("MIC READY",
                EpisodeHub.MicLabel(panel, voice.MicrophonePermissionRequested));
        }

        [Test]
        public void MicrophoneControlTogglesGlobalVoice()
        {
            var voice = new VoiceController(new StubDictation(), new StubSpeech(),
                new StubTutor(), VoiceConsentStatus.Accepted);
            var panel = Panel(voice);

            Assert.IsTrue(panel.Apply(new PanelCommand(PanelCommandKind.ToggleMicrophone)));
            StringAssert.Contains("Voice off", panel.LastCommandOutcome);
            Assert.IsFalse(panel.MicrophoneAvailable);
            Assert.AreEqual("MIC OFF", EpisodeHub.MicLabel(panel));

            Assert.IsTrue(panel.Apply(new PanelCommand(PanelCommandKind.ToggleMicrophone)));
            StringAssert.Contains("Voice enabled", panel.LastCommandOutcome);
            Assert.IsTrue(panel.MicrophoneAvailable);
            Assert.IsTrue(panel.MicrophoneConsentAccepted);
        }

        [Test]
        public void MicrophoneControlFailsClosedWithoutAVoiceLayer()
        {
            var panel = Panel(null);

            Assert.IsFalse(panel.Apply(new PanelCommand(PanelCommandKind.ToggleMicrophone)));
            Assert.IsFalse(panel.MicrophoneAvailable);
            Assert.AreEqual(VoiceState.Closed, panel.VoiceState);
        }

        [Test]
        public void HubHasNoAskTutorControl()
        {
            var source = System.IO.File.ReadAllText(
                "Assets/Amiss/Runtime/Amiss.Shell/UI/EpisodeHub.cs");

            Assert.IsFalse(source.Contains("\"AskTutor\""));
            Assert.IsFalse(source.Contains("ASK TUTOR"));
        }

        [Test]
        public void ManifestDeclaresNetworkAndMicrophoneForTheVoiceAndModelPaths()
        {
            var manifest = System.IO.File.ReadAllText(
                "Assets/Plugins/Android/AndroidManifest.xml");

            StringAssert.Contains("android.permission.INTERNET", manifest,
                "the model call and Wit requests need network access on device");
            StringAssert.Contains("android.permission.RECORD_AUDIO", manifest,
                "Wit dictation needs the recording permission");
        }

        [Test]
        public void UnconfiguredVoiceSetupHoldsNoProviderIdentity()
        {
            var setup = new WitVoiceSetup(
                new WitTokenStore("amiss.test.voice_token_" + Guid.NewGuid().ToString("N")));

            Assert.IsFalse(setup.Configured);
            Assert.IsTrue(string.IsNullOrEmpty(setup.Configuration.GetClientAccessToken()),
                "a build without a device token carries no Wit identity");
        }

        [Test]
        public void RuntimeCreatedWitSpeakerSubscribesToAvailablePlaybackEvents()
        {
            var tokens = new WitTokenStore("amiss.test.voice_token_" + Guid.NewGuid().ToString("N"));
            tokens.Save("editor-test-token");
            var setup = new WitVoiceSetup(tokens);
            var configuration = setup.Configuration;
            var root = new GameObject("VoiceSpeakerTest", typeof(AudioSource));
            var adapter = new WitSpeechSynthesisAdapter(setup, () => root.transform);
            try
            {
                var ensure = typeof(WitSpeechSynthesisAdapter).GetMethod("EnsureSpeaker",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(ensure);
                object speaker = null;
                Assert.DoesNotThrow(() => speaker = ensure.Invoke(adapter, null));
                Assert.IsNotNull(speaker);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(configuration);
                tokens.Clear();
            }
        }

        [Test]
        public void AecMicReportsTheWitTransmissionEncoding()
        {
            var root = new GameObject("VoiceAecEncodingTest");
            try
            {
                var mic = root.AddComponent<AndroidAecMic>();

                Assert.AreEqual(16000, mic.AudioEncoding.samplerate,
                    "Wit transmission runs at 16 kHz; reporting the 48 kHz capture " +
                    "rate makes the SDK resampler emit near-silent audio");
                Assert.AreEqual(1, mic.AudioEncoding.numChannels);
                StringAssert.Contains("rate=16k", mic.AudioEncoding.ToString());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DictationAdapterHandlesEveryTerminalSdkEvent()
        {
            var source = System.IO.File.ReadAllText(
                "Assets/Amiss/Runtime/Amiss.Shell/Voice/WitDictationAdapter.cs");

            StringAssert.Contains("OnComplete", source,
                "a request that completes with no transcript must end the turn");
            StringAssert.Contains("OnStoppedListeningDueToInactivity", source,
                "the 2 s silence / 20 s maximum endpoint must end the turn");
            StringAssert.Contains("OnCanceled", source,
                "a cancelled request must end the turn instead of stranding it");
        }

        [Test]
        public void RuntimeDictationDoesNotClaimPlatformOrAecSupportInEditor()
        {
            var tokens = new WitTokenStore("amiss.test.voice_token_" + Guid.NewGuid().ToString("N"));
            tokens.Save("editor-test-token");
            var setup = new WitVoiceSetup(tokens);
            var configuration = setup.Configuration;
            var root = new GameObject("VoiceDictationTest");
            var adapter = new WitDictationAdapter(setup, root.transform);
            try
            {
                var ensure = typeof(WitDictationAdapter).GetMethod("EnsureService",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(ensure);
                Assert.DoesNotThrow(() => ensure.Invoke(adapter, null));
                var field = typeof(WitDictationAdapter).GetField("service",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var service = field.GetValue(adapter);
                var property = service.GetType().GetProperty("UsePlatformIntegrations");
                Assert.IsFalse((bool)property.GetValue(service));
                Assert.IsFalse(adapter.PlatformIntegrationActive,
                    "the editor does not prove Android platform integration");
                Assert.IsFalse(adapter.AecActive, "no capture has started and no AEC is active");
                var mic = root.AddComponent<AndroidAecMic>();
                Assert.IsFalse(mic.AecActive);
                Assert.AreEqual(48000, mic.GetMicSampleRate());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(configuration);
                tokens.Clear();
            }
        }

        static int Count(string text, string value)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        static CorePanelController Panel(VoiceController voice) =>
            new(new UnboundSimulationRuntime(), new UnboundGuideRunner(), null, voice);

        sealed class StubDictation : IDictationPort, IDictationSession
        {
            public bool Configured { get; set; } = true;
            public bool Permission { get; set; } = true;
            public int Requests;
            public MicrophonePermissionRequest RequestOutcome =
                MicrophonePermissionRequest.PromptRaised;

            public bool MicrophonePermissionGranted => Permission;

            public MicrophonePermissionRequest RequestMicrophonePermission()
            {
                Requests++;
                return RequestOutcome;
            }

            public bool CaptureActive => false;

            public string TurnId => "stub-turn";

            public IDictationSession Begin(string turnId, DictationCallbacks callbacks,
                DictationActivationMode mode = DictationActivationMode.Immediate) => this;

            public void Close()
            {
            }

            public void Cancel()
            {
            }
        }

        sealed class StubSpeech : ISpeechSynthesisPort
        {
            public bool Configured { get; set; } = true;

            public void Speak(string unitId, string text, Action<string> completed,
                Action<string> failed)
            {
            }

            public void Stop()
            {
            }
        }

        sealed class StubTutor : ITutorTurnPort
        {
            public Task<TutorTurnOutcome> Ask(string origin, string turnId, string utterance) =>
                Task.FromResult(TutorTurnOutcome.Silent("not_used"));
        }
    }
}
