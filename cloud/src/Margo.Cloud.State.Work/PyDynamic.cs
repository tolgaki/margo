using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Work;

/// <summary>
/// Python <c>AttributeError</c>, for example <c>'list' object has no attribute 'get'</c>. The work scripts never
/// catch it, so it escapes as an uncaught exception (exit status 1), as in Python. (Belongs in State.Core.)
/// </summary>
public sealed class PyAttributeError : PyException
{
    public PyAttributeError(string message) : base(message)
    {
    }

    public override string PythonType => "AttributeError";
}

/// <summary>
/// Python's dynamic semantics over JSON values (<see cref="JsonNode"/>), for code that receives model-supplied
/// JSON and relies on Python behaviour: <c>str()</c>/<c>repr()</c>, iteration (a string yields characters, a
/// dict its keys), membership in a set of strings (lists and dicts are unhashable), subscripting,
/// <c>dict.update</c> and numeric ordering between ints and floats. (These belong in State.Core.)
/// </summary>
public static class PyDynamic
{
    /// <summary>
    /// A detached copy (a <see cref="JsonNode"/> can only have one parent). Unlike <see cref="JsonNode.DeepClone"/>,
    /// it keeps ints beyond 64 bits as ints (System.Text.Json would serialize a <see cref="BigInteger"/> as an
    /// object) and does not recurse, so the deepest JSON <see cref="PyJson"/> accepts is safe.
    /// </summary>
    public static JsonNode? Clone(JsonNode? node)
    {
        if (node is not (JsonObject or JsonArray))
        {
            return CloneScalar(node);
        }

        JsonNode root = node is JsonObject ? new JsonObject() : new JsonArray();
        var pending = new Stack<(JsonNode Source, JsonNode Target)>();
        pending.Push((node, root));
        while (pending.Count > 0)
        {
            var (source, target) = pending.Pop();
            if (source is JsonObject sourceObject)
            {
                var targetObject = (JsonObject)target;
                foreach (var (key, value) in sourceObject)
                {
                    targetObject[key] = Child(value, pending);
                }
            }
            else
            {
                var targetArray = (JsonArray)target;
                foreach (var value in (JsonArray)source)
                {
                    targetArray.Add(Child(value, pending));
                }
            }
        }

        return root;
    }

    /// <summary><see cref="Clone(JsonNode?)"/> of an object.</summary>
    public static JsonObject CloneObject(JsonNode? node) => (JsonObject)Clone(node ?? throw new ArgumentNullException(nameof(node)))!;

    private static JsonNode? Child(JsonNode? value, Stack<(JsonNode Source, JsonNode Target)> pending)
    {
        if (value is not (JsonObject or JsonArray))
        {
            return CloneScalar(value);
        }

        JsonNode child = value is JsonObject ? new JsonObject() : new JsonArray();
        pending.Push((value, child));
        return child;
    }

    private static JsonNode? CloneScalar(JsonNode? node) => node is null ? null : PyJson.Scalar(node) switch
    {
        null => null,
        bool flag => JsonValue.Create(flag),
        string text => JsonValue.Create(text),
        BigInteger integer => PyJson.Int(integer),
        double number => JsonValue.Create(number),
        _ => node.DeepClone(),
    };

    /// <summary>A Python <c>str</c> value.</summary>
    public static JsonValue Str(string value) => JsonValue.Create(value);

    /// <summary>A Python <c>int</c> value.</summary>
    public static JsonValue Int(long value) => JsonValue.Create(value);

    /// <summary>The string behind a <c>str</c> node, or null for any other type.</summary>
    public static string? AsString(JsonNode? node) => node is JsonValue value && PyJson.Scalar(value) is string text ? text : null;

    /// <summary><c>str(x)</c>.</summary>
    public static string ToStr(JsonNode? node) => AsString(node) ?? Repr(node);

    /// <summary><c>repr(x)</c> for JSON values (dicts and lists in Python's literal syntax).</summary>
    public static string Repr(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "None";
            case JsonObject obj:
                return "{" + string.Join(", ", obj.Select(pair => PyRepr.Str(pair.Key) + ": " + Repr(pair.Value))) + "}";
            case JsonArray array:
                return "[" + string.Join(", ", array.Select(Repr)) + "]";
            default:
                return PyJson.Scalar(node) switch
                {
                    null => "None",
                    bool flag => flag ? "True" : "False",
                    string text => PyRepr.Str(text),
                    BigInteger integer => integer.ToString(CultureInfo.InvariantCulture),
                    double number => PyJson.FloatRepr(number),
                    var other => other.ToString() ?? "None",
                };
        }
    }

    /// <summary><c>type(x).__name__</c>.</summary>
    public static string TypeName(JsonNode? node) => PyJson.TypeName(node);

    /// <summary><c>x == "text"</c>.</summary>
    public static bool Is(JsonNode? node, string text) => AsString(node) is { } value && string.Equals(value, text, StringComparison.Ordinal);

    /// <summary><c>x in {"a", "b"}</c>: lists and dicts are unhashable (<c>TypeError</c>).</summary>
    public static bool In(JsonNode? node, IReadOnlySet<string> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        RequireHashable(node);
        return AsString(node) is { } text && set.Contains(text);
    }

    /// <summary>Raises <c>TypeError: unhashable type: 'list'</c> (or <c>'dict'</c>) as <c>hash(x)</c> does.</summary>
    public static void RequireHashable(JsonNode? node)
    {
        if (node is JsonArray or JsonObject)
        {
            throw new PyTypeError("unhashable type: '" + TypeName(node) + "'");
        }
    }

    /// <summary><c>for x in value</c>: list items, dict keys, string characters; other types raise <c>TypeError</c>.</summary>
    public static List<JsonNode?> Iter(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                return [.. array];
            case JsonObject obj:
                return [.. obj.Select(pair => (JsonNode?)Str(pair.Key))];
            default:
                if (AsString(node) is { } text)
                {
                    var result = new List<JsonNode?>();
                    foreach (var codePoint in PyRepr.CodePoints(text))
                    {
                        result.Add(Str(IsSurrogate(codePoint) ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint)));
                    }

                    return result;
                }

                throw new PyTypeError("'" + TypeName(node) + "' object is not iterable");
        }
    }

    /// <summary>
    /// <c>x.get(...)</c> receiver: the dict itself, or <c>AttributeError: 'list' object has no attribute 'get'</c>.
    /// </summary>
    public static JsonObject Dict(JsonNode? node, string attribute = "get") =>
        node as JsonObject ?? throw new PyAttributeError("'" + TypeName(node) + "' object has no attribute '" + attribute + "'");

    /// <summary><c>d.get(key, fallback)</c>.</summary>
    public static JsonNode? Get(JsonObject obj, string key, JsonNode? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return obj.TryGetPropertyValue(key, out var value) ? value : fallback;
    }

    /// <summary><c>x[key]</c> with a string key: <c>KeyError</c> for a missing key, <c>TypeError</c> for non-dicts.</summary>
    public static JsonNode? Index(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject obj:
                return obj.TryGetPropertyValue(key, out var value) ? value : throw new PyKeyError(key);
            case JsonArray:
                throw new PyTypeError("list indices must be integers or slices, not str");
            default:
                if (AsString(node) is not null)
                {
                    throw new PyTypeError("string indices must be integers, not 'str'");
                }

                throw new PyTypeError("'" + TypeName(node) + "' object is not subscriptable");
        }
    }

    /// <summary>Python truthiness.</summary>
    public static bool Truthy(JsonNode? node) => PyJson.Truthy(node);

    /// <summary>Python <c>==</c>.</summary>
    public static bool Eq(JsonNode? left, JsonNode? right) => PyJson.PyEquals(left, right);

    /// <summary><c>isinstance(x, (int, float))</c> (bool included, as in Python).</summary>
    public static bool IsNumber(JsonNode? node) => node is JsonValue && PyJson.KindOf(node) is PyKind.Int or PyKind.Float or PyKind.Bool;

    /// <summary>
    /// Three-way comparison of two Python numbers (int, float or bool), exact across int and float like CPython
    /// (no rounding of large ints). Non-numbers raise <c>TypeError</c> with Python's text for <paramref name="op"/>.
    /// </summary>
    public static int CompareNumbers(JsonNode? left, JsonNode? right, string op = "<")
    {
        if (!IsNumber(left) || !IsNumber(right))
        {
            throw new PyTypeError("'" + op + "' not supported between instances of '" + TypeName(left) + "' and '" + TypeName(right) + "'");
        }

        var a = PyJson.Scalar(left);
        var b = PyJson.Scalar(right);
        return (a, b) switch
        {
            (double x, double y) => x.CompareTo(y),
            (double x, _) => -CompareIntFloat(Integer(b), x),
            (_, double y) => CompareIntFloat(Integer(a), y),
            _ => Integer(a).CompareTo(Integer(b)),
        };
    }

    private static BigInteger Integer(object? scalar) => scalar switch
    {
        BigInteger integer => integer,
        bool flag => flag ? BigInteger.One : BigInteger.Zero,
        _ => throw new PyTypeError("not an int"),
    };

    private static int CompareIntFloat(BigInteger integer, double number)
    {
        if (double.IsNaN(number))
        {
            return 1; // every ordering comparison with NaN is false; callers only use parsed finite numbers
        }

        if (double.IsPositiveInfinity(number))
        {
            return -1;
        }

        if (double.IsNegativeInfinity(number))
        {
            return 1;
        }

        var floor = Math.Floor(number);
        var whole = new BigInteger(floor);
        var comparison = integer.CompareTo(whole);
        if (comparison != 0)
        {
            return comparison;
        }

        return floor == number ? 0 : -1;
    }

    /// <summary>
    /// <c>target.update(other)</c> with <c>other</c> a JSON value: a dict merges; any other iterable is a sequence
    /// of key/value pairs (a 2-item list, a 2-character string or a 2-key dict); otherwise Python's
    /// <c>TypeError</c>/<c>ValueError</c>. Keys that are not strings cannot live in a <see cref="JsonObject"/>;
    /// they are returned (Python dict-key equality, first object kept) so the caller can reproduce what Python
    /// does next with them.
    /// </summary>
    public static List<JsonNode?> Update(JsonObject target, JsonNode? other)
    {
        ArgumentNullException.ThrowIfNull(target);
        var foreign = new List<JsonNode?>();
        if (other is JsonObject source)
        {
            foreach (var (key, value) in source.ToList())
            {
                target[key] = Clone(value);
            }

            return foreign;
        }

        if (other is not JsonArray && AsString(other) is null)
        {
            throw new PyTypeError("'" + TypeName(other) + "' object is not iterable");
        }

        var index = 0;
        foreach (var element in Iter(other))
        {
            List<JsonNode?> pair = element switch
            {
                JsonArray array => [.. array],
                JsonObject obj => Iter(obj),
                _ when AsString(element) is not null => Iter(element),
                _ => throw new PyTypeError("cannot convert dictionary update sequence element #"
                                           + index.ToString(CultureInfo.InvariantCulture) + " to a sequence"),
            };
            if (pair.Count != 2)
            {
                throw new PyValueError("dictionary update sequence element #" + index.ToString(CultureInfo.InvariantCulture)
                                       + " has length " + pair.Count.ToString(CultureInfo.InvariantCulture) + "; 2 is required");
            }

            var key = pair[0];
            RequireHashable(key);
            if (AsString(key) is { } name)
            {
                target[name] = Clone(pair[1]);
            }
            else if (!foreign.Any(existing => Eq(existing, key)))
            {
                foreign.Add(Clone(key));
            }

            index++;
        }

        return foreign;
    }

    /// <summary><c>", ".join(sorted(strings))</c> with code-point ordering.</summary>
    public static string JoinSorted(IEnumerable<string> values) =>
        string.Join(", ", values.Order(PyRepr.StringComparer));

    /// <summary>
    /// What <c>", ".join(sorted(set(data) - allowed))</c> raises when the unknown keys of <c>data</c> include
    /// non-string keys (only reachable through <c>dict.update</c> with a list of pairs): <paramref name="strings"/>
    /// are the unknown string keys, <paramref name="foreign"/> the non-string keys in insertion order,
    /// <paramref name="dictSize"/> the number of keys in <c>data</c> and <paramref name="allowedSize"/> the size
    /// of <c>allowed</c>. The text follows CPython's set iteration order and sort comparisons
    /// (<see cref="PySortedKeys"/>); only the slots of string keys depend on <c>PYTHONHASHSEED</c>.
    /// </summary>
    public static PyTypeError JoinSortedError(IReadOnlyCollection<string> strings, IReadOnlyList<JsonNode?> foreign,
                                              int dictSize, int allowedSize)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(foreign);
        return PySortedKeys.JoinSortedError(strings, foreign, dictSize, allowedSize);
    }

    private static bool IsSurrogate(int codePoint) => codePoint is >= 0xD800 and <= 0xDFFF;
}
