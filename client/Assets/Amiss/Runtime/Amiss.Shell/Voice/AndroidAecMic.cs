using System;
using System.Collections;
using Meta.WitAi.Interfaces;
using Meta.WitAi.Lib;
using UnityEngine;

namespace Amiss.Shell.Voice
{
    public sealed class AndroidAecMic : MicBase, IAudioInputSource
    {
        const int CaptureSampleRate = 48000;

        /// <summary>
        /// The rate Wit speech transmission uses. Captured frames are
        /// decimated to this rate before they are handed to the SDK.
        /// </summary>
        public const int TransmissionSampleRate = 16000;
        const int VoiceCommunicationSource = 7;
        const int MonoChannel = 16;
        const int Pcm16Encoding = 2;
        const int ReadNonBlocking = 1;

        AudioClip capabilityClip;
        AndroidJavaObject recorder;
        AndroidJavaObject echoCanceller;
        bool recording;
        Action started;
        Action startFailed;
        Action stopped;
        Action<int, float[], float> sampleReady;

        event Action IAudioInputSource.OnStartRecording
        {
            add => started += value;
            remove => started -= value;
        }

        event Action IAudioInputSource.OnStartRecordingFailed
        {
            add => startFailed += value;
            remove => startFailed -= value;
        }

        event Action IAudioInputSource.OnStopRecording
        {
            add => stopped += value;
            remove => stopped -= value;
        }

        event Action<int, float[], float> IAudioInputSource.OnSampleReady
        {
            add => sampleReady += value;
            remove => sampleReady -= value;
        }

        public bool AecActive { get; private set; }

        void Awake()
        {
            capabilityClip = AudioClip.Create("android-aec-input", 1, 1, CaptureSampleRate, false);
            // Wit transmission runs at 16 kHz. AudioBuffer resamples the
            // incoming frames from the rate this encoding reports, so it
            // must stay at the transmission rate even though Android
            // captures at 48 kHz; reporting the capture rate makes the
            // resampler stretch every frame 3x and drop the level below
            // the wake threshold, so no audio is ever sent.
            AudioEncoding.samplerate = TransmissionSampleRate;
        }

        public override string GetMicName() => "android-aec-input";
        public override int GetMicSampleRate() => CaptureSampleRate;
        public override AudioClip GetMicClip() => capabilityClip;
        public override int MicPosition => 0;
        public override bool IsMicListening => recording;

        public override void StartRecording(int sampleDurationMS)
        {
            if (IsRecording)
                StopRecording();
            AecActive = false;
            if (capabilityClip == null)
            {
                startFailed?.Invoke();
                return;
            }
            recording = true;
            // MicBase.StartRecording starts the ReadRawAudio coroutine only
            // for clip-based mics; this class overrides that coroutine, so
            // base.StartRecording is what runs the Android capture loop.
            base.StartRecording(sampleDurationMS);
        }

        public override void StopRecording()
        {
            recording = false;
            Cleanup();
            base.StopRecording();
            stopped?.Invoke();
        }

        protected override IEnumerator ReadRawAudio(int sampleDurationMS)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!StartAndroidCapture(sampleDurationMS))
            {
                startFailed?.Invoke();
                StopRecording();
                yield break;
            }

            started?.Invoke();
            var chunkSamples = CaptureSampleRate * Math.Max(10, sampleDurationMS) / 1000;
            var pcm = new short[chunkSamples];
            // AudioBuffer forwards the whole array it is handed, so the
            // frames delivered here must already be at the 16 kHz
            // transmission rate: every third 48 kHz sample is kept.
            var step = CaptureSampleRate / TransmissionSampleRate;
            var samples = new float[chunkSamples / step + 1];
            var sequence = 0;
            while (recording)
            {
                var count = recorder.Call<int>("read", pcm, 0, pcm.Length, ReadNonBlocking);
                if (count > 0)
                {
                    var levelMax = 0f;
                    var delivered = 0;
                    for (var index = 0; index < count; index += step)
                    {
                        var value = pcm[index] / 32768f;
                        samples[delivered++] = value;
                        var peak = value * value;
                        if (peak > levelMax)
                            levelMax = peak;
                    }
                    var frame = new float[delivered];
                    Array.Copy(samples, frame, delivered);
                    sampleReady?.Invoke(++sequence, frame, levelMax);
                }
                yield return null;
            }
            Cleanup();
#else
            startFailed?.Invoke();
            StopRecording();
            yield break;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        bool StartAndroidCapture(int sampleDurationMS)
        {
            try
            {
                var audioRecord = new AndroidJavaClass("android.media.AudioRecord");
                var minimum = audioRecord.CallStatic<int>("getMinBufferSize",
                    CaptureSampleRate, MonoChannel, Pcm16Encoding);
                if (minimum <= 0)
                    throw new InvalidOperationException("audio_buffer_unavailable");
                var requestedBytes = CaptureSampleRate * 2 * Math.Max(20, sampleDurationMS) / 1000;
                recorder = new AndroidJavaObject("android.media.AudioRecord", VoiceCommunicationSource,
                    CaptureSampleRate, MonoChannel, Pcm16Encoding, Math.Max(minimum, requestedBytes * 2));
                if (recorder.Call<int>("getState") != 1)
                    throw new InvalidOperationException("audio_record_uninitialized");

                var aecClass = new AndroidJavaClass("android.media.audiofx.AcousticEchoCanceler");
                if (!aecClass.CallStatic<bool>("isAvailable"))
                    throw new InvalidOperationException("aec_unavailable");
                echoCanceller = aecClass.CallStatic<AndroidJavaObject>("create",
                    recorder.Call<int>("getAudioSessionId"));
                if (echoCanceller == null)
                    throw new InvalidOperationException("aec_create_failed");
                if (echoCanceller.Call<int>("setEnabled", true) != 0 ||
                    !echoCanceller.Call<bool>("getEnabled"))
                    throw new InvalidOperationException("aec_enable_failed");

                recorder.Call("startRecording");
                AecActive = true;
                Debug.Log("[Amiss] Android microphone AEC active source=VOICE_COMMUNICATION rate=" +
                    CaptureSampleRate);
                return true;
            }
            catch (Exception error)
            {
                AecActive = false;
                Debug.LogWarning("[Amiss] Android AEC microphone failed: " + error.GetType().Name +
                    ":" + error.Message);
                Cleanup();
                return false;
            }
        }
#endif

        void Cleanup()
        {
            AecActive = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (echoCanceller != null)
            {
                try
                {
                    echoCanceller.Call("release");
                }
                catch (Exception)
                {
                }
                echoCanceller.Dispose();
                echoCanceller = null;
            }
            if (recorder != null)
            {
                try
                {
                    recorder.Call("stop");
                }
                catch (Exception)
                {
                }
                try
                {
                    recorder.Call("release");
                }
                catch (Exception)
                {
                }
                recorder.Dispose();
                recorder = null;
            }
#endif
        }

        void OnDestroy()
        {
            recording = false;
            Cleanup();
            if (capabilityClip == null)
                return;
            if (Application.isPlaying)
                Destroy(capabilityClip);
            else
                DestroyImmediate(capabilityClip);
            capabilityClip = null;
        }
    }
}
