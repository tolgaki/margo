using System.Globalization;
using System.Text;

namespace Margo.Cloud.State.Core;

/// <summary>Python <c>repr()</c> and small <c>str</c> helpers that measure in code points, as Python does.</summary>
public static class PyRepr
{
    /// <summary><c>repr(str)</c>: quote choice, escapes and printable rules of CPython's <c>unicode_repr</c>.</summary>
    public static string Str(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var quote = value.Contains('\'', StringComparison.Ordinal) && !value.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var builder = new StringBuilder(value.Length + 2);
        builder.Append(quote);
        foreach (var codePoint in CodePoints(value))
        {
            if (codePoint == quote || codePoint == '\\')
            {
                builder.Append('\\').Append((char)codePoint);
            }
            else if (codePoint == '\t')
            {
                builder.Append("\\t");
            }
            else if (codePoint == '\n')
            {
                builder.Append("\\n");
            }
            else if (codePoint == '\r')
            {
                builder.Append("\\r");
            }
            else if (codePoint < ' ' || codePoint == 0x7f)
            {
                builder.Append("\\x").Append(codePoint.ToString("x2", CultureInfo.InvariantCulture));
            }
            else if (codePoint < 0x7f)
            {
                builder.Append((char)codePoint);
            }
            else if (IsPrintable(codePoint))
            {
                AppendCodePoint(builder, codePoint);
            }
            else if (codePoint <= 0xff)
            {
                builder.Append("\\x").Append(codePoint.ToString("x2", CultureInfo.InvariantCulture));
            }
            else if (codePoint <= 0xffff)
            {
                builder.Append("\\u").Append(codePoint.ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append("\\U").Append(codePoint.ToString("x8", CultureInfo.InvariantCulture));
            }
        }

        builder.Append(quote);
        return builder.ToString();
    }

    /// <summary>
    /// <c>repr(value)</c> for the values ports print: <c>None</c>, <c>bool</c>, ints (any CLR integer or
    /// <see cref="System.Numerics.BigInteger"/>), floats, <c>str</c>, <c>bytes</c>, lists
    /// (<see cref="System.Collections.IList"/>), tuples (<see cref="PyRow"/> or <c>object?[]</c>).
    /// </summary>
    public static string Object(object? value) => value switch
    {
        null => "None",
        bool flag => flag ? "True" : "False",
        string text => Str(text),
        double number => PyJson.FloatRepr(number),
        float number => PyJson.FloatRepr(number),
        System.Numerics.BigInteger integer => integer.ToString(CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        byte[] bytes => Bytes(bytes),
        PyRow row => Tuple(row),
        object?[] items => Tuple(items),
        System.Collections.IList list => "[" + string.Join(", ", list.Cast<object?>().Select(Object)) + "]",
        _ => value.ToString() ?? "None",
    };

    /// <summary><c>str(value)</c>: like <see cref="Object"/>, but a string is itself.</summary>
    public static string ToStr(object? value) => value is string text ? text : Object(value);

    private static string Tuple(IEnumerable<object?> items)
    {
        var parts = items.Select(Object).ToList();
        return parts.Count == 1 ? "(" + parts[0] + ",)" : "(" + string.Join(", ", parts) + ")";
    }

    private static string Bytes(byte[] bytes)
    {
        var quote = bytes.Contains((byte)'\'') && !bytes.Contains((byte)'"') ? '"' : '\'';
        var builder = new StringBuilder("b").Append(quote);
        foreach (var b in bytes)
        {
            if (b == quote || b == '\\')
            {
                builder.Append('\\').Append((char)b);
            }
            else if (b == '\t')
            {
                builder.Append("\\t");
            }
            else if (b == '\n')
            {
                builder.Append("\\n");
            }
            else if (b == '\r')
            {
                builder.Append("\\r");
            }
            else if (b < ' ' || b >= 0x7f)
            {
                builder.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append((char)b);
            }
        }

        return builder.Append(quote).ToString();
    }

    /// <summary>
    /// Python <c>str.isprintable()</c> for one code point, from CPython's own Unicode tables
    /// (<see cref="PyUnicodeData"/>): .NET's tables are a newer Unicode version, so characters assigned
    /// after Unicode 15.1 would otherwise print raw where Python's <c>repr()</c> escapes them.
    /// </summary>
    public static bool IsPrintable(int codePoint) => FindRun(PyUnicodeData.NonPrintableRanges, 2, codePoint) < 0;

    /// <summary>
    /// <c>unicodedata.decimal(chr(codePoint), -1)</c>: the value of a character for which <c>str.isdecimal()</c>
    /// is true (what <c>int()</c>, <c>float()</c> and <c>re</c>'s <c>\d</c> accept), otherwise -1.
    /// </summary>
    public static int DecimalValue(int codePoint)
    {
        var run = FindRun(PyUnicodeData.DecimalRuns, 3, codePoint);
        return run < 0 ? -1 : PyUnicodeData.DecimalRuns[run + 2] + codePoint - PyUnicodeData.DecimalRuns[run];
    }

    /// <summary>Python <c>str.casefold()</c> (full case folding, CPython's Unicode version; lone surrogates kept).</summary>
    public static string CaseFold(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var codePoint in CodePoints(value))
        {
            if (codePoint < 0x80)
            {
                builder.Append(codePoint is >= 'A' and <= 'Z' ? (char)(codePoint + 32) : (char)codePoint);
                continue;
            }

            var index = Array.BinarySearch(PyUnicodeData.CaseFoldKeys, codePoint);
            if (index >= 0)
            {
                builder.Append(PyUnicodeData.CaseFoldValues[index]);
            }
            else
            {
                AppendCodePoint(builder, codePoint);
            }
        }

        return builder.ToString();
    }

    /// <summary>The offset of the run in <paramref name="table"/> (records of <paramref name="stride"/> ints starting with first, last) holding the code point, or -1.</summary>
    private static int FindRun(int[] table, int stride, int codePoint)
    {
        int low = 0, high = (table.Length / stride) - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            var offset = middle * stride;
            if (codePoint < table[offset])
            {
                high = middle - 1;
            }
            else if (codePoint > table[offset + 1])
            {
                low = middle + 1;
            }
            else
            {
                return offset;
            }
        }

        return -1;
    }

    /// <summary>
    /// Code points of a UTF-16 string as Python sees them: a valid surrogate pair is one code point; a lone
    /// surrogate is a code point of its own (Python strings may hold lone surrogates).
    /// </summary>
    public static IEnumerable<int> CodePoints(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                yield return char.ConvertToUtf32(c, value[i + 1]);
                i++;
            }
            else
            {
                yield return c;
            }
        }
    }

    /// <summary>Python <c>len(str)</c>: the number of code points.</summary>
    public static int Length(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
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

    /// <summary>Converts a UTF-16 index to a Python code point index.</summary>
    public static int CodePointIndex(string value, int utf16Index)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = 0;
        var limit = Math.Min(utf16Index, value.Length);
        var i = 0;
        while (i < limit)
        {
            i += char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
            count++;
        }

        return count;
    }

    /// <summary>
    /// Ordinal comparison by code point, which is how Python orders <c>str</c> (UTF-16 ordinal order
    /// differs for astral characters versus U+E000..U+FFFF).
    /// </summary>
    public static int CompareStrings(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        using var a = CodePoints(left).GetEnumerator();
        using var b = CodePoints(right).GetEnumerator();
        while (true)
        {
            var hasA = a.MoveNext();
            var hasB = b.MoveNext();
            if (!hasA || !hasB)
            {
                return hasA == hasB ? 0 : hasA ? 1 : -1;
            }

            if (a.Current != b.Current)
            {
                return a.Current < b.Current ? -1 : 1;
            }
        }
    }

    /// <summary>A comparer that orders strings like Python's <c>sorted()</c>.</summary>
    public static IComparer<string> StringComparer { get; } = Comparer<string>.Create(CompareStrings);

    /// <summary>
    /// Python <c>str.strip()</c> with no argument: strips Unicode whitespace as <c>str.isspace()</c> defines it.
    /// </summary>
    public static string Strip(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var start = 0;
        var end = value.Length;
        while (start < end && IsSpace(value[start]))
        {
            start++;
        }

        while (end > start && IsSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    /// <summary>Python <c>str.isspace()</c> for one BMP character.</summary>
    public static bool IsSpace(char c) =>
        c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x1f' or '\x85'
        || (c > 0x7f && (char.GetUnicodeCategory(c) is UnicodeCategory.SpaceSeparator
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator));

    /// <summary>Python <c>str.splitlines(keepends)</c>, which splits on every Unicode line boundary.</summary>
    public static List<string> SplitLines(string value, bool keepEnds = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        var lines = new List<string>();
        var start = 0;
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            var breakLength = 0;
            if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
            {
                breakLength = 2;
            }
            else if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or (char)0x2028 or (char)0x2029)
            {
                breakLength = 1;
            }

            if (breakLength == 0)
            {
                i++;
                continue;
            }

            lines.Add(keepEnds ? value[start..(i + breakLength)] : value[start..i]);
            i += breakLength;
            start = i;
        }

        if (start < value.Length)
        {
            lines.Add(value[start..]);
        }

        return lines;
    }

    internal static void AppendCodePoint(StringBuilder builder, int codePoint)
    {
        if (codePoint > 0xffff)
        {
            builder.Append(char.ConvertFromUtf32(codePoint));
        }
        else
        {
            builder.Append((char)codePoint);
        }
    }
}
