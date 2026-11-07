using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Margo.Cloud.WorkIQ;

/// <summary>
/// The Python value rules <c>transport.py</c> depends on: <c>json.loads</c>, truthiness,
/// <c>isinstance(x, int)</c>, <c>==</c> against an int, and <c>str.strip</c>/<c>str.lstrip</c>.
/// </summary>
/// <remarks>
/// <see cref="Loads"/> builds the tree iteratively from <see cref="Utf8JsonReader"/> so that, like
/// Python, duplicate object keys keep the last value at the first key's position, a leading BOM is
/// refused, lone UTF-16 surrogates (escaped, or as characters inside a string) survive, an integer literal
/// longer than Python's 4300-digit limit is refused, and errors surface in document order.
/// <c>System.Text.Json</c> itself throws on duplicate keys and on lone-surrogate strings.
/// </remarks>
internal static class PythonJson
{
    /// <summary>
    /// The deepest container nesting accepted. Python's limit depends on the interpreter (about 10 000 on
    /// 3.13) and fails with <c>RecursionError</c>, which is not a <c>ValueError</c>; this port uses 1 000,
    /// the default depth of <see cref="Utf8JsonWriter"/>, so every parsed tree can be written again.
    /// </summary>
    internal const int MaxNesting = 1000;

    /// <summary>
    /// Python's default <c>sys.get_int_max_str_digits()</c> (3.11 and later): <c>json.loads</c> raises
    /// <c>ValueError</c> for an integer literal with more digits (the sign is not counted). Floats have no limit.
    /// </summary>
    internal const int MaxIntegerDigits = 4300;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Python <c>json.loads(text)</c>.</summary>
    /// <exception cref="JsonException">The text is not JSON (Python <c>ValueError</c>).</exception>
    /// <exception cref="TransportFaultException">Nesting exceeds <see cref="MaxNesting"/> (Python <c>RecursionError</c>).</exception>
    internal static JsonNode? Loads(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            throw new JsonException("Unexpected UTF-8 BOM (decode using utf-8-sig)");
        }

        var utf8 = ToUtf8(text);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            MaxDepth = MaxNesting + 8,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        var containers = new Stack<JsonNode>();
        JsonNode? root = null;
        string? pendingName = null;

        void Attach(JsonNode? node)
        {
            if (containers.Count == 0)
            {
                root = node;
            }
            else if (containers.Peek() is JsonArray array)
            {
                array.Add(node);
            }
            else
            {
                // Python dicts: a repeated key keeps its first position and takes the last value.
                ((JsonObject)containers.Peek())[pendingName!] = node;
                pendingName = null;
            }
        }

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    if (containers.Count >= MaxNesting)
                    {
                        throw new TransportFaultException("JSON nesting exceeds the parser's limit");
                    }

                    JsonNode container = reader.TokenType == JsonTokenType.StartObject ? new JsonObject() : new JsonArray();
                    Attach(container);
                    containers.Push(container);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    containers.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    pendingName = ReadString(ref reader);
                    break;
                case JsonTokenType.String:
                    Attach(JsonValue.Create(ReadString(ref reader)));
                    break;
                case JsonTokenType.Number:
                    var literal = reader.ValueSpan;
                    if (literal.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0
                        && literal.Length - (literal[0] == (byte)'-' ? 1 : 0) > MaxIntegerDigits)
                    {
                        // Python's int() refuses it (ValueError); it also keeps BigInteger parsing bounded.
                        throw new JsonException("Exceeds the limit (4300 digits) for integer string conversion");
                    }

                    Attach(JsonNode.Parse(literal));
                    break;
                case JsonTokenType.True:
                    Attach(JsonValue.Create(true));
                    break;
                case JsonTokenType.False:
                    Attach(JsonValue.Create(false));
                    break;
                case JsonTokenType.Null:
                    Attach(null);
                    break;
                default:
                    throw new JsonException("unexpected JSON token " + reader.TokenType);
            }
        }

        return root;
    }

    /// <summary>Python truthiness of a JSON value (<c>None</c>, <c>False</c>, <c>0</c>, <c>""</c>, <c>[]</c> and <c>{}</c> are false).</summary>
    internal static bool IsTruthy(JsonNode? node) => node switch
    {
        null => false,
        JsonObject obj => obj.Count > 0,
        JsonArray array => array.Count > 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => NumberIsNonZero(NumberText(value)),
            _ => true,
        },
        _ => true,
    };

    /// <summary>
    /// Python <c>isinstance(x, int)</c> for a parsed JSON value: an integer literal, or a boolean
    /// (<c>bool</c> is a subclass of <c>int</c>; <c>True == 1</c>).
    /// </summary>
    internal static bool TryGetInteger(JsonNode? node, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (node is not JsonValue json)
        {
            return false;
        }

        switch (json.GetValueKind())
        {
            case JsonValueKind.True:
                value = BigInteger.One;
                return true;
            case JsonValueKind.False:
                return true;
            case JsonValueKind.Number:
                var text = NumberText(json);
                if (!IsIntegerLiteral(text))
                {
                    return false;
                }

                value = BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Python <c>node == number</c> for an int on the right (floats and booleans compare by value).</summary>
    internal static bool EqualsInteger(JsonNode? node, long number)
    {
        if (TryGetInteger(node, out var integer))
        {
            return integer == number;
        }

        if (node is JsonValue json && json.GetValueKind() == JsonValueKind.Number)
        {
            // Python compares a float with an int exactly, not by converting the int to a float.
            var parsed = double.Parse(NumberText(json), NumberStyles.Float, CultureInfo.InvariantCulture);
            return double.IsFinite(parsed) && Math.Floor(parsed) == parsed && new BigInteger(parsed) == number;
        }

        return false;
    }

    /// <summary>Python <c>isinstance(x, str)</c>, returning the string.</summary>
    internal static bool TryGetString(JsonNode? node, out string value)
    {
        if (node is JsonValue json && json.GetValueKind() == JsonValueKind.String)
        {
            value = json.GetValue<string>();
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Python <c>str.isspace()</c> for one UTF-16 unit (no whitespace lies outside the BMP).</summary>
    internal static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary>Python <c>str.strip()</c>.</summary>
    internal static string Strip(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
        {
            start++;
        }

        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    /// <summary>Python <c>str.lstrip()</c>.</summary>
    internal static string LStrip(string text)
    {
        var start = 0;
        while (start < text.Length && IsSpace(text[start]))
        {
            start++;
        }

        return text[start..];
    }

    /// <summary>The literal text of a JSON number as it appeared on the wire.</summary>
    internal static string NumberText(JsonValue value) =>
        value.TryGetValue(out JsonElement element) ? element.GetRawText() : value.ToJsonString();

    private static bool IsIntegerLiteral(string text) =>
        text.Length > 0 && text.AsSpan().IndexOfAny('.', 'e', 'E') < 0;

    private static bool NumberIsNonZero(string text)
    {
        if (IsIntegerLiteral(text))
        {
            return !BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).IsZero;
        }

        // Python floats: 1e-400 underflows to 0.0 (false), 1e400 overflows to inf (true).
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) != 0.0;
    }

    /// <summary>
    /// The UTF-8 the reader parses. Python parses a <c>str</c>, where a lone surrogate may stand inside a string;
    /// UTF-8 cannot carry one, so each is handed to the reader as the <c>\uXXXX</c> escape it decodes back to.
    /// Outside a string that escape is invalid, as the lone surrogate is in Python. After an odd run of
    /// backslashes it would be escaped itself (Python: "Invalid \escape"), so U+FFFD keeps it invalid.
    /// </summary>
    private static byte[] ToUtf8(string text)
    {
        if (!HasLoneSurrogate(text))
        {
            return Utf8NoBom.GetBytes(text);
        }

        var builder = new StringBuilder(text.Length + 16);
        var backslashes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[i + 1]);
                i++;
                backslashes = 0;
            }
            else if (char.IsSurrogate(c))
            {
                if (backslashes % 2 == 1)
                {
                    builder.Append('\uFFFD');
                }
                else
                {
                    builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }

                backslashes = 0;
            }
            else
            {
                builder.Append(c);
                backslashes = c == '\\' ? backslashes + 1 : 0;
            }
        }

        return Utf8NoBom.GetBytes(builder.ToString());
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadString(ref Utf8JsonReader reader)
    {
        var raw = reader.ValueSpan;
        return reader.ValueIsEscaped ? Unescape(raw) : Encoding.UTF8.GetString(raw);
    }

    /// <summary>Decode a validated JSON string body, keeping lone surrogate escapes as Python does.</summary>
    private static string Unescape(ReadOnlySpan<byte> raw)
    {
        var builder = new StringBuilder(raw.Length);
        var start = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != (byte)'\\')
            {
                continue;
            }

            builder.Append(Encoding.UTF8.GetString(raw[start..i]));
            i++;
            switch ((char)raw[i])
            {
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'u':
                    var hex = Encoding.ASCII.GetString(raw.Slice(i + 1, 4));
                    builder.Append((char)int.Parse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default:
                    // '"', '\\' and '/' stand for themselves; the reader rejected anything else.
                    builder.Append((char)raw[i]);
                    break;
            }

            start = i + 1;
        }

        builder.Append(Encoding.UTF8.GetString(raw[start..]));
        return builder.ToString();
    }
}
