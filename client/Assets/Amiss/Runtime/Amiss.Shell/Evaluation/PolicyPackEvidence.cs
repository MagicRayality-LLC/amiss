using System;
using System.IO;
using Amiss.Shell.Evaluation;
using UnityEngine;

namespace Amiss.Shell.Evaluation
{
    /// <summary>
    /// One bounded record of what this build actually loaded for the public
    /// instructor policy: versions, digests, the assembled byte count and
    /// hash, and whether the transport bound. It carries no policy body text
    /// and no credential, so it may be read off the device for evidence.
    /// </summary>
    [Serializable]
    public sealed class PolicyPackEvidence
    {
        public const string CurrentVersion = "1.0.0";

        public string evidence_version = CurrentVersion;
        public string application_version;
        public string unity_version;
        public string load_outcome;
        public string pack_id;
        public string policy_version;
        public string episode_id;
        public string pack_format_version;
        public string stage_data_format_version;
        public string pack_digest;
        public string stage_data_digest;
        public int stage_count;
        public int action_count;
        public int system_bytes;
        public string system_sha256;
        public bool transport_bound;
        public bool key_present;
        public string trace_line;
        public string reasons;

        public static PolicyPackEvidence Create(PolicyPack pack, string traceLine,
            bool keyPresent, bool transportBound)
        {
            var evidence = new PolicyPackEvidence
            {
                application_version = Application.version,
                unity_version = Application.unityVersion,
                load_outcome = pack != null && pack.Loaded ? "loaded" : "refused",
                pack_id = clean(pack?.PackId),
                policy_version = clean(pack?.PolicyVersion),
                episode_id = clean(pack?.EpisodeId),
                pack_format_version = clean(pack?.PackFormatVersion),
                stage_data_format_version = clean(pack?.StageDataFormatVersion),
                pack_digest = pack?.PackDigest ?? "",
                stage_data_digest = pack?.StageDataDigest ?? "",
                system_bytes = pack != null && pack.Loaded ? pack.SystemBytes : 0,
                system_sha256 = pack != null && pack.Loaded
                    ? PolicyPack.Digest(pack.System)
                    : "",
                transport_bound = transportBound,
                key_present = keyPresent,
                trace_line = clean(traceLine),
                reasons = pack == null || pack.Loaded
                    ? ""
                    : clean(string.Join("; ", pack.Errors))
            };
            evidence.stage_count = pack != null && pack.Loaded
                ? PolicyPack.List(PolicyPack.Map(pack.StageData, "stage_graph"), "nodes").Count
                : 0;
            evidence.action_count = pack != null && pack.Loaded
                ? PolicyPack.List(PolicyPack.Map(pack.StageData, "allowed_action_set"),
                    "action_ids").Count
                : 0;
            return evidence;
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        public string WriteAtomic(string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "policy-pack-evidence.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, ToJson());
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
            return path;
        }

        static string clean(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            var flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length <= 240 ? flat : flat.Substring(0, 240);
        }
    }
}
