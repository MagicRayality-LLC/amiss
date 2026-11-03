using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Amiss.Shell.Evaluation
{
    /// <summary>
    /// One closed <c>ModelProvenance</c> record per admitted reply (10 section
    /// 4). It holds only metadata and hashes: no request or response body, no
    /// policy body, no credential, and no account or device identifier.
    /// </summary>
    public static class ModelProvenance
    {
        public static readonly IReadOnlyList<string> Fields = Array.AsReadOnly(new[]
        {
            "model", "endpoint_host", "request_id", "ts_start", "ts_end", "input_tokens",
            "output_tokens", "prompt_hash", "response_hash", "mode"
        });

        public static IReadOnlyDictionary<string, object> Build(
            IReadOnlyDictionary<string, object> request,
            IReadOnlyDictionary<string, object> response,
            string tsStart, string tsEnd, int? inputTokens, int? outputTokens, string mode)
        {
            if (mode != "live" && mode != "recorded")
                throw new EvaluationContractException("malformed_parameters",
                    "Provenance mode is not live or recorded.");
            var record = new Dictionary<string, object>
            {
                ["model"] = EvaluationContract.ModelId,
                ["endpoint_host"] = EvaluationContract.EndpointHost,
                ["request_id"] = request.TryGetValue("request_id", out var requestId)
                    ? requestId
                    : null,
                ["ts_start"] = tsStart,
                ["ts_end"] = tsEnd,
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens,
                ["prompt_hash"] = EvaluationRequestBuilder.RequestHash(request),
                ["response_hash"] = EvaluationRequestBuilder.ResponseHash(response),
                ["mode"] = mode
            };
            var errors = Errors(record);
            if (errors.Count > 0)
                throw new EvaluationContractException("malformed_parameters",
                    string.Join("; ", errors));
            return record;
        }

        /// <summary>The closed-field and timestamp checks of the provenance schema.</summary>
        public static IReadOnlyList<string> Errors(IReadOnlyDictionary<string, object> record)
        {
            var errors = new List<string>();
            foreach (var field in Fields)
                if (!record.ContainsKey(field))
                    errors.Add("the provenance record is missing " + field);
            foreach (var key in record.Keys)
                if (!Contains(Fields, key))
                    errors.Add("the provenance record carries an unknown field: " + key);

            if (record.TryGetValue("model", out var model) && !Equals(model, EvaluationContract.ModelId))
                errors.Add("the provenance model differs from the frozen evaluation model");
            if (record.TryGetValue("endpoint_host", out var host) &&
                !Equals(host, EvaluationContract.EndpointHost))
                errors.Add("the provenance endpoint host differs from the frozen evaluation host");
            if (record.TryGetValue("mode", out var mode) && mode is not ("live" or "recorded"))
                errors.Add("the provenance mode is not live or recorded");
            if (record.TryGetValue("request_id", out var requestId) && !(requestId is string id &&
                                                                        Identifiers.IsIdentifier(id)))
                errors.Add("the provenance request id is not a usable identifier");
            foreach (var field in new[] { "prompt_hash", "response_hash" })
            {
                if (record.TryGetValue(field, out var value) && !IsSha256(value))
                    errors.Add("the provenance " + field + " is not a lowercase SHA-256 digest");
            }
            foreach (var field in new[] { "input_tokens", "output_tokens" })
            {
                if (!record.TryGetValue(field, out var value))
                    continue;
                if (value != null && !IsNonNegativeInteger(value))
                    errors.Add("the provenance " + field + " is neither unreported nor non-negative");
            }
            if (record.TryGetValue("ts_start", out var start) &&
                record.TryGetValue("ts_end", out var end))
            {
                if (!TryParseUtc(start, out var startTime))
                    errors.Add("the provenance ts_start is not a UTC RFC 3339 timestamp");
                if (!TryParseUtc(end, out var endTime))
                    errors.Add("the provenance ts_end is not a UTC RFC 3339 timestamp");
                if (TryParseUtc(start, out var parsedStart) && TryParseUtc(end, out var parsedEnd) &&
                    parsedEnd < parsedStart)
                    errors.Add("the provenance ts_end precedes ts_start");
            }
            return errors;
        }

        public static bool IsSha256(object value) =>
            value is string text && text.Length == 64 &&
            System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9a-f]{64}$");

        /// <summary>An integer from either parser; the canonical reader uses longs.</summary>
        static bool IsNonNegativeInteger(object value) => value switch
        {
            int count => count >= 0,
            long count => count >= 0,
            _ => false
        };

        static bool TryParseUtc(object value, out DateTimeOffset parsed)
        {
            parsed = default;
            if (value is not string text || !text.EndsWith("Z", StringComparison.Ordinal))
                return false;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);
        }

        /// <summary>The one panel trace line: model, mode, and request id.</summary>
        public static string TraceLine(IReadOnlyDictionary<string, object> record)
        {
            if (record == null)
                return "Model: no admitted reply.";
            var model = Text(record, "model");
            var mode = Text(record, "mode");
            var requestId = Text(record, "request_id");
            var label = mode == "recorded" ? " RECORDED" : "";
            return "Model " + model + " · " + mode + label + " · request " + requestId;
        }

        static string Text(IReadOnlyDictionary<string, object> record, string field) =>
            record.TryGetValue(field, out var value) && value is string text ? text : "";

        static bool Contains(IReadOnlyList<string> values, string value)
        {
            for (var index = 0; index < values.Count; index++)
                if (string.Equals(values[index], value, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// A minimal, ordered JSON reader for the closed response shape. It rejects
    /// duplicate object keys and non-finite numbers, so a model reply cannot
    /// smuggle a second value for one field or an infinite number past the
    /// admission boundary. Newtonsoft is already a resolved transitive
    /// dependency, but the closed contract is simpler to prove with one small
    /// reader than with serializer options.
    /// </summary>
    public static class CanonicalJsonReader
    {
        public static object Parse(string text)
        {
            if (text == null)
                throw new EvaluationContractException("malformed_parameters",
                    "The response body is missing.");
            var reader = new Cursor(text);
            var value = reader.ReadValue(0);
            reader.SkipWhitespace();
            if (!reader.AtEnd)
                throw reader.Fail("trailing content after the JSON value");
            return value;
        }

        sealed class Cursor
        {
            readonly string text;
            int index;

            public Cursor(string text)
            {
                this.text = text;
            }

            public bool AtEnd => index >= text.Length;

            public EvaluationContractException Fail(string message) =>
                new("malformed_parameters", "malformed JSON at offset " +
                    index.ToString(CultureInfo.InvariantCulture) + ": " + message);

            public void SkipWhitespace()
            {
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                    index++;
            }

            public object ReadValue(int depth)
            {
                if (depth > 16)
                    throw Fail("nesting exceeds the declared bound");
                SkipWhitespace();
                if (AtEnd)
                    throw Fail("unexpected end of input");
                var character = text[index];
                switch (character)
                {
                    case '{':
                        return ReadObject(depth + 1);
                    case '[':
                        return ReadArray(depth + 1);
                    case '"':
                        return ReadString();
                    case 't':
                        Expect("true");
                        return true;
                    case 'f':
                        Expect("false");
                        return false;
                    case 'n':
                        Expect("null");
                        return null;
                    default:
                        return ReadNumber();
                }
            }

            Dictionary<string, object> ReadObject(int depth)
            {
                index++;
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipWhitespace();
                if (!AtEnd && text[index] == '}')
                {
                    index++;
                    return result;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || text[index] != '"')
                        throw Fail("an object key must be a string");
                    var key = ReadString();
                    if (result.ContainsKey(key))
                        throw Fail("duplicate object key: " + key);
                    SkipWhitespace();
                    if (AtEnd || text[index] != ':')
                        throw Fail("expected a colon after the key");
                    index++;
                    result[key] = ReadValue(depth);
                    SkipWhitespace();
                    if (AtEnd)
                        throw Fail("unterminated object");
                    if (text[index] == ',')
                    {
                        index++;
                        continue;
                    }
                    if (text[index] == '}')
                    {
                        index++;
                        return result;
                    }
                    throw Fail("expected a comma or the end of the object");
                }
            }

            List<object> ReadArray(int depth)
            {
                index++;
                var result = new List<object>();
                SkipWhitespace();
                if (!AtEnd && text[index] == ']')
                {
                    index++;
                    return result;
                }
                while (true)
                {
                    result.Add(ReadValue(depth));
                    SkipWhitespace();
                    if (AtEnd)
                        throw Fail("unterminated array");
                    if (text[index] == ',')
                    {
                        index++;
                        continue;
                    }
                    if (text[index] == ']')
                    {
                        index++;
                        return result;
                    }
                    throw Fail("expected a comma or the end of the array");
                }
            }

            string ReadString()
            {
                index++;
                var builder = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                        throw Fail("unterminated string");
                    var character = text[index++];
                    if (character == '"')
                        return builder.ToString();
                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }
                    if (AtEnd)
                        throw Fail("unterminated escape");
                    var escaped = text[index++];
                    switch (escaped)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (index + 4 > text.Length)
                                throw Fail("truncated unicode escape");
                            builder.Append((char)int.Parse(text.Substring(index, 4),
                                NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            index += 4;
                            break;
                        default:
                            throw Fail("unknown escape");
                    }
                }
            }

            object ReadNumber()
            {
                var start = index;
                if (!AtEnd && (text[index] == '-' || text[index] == '+'))
                    index++;
                while (!AtEnd && (char.IsDigit(text[index]) || text[index] == '.' ||
                                  text[index] == 'e' || text[index] == 'E' ||
                                  text[index] == '-' || text[index] == '+'))
                    index++;
                if (start == index)
                    throw Fail("unexpected character");
                var literal = text.Substring(start, index - start);
                if (literal.IndexOfAny(new[] { '.', 'e', 'E' }) < 0)
                {
                    if (!long.TryParse(literal, NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture, out var integer))
                        throw Fail("integer is out of range");
                    return integer;
                }
                if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var number) || double.IsNaN(number) || double.IsInfinity(number))
                    throw Fail("non-finite number");
                return number;
            }

            void Expect(string literal)
            {
                if (index + literal.Length > text.Length ||
                    string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                    throw Fail("expected " + literal);
                index += literal.Length;
            }
        }
    }
}
