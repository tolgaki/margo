using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Memory;

/// <summary>
/// A Python argument that is either omitted (the Python function's default applies) or given (where
/// <see langword="null"/> is Python's <c>None</c>). C# optional parameters cannot tell "omitted" from
/// <see langword="null"/>; the memory API needs the difference where Python's default is not <c>None</c>
/// (for example <c>put(..., status="candidate")</c> versus an explicit <c>status=None</c> from model JSON).
/// </summary>
public readonly struct PyArg : IEquatable<PyArg>
{
    private PyArg(JsonNode? value)
    {
        Value = value;
        IsGiven = true;
    }

    /// <summary>Whether the caller passed the argument (possibly as <c>None</c>).</summary>
    public bool IsGiven { get; }

    /// <summary>The given value; <see langword="null"/> is <c>None</c>.</summary>
    public JsonNode? Value { get; }

    /// <summary>The argument was not passed.</summary>
    public static PyArg Omitted => default;

    /// <summary>An explicit <c>None</c>.</summary>
    public static PyArg None => new(null);

    /// <summary>An explicit value.</summary>
    public static PyArg Of(JsonNode? value) => new(value);

    /// <summary>The given value, or the Python default when omitted.</summary>
    public JsonNode? Or(JsonNode? fallback) => IsGiven ? Value : fallback;

    public static implicit operator PyArg(JsonNode? value) => new(value);

    public static implicit operator PyArg(string? value) => new(value is null ? null : JsonValue.Create(value));

    public static implicit operator PyArg(long value) => new(JsonValue.Create(value));

    public static implicit operator PyArg(int value) => new(JsonValue.Create((long)value));

    public static implicit operator PyArg(bool value) => new(JsonValue.Create(value));

    /// <summary>Named alternative of the implicit conversions.</summary>
    public static PyArg FromJsonNode(JsonNode? value) => new(value);

    public bool Equals(PyArg other) => IsGiven == other.IsGiven && PyJson.PyEquals(Value, other.Value);

    public override bool Equals(object? obj) => obj is PyArg other && Equals(other);

    public override int GetHashCode() => IsGiven.GetHashCode();

    public static bool operator ==(PyArg left, PyArg right) => left.Equals(right);

    public static bool operator !=(PyArg left, PyArg right) => !left.Equals(right);
}

/// <summary>
/// Python's dynamic semantics on model-supplied JSON values, as the memory modules rely on them: <c>str()</c>,
/// <c>repr()</c>, set membership with unhashable values, <c>dict(x, **kwargs)</c>, <c>str.isdigit()</c>,
/// <c>re</c>'s <c>\w</c>/<c>\s</c>, and BigInteger-safe copies. They belong in State.Core (the Work port has an
/// equivalent <c>PyDynamic</c>); they live here until the two are merged.
/// </summary>
internal static class Py
{
    /// <summary>A deep copy (iterative; keeps Python ints beyond 64 bits as ints, which <c>DeepClone</c> does not).</summary>
    public static JsonNode? Clone(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            return Scalar(PyJson.Scalar(value));
        }

        var root = node is JsonObject ? (JsonNode)new JsonObject() : new JsonArray();
        var stack = new Stack<(JsonNode Source, JsonNode Target)>();
        stack.Push((node, root));
        while (stack.Count > 0)
        {
            var (source, target) = stack.Pop();
            if (source is JsonObject obj)
            {
                var copy = (JsonObject)target;
                foreach (var (key, child) in obj)
                {
                    copy[key] = Shell(child, stack);
                }
            }
            else
            {
                var copy = (JsonArray)target;
                foreach (var child in (JsonArray)source)
                {
                    copy.Add(Shell(child, stack));
                }
            }
        }

        return root;

        static JsonNode? Shell(JsonNode? child, Stack<(JsonNode, JsonNode)> stack)
        {
            switch (child)
            {
                case null:
                    return null;
                case JsonValue scalar:
                    return Scalar(PyJson.Scalar(scalar));
                default:
                    var shell = child is JsonObject ? (JsonNode)new JsonObject() : new JsonArray();
                    stack.Push((child, shell));
                    return shell;
            }
        }
    }

    /// <summary>A JSON node for a Python scalar (as <see cref="PyJson.Scalar"/> returns them) or an SQLite value.</summary>
    public static JsonNode? Scalar(object? value) => value switch
    {
        null => null,
        bool flag => JsonValue.Create(flag),
        string text => JsonValue.Create(text),
        BigInteger integer => PyJson.Int(integer),
        long integer => JsonValue.Create(integer),
        int integer => JsonValue.Create((long)integer),
        double number => PyJson.Float(number),
        JsonNode node => Clone(node),
        _ => throw new PyTypeError("unsupported value of type " + value.GetType().Name),
    };

    /// <summary>A copy of a JSON object (Python <c>dict(value)</c>; nested values are copied, not shared).</summary>
    public static JsonObject Copy(JsonObject value) => (JsonObject)Clone(value)!;

    /// <summary><c>value[key]</c> as a detached copy, or <see langword="null"/> when absent.</summary>
    public static JsonNode? Get(JsonObject value, string key) => value.TryGetPropertyValue(key, out var found) ? found : null;

    /// <summary><c>key in value</c> for a dict.</summary>
    public static bool Has(JsonObject value, string key) => value.ContainsKey(key);

    /// <summary>Sets <c>target[key] = value</c> (copying the value so nodes are never shared between parents).</summary>
    public static void Set(JsonObject target, string key, JsonNode? value) => target[key] = value?.Parent is null ? value : Clone(value);

    /// <summary><c>str(value)</c>.</summary>
    public static string Str(JsonNode? value) => value is JsonValue && PyJson.Scalar(value) is string text ? text : Repr(value);

    /// <summary><c>str(value)</c> for a value read from SQLite.</summary>
    public static string Str(object? value) => value switch
    {
        string text => text,
        JsonNode node => Str(node),
        _ => PyRepr.Object(value),
    };

    /// <summary><c>repr(value)</c> of a JSON value (dicts as <c>{'k': 'v'}</c>, lists as <c>[1, 2]</c>).</summary>
    public static string Repr(JsonNode? value)
    {
        var builder = new StringBuilder();
        AppendRepr(builder, value);
        return builder.ToString();
    }

    private static void AppendRepr(StringBuilder builder, JsonNode? value)
    {
        switch (value)
        {
            case null:
                builder.Append("None");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, child) in obj)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    builder.Append(PyRepr.Str(key)).Append(": ");
                    AppendRepr(builder, child);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    AppendRepr(builder, array[i]);
                }

                builder.Append(']');
                break;
            default:
                builder.Append(PyRepr.Object(PyJson.Scalar(value)));
                break;
        }
    }

    /// <summary>Raises Python's <c>TypeError</c> when a value cannot be a set member or dict key.</summary>
    public static void EnsureHashable(JsonNode? value)
    {
        if (value is JsonObject or JsonArray)
        {
            throw new PyTypeError("unhashable type: '" + PyJson.TypeName(value) + "'");
        }
    }

    /// <summary><c>value in {"a", "b"}</c>: unhashable values raise <c>TypeError</c>; only strings can be members.</summary>
    public static bool In(JsonNode? value, IReadOnlySet<string> set)
    {
        EnsureHashable(value);
        return value is JsonValue && PyJson.Scalar(value) is string text && set.Contains(text);
    }

    /// <summary><c>value in list</c> with Python equality.</summary>
    public static bool InList(JsonNode? value, JsonArray list) => list.Any(item => PyJson.PyEquals(item, value));

    /// <summary>The string behind a node, or <see langword="null"/> when it is not a <c>str</c>.</summary>
    public static string? AsString(JsonNode? value) => value is JsonValue && PyJson.Scalar(value) is string text ? text : null;

    /// <summary><c>isinstance(value, str) and value.strip()</c>.</summary>
    public static bool IsText(JsonNode? value) => AsString(value) is { } text && PyRepr.Strip(text).Length > 0;

    /// <summary>A JSON string node.</summary>
    public static JsonValue S(string value) => JsonValue.Create(value);

    /// <summary>A Python int node.</summary>
    public static JsonValue I(long value) => JsonValue.Create(value);

    /// <summary>A JSON value read from SQLite (TEXT, INTEGER, REAL or NULL).</summary>
    public static JsonNode? FromDb(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        long integer => JsonValue.Create(integer),
        double number => PyJson.Float(number),
        byte[] => throw new PyTypeError("Object of type bytes is not JSON serializable"),
        _ => throw new PyTypeError("unsupported SQLite value " + value.GetType().Name),
    };

    /// <summary>Python <c>==</c> between a value read from SQLite and a JSON value.</summary>
    public static bool Equal(object? stored, JsonNode? value) => stored is byte[] ? false : PyJson.PyEquals(FromDb(stored), value);

    /// <summary>Python <c>==</c> between two values read from SQLite.</summary>
    public static bool Equal(object? left, object? right) => PyConnection.ValueEquals(left, right);

    /// <summary>Python <c>str &lt; str</c> ordering (code points).</summary>
    public static int Compare(string left, string right) => PyRepr.CompareStrings(left, right);

    /// <summary><c>sorted(strings)</c>.</summary>
    public static List<string> Sorted(IEnumerable<string> values)
    {
        var list = values.ToList();
        list.Sort(PyRepr.StringComparer);
        return list;
    }

    /// <summary>A JSON array of strings.</summary>
    public static JsonArray StrArray(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    /// <summary>
    /// <c>dict(source, **updates)</c> where <paramref name="source"/> may be any JSON value: a dict, or an iterable of
    /// pairs (lists, two-character strings, two-key dicts). Errors follow CPython 3.13. A pair with a non-string
    /// key cannot be held by a JSON object; Python's dict accepts it but every later <c>canonical_json</c> fails
    /// (mixed key types cannot be sorted), so it raises that <see cref="StateError"/> here.
    /// </summary>
    public static JsonObject DictWith(JsonNode? source, params (string Key, JsonNode? Value)[] updates)
    {
        var result = new JsonObject();
        switch (source)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    result[key] = Clone(value);
                }

                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var pair = Elements(array[index], index);
                    if (pair.Count != 2)
                    {
                        throw new PyValueError("dictionary update sequence element #" + index.ToString(CultureInfo.InvariantCulture)
                                               + " has length " + pair.Count.ToString(CultureInfo.InvariantCulture) + "; 2 is required");
                    }

                    EnsureHashable(pair[0]);
                    if (AsString(pair[0]) is not { } key)
                    {
                        throw new StateError("value must contain finite JSON data");
                    }

                    result[key] = Clone(pair[1]);
                }

                break;
            case JsonValue value when PyJson.Scalar(value) is string text:
                var points = PyRepr.CodePoints(text).ToList();
                if (points.Count > 0)
                {
                    throw new PyValueError("dictionary update sequence element #0 has length 1; 2 is required");
                }

                break;
            case null:
                throw new PyTypeError("'NoneType' object is not iterable");
            default:
                throw new PyTypeError("'" + PyJson.TypeName(source) + "' object is not iterable");
        }

        foreach (var (key, value) in updates)
        {
            result[key] = value?.Parent is null ? value : Clone(value);
        }

        return result;
    }

    private static List<JsonNode?> Elements(JsonNode? element, int index)
    {
        switch (element)
        {
            case JsonArray array:
                return array.ToList();
            case JsonObject obj:
                return obj.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToList();
            case JsonValue value when PyJson.Scalar(value) is string text:
                return PyRepr.CodePoints(text).Select(point => (JsonNode?)JsonValue.Create(CodePointString(point))).ToList();
            default:
                throw new PyTypeError("cannot convert dictionary update sequence element #" + index.ToString(CultureInfo.InvariantCulture)
                                      + " to a sequence");
        }
    }

    /// <summary>One code point as a string (lone surrogates kept).</summary>
    public static string CodePointString(int point) => point is >= 0xD800 and <= 0xDFFF ? ((char)point).ToString() : char.ConvertFromUtf32(point);

    /// <summary>The code points of a string (lone surrogates are their own code points, as in Python).</summary>
    public static int[] Points(string value) => PyRepr.CodePoints(value).ToArray();

    /// <summary><c>str.isdigit()</c>: nonempty and every character has a digit value (Unicode 15.1).</summary>
    public static bool IsDigit(string value)
    {
        var points = Points(value);
        return points.Length > 0 && points.All(point => InRanges(MemoryUnicodeData.DigitRanges, point));
    }

    /// <summary><c>re</c>'s <c>\w</c> for one code point.</summary>
    public static bool IsWord(int point) => InRanges(MemoryUnicodeData.WordRanges, point);

    /// <summary><c>re</c>'s <c>\s</c> (<c>str.isspace()</c>) for one code point.</summary>
    public static bool IsSpace(int point) => InRanges(MemoryUnicodeData.SpaceRanges, point);

    private static bool InRanges(int[] ranges, int point)
    {
        int low = 0, high = ranges.Length / 2 - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (point < ranges[middle * 2])
            {
                high = middle - 1;
            }
            else if (point > ranges[middle * 2 + 1])
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
}
