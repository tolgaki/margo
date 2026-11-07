using System.Globalization;
using System.Numerics;
using System.Text;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Helpers.Python;

/// <summary>
/// CPython 3.13 <c>html.unescape</c>: the <c>_charref</c> regular expression
/// (<c>&amp;(#[0-9]+;?|#[xX][0-9a-fA-F]+;?|[^\t\n\f &lt;&amp;#;]{1,32};?)</c>), HTML5 named references with
/// longest-prefix matching for names without <c>;</c>, and the HTML5 rules for invalid numeric references
/// (Windows-1252 remapping, surrogates and out-of-range numbers as U+FFFD, noncharacters dropped).
/// </summary>
public static class PyHtml
{
    private static readonly Dictionary<string, string> Html5 =
        PyHelpersTables.Html5.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static readonly Dictionary<int, string> InvalidCharrefs =
        PyHelpersTables.InvalidCharrefs.ToDictionary(pair => pair.Key, pair => pair.Value);

    private static readonly HashSet<int> InvalidCodepoints = [.. PyHelpersTables.InvalidCodepoints];

    private static readonly BigInteger MaxCodePoint = 0x10FFFF;

    /// <summary><c>html.unescape(text)</c>.</summary>
    public static string Unescape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var last = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '&')
            {
                i++;
                continue;
            }

            var end = MatchReference(text, i);
            if (end < 0)
            {
                i++;
                continue;
            }

            builder.Append(text, last, i - last);
            builder.Append(Replace(text.Substring(i + 1, end - i - 1)));
            i = end;
            last = end;
        }

        builder.Append(text, last, text.Length - last);
        return builder.ToString();
    }

    /// <summary>The end (exclusive) of the <c>_charref</c> match starting at the <c>&amp;</c> at <paramref name="start"/>, or -1.</summary>
    private static int MatchReference(string text, int start)
    {
        var i = start + 1;
        if (i < text.Length && text[i] == '#')
        {
            // #[0-9]+;?  then  #[xX][0-9a-fA-F]+;?  (the named alternative excludes '#').
            var j = i + 1;
            while (j < text.Length && text[j] is >= '0' and <= '9')
            {
                j++;
            }

            if (j > i + 1)
            {
                return j < text.Length && text[j] == ';' ? j + 1 : j;
            }

            if (i + 1 < text.Length && text[i + 1] is 'x' or 'X')
            {
                j = i + 2;
                while (j < text.Length && char.IsAsciiHexDigit(text[j]))
                {
                    j++;
                }

                if (j > i + 2)
                {
                    return j < text.Length && text[j] == ';' ? j + 1 : j;
                }
            }

            return -1;
        }

        // [^\t\n\f <&#;]{1,32};?  counted in code points.
        var count = 0;
        while (i < text.Length && count < 32 && text[i] is not ('\t' or '\n' or '\f' or ' ' or '<' or '&' or '#' or ';'))
        {
            PyText.CodePointAt(text, i, out var length);
            i += length;
            count++;
        }

        if (count == 0)
        {
            return -1;
        }

        return i < text.Length && text[i] == ';' ? i + 1 : i;
    }

    private static string Replace(string reference)
    {
        if (reference[0] == '#')
        {
            BigInteger number;
            if (reference[1] is 'x' or 'X')
            {
                var digits = reference[2..].TrimEnd(';');
                number = BigInteger.Parse("0" + digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            }
            else
            {
                // int() of a decimal string: more than 4300 digits raises ValueError, as in Python.
                number = PyArgumentParser.ParsePyInt(reference[1..].TrimEnd(';'));
            }

            if (number <= 0x9F && InvalidCharrefs.TryGetValue((int)number, out var replacement))
            {
                return replacement;
            }

            if ((number >= 0xD800 && number <= 0xDFFF) || number > MaxCodePoint)
            {
                return "\uFFFD";
            }

            var codePoint = (int)number;
            return InvalidCodepoints.Contains(codePoint) ? "" : char.ConvertFromUtf32(codePoint);
        }

        if (Html5.TryGetValue(reference, out var named))
        {
            return named;
        }

        // The longest prefix (at least two characters) that names an entity.
        var length = PyRepr.Length(reference);
        for (var x = length - 1; x > 1; x--)
        {
            var prefix = PyText.Prefix(reference, x);
            if (Html5.TryGetValue(prefix, out var value))
            {
                return value + reference[prefix.Length..];
            }
        }

        return "&" + reference;
    }
}
