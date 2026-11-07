using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Margo.Cloud.Core;

/// <summary>
/// JSON read and written the way Python's <c>json.loads</c> and <c>json.dumps</c> do it, for data that
/// Python and C# services exchange: the route table blob and provisioning queue messages.
/// </summary>
/// <remarks>
/// <para><see cref="Dumps"/> reproduces <c>json.dumps</c> byte for byte for the values
/// <see cref="Loads"/> and this library produce: <c>sort_keys</c> by code point, <c>indent</c>,
/// default separators, <c>ensure_ascii</c> escaping with lower-case hex, Python <c>int</c> text and
/// <c>float.__repr__</c> (<c>1e+16</c>, <c>1e-05</c>, <c>100.0</c>, <c>-0.0</c>, <c>Infinity</c>).</para>
/// <para><see cref="Loads"/> keeps Python's typing (a number with a fraction or exponent is a float,
/// otherwise an int of any size) and resolves a repeated key as Python does: the last value wins at
/// the key's first position. It rejects what <c>System.Text.Json</c> cannot represent and Python
/// would accept: the literals <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c>, escaped lone
/// surrogates, and nesting deeper than <see cref="MaxDepth"/>. Python never writes these for the data
/// above.</para>
/// </remarks>
public static class PythonJson
{
    /// <summary>The deepest nesting <see cref="Loads"/> accepts.</summary>
    public const int MaxDepth = 1000;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = MaxDepth };

    /// <summary>Parse JSON text as Python's <c>json.loads</c> would; <see langword="null"/> is JSON null.</summary>
    /// <exception cref="JsonException">The text is not JSON this reader accepts.</exception>
    public static JsonNode? Loads(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using var document = JsonDocument.Parse(text, DocumentOptions);
            return Convert(document.RootElement);
        }
        catch (InvalidOperationException exc)
        {
            // System.Text.Json refuses to materialise a string with an escaped lone surrogate.
            throw new JsonException("JSON text holds a string that is not valid UTF-16", exc);
        }
    }

    /// <summary>Serialise as Python's <c>json.dumps(value, sort_keys=..., indent=..., ensure_ascii=...)</c>.</summary>
    public static string Dumps(JsonNode? value, bool sortKeys = false, int? indent = null, bool ensureAscii = true)
    {
        var builder = new StringBuilder();
        new Writer(builder, sortKeys, indent is null ? null : new string(' ', indent.Value), ensureAscii).Write(value, 0);
        return builder.ToString();
    }

    /// <summary>Python's <c>float.__repr__</c> as <c>json</c> writes it.</summary>
    public static string FormatFloat(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        var sign = double.IsNegative(value) ? "-" : string.Empty;
        if (value == 0)
        {
            return sign + "0.0";
        }

        // .NET's round-trip text has the same shortest digits as Python's repr; only the layout differs.
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = text.IndexOfAny(['E', 'e']);
        var mantissa = exponentAt < 0 ? text : text[..exponentAt];
        var exponent = exponentAt < 0 ? 0 : int.Parse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = point < 0 ? mantissa : string.Concat(mantissa.AsSpan(0, point), mantissa.AsSpan(point + 1));
        var decimalPoint = (point < 0 ? mantissa.Length : point) + exponent;
        var leadingZeros = 0;
        while (leadingZeros < digits.Length - 1 && digits[leadingZeros] == '0')
        {
            leadingZeros++;
        }

        digits = digits[leadingZeros..].TrimEnd('0');
        decimalPoint -= leadingZeros;
        if (decimalPoint <= -4 || decimalPoint > 16)
        {
            var exp = decimalPoint - 1;
            var body = digits.Length == 1 ? digits : digits[..1] + "." + digits[1..];
            return sign + body + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }

        if (decimalPoint <= 0)
        {
            return sign + "0." + new string('0', -decimalPoint) + digits;
        }

        if (decimalPoint >= digits.Length)
        {
            return sign + digits + new string('0', decimalPoint - digits.Length) + ".0";
        }

        return sign + digits[..decimalPoint] + "." + digits[decimalPoint..];
    }

    private static JsonNode? Convert(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    obj[property.Name] = Convert(property.Value); // a repeated key: last value, first position
                }

                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(Convert(item));
                }

                return array;
            case JsonValueKind.String:
                return JsonValue.Create(element.GetString()!);
            case JsonValueKind.Number:
                return JsonValue.Create(element.Clone()); // keeps the number's text: int or float, any size
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            default:
                return null;
        }
    }

    private sealed class Writer(StringBuilder output, bool sortKeys, string? indent, bool ensureAscii)
    {
        private const string Hex = "0123456789abcdef";

        public void Write(JsonNode? node, int level)
        {
            switch (node)
            {
                case null:
                    output.Append("null");
                    break;
                case JsonObject obj:
                    WriteObject(obj, level);
                    break;
                case JsonArray array:
                    WriteArray(array, level);
                    break;
                default:
                    WriteValue((JsonValue)node);
                    break;
            }
        }

        private void WriteObject(JsonObject obj, int level)
        {
            if (obj.Count == 0)
            {
                output.Append("{}");
                return;
            }

            IEnumerable<KeyValuePair<string, JsonNode?>> items = obj;
            if (sortKeys)
            {
                var sorted = obj.ToList();
                sorted.Sort((a, b) => PythonText.CompareCodePoints(a.Key, b.Key));
                items = sorted;
            }

            output.Append('{');
            var separator = Open(level + 1);
            var first = true;
            foreach (var (key, value) in items)
            {
                if (!first)
                {
                    output.Append(separator);
                }

                first = false;
                WriteString(key);
                output.Append(": ");
                Write(value, level + 1);
            }

            Close(level);
            output.Append('}');
        }

        private void WriteArray(JsonArray array, int level)
        {
            if (array.Count == 0)
            {
                output.Append("[]");
                return;
            }

            output.Append('[');
            var separator = Open(level + 1);
            for (var i = 0; i < array.Count; i++)
            {
                if (i > 0)
                {
                    output.Append(separator);
                }

                Write(array[i], level + 1);
            }

            Close(level);
            output.Append(']');
        }

        private string Open(int level)
        {
            if (indent is null)
            {
                return ", ";
            }

            var newline = "\n" + string.Concat(Enumerable.Repeat(indent, level));
            output.Append(newline);
            return "," + newline;
        }

        private void Close(int level)
        {
            if (indent is not null)
            {
                output.Append('\n').Append(string.Concat(Enumerable.Repeat(indent, level)));
            }
        }

        private void WriteValue(JsonValue value)
        {
            switch (value.GetValueKind())
            {
                case JsonValueKind.String:
                    WriteString(value.GetValue<string>());
                    break;
                case JsonValueKind.True:
                    output.Append("true");
                    break;
                case JsonValueKind.False:
                    output.Append("false");
                    break;
                case JsonValueKind.Number:
                    output.Append(PythonValues.TryNumber(value, out var integer, out var real)
                        ? integer.ToString(CultureInfo.InvariantCulture)
                        : FormatFloat(real));
                    break;
                case JsonValueKind.Null:
                    output.Append("null");
                    break;
                default:
                    throw new InvalidOperationException("a JSON value node must hold a string, number, boolean or null");
            }
        }

        private void WriteString(string text)
        {
            output.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"':
                        output.Append("\\\"");
                        break;
                    case '\\':
                        output.Append("\\\\");
                        break;
                    case '\n':
                        output.Append("\\n");
                        break;
                    case '\r':
                        output.Append("\\r");
                        break;
                    case '\t':
                        output.Append("\\t");
                        break;
                    case '\b':
                        output.Append("\\b");
                        break;
                    case '\f':
                        output.Append("\\f");
                        break;
                    default:
                        if (c < ' ' || (ensureAscii && c > '~'))
                        {
                            output.Append("\\u")
                                .Append(Hex[(c >> 12) & 0xf]).Append(Hex[(c >> 8) & 0xf])
                                .Append(Hex[(c >> 4) & 0xf]).Append(Hex[c & 0xf]);
                        }
                        else
                        {
                            output.Append(c);
                        }

                        break;
                }
            }

            output.Append('"');
        }
    }
}
