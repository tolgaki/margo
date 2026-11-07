using System.Numerics;
using System.Text.Json.Nodes;

namespace Margo.Cloud.State.Core;

/// <summary>
/// <c>json.dump(value, sys.stdout, indent=2)</c> as CPython 3.13 runs it: not the C accelerator that
/// <c>json.dumps</c> uses but the pure-Python <c>_make_iterencode</c> generators, which write each chunk to
/// the stream as it is produced and nest one Python frame per container. A container nested deeper than
/// the interpreter's recursion limit allows raises <c>RecursionError</c> after everything before it has
/// been written, so the partial output is part of the observable behaviour.
/// <para>Values: <c>null</c>, <see cref="bool"/>, integers, <see cref="double"/>, <see cref="string"/>,
/// <see cref="JsonNode"/>, lists (<see cref="IReadOnlyList{T}"/> of <see cref="object"/>) and dicts with
/// string keys (<see cref="IReadOnlyList{T}"/> of <see cref="KeyValuePair{TKey, TValue}"/>).</para>
/// </summary>
public static class PyJsonDump
{
    /// <summary>
    /// The deepest container (the outermost is depth 1) that <c>json.dump</c> encodes when called from a
    /// script's <c>main()</c> under the default recursion limit of 1000: <c>&lt;module&gt;</c>, <c>main</c>,
    /// <c>dump</c> and <c>_iterencode</c> hold four frames, each container one more. Measured with CPython
    /// 3.13 for teams_feedback.py and engage_parse.py.
    /// </summary>
    public const int MaxDepthFromMain = 996;

    /// <summary>Writes <paramref name="value"/> with <c>indent=2</c>, <c>ensure_ascii=True</c>, <c>allow_nan=True</c>.</summary>
    public static void Dump(TextWriter writer, object? value, int maxDepth = MaxDepthFromMain)
    {
        ArgumentNullException.ThrowIfNull(writer);
        new Encoder(writer, maxDepth).Encode(value);
    }

    private sealed class Encoder(TextWriter writer, int maxDepth)
    {
        private const string Indent = "  ";

        public void Encode(object? value)
        {
            if (!TryScalar(value, out var text))
            {
                EncodeContainer(value, 0, 1);
                return;
            }

            Write(text);
        }

        private void Write(string chunk) => PyIO.Write(writer, chunk);

        private static bool TryScalar(object? value, out string text)
        {
            switch (Classify(value))
            {
                case null:
                    text = "null";
                    return true;
                case bool flag:
                    text = flag ? "true" : "false";
                    return true;
                case BigInteger integer:
                    text = PyJson.IntStr(integer);
                    return true;
                case double number:
                    text = double.IsNaN(number) ? "NaN"
                        : double.IsPositiveInfinity(number) ? "Infinity"
                        : double.IsNegativeInfinity(number) ? "-Infinity"
                        : PyJson.FloatRepr(number);
                    return true;
                case string str:
                    text = PyJson.EncodeString(str, ensureAscii: true);
                    return true;
                default:
                    text = "";
                    return false;
            }
        }

        private static object? Classify(object? value) => value switch
        {
            IReadOnlyList<KeyValuePair<string, object?>> => value,
            _ => PyObj.Normalize(value),
        };

        private void EncodeContainer(object? value, int level, int depth)
        {
            if (depth > maxDepth)
            {
                // The generator for this container is a new Python frame past the recursion limit.
                throw new PyRecursionError("maximum recursion depth exceeded");
            }

            if (Classify(value) is JsonObject or IReadOnlyList<KeyValuePair<string, object?>>)
            {
                EncodeDict(value!, level, depth);
            }
            else
            {
                EncodeList(PyObj.Items(value), level, depth);
            }
        }

        private void EncodeList(IReadOnlyList<object?> items, int level, int depth)
        {
            if (items.Count == 0)
            {
                Write("[]");
                return;
            }

            level++;
            var newlineIndent = "\n" + Repeat(level);
            var separator = "," + newlineIndent;
            var buffer = "[" + newlineIndent;
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    buffer = separator;
                }

                if (TryScalar(items[i], out var text))
                {
                    Write(buffer + text);
                }
                else
                {
                    Write(buffer);
                    EncodeContainer(items[i], level, depth + 1);
                }
            }

            level--;
            Write("\n" + Repeat(level));
            Write("]");
        }

        private void EncodeDict(object value, int level, int depth)
        {
            var pairs = value is JsonObject dict
                ? dict.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value)).ToList()
                : (IReadOnlyList<KeyValuePair<string, object?>>)value;
            if (pairs.Count == 0)
            {
                Write("{}");
                return;
            }

            Write("{");
            level++;
            var newlineIndent = "\n" + Repeat(level);
            var itemSeparator = "," + newlineIndent;
            var first = true;
            foreach (var (key, item) in pairs)
            {
                if (first)
                {
                    first = false;
                    Write(newlineIndent);
                }
                else
                {
                    Write(itemSeparator);
                }

                Write(PyJson.EncodeString(key, ensureAscii: true));
                Write(": ");
                if (TryScalar(item, out var text))
                {
                    Write(text);
                }
                else
                {
                    EncodeContainer(item, level, depth + 1);
                }
            }

            level--;
            Write("\n" + Repeat(level));
            Write("}");
        }

        private static string Repeat(int level) => level <= 0 ? "" : new string(' ', Indent.Length * level);
    }
}
