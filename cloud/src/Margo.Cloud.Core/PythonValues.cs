using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Margo.Cloud.Core;

/// <summary>Python's view of a parsed JSON value: truthiness, <c>int()</c>, <c>== 1</c> and strings.</summary>
internal static class PythonValues
{
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
            JsonValueKind.False => false,
            JsonValueKind.Number => TryNumber(value, out var integer, out var real) ? !integer.IsZero : real != 0 || double.IsNaN(real),
            _ => false,
        };
    }

    /// <summary>Python's <c>value == 1</c>: an int or float equal to one, or <see langword="true"/>.</summary>
    internal static bool EqualsOne(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return false;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => TryNumber(value, out var integer, out var real) ? integer.IsOne : real == 1.0,
            _ => false,
        };
    }

    /// <summary>
    /// Python's <c>int(value)</c> for a value from <c>json.loads</c>. Returns <see langword="false"/>
    /// where Python raises (TypeError for null, lists and objects; ValueError for text that is not an
    /// integer or for NaN; OverflowError for an infinity).
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

    /// <summary>Python's <c>int(text)</c> in base 10: stripped, signed, digits with single underscores,
    /// at most <see cref="PythonJson.MaxIntDigits"/> digits (leading zeros count, underscores do not).</summary>
    internal static bool TryParseInt(string text, out BigInteger result)
    {
        result = BigInteger.Zero;
        int start = 0, end = text.Length;
        while (start < end && PythonText.IsSpace(text[start]))
        {
            start++;
        }

        while (end > start && PythonText.IsSpace(text[end - 1]))
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

        if (digits.Length == 0 || !previousWasDigit || digits.Length > PythonJson.MaxIntDigits)
        {
            return false; // the last: Python's ValueError beyond sys.get_int_max_str_digits()
        }

        result = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
        {
            result = -result;
        }

        return true;
    }

    /// <summary>
    /// Reads a JSON number the way Python's <c>json</c> module types it: an integer when its text has
    /// no fraction or exponent (returns <see langword="true"/> with <paramref name="integer"/>), a float
    /// otherwise (returns <see langword="false"/> with <paramref name="real"/>).
    /// </summary>
    internal static bool TryNumber(JsonValue value, out BigInteger integer, out double real)
    {
        integer = BigInteger.Zero;
        real = 0;
        if (value.TryGetValue<JsonElement>(out var element))
        {
            var raw = element.GetRawText();
            if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
            {
                integer = BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                return true;
            }

            real = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            return false;
        }

        if (value.TryGetValue<int>(out var small))
        {
            integer = small;
            return true;
        }

        if (value.TryGetValue<long>(out var large))
        {
            integer = large;
            return true;
        }

        if (value.TryGetValue<double>(out real))
        {
            return false;
        }

        throw new InvalidOperationException("unsupported JSON number representation");
    }
}
