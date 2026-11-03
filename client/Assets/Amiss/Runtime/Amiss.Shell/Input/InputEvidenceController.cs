#if DEVELOPMENT_BUILD
using System;
using System.Globalization;
using UnityEngine;

namespace Amiss.Shell.Input
{
    public sealed class InputEvidenceController : MonoBehaviour
    {
        OvrToolInputAdapter adapter;
        InputEvidenceManifest manifest;
        InputEvidenceAccumulator current;
        string scenario = "unlabelled";
        string lastCommand = "";

        public string EvidencePath { get; private set; } = "";
        public bool EvidenceWriteFailed { get; private set; }

        void Awake()
        {
            adapter = GetComponent<OvrToolInputAdapter>();
            manifest = InputEvidenceManifest.Create();
            current = new InputEvidenceAccumulator();
            current.Begin(Time.unscaledTime);
            adapter.SampleProcessed += Observe;
        }

        void OnDestroy()
        {
            if (adapter != null)
                adapter.SampleProcessed -= Observe;
        }

        void Update()
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = activity.Call<AndroidJavaObject>("getIntent");
            var raw = intent.Call<string>("getStringExtra", "amiss_input_command");
            if (string.IsNullOrEmpty(raw) || raw == lastCommand)
                return;
            lastCommand = raw;
            EvidenceCommand(raw);
        }

        public void EvidenceCommand(string raw)
        {
            var parts = raw.Split('#');
            switch (parts[0])
            {
                case "begin":
                    scenario = parts.Length > 1 ? parts[1] : "unlabelled";
                    current = new InputEvidenceAccumulator();
                    current.Begin(Time.unscaledTime);
                    break;
                case "finish":
                    Finish(parts.Length > 1 ? parts[1] : "observed");
                    break;
                case "state":
                    Write();
                    break;
                case "profile":
                    ApplyProfile(parts);
                    break;
            }
        }

        void Observe(RawInputSample sample, InputNormalizationResult result)
        {
            current.Observe(sample, result, Time.unscaledTime);
        }

        void Finish(string outcome)
        {
            manifest.entries.Add(current.Complete(scenario, outcome, "quest_diagnostic",
                adapter.Profile, Time.unscaledTime));
            Write();
        }

        void ApplyProfile(string[] parts)
        {
            if (parts.Length != 11)
            {
                Finish("profile_refused");
                return;
            }
            try
            {
                adapter.SetProfile(new InputLimitProfile(parts[1], true,
                    Integer(parts[2]), Integer(parts[3]), Number(parts[4]), Number(parts[5]),
                    Number(parts[6]), Number(parts[7]), Number(parts[8]), Integer(parts[9]),
                    Integer(parts[10])));
            }
            catch (Exception error) when (error is ArgumentException ||
                                           error is FormatException ||
                                           error is OverflowException)
            {
                Finish("profile_refused");
            }
        }

        void Write()
        {
            try
            {
                EvidencePath = manifest.WriteAtomic(Application.persistentDataPath);
            }
            catch (Exception)
            {
                EvidenceWriteFailed = true;
            }
        }

        static int Integer(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        static float Number(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
#endif
