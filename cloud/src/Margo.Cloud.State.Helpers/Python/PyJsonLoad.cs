using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Helpers.Python;

/// <summary>
/// <c>json.loads</c> where the caller's position on CPython's C stack lowers the nesting the decoder accepts.
/// <see cref="PyJson.Loads(string)"/> accepts <see cref="PyJson.MaxDepth"/> (9998) nested containers, which
/// is what a call from a script's <c>main()</c> allows; a call from inside a generator resumed by a
/// <c>for</c> loop (teams_feedback.py's <c>iter_messages</c>) has two levels fewer. The decoder raises
/// <c>RecursionError</c> when it reaches the first container past the limit, so a decode error earlier in
/// the document still wins, as do the other errors that come first.
/// </summary>
public static class PyJsonLoad
{
    /// <summary><c>json.loads(text)</c> accepting at most <paramref name="maxDepth"/> nested containers.</summary>
    public static JsonNode? Loads(string text, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (beyond, bigInteger) = Scan(text, maxDepth);
        JsonNode? value;
        try
        {
            value = PyJson.Loads(text);
        }
        catch (PyJsonDecodeError error) when (beyond >= 0 && PyRepr.CodePointIndex(text, beyond) < error.Pos)
        {
            throw TooDeep();
        }
        catch (PyValueError error) when (error is not PyJsonDecodeError && beyond >= 0 && bigInteger >= 0 && beyond < bigInteger)
        {
            throw TooDeep();
        }

        if (beyond >= 0)
        {
            throw TooDeep();
        }

        return value;
    }

    private static PyRecursionError TooDeep() =>
        new("maximum recursion depth exceeded while decoding a JSON array from a unicode string");

    /// <summary>
    /// The UTF-16 index of the first container that opens deeper than <paramref name="maxDepth"/>, and of the
    /// first integer literal longer than 4300 digits, outside strings (-1 when there is none). Only the part of
    /// the text the decoder would reach before failing matters, and up to that point the text is valid JSON,
    /// so a plain lexer agrees with the decoder there.
    /// </summary>
    private static (int Beyond, int BigInteger) Scan(string text, int maxDepth)
    {
        var depth = 0;
        var beyond = -1;
        var bigInteger = -1;
        var i = 0;
        while (i < text.Length && (beyond < 0 || bigInteger < 0))
        {
            var c = text[i];
            if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    i += text[i] == '\\' ? 2 : 1;
                }

                i++;
                continue;
            }

            if (c is '[' or '{')
            {
                depth++;
                if (depth > maxDepth && beyond < 0)
                {
                    beyond = i;
                }
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
            else if (c == '-' || char.IsAsciiDigit(c))
            {
                var start = i;
                var j = c == '-' ? i + 1 : i;
                var digits = 0;
                while (j < text.Length && char.IsAsciiDigit(text[j]))
                {
                    j++;
                    digits++;
                }

                var isFloat = j < text.Length && text[j] is '.' or 'e' or 'E';
                if (!isFloat && digits > PyJson.MaxIntDigits && bigInteger < 0)
                {
                    bigInteger = start;
                }

                i = Math.Max(j, i + 1);
                continue;
            }

            i++;
        }

        return (beyond, bigInteger);
    }
}
