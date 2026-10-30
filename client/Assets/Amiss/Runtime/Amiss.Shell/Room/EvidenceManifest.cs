using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Amiss.Shell.Room
{
    [Serializable]
    public sealed class EvidenceEntry
    {
        public string scenario_id;
        public string outcome;
        public string source;
        public string boundary_label;
        public string collision_checksum;
        public int surface_count;
        public float effective_volume_m3;
    }

    [Serializable]
    public sealed class EvidenceManifest
    {
        public const string CurrentVersion = "1.0.0";
        public string evidence_manifest_version = CurrentVersion;
        public string application_version;
        public string unity_version;
        public string framework_api_version;
        public string collision_format_version;
        public List<EvidenceEntry> entries = new();

        public static EvidenceManifest Create() => new()
        {
            application_version = Application.version,
            unity_version = Application.unityVersion,
            framework_api_version = Contracts.FrameworkApiVersions.FrameworkApi,
            collision_format_version = CollisionRepresentation.CurrentFormatVersion
        };

        public void Record(string scenarioId, string outcome, string source,
            RoomPreparation preparation = null)
        {
            entries.Add(new EvidenceEntry
            {
                scenario_id = Clean(scenarioId),
                outcome = Clean(outcome),
                source = Clean(source),
                boundary_label = Clean(preparation?.Draft?.BoundaryLabel ?? ""),
                collision_checksum = preparation?.Collision?.Checksum ?? "",
                surface_count = preparation?.Collision?.Surfaces.Count ?? 0,
                effective_volume_m3 = preparation?.Collision?.EffectiveVolumeM3 ?? 0f
            });
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        public string WriteAtomic(string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "room-evidence.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, ToJson());
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
            return path;
        }

        static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return clean.Length <= 160 ? clean : clean.Substring(0, 160);
        }
    }
}
