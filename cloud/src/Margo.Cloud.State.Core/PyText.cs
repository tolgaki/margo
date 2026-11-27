using System.Text;

namespace Margo.Cloud.State.Core;

/// <summary>
/// <c>str</c> operations measured in code points (a surrogate pair is one character, a lone surrogate is
/// one character), plus the character classes of CPython's <c>re</c> for <c>str</c> patterns with the
/// Unicode 15.1 tables CPython 3.13 uses.
/// </summary>
public static class PyText
{
    /// <summary>The code point at UTF-16 index <paramref name="index"/> and its UTF-16 length (1 or 2).</summary>
    public static int CodePointAt(string text, int index, out int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        var c = text[index];
        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            length = 2;
            return char.ConvertToUtf32(c, text[index + 1]);
        }

        length = 1;
        return c;
    }

    /// <summary><c>text[:count]</c>.</summary>
    public static string Prefix(string text, int count)
    {
        ArgumentNullException.ThrowIfNull(text);
        var index = 0;
        for (var seen = 0; seen < count && index < text.Length; seen++)
        {
            CodePointAt(text, index, out var length);
            index += length;
        }

        return index >= text.Length ? text : text[..index];
    }

    /// <summary>One code point as a string (a lone surrogate stays a lone surrogate, as in Python).</summary>
    public static string CodePointString(int codePoint) =>
        codePoint is >= 0xD800 and <= 0xDFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint);

    /// <summary>The code points of a string (lone surrogates are their own code points, as in Python).</summary>
    public static int[] CodePoints(string text) => [.. PyRepr.CodePoints(text ?? throw new ArgumentNullException(nameof(text)))];

    /// <summary><c>sorted(strings)</c>: code-point order (not UTF-16 or culture order).</summary>
    public static List<string> Sorted(IEnumerable<string> values)
    {
        var list = (values ?? throw new ArgumentNullException(nameof(values))).ToList();
        list.Sort(PyRepr.StringComparer);
        return list;
    }

    /// <summary><c>list(text)</c>: one string per code point.</summary>
    public static List<object?> CodePointStrings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<object?>(text.Length);
        for (var i = 0; i < text.Length;)
        {
            CodePointAt(text, i, out var length);
            result.Add(text.Substring(i, length));
            i += length;
        }

        return result;
    }

    /// <summary><c>'%-Ns' % text</c>: left-justified, padded with spaces to <paramref name="width"/> code points.</summary>
    public static string LeftJustify(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        var length = PyRepr.Length(text);
        return length >= width ? text : text + new string(' ', width - length);
    }

    /// <summary><c>text.rstrip(chars)</c> for a set of BMP characters.</summary>
    public static string RStrip(string text, string chars)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(chars);
        var end = text.Length;
        while (end > 0 && chars.Contains(text[end - 1], StringComparison.Ordinal) && !char.IsSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }

    /// <summary><c>re</c>'s <c>\s</c> for <c>str</c> patterns (<c>str.isspace()</c>).</summary>
    public static bool IsSpace(int codePoint) => InRanges(PyUnicodeData.SpaceRanges, codePoint);

    /// <summary><c>re</c>'s <c>\d</c> for <c>str</c> patterns (<c>str.isdecimal()</c>, the digits <c>int()</c> accepts).</summary>
    public static bool IsDecimal(int codePoint) => PyRepr.DecimalValue(codePoint) >= 0;

    /// <summary><c>str.isdigit()</c>: nonempty, and every code point has a digit value (Unicode 15.1).</summary>
    public static bool IsDigit(string text)
    {
        var points = CodePoints(text);
        return points.Length > 0 && points.All(point => InRanges(PyUnicodeData.DigitRanges, point));
    }

    /// <summary><c>re</c>'s <c>\w</c> for <c>str</c> patterns: <c>str.isalnum()</c> or <c>_</c>.</summary>
    public static bool IsWord(int codePoint) => codePoint == '_' || IsAlnum(codePoint);

    /// <summary><c>str.isalnum()</c> for one code point (Unicode 15.1).</summary>
    public static bool IsAlnum(int codePoint) => InRanges(PyUnicodeData.AlnumRanges, codePoint);

    /// <summary>
    /// <c>re.fullmatch("(?i)" + letter, chr(codePoint))</c> for an ASCII lowercase letter: both cases, and the
    /// non-ASCII equivalents CPython's <c>re</c> adds (KELVIN SIGN for <c>k</c>, LONG S for <c>s</c>, dotted and
    /// dotless I for <c>i</c>).
    /// </summary>
    public static bool MatchesIgnoreCase(char letter, int codePoint) =>
        codePoint == letter || codePoint == char.ToUpperInvariant(letter)
        || (PyUnicodeData.IgnoreCaseExtras.TryGetValue(letter, out var extras) && extras.Contains(codePoint));

    private static bool InRanges(int[] table, int codePoint)
    {
        int low = 0, high = (table.Length / 2) - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            if (codePoint < table[2 * middle])
            {
                high = middle - 1;
            }
            else if (codePoint > table[(2 * middle) + 1])
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>re.sub(r'\s+', ' ', text)</c> with Python's whitespace set.</summary>
    public static string CollapseWhitespace(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var inRun = false;
        foreach (var c in text)
        {
            if (PyRepr.IsSpace(c))
            {
                if (!inRun)
                {
                    builder.Append(' ');
                    inRun = true;
                }
            }
            else
            {
                builder.Append(c);
                inRun = false;
            }
        }

        return builder.ToString();
    }
}
