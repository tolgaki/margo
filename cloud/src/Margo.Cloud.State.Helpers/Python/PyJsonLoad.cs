using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Helpers.Python;

/// <summary>
/// <c>json.loads</c> as CPython 3.13's C scanner behaves where its C recursion budget runs out. Each container the
/// scanner enters costs one level (<c>_Py_EnterRecursiveCall</c>), and so does every call it makes into a Python
/// object, so how much budget a call site has left decides more than the container limit
/// (<see cref="PyJson.MaxDepth"/> models only that, for a call from a script's <c>main()</c>). With B the deepest
/// container a call site accepts and n the nesting level of the innermost open container where something happens
/// (the document root is level 1), measured with the scripts themselves:
/// <list type="bullet">
/// <item>a container opens while n &lt;= B, else <c>RecursionError</c> ("while decoding a JSON array/object");</item>
/// <item><c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> call <c>parse_constant</c>: they need n &lt; B, else
/// <c>RecursionError</c> ("while calling a Python object");</item>
/// <item>a decode error built in C (<c>raise_errmsg</c> calls <c>json.decoder.JSONDecodeError</c>, which runs
/// Python code) is a <c>JSONDecodeError</c> only while n &lt;= B - <see cref="RaiseErrmsgLevels"/>, else
/// <c>RecursionError</c>;</item>
/// <item>"Expecting value" is a <c>StopIteration</c> that <c>raw_decode</c> turns into the <c>JSONDecodeError</c>
/// once the C stack has unwound, and the over-long int is a <c>ValueError</c>: both fail to be created at n = B,
/// where CPython raises <c>RecursionError</c> with the note "Normalization failed: type=... args=&lt;unknown&gt;"
/// (the last line of the traceback, so the line the hosted tool reports);</item>
/// <item>errors raised after the scan (<c>Extra data</c>, the BOM check) are at level 0 and unaffected.</item>
/// </list>
/// The first of these CPython meets in document order wins, so everything before the decoder's own stopping point
/// is valid JSON and a plain lexer agrees with the scanner there.
/// </summary>
public static class PyJsonLoad
{
    /// <summary>
    /// Levels <c>raise_errmsg</c> needs beyond the innermost open container to build a <c>JSONDecodeError</c>
    /// (measured with CPython 3.13 for every message it raises).
    /// </summary>
    public const int RaiseErrmsgLevels = 4;

    private const string CallingPythonObject = "maximum recursion depth exceeded while calling a Python object";

    /// <summary><c>json.loads(text)</c> at a call site that accepts at most <paramref name="maxDepth"/> nested containers.</summary>
    public static JsonNode? Loads(string text, int maxDepth = PyJson.MaxDepth)
    {
        ArgumentNullException.ThrowIfNull(text);
        PyException? failure = null;
        JsonNode? value = null;
        try
        {
            value = PyJson.Loads(text);
        }
        catch (PyException ex) when (ex is PyValueError or PyRecursionError)
        {
            // A JSONDecodeError, the 4300-digit ValueError, or PyJson's own container limit.
            failure = ex;
        }

        // Where CPython's scanner stops: at the decode error, at the first over-long int, or at the end.
        var stop = failure switch
        {
            PyJsonDecodeError error => Utf16Index(text, error.Pos),
            PyRecursionError => text.Length,
            not null => FirstOverlongInt(text),
            _ => text.Length,
        };
        var level = Walk(text, stop, maxDepth);
        switch (failure)
        {
            case null:
                return value;
            case PyJsonDecodeError { Msg: "Expecting value" } when level >= maxDepth:
                throw NormalizationFailed("StopIteration");
            case PyJsonDecodeError { Msg: not "Expecting value" } when level > 0 && level > maxDepth - RaiseErrmsgLevels:
                throw new PyRecursionError(CallingPythonObject);
            case PyJsonDecodeError or PyRecursionError:
                throw failure;
            default:
                // The over-long int: its ValueError cannot be created at the limit.
                if (level >= maxDepth)
                {
                    throw NormalizationFailed("ValueError");
                }

                throw failure;
        }
    }

    /// <summary>
    /// CPython's <c>RecursionError</c> raised while it created a <paramref name="type"/> exception, with the note
    /// <c>_PyErr_SetObject</c> adds; the traceback prints the note on the line after the exception, so it is part of
    /// the message here (the scripts never catch this error).
    /// </summary>
    private static PyRecursionError NormalizationFailed(string type) =>
        new(CallingPythonObject + "\nNormalization failed: type=" + type + " args=<unknown>");

    /// <summary>
    /// Walks the valid prefix <c>text[..stop]</c> as the scanner does, raising the first <c>RecursionError</c> it would
    /// raise there, and returns the nesting level at <paramref name="stop"/>.
    /// </summary>
    private static int Walk(string text, int stop, int maxDepth)
    {
        var level = 0;
        var i = 0;
        while (i < stop)
        {
            switch (text[i])
            {
                case '"':
                    i = SkipString(text, i + 1);
                    break;
                case '[':
                case '{':
                    level++;
                    if (level > maxDepth)
                    {
                        throw new PyRecursionError("maximum recursion depth exceeded while decoding a JSON "
                                                   + (text[i] == '[' ? "array" : "object") + " from a unicode string");
                    }

                    i++;
                    break;
                case ']':
                case '}':
                    level--;
                    i++;
                    break;
                case 'N':
                case 'I':
                    // NaN, Infinity or -Infinity (outside strings no other token holds these letters).
                    if (level >= maxDepth)
                    {
                        throw new PyRecursionError(CallingPythonObject);
                    }

                    i += text[i] == 'N' ? 3 : 8;
                    break;
                default:
                    i++;
                    break;
            }
        }

        return level;
    }

    /// <summary>The index just after the string whose body starts at <paramref name="start"/> (or the end of the text).</summary>
    private static int SkipString(string text, int start)
    {
        var i = start;
        while (i < text.Length && text[i] != '"')
        {
            i += text[i] == '\\' ? 2 : 1;
        }

        return i + 1;
    }

    /// <summary>
    /// The UTF-16 index of the first integer literal over <see cref="PyJson.MaxIntDigits"/> digits, lexing numbers as
    /// <c>_match_number_unicode</c> does (fraction and exponent digits belong to a float, which has no limit).
    /// </summary>
    private static int FirstOverlongInt(string text)
    {
        var end = text.Length - 1;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipString(text, i + 1);
                continue;
            }

            if (c != '-' && !char.IsAsciiDigit(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (c == '-')
            {
                i++;
                if (i > end || !char.IsAsciiDigit(text[i]))
                {
                    continue; // -Infinity, or not a number
                }
            }

            if (text[i] == '0')
            {
                i++;
            }
            else
            {
                while (i <= end && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }
            }

            var isFloat = false;
            if (i < end && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
            {
                isFloat = true;
                i += 2;
                while (i <= end && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }
            }

            if (i < end && text[i] is 'e' or 'E')
            {
                var exponentStart = i;
                i++;
                if (i < end && text[i] is '-' or '+')
                {
                    i++;
                }

                while (i <= end && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }

                if (char.IsAsciiDigit(text[i - 1]))
                {
                    isFloat = true;
                }
                else
                {
                    i = exponentStart;
                }
            }

            if (!isFloat && i - start - (c == '-' ? 1 : 0) > PyJson.MaxIntDigits)
            {
                return start;
            }
        }

        return text.Length;
    }

    /// <summary>The UTF-16 index of code point <paramref name="codePoints"/> (a decode error position).</summary>
    private static int Utf16Index(string text, int codePoints)
    {
        var index = 0;
        for (var seen = 0; seen < codePoints && index < text.Length; seen++)
        {
            PyText.CodePointAt(text, index, out var length);
            index += length;
        }

        return index;
    }
}
