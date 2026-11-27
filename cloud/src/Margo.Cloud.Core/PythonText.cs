using System.Text;

namespace Margo.Cloud.Core;

/// <summary>
/// The few Python <c>str</c> semantics the hosted core depends on: code-point length and slicing,
/// code-point ordering (as <c>sorted</c> uses), <c>str.isspace</c>, <c>str.lower()</c> compared with
/// an ASCII word, and strict UTF-8 encoding (<c>str.encode("utf-8")</c> refuses lone surrogates).
/// </summary>
internal static class PythonText
{
    /// <summary>UTF-8 that throws <see cref="EncoderFallbackException"/> on a lone surrogate, like Python.</summary>
    internal static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Python's <c>str.isspace()</c> for one code point (every such code point is in the BMP).</summary>
    internal static bool IsSpace(char c) => (int)c switch
    {
        >= 0x09 and <= 0x0d => true,
        >= 0x1c and <= 0x20 => true,
        0x85 or 0xa0 or 0x1680 => true,
        >= 0x2000 and <= 0x200a => true,
        0x2028 or 0x2029 or 0x202f or 0x205f or 0x3000 => true,
        _ => false,
    };

    /// <summary>Python's <c>len(s)</c>: a valid surrogate pair is one code point, a lone surrogate is one.</summary>
    internal static int CodePointCount(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }

    /// <summary>Python's <c>s[:limit]</c>, counting code points so a surrogate pair is never split.</summary>
    internal static string TruncateCodePoints(string value, int limit)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (count == limit)
            {
                return value[..i];
            }

            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }

            count++;
        }

        return value;
    }

    /// <summary>Python's string ordering: by code point, not by UTF-16 code unit.</summary>
    internal static int CompareCodePoints(string left, string right)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            var a = NextCodePoint(left, ref i);
            var b = NextCodePoint(right, ref j);
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return (i < left.Length).CompareTo(j < right.Length);
    }

    /// <summary>
    /// Python's <c>s.lower()</c> when the result is ASCII, otherwise <see langword="null"/>.
    /// Only ASCII and U+212A KELVIN SIGN lower-case to ASCII (U+0130 lower-cases to "i" plus a
    /// combining dot), so comparing the result with an ASCII word is exact.
    /// </summary>
    internal static string? LowerAscii(string value)
    {
        var chars = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            chars[i] = c switch
            {
                >= 'A' and <= 'Z' => (char)(c + 32),
                < '\u0080' => c,
                (char)0x212a => 'k', // KELVIN SIGN
                _ => '\0',
            };
            if (chars[i] == '\0' && c != '\0')
            {
                return null;
            }
        }

        return new string(chars);
    }

    /// <summary><c>value.lower() == word</c> for an ASCII lower-case <paramref name="word"/>.</summary>
    internal static bool LowerEquals(string? value, string word) =>
        value is not null && string.Equals(LowerAscii(value), word, StringComparison.Ordinal);

    private static int NextCodePoint(string value, ref int index)
    {
        var c = value[index++];
        if (char.IsHighSurrogate(c) && index < value.Length && char.IsLowSurrogate(value[index]))
        {
            return char.ConvertToUtf32(c, value[index++]);
        }

        return c;
    }
}
