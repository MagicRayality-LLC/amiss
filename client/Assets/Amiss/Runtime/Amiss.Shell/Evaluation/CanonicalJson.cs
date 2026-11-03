using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Amiss.Shell.Evaluation
{
    /// <summary>
    /// Canonical JSON encoding of 04 section 3.2: object keys in ordinal UTF-8
    /// byte order, no insignificant whitespace, strings escaped one way, and
    /// the shortest round-trip decimal form for every finite number.
    ///
    /// The device computes the same bytes as the headless oracle so a request
    /// hash, a response hash, and a validation fingerprint agree across the two
    /// languages. The shared vector file carries the exact expected bytes for
    /// both, and both suites fail if the encoders diverge.
    /// </summary>
    public static class CanonicalJson
    {
        public const string ToolProposalDomainSeparator = "amiss.tool-proposal\0";

        public static byte[] Bytes(object value) => Encoding.UTF8.GetBytes(Text(value));

        public static string Sha256Hex(object value) => Sha256Hex(Bytes(value));

        public static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(data);
            var builder = new StringBuilder(digest.Length * 2);
            foreach (var item in digest)
                builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        public static string ToolProposalFingerprint(object canonicalProposal) =>
            Sha256Hex(Concat(
                Encoding.UTF8.GetBytes(ToolProposalDomainSeparator),
                Bytes(canonicalProposal)));

        static byte[] Concat(byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }

        public static string Text(object value)
        {
            var builder = new StringBuilder();
            Write(builder, value);
            return builder.ToString();
        }

        static void Write(StringBuilder builder, object value)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    return;
                case bool flag:
                    builder.Append(flag ? "true" : "false");
                    return;
                case string text:
                    WriteString(builder, text);
                    return;
                case IDictionary<string, object> map:
                    WriteObject(builder, map);
                    return;
                case IReadOnlyDictionary<string, object> readOnlyMap:
                    WriteObject(builder, readOnlyMap.ToDictionary(pair => pair.Key,
                        pair => pair.Value));
                    return;
                case IEnumerable<object> list when value is not string:
                    WriteArray(builder, list);
                    return;
                case IList<object> list:
                    WriteArray(builder, list);
                    return;
                case long integer:
                    builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                    return;
                case int integer:
                    builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                    return;
                case short integer:
                    builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                    return;
                case byte integer:
                    builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                    return;
                case ulong unsigned:
                    builder.Append(unsigned.ToString(CultureInfo.InvariantCulture));
                    return;
                case double number:
                    builder.Append(Number(number));
                    return;
                case float number:
                    builder.Append(Number(number));
                    return;
                case decimal number:
                    builder.Append(Number((double)number));
                    return;
                default:
                    throw new ArgumentException("unsupported canonical JSON value: " +
                                                value.GetType().Name, nameof(value));
            }
        }

        static void WriteObject(StringBuilder builder, IDictionary<string, object> map)
        {
            builder.Append('{');
            var first = true;
            foreach (var key in map.Keys.OrderBy(item => item, Utf8ByteOrder.Instance))
            {
                if (!first)
                    builder.Append(',');
                first = false;
                WriteString(builder, key);
                builder.Append(':');
                Write(builder, map[key]);
            }
            builder.Append('}');
        }

        static void WriteArray(StringBuilder builder, IEnumerable<object> list)
        {
            builder.Append('[');
            var first = true;
            foreach (var item in list)
            {
                if (!first)
                    builder.Append(',');
                first = false;
                Write(builder, item);
            }
            builder.Append(']');
        }

        static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (var character in text)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < 0x20)
                            builder.Append("\\u").Append(((int)character).ToString("x4",
                                CultureInfo.InvariantCulture));
                        else
                            builder.Append(character);
                        break;
                }
            }
            builder.Append('"');
        }

        /// <summary>
        /// Shortest round-trip decimal for one finite double, byte-identical to
        /// CPython's <c>json.dumps</c>: shortest significant digits, fixed
        /// notation while <c>-4 &lt; decimal-point &lt;= 16</c>, otherwise
        /// lowercase-<c>e</c> scientific with a signed two-digit exponent.
        /// Integral doubles keep a <c>.0</c> suffix so they never read as ints.
        /// </summary>
        public static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("non-finite number", nameof(value));

            var negative = value < 0 || (value == 0 &&
                                         BitConverter.DoubleToInt64Bits(value) < 0);
            var magnitude = Math.Abs(value);
            string digits;
            int decpt;
            if (magnitude == 0.0)
            {
                digits = "0";
                decpt = 1;
            }
            else
            {
                SplitShortest(magnitude.ToString("R", CultureInfo.InvariantCulture),
                    out digits, out decpt);
            }

            var body = decpt <= -4 || decpt > 16
                ? Scientific(digits, decpt)
                : Fixed(digits, decpt);
            return negative ? "-" + body : body;
        }

        static void SplitShortest(string text, out string digits, out int decpt)
        {
            var exponent = 0;
            var exponentIndex = text.IndexOfAny(new[] { 'E', 'e' });
            var mantissa = text;
            if (exponentIndex >= 0)
            {
                mantissa = text.Substring(0, exponentIndex);
                exponent = int.Parse(text.Substring(exponentIndex + 1),
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }
            var point = mantissa.IndexOf('.');
            string whole;
            string fraction;
            if (point < 0)
            {
                whole = mantissa;
                fraction = "";
            }
            else
            {
                whole = mantissa.Substring(0, point);
                fraction = mantissa.Substring(point + 1);
            }
            var combined = whole + fraction;
            var trimmed = combined.TrimStart('0');
            var leadingZeros = combined.Length - trimmed.Length;
            digits = trimmed.TrimEnd('0');
            if (digits.Length == 0)
                digits = "0";
            decpt = whole.Length - leadingZeros + exponent;
        }

        static string Fixed(string digits, int decpt)
        {
            if (decpt <= 0)
                return "0." + new string('0', -decpt) + digits;
            if (decpt >= digits.Length)
                return digits + new string('0', decpt - digits.Length) + ".0";
            return digits.Substring(0, decpt) + "." + digits.Substring(decpt);
        }

        static string Scientific(string digits, int decpt)
        {
            var exponent = decpt - 1;
            var sign = exponent < 0 ? "-" : "+";
            var magnitude = Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
            var mantissa = digits.Length == 1
                ? digits
                : digits.Substring(0, 1) + "." + digits.Substring(1);
            return mantissa + "e" + sign + magnitude;
        }

        sealed class Utf8ByteOrder : IComparer<string>
        {
            public static readonly Utf8ByteOrder Instance = new();

            public int Compare(string left, string right)
            {
                if (ReferenceEquals(left, right))
                    return 0;
                if (left == null)
                    return -1;
                if (right == null)
                    return 1;
                var leftBytes = Encoding.UTF8.GetBytes(left);
                var rightBytes = Encoding.UTF8.GetBytes(right);
                var limit = Math.Min(leftBytes.Length, rightBytes.Length);
                for (var index = 0; index < limit; index++)
                {
                    if (leftBytes[index] != rightBytes[index])
                        return leftBytes[index] < rightBytes[index] ? -1 : 1;
                }
                return leftBytes.Length.CompareTo(rightBytes.Length);
            }
        }
    }
}
