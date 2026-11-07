using System.Text;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Helpers.Python;

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
    public static bool IsSpace(int codePoint) => codePoint <= 0xFFFF && PyRepr.IsSpace((char)codePoint);

    /// <summary><c>re</c>'s <c>\d</c> for <c>str</c> patterns (<c>str.isdecimal()</c>).</summary>
    public static bool IsDigit(int codePoint) => PyRepr.DecimalValue(codePoint) >= 0;

    /// <summary><c>re</c>'s <c>\w</c> for <c>str</c> patterns: <c>str.isalnum()</c> or <c>_</c>.</summary>
    public static bool IsWord(int codePoint) => codePoint == '_' || IsAlnum(codePoint);

    /// <summary><c>str.isalnum()</c> for one code point (Unicode 15.1).</summary>
    public static bool IsAlnum(int codePoint)
    {
        var table = PyHelpersTables.AlnumRanges;
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
