using System;
using Meta.WitAi.Configuration;
using Meta.WitAi.Data;
using Oculus.Voice.Dictation;
using Oculus.Voice.Dictation.Configuration;
using Oculus.VoiceSDK.Utilities;
using UnityEngine;

namespace Amiss.Shell.Voice
{
    /// <summary>
    /// Wit dictation behind the <see cref="IDictationPort"/> contract
    /// (06 section 1). Automatic full-duplex turns use
    /// <c>AppDictationExperience.Activate()</c>; explicit fallback uses
    /// <c>ActivateImmediately()</c>. Partial text supplies barge-in evidence,
    /// stop events close capture before transcription, and every local close
    /// path calls <c>Deactivate()</c>. Raw audio stays inside the SDK request.
    /// </summary>
    public sealed class WitDictationAdapter : IDictationPort
    {
        readonly WitVoiceSetup setup;
        readonly Transform parent;

        AppDictationExperience service;
        AndroidAecMic aecMic;
        Window window;
        bool unavailable;

        public WitDictationAdapter(WitVoiceSetup setup, Transform parent)
        {
            this.setup = setup ?? throw new ArgumentNullException(nameof(setup));
            this.parent = parent;
        }

        public bool Configured => setup.Configured;

        public bool PlatformIntegrationActive => service != null && service.HasPlatformIntegrations;

        public bool AecActive => aecMic != null && aecMic.AecActive;

        public bool MicrophonePermissionGranted => MicPermissionsManager.HasMicPermission();

        /// <summary>
        /// Raises the SDK's system microphone prompt without creating a
        /// dictation service or opening a window. <c>RequestMicPermission</c>
        /// is the same call <c>AppDictationExperience.OnEnable</c> makes, so a
        /// fresh install can be granted from the MIC press instead of waiting
        /// for a prompt that never comes.
        /// </summary>
        public MicrophonePermissionRequest RequestMicrophonePermission()
        {
            if (MicPermissionsManager.HasMicPermission())
                return MicrophonePermissionRequest.AlreadyGranted;
            try
            {
                // The SDK invokes its optional callback only on a grant and
                // offers no denial callback; the live HasMicPermission() read
                // stays the one truth, so no callback is registered here.
                MicPermissionsManager.RequestMicPermission();
                return MicrophonePermissionRequest.PromptRaised;
            }
            catch (Exception)
            {
                return MicrophonePermissionRequest.Unavailable;
            }
        }

        public bool CaptureActive => window != null && service != null && service.MicActive;

        /// <summary>Rebuilds the platform object after a configuration change.</summary>
        public void Reset()
        {
            if (window != null)
            {
                window.Cancel();
                window = null;
            }

            if (service != null)
            {
                service.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(service.gameObject);
            }

            service = null;
            unavailable = false;
        }

        public IDictationSession Begin(string turnId, DictationCallbacks callbacks,
            DictationActivationMode mode = DictationActivationMode.Immediate)
        {
            if (!setup.Configured || unavailable)
                return null;

            var platform = EnsureService();
            if (platform == null)
                return null;

            if (window != null)
            {
                Debug.Log("[Amiss] dictation begin replaces open window " + window.TurnId);
                window.Cancel();
                window = null;
            }

            var started = new Window(this, turnId, callbacks);
            window = started;
            try
            {
                if (mode == DictationActivationMode.Automatic)
                    platform.Activate();
                else
                    platform.ActivateImmediately();
            }
            catch (Exception)
            {
                window = null;
                return null;
            }

            return started;
        }

        AppDictationExperience EnsureService()
        {
            if (service != null)
                return service;
            try
            {
                var host = new GameObject("WitDictation");
                host.transform.SetParent(parent, false);
                host.SetActive(false);

                var runtime = new WitDictationRuntimeConfiguration
                {
                    witConfiguration = setup.Configuration,
                    // No SDK multi-phrase chain: the controller owns each turn.
                    dictationConfiguration = new DictationConfiguration
                    {
                        multiPhrase = false,
                        scenario = "default",
                        inputType = "text_default"
                    }
                };

                service = host.AddComponent<AppDictationExperience>();
                service.RuntimeDictationConfiguration = runtime;
                service.UsePlatformIntegrations = false;
#if UNITY_ANDROID && !UNITY_EDITOR
                aecMic = host.AddComponent<AndroidAecMic>();
                AudioBuffer.Instance.MicInput = aecMic;
#endif

                // The platform requests the microphone on enable, so this
                // object is only created after the local consent and
                // permission gates have already passed.
                service.DictationEvents.OnStartListening.AddListener(OnPlatformListening);
                service.DictationEvents.OnPartialTranscription.AddListener(OnPlatformPartialTranscription);
                service.DictationEvents.OnStoppedListening.AddListener(OnPlatformCaptureEnded);
                service.DictationEvents.OnFullTranscription.AddListener(OnPlatformTranscription);
                service.DictationEvents.OnError.AddListener(OnPlatformError);
                host.SetActive(true);
                Debug.Log("[Amiss] dictation input platform=" +
                    service.HasPlatformIntegrations + " aec=" + AecActive);
                return service;
            }
            catch (Exception)
            {
                unavailable = true;
                if (service != null)
                {
                    UnityEngine.Object.Destroy(service.gameObject);
                    service = null;
                }

                return null;
            }
        }

        void OnPlatformListening()
        {
            Debug.Log("[Amiss] dictation listening platform=" + PlatformIntegrationActive +
                " aec=" + AecActive);
            window?.Activated();
        }

        void OnPlatformPartialTranscription(string text) => window?.Partial(text);

        void OnPlatformCaptureEnded()
        {
            if (window == null)
                Debug.Log("[Amiss] dictation capture ended with no open window");
            window?.CaptureEnded();
        }

        void OnPlatformTranscription(string text)
        {
            if (window == null)
                Debug.Log("[Amiss] dictation transcript dropped: no open window \"" +
                    (text ?? "") + "\"");
            window?.Deliver(text);
        }

        void OnPlatformError(string error, string message)
        {
            Debug.Log("[Amiss] dictation error code=" + error + " message=" + message);
            window?.Fail(string.IsNullOrEmpty(message) ? error : message);
        }

        sealed class Window : IDictationSession
        {
            readonly WitDictationAdapter adapter;
            readonly DictationCallbacks callbacks;
            bool closed;
            bool cancelled;

            public Window(WitDictationAdapter adapter, string turnId,
                DictationCallbacks callbacks)
            {
                this.adapter = adapter;
                this.callbacks = callbacks ?? new DictationCallbacks();
                TurnId = turnId ?? "";
            }

            public string TurnId { get; }

            public void Close()
            {
                if (closed || cancelled)
                    return;
                closed = true;
                try
                {
                    // Microphone off first; the transcript may follow.
                    if (adapter.service != null)
                        adapter.service.Deactivate();
                }
                catch (Exception)
                {
                    // The local window is already closed; a platform stop
                    // failure cannot reopen it.
                }
            }

            public void Cancel()
            {
                if (cancelled)
                    return;
                cancelled = true;
                closed = true;
                try
                {
                    adapter.service?.Cancel();
                }
                catch (Exception)
                {
                }

                adapter.window = null;
            }

            internal void Activated()
            {
                if (!cancelled)
                    callbacks.Activated?.Invoke();
            }

            internal void Partial(string text)
            {
                if (!cancelled)
                    callbacks.PartialText?.Invoke(text ?? "");
            }

            internal void CaptureEnded()
            {
                if (!cancelled)
                    callbacks.CaptureEnded?.Invoke();
            }

            internal void Deliver(string text)
            {
                if (cancelled)
                    return;
                adapter.window = null;
                callbacks.FinalText?.Invoke(text ?? "");
            }

            internal void Fail(string code)
            {
                if (cancelled)
                    return;
                adapter.window = null;
                callbacks.Failed?.Invoke(code ?? "voice_transcription");
            }
        }
    }
}
