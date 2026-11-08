// Copied from Margo.Cloud.Router (namespace changed only): the Agents SDK token check the Python host also ran.
// Keep the copies in step; see Margo.Cloud.Host/PORTING.md, "Requests to other components".
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Margo.Cloud.Host.Authentication;

/// <summary>
/// The Python semantics the router and its token check depend on: <c>str.isspace</c>,
/// <c>str.lower()</c> compared with a configured value, truthiness and <c>int()</c> of a value from
/// <c>json.loads</c>. Margo.Cloud.Core has the same helpers, but internal; these follow them.
/// </summary>
internal static class PythonCompat
{
    /// <summary>Python 3.13's limit on int/text conversion (<c>sys.get_int_max_str_digits()</c>).</summary>
    internal const int MaxIntDigits = 4300;

    /// <summary>Python's <c>str.isspace()</c> for one UTF-16 unit (every such code point is in the BMP).</summary>
    internal static bool IsSpace(char c) => (int)c switch
    {
        >= 0x09 and <= 0x0d => true,
        >= 0x1c and <= 0x20 => true,
        0x85 or 0xa0 or 0x1680 => true,
        >= 0x2000 and <= 0x200a => true,
        0x2028 or 0x2029 or 0x202f or 0x205f or 0x3000 => true,
        _ => false,
    };

    /// <summary>Python's <c>value.lower() == other.lower()</c>.</summary>
    /// <remarks>
    /// Exact when <paramref name="other"/> is ASCII (client IDs and tenant GUIDs are): only ASCII
    /// letters and U+212A KELVIN SIGN lower-case to ASCII in Python (U+0130 becomes <c>i</c> plus a
    /// combining dot, which never equals ASCII text). Otherwise both sides are lower-cased with the
    /// invariant culture, which can differ from Python for a few characters.
    /// </remarks>
    internal static bool LowerEquals(string value, string other)
    {
        if (!Ascii.IsValid(other))
        {
            return string.Equals(value.ToLowerInvariant(), other.ToLowerInvariant(), StringComparison.Ordinal);
        }

        if (value.Length != other.Length)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var lowered = c switch
            {
                >= 'A' and <= 'Z' => (char)(c + 32),
                < (char)0x80 => c,
                'K' => 'k',
                _ => '\0',
            };
            var expected = other[i] is >= 'A' and <= 'Z' ? (char)(other[i] + 32) : other[i];
            if (lowered == '\0' || lowered != expected)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The value as a string if it is a JSON string, otherwise <see langword="null"/>.</summary>
    internal static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Python's <c>bool(value)</c> for a value from <c>json.loads</c>.</summary>
    internal static bool IsTruthy(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return false;
            case JsonObject obj:
                return obj.Count > 0;
            case JsonArray array:
                return array.Count > 0;
        }

        var value = (JsonValue)node;
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.True => true,
            JsonValueKind.Number => TryNumber(value, out var integer, out var real) ? !integer.IsZero : real != 0 || double.IsNaN(real),
            _ => false,
        };
    }

    /// <summary>
    /// Python's <c>int(value)</c> for a value from <c>json.loads</c>. Returns <see langword="false"/>
    /// where Python raises (TypeError for null, lists and objects; ValueError for text that is not an
    /// integer; OverflowError for an infinite float).
    /// </summary>
    internal static bool TryToInt(JsonNode? node, out BigInteger result)
    {
        result = BigInteger.Zero;
        if (node is not JsonValue value)
        {
            return false;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.True:
                result = BigInteger.One;
                return true;
            case JsonValueKind.False:
                return true;
            case JsonValueKind.String:
                return TryParseInt(value.GetValue<string>(), out result);
            case JsonValueKind.Number:
                if (TryNumber(value, out result, out var real))
                {
                    return true;
                }

                if (!double.IsFinite(real))
                {
                    return false;
                }

                result = new BigInteger(Math.Truncate(real));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Python's <c>int(text)</c> in base 10: stripped, signed, digits with single underscores.</summary>
    /// <remarks><c>int()</c> strips the <c>str.isspace</c> characters except U+001C to U+001F: CPython
    /// maps non-ASCII spaces to a space and then skips C <c>isspace</c> ones.</remarks>
    internal static bool TryParseInt(string text, out BigInteger result)
    {
        static bool IsIntSpace(char c) => IsSpace(c) && c is not (>= '\u001c' and <= '\u001f');

        result = BigInteger.Zero;
        int start = 0, end = text.Length;
        while (start < end && IsIntSpace(text[start]))
        {
            start++;
        }

        while (end > start && IsIntSpace(text[end - 1]))
        {
            end--;
        }

        var negative = false;
        if (start < end && (text[start] == '+' || text[start] == '-'))
        {
            negative = text[start] == '-';
            start++;
        }

        var digits = new StringBuilder();
        var previousWasDigit = false;
        var index = start;
        while (index < end)
        {
            if (text[index] == '_')
            {
                if (!previousWasDigit)
                {
                    return false;
                }

                previousWasDigit = false;
                index++;
                continue;
            }

            if (Rune.DecodeFromUtf16(text.AsSpan(index, end - index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done
                || !Rune.IsDigit(rune))
            {
                return false;
            }

            digits.Append((char)('0' + (int)Rune.GetNumericValue(rune)));
            previousWasDigit = true;
            index += consumed;
        }

        if (digits.Length == 0 || !previousWasDigit || digits.Length > MaxIntDigits)
        {
            return false;
        }

        result = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
        {
            result = -result;
        }

        return true;
    }

    /// <summary>A JSON number typed as Python's <c>json</c> types it: an int when its text has no
    /// fraction or exponent (returns <see langword="true"/>), a float otherwise.</summary>
    internal static bool TryNumber(JsonValue value, out BigInteger integer, out double real)
    {
        integer = BigInteger.Zero;
        real = 0;
        string raw;
        if (value.TryGetValue<JsonElement>(out var element))
        {
            raw = element.GetRawText();
        }
        else if (value.TryGetValue<long>(out var whole))
        {
            integer = whole;
            return true;
        }
        else if (value.TryGetValue<double>(out real))
        {
            return false;
        }
        else
        {
            raw = value.ToJsonString();
        }

        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
        {
            integer = BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            return true;
        }

        real = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        return false;
    }
}
