using System.Globalization;
using Meta.WitAi.Data.Configuration;
using UnityEngine;

namespace Amiss.Shell.Voice
{
    /// <summary>
    /// The device-saved Wit client token. It is build/device configuration,
    /// never a committed value: the repository carries no token, and the field
    /// that writes this store is a password input whose value is never read
    /// back into a view. Design 06 section 1 fixes one configuration for both
    /// Wit adapters.
    /// </summary>
    public sealed class WitTokenStore
    {
        public const string DefaultPreferenceKey = "amiss.voice.wit_client_token";

        readonly string preferenceKey;

        public WitTokenStore(string preferenceKey = DefaultPreferenceKey)
        {
            this.preferenceKey = string.IsNullOrEmpty(preferenceKey)
                ? DefaultPreferenceKey
                : preferenceKey;
        }

        public bool Present => Stored().Length > 0;

        public bool Save(string token)
        {
            var trimmed = (token ?? "").Trim();
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

        /// <summary>The adapter-only read. No view consumes this value.</summary>
        internal string Read() => Stored();

        string Stored() => PlayerPrefs.GetString(preferenceKey, "");
    }

    /// <summary>
    /// The one Wit configuration both adapters share. It is created in memory
    /// with empty fields and filled from the device store at the send
    /// boundary, so an unconfigured build carries no provider identity and
    /// fails closed. The SDK's own config asset is used; no value is committed.
    /// </summary>
    public sealed class WitVoiceSetup
    {
        readonly WitTokenStore tokens;
        WitConfiguration configuration;

        public WitVoiceSetup(WitTokenStore tokens)
        {
            this.tokens = tokens ?? new WitTokenStore();
        }

        public WitTokenStore Tokens => tokens;

        /// <summary>True only while a device-held client token exists.</summary>
        public bool Configured => tokens.Present;

        public WitConfiguration Configuration
        {
            get
            {
                if (configuration != null)
                    return configuration;
                configuration = ScriptableObject.CreateInstance<WitConfiguration>();
                configuration.name = "AmissWitConfiguration";
                var saved = tokens.Read();
                if (!string.IsNullOrEmpty(saved))
                    configuration.SetClientAccessToken(saved);
                return configuration;
            }
        }

        /// <summary>Drops the in-memory configuration after the token changes.</summary>
        public void Reset() => configuration = null;
    }

    /// <summary>
    /// The bounded local consent record of 06 section 4.1: version, accepted
    /// time, and revoked time. It never holds audio or a transcript.
    /// </summary>
    public sealed class PlayerPrefsVoiceConsentStore
    {
        public const string VersionKey = "amiss.voice.consent.version";
        public const string AcceptedKey = "amiss.voice.consent.accepted_at";
        public const string RevokedKey = "amiss.voice.consent.revoked_at";

        public VoiceConsentStatus Load()
        {
            var version = PlayerPrefs.GetString(VersionKey, "");
            if (version != VoiceController.ConsentVersion)
                return VoiceConsentStatus.Absent;
            var revoked = ReadTime(RevokedKey);
            var accepted = ReadTime(AcceptedKey);
            if (revoked.HasValue && (!accepted.HasValue || revoked.Value >= accepted.Value))
                return VoiceConsentStatus.Revoked;
            return accepted.HasValue ? VoiceConsentStatus.Accepted : VoiceConsentStatus.Absent;
        }

        public void Save(VoiceConsentStatus status, double now)
        {
            PlayerPrefs.SetString(VersionKey, VoiceController.ConsentVersion);
            var stamp = now.ToString("R", CultureInfo.InvariantCulture);
            if (status == VoiceConsentStatus.Accepted)
                PlayerPrefs.SetString(AcceptedKey, stamp);
            if (status == VoiceConsentStatus.Revoked)
                PlayerPrefs.SetString(RevokedKey, stamp);
            PlayerPrefs.Save();
        }

        static double? ReadTime(string key)
        {
            var text = PlayerPrefs.GetString(key, "");
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var value)
                ? value
                : null;
        }
    }
}
