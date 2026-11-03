using System;
using System.Collections.Generic;
using System.Linq;

namespace Amiss.Shell.Evaluation
{
    /// <summary>
    /// Converts between the canonical evaluation contracts and Meta's
    /// OpenAI-compatible chat-completions wire format.
    /// </summary>
    public static class MetaChatCompletionCodec
    {
        public static byte[] RequestBytes(IReadOnlyDictionary<string, object> request)
        {
            if (request == null ||
                !request.TryGetValue("policy_envelope", out var rawPolicy) ||
                rawPolicy is not IReadOnlyDictionary<string, object> policy ||
                policy.GetValueOrDefault("system") is not string system ||
                string.IsNullOrWhiteSpace(system) ||
                request.GetValueOrDefault("model") is not string model ||
                model.Length == 0)
                throw new SenderRefusedException("malformed_request");

            var content = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in request)
                if (pair.Key != "policy_envelope")
                    content[pair.Key] = pair.Value;

            var wire = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] = system
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = CanonicalJson.Text(content)
                    }
                },
                ["response_format"] = new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new Dictionary<string, object>
                    {
                        ["name"] = "amiss_tutor_response",
                        ["schema"] = ResponseSchema()
                    }
                }
            };
            return CanonicalJson.Bytes(wire);
        }

        public static IReadOnlyDictionary<string, object> Response(
            IReadOnlyDictionary<string, object> completion)
        {
            if (completion == null ||
                completion.GetValueOrDefault("choices") is not IEnumerable<object> choices ||
                choices.FirstOrDefault() is not IReadOnlyDictionary<string, object> choice ||
                choice.GetValueOrDefault("message") is not IReadOnlyDictionary<string, object> message ||
                message.GetValueOrDefault("content") is not string content ||
                string.IsNullOrWhiteSpace(content))
                throw new SenderRefusedException("malformed_response");

            try
            {
                if (CanonicalJsonReader.Parse(content) is IReadOnlyDictionary<string, object> response)
                    return response;
            }
            catch (Exception error)
            {
                throw new SenderRefusedException("malformed_response: " + error.GetType().Name);
            }
            throw new SenderRefusedException("malformed_response");
        }

        public static (int? input, int? output) UsageTokens(
            IReadOnlyDictionary<string, object> completion)
        {
            if (completion == null ||
                completion.GetValueOrDefault("usage") is not IReadOnlyDictionary<string, object> usage)
                return (null, null);
            return (TokenCount(usage.GetValueOrDefault("prompt_tokens")),
                TokenCount(usage.GetValueOrDefault("completion_tokens")));
        }

        static int? TokenCount(object value) => value switch
        {
            int count when count >= 0 => count,
            long count when count >= 0 && count <= int.MaxValue => (int)count,
            _ => null
        };

        static IReadOnlyDictionary<string, object> ResponseSchema() => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new List<object>
            {
                "response_contract_version", "request_id", "refs", "speech_units", "tool_proposals"
            },
            ["properties"] = new Dictionary<string, object>
            {
                ["response_contract_version"] = new Dictionary<string, object> { ["type"] = "string" },
                ["request_id"] = new Dictionary<string, object> { ["type"] = "string" },
                ["refs"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = 4,
                    ["items"] = TrialRefSchema()
                },
                ["speech_units"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["maxItems"] = 2,
                    ["items"] = SpeechUnitSchema()
                },
                ["tool_proposals"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["maxItems"] = 32,
                    ["items"] = new Dictionary<string, object> { ["type"] = "object" }
                }
            }
        };

        static IReadOnlyDictionary<string, object> SpeechUnitSchema() => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new List<object> { "text", "citations" },
            ["properties"] = new Dictionary<string, object>
            {
                ["text"] = new Dictionary<string, object> { ["type"] = "string" },
                ["citations"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new List<object> { "measurement_id", "trial_ref" },
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["measurement_id"] = new Dictionary<string, object> { ["type"] = "string" },
                            ["trial_ref"] = TrialRefSchema(),
                            ["display_tuple_id"] = new Dictionary<string, object> { ["type"] = "string" }
                        }
                    }
                }
            }
        };

        static IReadOnlyDictionary<string, object> TrialRefSchema() => new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new List<object> { "trial_id", "trial_revision" },
            ["properties"] = new Dictionary<string, object>
            {
                ["trial_id"] = new Dictionary<string, object> { ["type"] = "string" },
                ["trial_revision"] = new Dictionary<string, object> { ["type"] = "integer" }
            }
        };
    }
}
