using System;
using System.Collections.Generic;
using Amiss.Contracts;

namespace Amiss.Shell.Evaluation
{
    /// <summary>
    /// Builds the one canonical evaluation request every judge path shares:
    /// the committed Context Payload plus the policy envelope assembled here at
    /// the send boundary. The payload is never given a policy field, and the
    /// policy body never leaves this object.
    /// </summary>
    public static class EvaluationRequestBuilder
    {
        public const int MaxRefs = 4;

        public static IReadOnlyDictionary<string, object> Build(
            IReadOnlyDictionary<string, object> contextPayload, PolicyEnvelope policy,
            string requestId, string turnId, string origin)
        {
            if (policy == null)
                throw new EvaluationContractException("no_policy",
                    "No policy envelope was supplied, so no model request is built.");
            if (contextPayload == null)
                throw new EvaluationContractException("malformed_parameters",
                    "The context payload is missing.");
            AssertNoPolicyInPayload(contextPayload);
            var errors = ContextPayloadErrors(contextPayload);
            if (errors.Count > 0)
                throw new EvaluationContractException("malformed_parameters",
                    string.Join("; ", errors));
            if (!Identifiers.IsIdentifier(requestId))
                throw new EvaluationContractException("malformed_parameters",
                    "The request id is not a usable identifier.");
            if (turnId != null && !Identifiers.IsIdentifier(turnId))
                throw new EvaluationContractException("malformed_parameters",
                    "The turn id is not a usable identifier.");
            if (!Contains(EvaluationContract.Origins, origin))
                throw new EvaluationContractException("malformed_parameters",
                    "The request origin is not declared.");

            var refs = new List<object>();
            if (contextPayload.TryGetValue("refs", out var rawRefs) && rawRefs is IEnumerable<object> list)
                refs.AddRange(list);

            return new Dictionary<string, object>
            {
                ["request_contract_version"] = EvaluationContract.RequestContractVersion,
                ["turn_id"] = turnId,
                ["request_id"] = requestId,
                ["origin"] = origin,
                ["model"] = EvaluationContract.ModelId,
                ["refs"] = refs,
                ["context_payload"] = contextPayload,
                ["policy_envelope"] = policy.ToRecord()
            };
        }

        public static byte[] RequestBytes(IReadOnlyDictionary<string, object> request) =>
            CanonicalJson.Bytes(request);

        public static string RequestHash(IReadOnlyDictionary<string, object> request) =>
            CanonicalJson.Sha256Hex(request);

        public static string ResponseHash(IReadOnlyDictionary<string, object> response) =>
            CanonicalJson.Sha256Hex(response);

        /// <summary>
        /// A log-safe view: the policy body is replaced by its version and hash
        /// so an operational record never carries the instructor supply.
        /// </summary>
        public static IReadOnlyDictionary<string, object> RedactForLog(
            IReadOnlyDictionary<string, object> request)
        {
            var record = new Dictionary<string, object>();
            foreach (var pair in request)
            {
                if (pair.Key == "policy_envelope")
                    continue;
                record[pair.Key] = pair.Value;
            }
            var envelope = request.TryGetValue("policy_envelope", out var raw) &&
                           raw is IReadOnlyDictionary<string, object> map
                ? map
                : new Dictionary<string, object>();
            record["policy"] = new Dictionary<string, object>
            {
                ["policy_version"] = envelope.TryGetValue("policy_version", out var version)
                    ? version
                    : null,
                ["policy_hash"] = CanonicalJson.Sha256Hex(envelope)
            };
            return record;
        }

        /// <summary>The payload may not carry policy, capability, or script fields.</summary>
        public static void AssertNoPolicyInPayload(IReadOnlyDictionary<string, object> payload)
        {
            foreach (var key in payload.Keys)
            {
                var lower = key.ToLowerInvariant();
                if (lower is "policy" or "system" or "policy_envelope" or "script" or "source" or
                    "expression" or "code" or "shell" or "command" or "url" or "method" or "headers")
                    throw new EvaluationContractException("malformed_parameters",
                        "The context payload may not carry policy, capability, or script fields.");
            }
        }

        /// <summary>The closed Context Payload shape of 04 section 6.</summary>
        public static IReadOnlyList<string> ContextPayloadErrors(
            IReadOnlyDictionary<string, object> payload)
        {
            var errors = new List<string>();
            var required = new[]
            {
                "context_payload_version", "comparison_package", "refs", "policy_tokens",
                "compressed_session_context", "current_user_utterance"
            };
            foreach (var field in required)
                if (!payload.ContainsKey(field))
                    errors.Add("the context payload is missing " + field);
            foreach (var key in payload.Keys)
                if (!Contains(required, key))
                    errors.Add("the context payload carries an unknown field: " + key);

            if (payload.TryGetValue("context_payload_version", out var version) &&
                version is string versionText &&
                !Identifiers.IsSameMajor(versionText, FrameworkApiVersions.ContextPayload))
                errors.Add("the context payload contract major is unsupported");

            if (payload.TryGetValue("refs", out var rawRefs) && rawRefs is IEnumerable<object> refs)
            {
                var list = new List<object>(refs);
                if (list.Count < 1 || list.Count > MaxRefs)
                    errors.Add("the context payload carries an unsupported comparison width");
            }
            else
            {
                errors.Add("the context payload has no refs array");
            }
            return errors;
        }

        static bool Contains(IReadOnlyList<string> values, string value)
        {
            for (var index = 0; index < values.Count; index++)
                if (string.Equals(values[index], value, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }

    /// <summary>A request or transport contract failure that carries a reason code.</summary>
    public sealed class EvaluationContractException : Exception
    {
        public EvaluationContractException(string reasonCode, string message) : base(message)
        {
            ReasonCode = reasonCode ?? "";
        }

        public string ReasonCode { get; }
    }

    /// <summary>The declared identifier syntax shared by every contract record.</summary>
    public static class Identifiers
    {
        public static bool IsIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
                return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                var first = index == 0;
                var alphanumeric = character >= 'A' && character <= 'Z' ||
                                   character >= 'a' && character <= 'z' ||
                                   character >= '0' && character <= '9';
                if (first && !alphanumeric)
                    return false;
                if (!alphanumeric && character != '.' && character != '_' && character != ':' &&
                    character != '-')
                    return false;
            }
            return true;
        }

        public static bool IsSameMajor(string version, string supported)
        {
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(supported))
                return false;
            var versionMajor = version.Split('.')[0];
            var supportedMajor = supported.Split('.')[0];
            return versionMajor == supportedMajor;
        }
    }
}
