using Amiss.Shell.Evaluation;
using UnityEngine;

namespace Amiss.Shell.Tutor
{
    /// <summary>
    /// The one seam that holds the person's hackathon evaluation key. A
    /// settings operation writes or removes the key without returning its
    /// value to the view model; <see cref="MaskedTail"/> is the only
    /// UI-facing form of the value, and <see cref="OpenHandle"/> gives the
    /// transport a scoped handle at the send boundary. No member logs,
    /// serializes, or renders the key itself.
    /// </summary>
    public interface IModelKeyStore
    {
        /// <summary>True while a non-empty key is stored.</summary>
        bool Present { get; }

        /// <summary>The masked tail for display, or empty when no key is stored.</summary>
        string MaskedTail { get; }

        /// <summary>Stores one key. An empty value is refused and stores nothing.</summary>
        bool Save(string key);

        /// <summary>Removes the stored key; safe when none is stored.</summary>
        void Clear();

        /// <summary>Opens a send-scoped handle over the stored value.</summary>
        ModelKeyHandle OpenHandle();
    }

    /// <summary>
    /// The device binding: the key lives in this application's private
    /// PlayerPrefs record, device-local, and is never written to a log, an
    /// evidence file, an exception message, or a request record. Founder
    /// disposition P4 selects this store for the evaluation build; approved
    /// design 08 section 3.3 prefers an encrypted device-backed secret handle,
    /// so this plain store is an evaluation-only exception to record.
    /// </summary>
    public sealed class PlayerPrefsModelKeyStore : IModelKeyStore
    {
        public const string DefaultPreferenceKey = "amiss.evaluation.meta_model_key";

        readonly string preferenceKey;

        public PlayerPrefsModelKeyStore(string preferenceKey = DefaultPreferenceKey)
        {
            this.preferenceKey = string.IsNullOrEmpty(preferenceKey)
                ? DefaultPreferenceKey
                : preferenceKey;
        }

        public bool Present => Stored().Length > 0;

        public string MaskedTail => Mask(Stored());

        public bool Save(string key)
        {
            if (key == null)
                return false;
            var trimmed = key.Trim();
            if (trimmed.Length == 0)
                return false;
            PlayerPrefs.SetString(preferenceKey, trimmed);
            PlayerPrefs.Save();
            return true;
        }

        public void Clear()
        {
            PlayerPrefs.DeleteKey(preferenceKey);
            PlayerPrefs.Save();
        }

        public ModelKeyHandle OpenHandle() => new(Stored());

        string Stored() => PlayerPrefs.GetString(preferenceKey, "");

        /// <summary>
        /// The only displayable form. A value of four characters or fewer
        /// shows no tail, so no short key is revealed by its mask.
        /// </summary>
        public static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "";
            return key.Length <= 4 ? "••••" : "••••" + key.Substring(key.Length - 4);
        }

        public override string ToString() =>
            "PlayerPrefsModelKeyStore(" + (Present ? "present" : "absent") + ")";
    }

    /// <summary>
    /// The PC-provisioning import format for evaluation/debug builds. The
    /// judge flow already installs over USB, so <c>tools/set-keys.sh</c>
    /// pushes one small <c>key=value</c> file and the hub saves the values
    /// through the same device stores as typed entry, then deletes the
    /// file. Parsing is fail-closed: any unknown key, duplicate, missing
    /// separator, empty value, whitespace inside a value, or oversized
    /// value refuses the whole import and yields no partial value. Nothing here logs or
    /// renders a value.
    /// </summary>
    public static class KeyImport
    {
        public const string FileName = "key-import.txt";
        public const int MaxFileBytes = 4096;
        public const int MaxValueChars = 512;

        public static bool TryParse(string raw, out string metaModelKey, out string witValue)
        {
            metaModelKey = "";
            witValue = "";
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            string meta = null;
            string wit = null;
            foreach (var line in raw.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                    continue;
                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                    return false;
                var name = trimmed.Substring(0, separator).Trim();
                var value = trimmed.Substring(separator + 1).Trim();
                if (value.Length == 0 || value.Length > MaxValueChars || !IsBareValue(value))
                    return false;
                switch (name)
                {
                    case "meta_model_key":
                        if (meta != null)
                            return false;
                        meta = value;
                        break;
                    case "wit_client_token":
                        if (wit != null)
                            return false;
                        wit = value;
                        break;
                    default:
                        return false;
                }
            }

            metaModelKey = meta ?? "";
            witValue = wit ?? "";
            return metaModelKey.Length > 0 || witValue.Length > 0;
        }

        static bool IsBareValue(string value)
        {
            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                    return false;
            }

            return true;
        }
    }
}
