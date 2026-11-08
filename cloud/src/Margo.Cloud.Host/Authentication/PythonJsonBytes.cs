// Copied from Margo.Cloud.Router (namespace changed only): the Agents SDK token check the Python host also ran.
// Keep the copies in step; see Margo.Cloud.Host/PORTING.md, "Requests to other components".
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;

namespace Margo.Cloud.Host.Authentication;

/// <summary>
/// Python's <c>json.loads(data)</c> for <c>bytes</c>: the encoding is detected as
/// <c>json.detect_encoding</c> does (UTF-8, UTF-8 with BOM, UTF-16 or UTF-32), decoded with the
/// <c>surrogatepass</c> error handler, then parsed. Used for activity bodies, token segments and the
/// JWKS document, which Python all handed to <c>json.loads</c> as bytes.
/// </summary>
/// <remarks>
/// Parsing uses <see cref="PythonJson.Loads"/> from Margo.Cloud.Core. That parser refuses lone
/// surrogates (escaped or raw), which Python accepts; for such text this class parses again with a
/// reader that keeps them, so an activity whose text holds a broken surrogate is still routed, and one
/// whose recipient holds one fails at the instance key, as in Python. The literals <c>NaN</c>,
/// <c>Infinity</c> and <c>-Infinity</c> and nesting deeper than <see cref="PythonJson.MaxDepth"/> are
/// still refused (Python accepts them).
/// </remarks>
internal static class PythonJsonBytes
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Decode and parse.</summary>
    /// <exception cref="JsonException">Not decodable or not JSON (Python: a <c>ValueError</c>).</exception>
    internal static JsonNode? Loads(ReadOnlySpan<byte> data) => LoadsText(Decode(data));

    /// <summary>Python's <c>json.loads</c> of already decoded text, without the BOM check Python applies
    /// only to <c>str</c> input (the bytes path never reaches it).</summary>
    /// <exception cref="JsonException">Not JSON.</exception>
    internal static JsonNode? LoadsText(string text)
    {
        try
        {
            return PythonJson.Loads(text);
        }
        catch (JsonException) when (MayHoldSurrogates(text))
        {
            return LoadsKeepingSurrogates(text);
        }
    }

    /// <summary><c>data.decode(json.detect_encoding(data), "surrogatepass")</c>.</summary>
    /// <exception cref="JsonException">The bytes are not valid in the detected encoding (Python:
    /// <c>UnicodeDecodeError</c>, a <c>ValueError</c>).</exception>
    internal static string Decode(ReadOnlySpan<byte> data)
    {
        return DetectEncoding(data) switch
        {
            "utf-32" => DecodeUtf32(data[4..], bigEndian: data[0] == 0),
            "utf-16" => DecodeUtf16(data[2..], bigEndian: data[0] == 0xFE),
            "utf-8-sig" => DecodeUtf8(data[3..]),
            "utf-16-be" => DecodeUtf16(data, bigEndian: true),
            "utf-16-le" => DecodeUtf16(data, bigEndian: false),
            "utf-32-be" => DecodeUtf32(data, bigEndian: true),
            "utf-32-le" => DecodeUtf32(data, bigEndian: false),
            _ => DecodeUtf8(data),
        };
    }

    /// <summary>Python's <c>json.detect_encoding</c>.</summary>
    internal static string DetectEncoding(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]) || b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return "utf-32";
        }

        if (b.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) || b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return "utf-16";
        }

        if (b.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return "utf-8-sig";
        }

        if (b.Length >= 4)
        {
            if (b[0] == 0)
            {
                return b[1] != 0 ? "utf-16-be" : "utf-32-be";
            }

            if (b[1] == 0)
            {
                return b[2] != 0 || b[3] != 0 ? "utf-16-le" : "utf-32-le";
            }
        }
        else if (b.Length == 2)
        {
            if (b[0] == 0)
            {
                return "utf-16-be";
            }

            if (b[1] == 0)
            {
                return "utf-16-le";
            }
        }

        return "utf-8";
    }

    /// <summary>Strict UTF-8 in which a three-byte encoded surrogate is accepted (<c>surrogatepass</c>).</summary>
    private static string DecodeUtf8(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(data.Length);
        var i = 0;
        while (i < data.Length)
        {
            var b0 = data[i];
            if (b0 < 0x80)
            {
                builder.Append((char)b0);
                i++;
                continue;
            }

            int length, codePoint;
            byte low = 0x80, high = 0xBF;
            switch (b0)
            {
                case >= 0xC2 and <= 0xDF:
                    length = 2;
                    codePoint = b0 & 0x1F;
                    break;
                case 0xE0:
                    length = 3;
                    codePoint = b0 & 0x0F;
                    low = 0xA0;
                    break;
                case >= 0xE1 and <= 0xEF: // 0xED included: surrogatepass accepts ED A0..BF xx
                    length = 3;
                    codePoint = b0 & 0x0F;
                    break;
                case 0xF0:
                    length = 4;
                    codePoint = b0 & 0x07;
                    low = 0x90;
                    break;
                case >= 0xF1 and <= 0xF3:
                    length = 4;
                    codePoint = b0 & 0x07;
                    break;
                case 0xF4:
                    length = 4;
                    codePoint = b0 & 0x07;
                    high = 0x8F;
                    break;
                default:
                    throw DecodeError("utf-8", i);
            }

            if (i + length > data.Length)
            {
                throw DecodeError("utf-8", i);
            }

            for (var k = 1; k < length; k++)
            {
                var b = data[i + k];
                var min = k == 1 ? low : (byte)0x80;
                var max = k == 1 ? high : (byte)0xBF;
                if (b < min || b > max)
                {
                    throw DecodeError("utf-8", i);
                }

                codePoint = (codePoint << 6) | (b & 0x3F);
            }

            AppendCodePoint(builder, codePoint);
            i += length;
        }

        return builder.ToString();
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length % 2 != 0)
        {
            throw DecodeError("utf-16", data.Length - 1);
        }

        var chars = new char[data.Length / 2];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(bigEndian ? (data[2 * i] << 8) | data[(2 * i) + 1] : data[2 * i] | (data[(2 * i) + 1] << 8));
        }

        return new string(chars);
    }

    private static string DecodeUtf32(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length % 4 != 0)
        {
            throw DecodeError("utf-32", data.Length - (data.Length % 4));
        }

        var builder = new StringBuilder(data.Length / 4);
        for (var i = 0; i < data.Length; i += 4)
        {
            var value = bigEndian
                ? ((uint)data[i] << 24) | ((uint)data[i + 1] << 16) | ((uint)data[i + 2] << 8) | data[i + 3]
                : data[i] | ((uint)data[i + 1] << 8) | ((uint)data[i + 2] << 16) | ((uint)data[i + 3] << 24);
            if (value > 0x10FFFF)
            {
                throw DecodeError("utf-32", i);
            }

            AppendCodePoint(builder, (int)value);
        }

        return builder.ToString();
    }

    private static void AppendCodePoint(StringBuilder builder, int codePoint)
    {
        if (codePoint < 0x10000)
        {
            builder.Append((char)codePoint); // a lone surrogate stays one UTF-16 unit, as in Python
        }
        else
        {
            builder.Append(char.ConvertFromUtf32(codePoint));
        }
    }

    private static JsonException DecodeError(string encoding, int position) =>
        new(string.Create(CultureInfo.InvariantCulture, $"'{encoding}' codec can't decode the data at position {position}"));

    private static bool MayHoldSurrogates(string text)
    {
        if (text.Contains("\\u", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var c in text)
        {
            if (char.IsSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The same grammar as <see cref="PythonJson.Loads"/> (repeated key: last value at the
    /// first position; ints up to <see cref="PythonJson.MaxIntDigits"/> digits), but strings may hold
    /// lone surrogates.</summary>
    private static JsonNode? LoadsKeepingSurrogates(string text)
    {
        var escaped = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                escaped.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                // Inside a string this is the same character; outside one it is not JSON either way.
                escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                escaped.Append(c);
            }
        }

        var reader = new Utf8JsonReader(Utf8.GetBytes(escaped.ToString()), new JsonReaderOptions { MaxDepth = PythonJson.MaxDepth });
        if (!reader.Read())
        {
            throw new JsonException("Expecting value");
        }

        var value = ReadValue(ref reader);
        if (reader.Read())
        {
            throw new JsonException("Extra data");
        }

        return value;
    }

    private static JsonNode? ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var obj = new JsonObject();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = ReadString(ref reader);
                    reader.Read();
                    obj[name] = ReadValue(ref reader); // a repeated key: last value, first position
                }

                return obj;
            case JsonTokenType.StartArray:
                var array = new JsonArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    array.Add(ReadValue(ref reader));
                }

                return array;
            case JsonTokenType.String:
                return JsonValue.Create(ReadString(ref reader));
            case JsonTokenType.Number:
                var raw = Encoding.ASCII.GetString(reader.ValueSpan);
                if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0 && raw.Length - (raw[0] == '-' ? 1 : 0) > PythonJson.MaxIntDigits)
                {
                    throw new JsonException("Exceeds the limit (4300 digits) for integer string conversion");
                }

                using (var document = JsonDocument.Parse(raw))
                {
                    return JsonValue.Create(document.RootElement.Clone());
                }

            case JsonTokenType.True:
                return JsonValue.Create(true);
            case JsonTokenType.False:
                return JsonValue.Create(false);
            default:
                return null;
        }
    }

    /// <summary>A string or property name, unescaped by hand so that a lone surrogate escape is kept.</summary>
    private static string ReadString(ref Utf8JsonReader reader)
    {
        var span = reader.ValueSpan;
        if (!reader.ValueIsEscaped)
        {
            return Utf8.GetString(span);
        }

        var builder = new StringBuilder(span.Length);
        var i = 0;
        while (i < span.Length)
        {
            if (span[i] != (byte)'\\')
            {
                var next = span[i..].IndexOf((byte)'\\');
                var run = next < 0 ? span[i..] : span.Slice(i, next);
                builder.Append(Utf8.GetString(run));
                i += run.Length;
                continue;
            }

            var escape = (char)span[i + 1];
            switch (escape)
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
                    builder.Append((char)int.Parse(Encoding.ASCII.GetString(span.Slice(i + 2, 4)), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: // '"', '\\' and '/'; the reader has validated every escape
                    builder.Append(escape);
                    break;
            }

            i += 2;
        }

        return builder.ToString();
    }
}
