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

        /// <summary>
        /// The speech-recognition source: the STT-tuned input with device
        /// voice processing. The voice-communication source previously used
        /// here delivered exactly zeroed frames outside telephony, so every
        /// request heard digital silence and ended in inactivity.
        /// </summary>
        const int VoiceRecognitionSource = 6;

        /// <summary>
        /// Echo cancellation stays off in this build. The platform stub
        /// effect attaches successfully but the captured stream is exactly
        /// zeroed frames on every source tried, so no speech reaches
        /// transcription while it is in the chain. Revisit only with a real
        /// effect and a nonzero first-frames level to prove it passes audio.
        /// </summary>
        const bool EnableEchoCancellation = false;
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
            // AudioRecord.read fills the array it is given, but
            // AndroidJavaObject.Call cannot marshal a mutated primitive array
            // back to C#: the read must go through raw JNI with an explicit
            // copy-back, or every frame stays zeroed and transcription hears
            // digital silence while reporting success.
            var recorderObject = recorder.GetRawObject();
            var readMethod = AndroidJNIHelper.GetMethodID(recorder.GetRawClass(),
                "read", "([SII)I");
            var javaPcm = AndroidJNI.NewShortArray(pcm.Length);
            var readArgs = new jvalue[4];
            readArgs[0].l = javaPcm;
            readArgs[1].i = 0;
            readArgs[2].i = pcm.Length;
            readArgs[3].i = ReadNonBlocking;
            // AudioBuffer forwards the whole array it is handed, so the
            // frames delivered here must already be at the 16 kHz
            // transmission rate: every third 48 kHz sample is kept.
            var step = CaptureSampleRate / TransmissionSampleRate;
            var samples = new float[chunkSamples / step + 1];
            var sequence = 0;
            var firstDeliveryLogged = false;
            var emptyReads = 0;
            var silentFrames = 0;
            while (recording)
            {
                var count = AndroidJNI.CallIntMethod(recorderObject, readMethod, readArgs);
                if (count > 0)
                {
                    if (count > pcm.Length)
                        count = pcm.Length;
                    Array.Copy(AndroidJNI.FromShortArray(javaPcm), pcm, count);
                    emptyReads = 0;
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
                    if (!firstDeliveryLogged)
                    {
                        firstDeliveryLogged = true;
                        Debug.Log("[Amiss] Android microphone first frames read=" + count +
                            " delivered=" + delivered + " level=" + levelMax);
                    }
                    if (levelMax <= 0f)
                    {
                        silentFrames++;
                        if (silentFrames == 300)
                            Debug.LogWarning("[Amiss] Android microphone delivers only " +
                                "digital silence; transcription hears nothing.");
                    }
                    else
                    {
                        silentFrames = 0;
                    }
                }
                else
                {
                    emptyReads++;
                    if (emptyReads == 300)
                        Debug.LogWarning("[Amiss] Android microphone read no frames " +
                            "in 300 consecutive reads; transcription hears silence.");
                }
                yield return null;
            }
            AndroidJNI.DeleteLocalRef(javaPcm);
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
                recorder = new AndroidJavaObject("android.media.AudioRecord", VoiceRecognitionSource,
                    CaptureSampleRate, MonoChannel, Pcm16Encoding, Math.Max(minimum, requestedBytes * 2));
                if (recorder.Call<int>("getState") != 1)
                    throw new InvalidOperationException("audio_record_uninitialized");

                // Echo cancellation is opportunistic, never a gate: a stub or
                // missing effect must not silence the microphone. Any AEC
                // failure falls back to the plain capture below, which still
                // delivers usable speech to transcription.
                if (EnableEchoCancellation)
                try
                {
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
                    AecActive = true;
                }
                catch (Exception aecError)
                {
                    Debug.LogWarning("[Amiss] Android AEC unavailable (" +
                        aecError.GetType().Name + ":" + aecError.Message +
                        "); continuing without echo cancellation.");
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
                    AecActive = false;
                }
                else
                {
                    AecActive = false;
                    Debug.Log("[Amiss] Android AEC disabled by build configuration; " +
                        "using the unprocessed capture.");
                }

                recorder.Call("startRecording");
                Debug.Log("[Amiss] Android microphone active source=VOICE_RECOGNITION rate=" +
                    CaptureSampleRate + " aec=" + AecActive);
                return true;
            }
            catch (Exception error)
            {
                AecActive = false;
                Debug.LogWarning("[Amiss] Android microphone failed: " + error.GetType().Name +
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
