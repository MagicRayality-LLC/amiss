using System;
using System.Threading.Tasks;
using Amiss.Shell.Evaluation;
using Amiss.Shell.Tutor;
using UnityEngine;

namespace Amiss.Shell.Voice
{
    /// <summary>
    /// The device voice rig: the two Wit adapters behind the design's ports,
    /// one shared configuration, and the device-saved token. Nothing here is
    /// created until a gate has passed, and an unconfigured build never
    /// captures or sends audio.
    /// </summary>
    public sealed class WitVoiceRig : MonoBehaviour
    {
        WitVoiceSetup setup;

        public WitTokenStore Tokens { get; private set; }

        public WitDictationAdapter Dictation { get; private set; }

        public WitSpeechSynthesisAdapter Speech { get; private set; }

        void Awake()
        {
            Tokens = new WitTokenStore();
            setup = new WitVoiceSetup(Tokens);
            Dictation = new WitDictationAdapter(setup, transform);
            Speech = new WitSpeechSynthesisAdapter(setup, FindSpeakerRoot);
        }

        public bool TokenPresent => Tokens != null && Tokens.Present;

        /// <summary>
        /// Saves the Wit client token from the hub field and rebuilds both
        /// adapters against it. The value is never read back into a view.
        /// </summary>
        public bool SaveToken(string token)
        {
            var saved = Tokens.Save(token);
            if (!saved)
                return false;
            setup.Reset();
            Dictation.Reset();
            Speech.Reset();
            return true;
        }

        public void ClearToken()
        {
            Tokens.Clear();
            setup.Reset();
            Dictation.Reset();
            Speech.Reset();
        }

        /// <summary>
        /// The approved wall speaker root: the wall set's PA speaker, which
        /// carries the one 3D <c>AudioSource</c> of the agent. The TTS speaker
        /// attaches to that object instead of creating a second source.
        /// </summary>
        Transform FindSpeakerRoot()
        {
            var instrument = FindFirstObjectByType<InstrumentRoot>();
            if (instrument == null)
                return null;
            var sources = instrument.GetComponentsInChildren<AudioSource>(true);
            foreach (var source in sources)
            {
                if (source != null && source.spatialBlend > 0.5f)
                    return source.transform;
            }

            return null;
        }
    }

    /// <summary>
    /// Binds the voice turn to the existing tutor conversation and to the one
    /// subtitle slot. Only an admitted reply publishes a subtitle, and only its
    /// accepted speech units can be spoken (06 sections 5 and 6).
    /// </summary>
    public sealed class TutorTurnAdapter : ITutorTurnPort
    {
        readonly TutorConversation conversation;
        readonly Action<string> showSubtitle;

        public TutorTurnAdapter(TutorConversation conversation, Action<string> showSubtitle)
        {
            this.conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
            this.showSubtitle = showSubtitle;
        }

        public async Task<TutorTurnOutcome> Ask(string origin, string turnId, string utterance)
        {
            var admission = await conversation.Ask(origin, "utterance-" + (turnId ?? ""), utterance);
            if (!admission.Admitted)
                return TutorTurnOutcome.Silent(
                    admission.Failure != null ? admission.Failure.ReasonCode : "model_unavailable");
            if (conversation.LastSubtitle.Length > 0)
                showSubtitle?.Invoke(conversation.LastSubtitle);
            return TutorTurnOutcome.Spoken(admission.AcceptedSpeech);
        }
    }

    /// <summary>
    /// The small frame loop the controller needs: it advances the local
    /// transcription deadline and records consent changes for the bounded
    /// local record. It writes no trial state.
    /// </summary>
    public sealed class VoiceLoop : MonoBehaviour
    {
        VoiceController voice;
        PlayerPrefsVoiceConsentStore store;
        Func<bool> safetyHold;
        VoiceConsentStatus saved = VoiceConsentStatus.Absent;

        public void Initialize(VoiceController controller, PlayerPrefsVoiceConsentStore consentStore,
            Func<bool> safetyHold = null)
        {
            voice = controller;
            store = consentStore;
            this.safetyHold = safetyHold;
            saved = controller != null ? controller.Consent : VoiceConsentStatus.Absent;
        }

        void OnEnable() => Application.focusChanged += OnFocusChanged;

        void OnDisable() => Application.focusChanged -= OnFocusChanged;

        void Update()
        {
            if (voice == null)
                return;

            // Focus loss and a local safety hold invalidate the turn and both
            // adapters immediately; capture never restarts by itself
            // (06 section 3.2).
            if (safetyHold != null && safetyHold() && voice.IsBusy)
                voice.CloseForLifecycle("safety_hold");

            voice.Tick();
            if (voice.Consent != saved)
            {
                store?.Save(voice.Consent, Time.realtimeSinceStartupAsDouble);
                saved = voice.Consent;
            }
        }

        void OnFocusChanged(bool focused)
        {
            if (!focused && voice != null && voice.IsBusy)
                voice.CloseForLifecycle("focus_lost");
        }
    }
}
