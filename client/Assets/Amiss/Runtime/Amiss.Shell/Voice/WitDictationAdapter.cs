using System;
using Meta.WitAi.Configuration;
using Oculus.Voice.Dictation;
using Oculus.Voice.Dictation.Configuration;
using Oculus.VoiceSDK.Utilities;
using UnityEngine;

namespace Amiss.Shell.Voice
{
    /// <summary>
    /// Wit dictation behind the <see cref="IDictationPort"/> contract
    /// (06 section 1). One consented window uses
    /// <c>AppDictationExperience.ActivateImmediately()</c>; every close path
    /// calls <c>Deactivate()</c> before any transcript is accepted. Raw audio
    /// stays inside the SDK request and is never returned here. The SDK's own
    /// 20-second <c>Activate()</c> keepalive timers are not used: the local
    /// controller owns the window length.
    /// </summary>
    public sealed class WitDictationAdapter : IDictationPort
    {
        readonly WitVoiceSetup setup;
        readonly Transform parent;

        AppDictationExperience service;
        Window window;
        bool unavailable;

        public WitDictationAdapter(WitVoiceSetup setup, Transform parent)
        {
            this.setup = setup ?? throw new ArgumentNullException(nameof(setup));
            this.parent = parent;
        }

        public bool Configured => setup.Configured;

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

        public IDictationSession Begin(string turnId, DictationCallbacks callbacks)
        {
            if (!setup.Configured || unavailable)
                return null;

            var platform = EnsureService();
            if (platform == null)
                return null;

            if (window != null)
            {
                window.Cancel();
                window = null;
            }

            var started = new Window(this, turnId, callbacks);
            window = started;
            try
            {
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
                    // No chained reactivation: one window per explicit turn
                    // (06 section 3.1).
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

                // The platform requests the microphone on enable, so this
                // object is only created after the local consent and
                // permission gates have already passed.
                service.DictationEvents.OnStartListening.AddListener(OnPlatformListening);
                service.DictationEvents.OnFullTranscription.AddListener(OnPlatformTranscription);
                service.DictationEvents.OnError.AddListener(OnPlatformError);
                host.SetActive(true);
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

        void OnPlatformListening() => window?.Activated();

        void OnPlatformTranscription(string text) => window?.Deliver(text);

        void OnPlatformError(string error, string message) =>
            window?.Fail(string.IsNullOrEmpty(message) ? error : message);

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
