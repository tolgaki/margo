using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Margo.Cloud.State.Core;

/// <summary>
/// Python's dynamic semantics on JSON values (<see cref="JsonNode"/>, the <see cref="PyJson"/> value model), for code
/// that receives model-supplied JSON and relies on Python behaviour: copies, <c>str()</c>/<c>repr()</c>, iteration (a
/// string yields characters, a dict its keys), membership with unhashable lists and dicts, subscripting,
/// <c>dict.get</c>/<c>dict.pop</c>/<c>dict.update</c>, <c>dict(x, **kw)</c>, and SQLite values as <c>dict(row)</c> holds
/// them. Operations on any Python value (CLR scalars, bytes, lists) are in <see cref="PyObj"/>; numbers in
/// <see cref="PyNumbers"/>.
/// </summary>
public static class PyNode
{
    // ------------------------------------------------------------------------------------ construction and copies

    /// <summary>A Python <c>str</c> value.</summary>
    public static JsonValue Of(string value) => JsonValue.Create(value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>A Python <c>int</c> value.</summary>
    public static JsonValue Of(long value) => JsonValue.Create(value);

    /// <summary>A Python <c>bool</c> value.</summary>
    public static JsonValue Of(bool value) => JsonValue.Create(value);

    /// <summary>A list of <c>str</c> values.</summary>
    public static JsonArray StrList(IEnumerable<string> values) =>
        new((values ?? throw new ArgumentNullException(nameof(values))).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    /// <summary>
    /// A detached deep copy of a Python value (a <see cref="JsonNode"/> can only have one parent). This is the one copy
    /// of Python values the state ports use: <see cref="JsonNode.DeepClone"/> re-serializes a <see cref="BigInteger"/>
    /// value (any Python int beyond 64 bits, which <see cref="PyJson.Loads(string)"/> produces) through System.Text.Json
    /// reflection as <c>{"IsEven":true,"IsOne":false,...}</c>, silently changing its type. The copy is iterative, so the
    /// deepest JSON <see cref="PyJson"/> accepts is safe, and scalars are rebuilt from their Python value.
    /// </summary>
    public static JsonNode? Copy(JsonNode? node)
    {
        if (node is not (JsonObject or JsonArray))
        {
            return CopyScalar(node);
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

    /// <summary>
    /// <see cref="Copy(JsonNode?)"/> of a value the caller knows to be a dict (<c>dict(value)</c> with its nested values
    /// copied too); anything else is an <see cref="InvalidCastException"/>.
    /// </summary>
    public static JsonObject CopyObject(JsonNode? node) => (JsonObject)Copy(node ?? throw new ArgumentNullException(nameof(node)))!;

    private static JsonNode? Child(JsonNode? value, Stack<(JsonNode Source, JsonNode Target)> pending)
    {
        if (value is not (JsonObject or JsonArray))
        {
            return CopyScalar(value);
        }

        JsonNode child = value is JsonObject ? new JsonObject() : new JsonArray();
        pending.Push((value, child));
        return child;
    }

    private static JsonValue? CopyScalar(JsonNode? node) => PyJson.Scalar(node) switch
    {
        null => null,
        bool flag => JsonValue.Create(flag),
        string text => JsonValue.Create(text),
        BigInteger integer => PyJson.Int(integer),
        double number => JsonValue.Create(number),
        var other => throw new PyTypeError("Object of type " + other.GetType().Name + " is not JSON serializable"),
    };

    /// <summary><c>target[key] = value</c>, copying a value that already belongs to another container.</summary>
    public static void Set(JsonObject target, string key, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        target[key] = value?.Parent is null ? value : Copy(value);
    }

    // ------------------------------------------------------------------------------------ types

    /// <summary><c>type(x).__name__</c>.</summary>
    public static string TypeName(JsonNode? node) => PyJson.TypeName(node);

    /// <summary>The string behind a <c>str</c> node, or null for any other type.</summary>
    public static string? AsString(JsonNode? node) => node is JsonValue value && PyJson.Scalar(value) is string text ? text : null;

    /// <summary><c>isinstance(x, str)</c>, returning the text.</summary>
    public static bool IsStr(JsonNode? node, out string text)
    {
        if (AsString(node) is { } value)
        {
            text = value;
            return true;
        }

        text = "";
        return false;
    }

    /// <summary><c>isinstance(x, str) and x.strip()</c>.</summary>
    public static bool IsText(JsonNode? node) => AsString(node) is { } text && PyRepr.Strip(text).Length > 0;

    /// <summary><c>x is None</c>.</summary>
    public static bool IsNone(JsonNode? node) => node is null || (node is JsonValue && PyJson.KindOf(node) == PyKind.None);

    /// <summary><c>x == "text"</c> (only a <c>str</c> can equal a <c>str</c>).</summary>
    public static bool Is(JsonNode? node, string text) => AsString(node) is { } value && string.Equals(value, text, StringComparison.Ordinal);

    /// <summary><c>x in ("a", "b", ...)</c> for a tuple of strings.</summary>
    public static bool IsOneOf(JsonNode? node, params string[] options) =>
        AsString(node) is { } value && options.Contains(value, StringComparer.Ordinal);

    /// <summary><c>isinstance(x, (int, float))</c> (bool included, as in Python).</summary>
    public static bool IsNumber(JsonNode? node) => node is JsonValue && PyJson.KindOf(node) is PyKind.Int or PyKind.Float or PyKind.Bool;

    // ------------------------------------------------------------------------------------ str() and repr()

    /// <summary><c>str(x)</c>: a string is itself, anything else its <see cref="Repr"/>.</summary>
    public static string Str(JsonNode? node) => AsString(node) ?? Repr(node);

    /// <summary><c>repr(x)</c> for JSON values (dicts as <c>{'key': value}</c>, lists, None/True/False, numbers).</summary>
    public static string Repr(JsonNode? node)
    {
        var builder = new StringBuilder();
        AppendRepr(builder, node);
        return builder.ToString();
    }

    private static void AppendRepr(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    builder.Append(PyRepr.Str(key)).Append(": ");
                    AppendRepr(builder, value);
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
                builder.Append(PyJson.Scalar(node) switch
                {
                    null => "None",
                    bool flag => flag ? "True" : "False",
                    string text => PyRepr.Str(text),
                    BigInteger integer => PyJson.IntStr(integer),
                    double number => PyJson.FloatRepr(number),
                    var other => Convert.ToString(other, CultureInfo.InvariantCulture),
                });
                break;
        }
    }

    // ------------------------------------------------------------------------------------ hashing and membership

    /// <summary>Raises <c>TypeError: unhashable type: 'list'</c> (or <c>'dict'</c>) as <c>hash(x)</c> does.</summary>
    public static void RequireHashable(JsonNode? node)
    {
        if (node is JsonArray or JsonObject)
        {
            throw new PyTypeError("unhashable type: '" + TypeName(node) + "'");
        }
    }

    /// <summary><c>x in {"a", "b"}</c>: lists and dicts are unhashable (<c>TypeError</c>); only strings can be members.</summary>
    public static bool In(JsonNode? node, IReadOnlySet<string> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        RequireHashable(node);
        return AsString(node) is { } text && set.Contains(text);
    }

    /// <summary><c>x in some_list</c> with Python equality.</summary>
    public static bool InList(JsonNode? node, JsonArray list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list.Any(item => PyJson.PyEquals(item, node));
    }

    // ------------------------------------------------------------------------------------ containers

    /// <summary><c>for x in value</c>: list items, dict keys, string characters; other types raise <c>TypeError</c>.</summary>
    public static List<JsonNode?> Iter(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                return [.. array];
            case JsonObject obj:
                return [.. obj.Select(pair => (JsonNode?)Of(pair.Key))];
            default:
                if (AsString(node) is { } text)
                {
                    return [.. PyRepr.CodePoints(text).Select(codePoint => (JsonNode?)Of(PyText.CodePointString(codePoint)))];
                }

                throw new PyTypeError("'" + TypeName(node) + "' object is not iterable");
        }
    }

    /// <summary>
    /// The receiver of <c>x.get(...)</c> (or another dict method named <paramref name="attribute"/>): the dict itself, or
    /// <c>AttributeError: 'list' object has no attribute 'get'</c>.
    /// </summary>
    public static JsonObject Dict(JsonNode? node, string attribute = "get") =>
        node as JsonObject ?? throw new PyAttributeError("'" + TypeName(node) + "' object has no attribute '" + attribute + "'");

    /// <summary><c>d.get(key)</c>, or <c>d.get(key, fallback)</c>: the fallback only when the key is absent (a present None stays None).</summary>
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

    /// <summary><c>x.pop(key, None)</c>, with Python's errors for non-dicts.</summary>
    public static void Pop(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove(key);
                break;
            case JsonArray:
                throw new PyTypeError("pop expected at most 1 argument, got 2");
            default:
                throw new PyAttributeError("'" + TypeName(node) + "' object has no attribute 'pop'");
        }
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
                target[key] = Copy(value);
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
                target[name] = Copy(pair[1]);
            }
            else if (!foreign.Any(existing => PyJson.PyEquals(existing, key)))
            {
                foreign.Add(Copy(key));
            }

            index++;
        }

        return foreign;
    }

    /// <summary>
    /// <c>dict(source, **updates)</c> where <paramref name="source"/> may be any JSON value: a dict, or an iterable of
    /// pairs (see <see cref="Update"/>). A pair with a key that is not a string cannot be held by a JSON object; Python's
    /// dict accepts it, but <c>canonical_json</c> (which every caller applies to the result before anything else can
    /// observe it) then fails, so it raises that <see cref="StateError"/> once the whole source has been read.
    /// </summary>
    public static JsonObject DictWith(JsonNode? source, params (string Key, JsonNode? Value)[] updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var result = new JsonObject();
        if (source is null)
        {
            throw new PyTypeError("'NoneType' object is not iterable");
        }

        if (Update(result, source).Count > 0)
        {
            throw new StateError("value must contain finite JSON data");
        }

        foreach (var (key, value) in updates)
        {
            Set(result, key, value);
        }

        return result;
    }

    /// <summary><c>", ".join(sorted(strings))</c> with code-point ordering.</summary>
    public static string JoinSorted(IEnumerable<string> values) => string.Join(", ", PyText.Sorted(values));

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

    // ------------------------------------------------------------------------------------ SQLite values

    /// <summary>
    /// A value read from SQLite as <c>dict(row)</c> holds it: INTEGER, REAL and TEXT become int, float and str nodes;
    /// a BLOB (bytes) cannot become JSON and raises the <c>TypeError</c> <c>json.dumps</c> would.
    /// </summary>
    public static JsonNode? FromSql(object? value) => value switch
    {
        null => null,
        long integer => JsonValue.Create(integer),
        double number => JsonValue.Create(number),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        BigInteger big => PyJson.Int(big),
        JsonNode node => Copy(node),
        byte[] => throw new PyTypeError("Object of type bytes is not JSON serializable"),
        _ => throw new PyTypeError("Object of type " + value.GetType().Name + " is not JSON serializable"),
    };

    /// <summary><c>dict(row)</c>.</summary>
    public static JsonObject FromRow(PyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var result = new JsonObject();
        foreach (var (key, value) in row.ToDict())
        {
            result[key] = FromSql(value);
        }

        return result;
    }

    /// <summary><c>json.loads(value)</c> for a value read from SQLite (str, or UTF-8 bytes); other types raise <c>TypeError</c>.</summary>
    public static JsonNode? Loads(object? value) => value switch
    {
        string text => PyJson.Loads(text),
        byte[] bytes => PyJson.Loads(PyIO.DecodeUtf8(bytes)),
        _ => throw new PyTypeError("the JSON object must be str, bytes or bytearray, not " + PyObj.TypeName(value)),
    };
}
