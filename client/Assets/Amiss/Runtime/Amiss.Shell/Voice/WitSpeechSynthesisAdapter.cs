using System;
using Meta.WitAi.TTS.Integrations;
using Meta.WitAi.TTS.Utilities;
using UnityEngine;

namespace Amiss.Shell.Voice
{
    /// <summary>
    /// Wit synthesis behind the <see cref="ISpeechSynthesisPort"/> contract
    /// (06 section 1). One <c>TTSSpeaker</c> is placed at the approved wall
    /// speaker root; its existing 3D <c>AudioSource</c> is reused, so no second
    /// agent sound source is created. Only text the controller already
    /// validated can reach <see cref="Speak"/>. <c>Stop()</c> stops loading and
    /// playback immediately.
    /// </summary>
    public sealed class WitSpeechSynthesisAdapter : ISpeechSynthesisPort
    {
        readonly WitVoiceSetup setup;
        readonly Func<Transform> speakerRoot;

        TTSWit service;
        TTSSpeaker speaker;
        string unitId;
        Action<string> completed;
        Action<string> failed;

        public WitSpeechSynthesisAdapter(WitVoiceSetup setup, Func<Transform> speakerRoot)
        {
            this.setup = setup ?? throw new ArgumentNullException(nameof(setup));
            this.speakerRoot = speakerRoot;
        }

        public bool Configured => setup.Configured;

        /// <summary>Rebuilds the service after a configuration change.</summary>
        public void Reset()
        {
            if (speaker != null)
            {
                try
                {
                    speaker.Stop();
                }
                catch (Exception)
                {
                }

                UnityEngine.Object.Destroy(speaker);
                speaker = null;
            }

            if (service != null)
            {
                UnityEngine.Object.Destroy(service.gameObject);
                service = null;
            }

            unitId = null;
            completed = null;
            failed = null;
        }

        public void Speak(string unitId, string text, Action<string> completed,
            Action<string> failed)
        {
            var output = EnsureSpeaker();
            if (output == null)
            {
                failed?.Invoke(unitId);
                return;
            }

            this.unitId = unitId;
            this.completed = completed;
            this.failed = failed;
            try
            {
                output.Speak(text ?? "");
            }
            catch (Exception)
            {
                Finish(unitId, false);
            }
        }

        /// <summary>
        /// Stops all loading and playback now. No completion callback follows
        /// an explicit stop, so no unit is marked complete by inference.
        /// </summary>
        public void Stop()
        {
            unitId = null;
            completed = null;
            failed = null;
            try
            {
                speaker?.Stop();
            }
            catch (Exception)
            {
            }
        }

        TTSSpeaker EnsureSpeaker()
        {
            var root = speakerRoot?.Invoke();
            if (root == null)
                return null;
            if (speaker != null && service != null)
            {
                service.Configuration = setup.Configuration;
                return speaker;
            }

            try
            {
                var host = new GameObject("WitTts");
                host.transform.SetParent(root, false);
                host.SetActive(false);
                service = host.AddComponent<TTSWit>();
                host.AddComponent<TTSRuntimeLRUCache>();
                host.AddComponent<TTSDiskCache>();
                service.Configuration = setup.Configuration;
                host.SetActive(true);
                // The voice preset is a founder product choice (06 section 9),
                // so the adapter uses the SDK's declared default voice through
                // the speaker's custom settings rather than naming one here.
                speaker = root.gameObject.GetComponent<TTSSpeaker>();
                if (speaker == null)
                    speaker = root.gameObject.AddComponent<TTSSpeaker>();
                speaker.customWitVoiceSettings = new TTSWitVoiceSettings();
                speaker.Events.OnFinishedSpeaking.AddListener(OnFinishedSpeaking);
                speaker.Events.OnClipLoadFailed.AddListener(OnLoadFailed);
                return speaker;
            }
            catch (Exception)
            {
                if (service != null)
                {
                    UnityEngine.Object.Destroy(service.gameObject);
                    service = null;
                }

                speaker = null;
                return null;
            }
        }

        void OnFinishedSpeaking(TTSSpeaker source, string text)
        {
            if (unitId == null)
                return;
            Finish(unitId, true);
        }

        void OnLoadFailed(TTSSpeaker source, string text)
        {
            if (unitId == null)
                return;
            Finish(unitId, false);
        }

        void Finish(string id, bool succeeded)
        {
            var callback = succeeded ? completed : failed;
            unitId = null;
            completed = null;
            failed = null;
            callback?.Invoke(id);
        }
    }
}
