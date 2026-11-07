using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Helpers.Python;

/// <summary>
/// Python's dynamic operations on values that came from <c>json.loads</c>, as the stateless helpers apply
/// them to untrusted input: <c>x.get(k)</c>, truthiness, <c>x[:n]</c>, <c>len</c>, <c>in</c>, iteration,
/// <c>==</c>, <c>&lt;</c>, <c>str()</c>/<c>repr()</c>, <c>int()</c> and hashing, each raising the exception
/// class CPython 3.13 raises for the wrong type.
/// <para>Values are <c>null</c> (None), <see cref="bool"/>, <see cref="BigInteger"/> (or another CLR integer),
/// <see cref="double"/>, <see cref="string"/>, <see cref="JsonNode"/> (as <see cref="PyJson"/> maps it) and
/// lists built by the helpers (<see cref="IReadOnlyList{T}"/> of <see cref="object"/>).</para>
/// <para>Identity: every NaN that <c>json.loads</c> produces is the same object
/// (<c>json.decoder.NaN</c>), so containment, container equality and dict keys treat NaN as identical to
/// NaN while <c>==</c> itself is false, as in CPython.</para>
/// </summary>
public static class PyObj
{
    /// <summary>
    /// The canonical form of a value: <c>null</c>, <see cref="bool"/>, <see cref="BigInteger"/>,
    /// <see cref="double"/>, <see cref="string"/>, <see cref="JsonArray"/>, <see cref="JsonObject"/> or an
    /// <see cref="IReadOnlyList{T}"/>.
    /// </summary>
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        JsonValue scalar => PyJson.Scalar(scalar),
        JsonArray or JsonObject => value,
        bool or BigInteger or double or string => value,
        long number => new BigInteger(number),
        int number => new BigInteger(number),
        float number => (double)number,
        IReadOnlyList<object?> => value,
        _ => throw new ArgumentException("not a Python JSON value: " + value.GetType().Name),
    };

    /// <summary>The Python type of a value.</summary>
    public static PyKind Kind(object? value) => Normalize(value) switch
    {
        null => PyKind.None,
        bool => PyKind.Bool,
        BigInteger => PyKind.Int,
        double => PyKind.Float,
        string => PyKind.Str,
        JsonObject => PyKind.Dict,
        _ => PyKind.List,
    };

    /// <summary><c>type(x).__name__</c>.</summary>
    public static string TypeName(object? value) => Kind(value) switch
    {
        PyKind.None => "NoneType",
        PyKind.Bool => "bool",
        PyKind.Int => "int",
        PyKind.Float => "float",
        PyKind.Str => "str",
        PyKind.List => "list",
        _ => "dict",
    };

    /// <summary><c>bool(x)</c>.</summary>
    public static bool Truthy(object? value) => Normalize(value) switch
    {
        null => false,
        bool flag => flag,
        BigInteger integer => !integer.IsZero,
        double number => number != 0,
        string text => text.Length > 0,
        JsonObject dict => dict.Count > 0,
        JsonArray array => array.Count > 0,
        IReadOnlyList<object?> list => list.Count > 0,
        _ => true,
    };

    /// <summary><c>a or b</c>: <paramref name="value"/> when truthy, otherwise <paramref name="fallback"/>.</summary>
    public static object? Or(object? value, object? fallback) => Truthy(value) ? value : fallback;

    /// <summary>The items of a list value.</summary>
    public static IReadOnlyList<object?> Items(object? value) => Normalize(value) switch
    {
        JsonArray array => new JsonArrayView(array),
        IReadOnlyList<object?> list => list,
        _ => throw new PyTypeError("'" + TypeName(value) + "' object is not a list"),
    };

    /// <summary><c>x.get(key, default)</c>: <c>AttributeError</c> unless <paramref name="value"/> is a dict.</summary>
    public static object? Get(object? value, string key, object? fallback = null)
    {
        if (value is JsonObject dict)
        {
            return dict.TryGetPropertyValue(key, out var item) ? item : fallback;
        }

        throw new PyAttributeError("'" + TypeName(value) + "' object has no attribute 'get'");
    }

    /// <summary><c>x[key]</c> for a string key.</summary>
    public static object? Index(object? value, string key)
    {
        switch (Normalize(value))
        {
            case JsonObject dict:
                return dict.TryGetPropertyValue(key, out var item) ? item : throw new PyKeyError(key);
            case JsonArray or IReadOnlyList<object?>:
                throw new PyTypeError("list indices must be integers or slices, not str");
            case string:
                throw new PyTypeError("string indices must be integers, not 'str'");
            default:
                throw new PyTypeError("'" + TypeName(value) + "' object is not subscriptable");
        }
    }

    /// <summary><c>x[:stop]</c> (stop &gt;= 0): a code-point prefix of a str, a new list, or the error.</summary>
    public static object? Slice(object? value, int stop)
    {
        switch (Normalize(value))
        {
            case string text:
                return PyText.Prefix(text, stop);
            case JsonArray or IReadOnlyList<object?>:
                var items = Items(value);
                var result = new List<object?>(Math.Min(stop, items.Count));
                for (var i = 0; i < items.Count && i < stop; i++)
                {
                    result.Add(items[i]);
                }

                return result;
            case JsonObject:
                // Slices are hashable since 3.12, so a dict looks the slice up as a key.
                throw new PyKeyErrorRepr("slice(None, " + stop.ToString(CultureInfo.InvariantCulture) + ", None)");
            default:
                throw new PyTypeError("'" + TypeName(value) + "' object is not subscriptable");
        }
    }

    /// <summary><c>len(x)</c>.</summary>
    public static int Len(object? value) => Normalize(value) switch
    {
        string text => PyRepr.Length(text),
        JsonObject dict => dict.Count,
        JsonArray array => array.Count,
        IReadOnlyList<object?> list => list.Count,
        _ => throw new PyTypeError("object of type '" + TypeName(value) + "' has no len()"),
    };

    /// <summary><c>item in container</c>.</summary>
    public static bool Contains(object? container, object? item)
    {
        switch (Normalize(container))
        {
            case JsonObject dict:
                var key = PyKey.Of(item);
                return key.Str is { } name && key.Tag == PyKey.StrTag && dict.ContainsKey(name);
            case JsonArray or IReadOnlyList<object?>:
                foreach (var element in Items(container))
                {
                    if (EqBool(element, item))
                    {
                        return true;
                    }
                }

                return false;
            case string text:
                if (Normalize(item) is string needle)
                {
                    return text.Contains(needle, StringComparison.Ordinal);
                }

                throw new PyTypeError("'in <string>' requires string as left operand, not " + TypeName(item));
            default:
                throw new PyTypeError("argument of type '" + TypeName(container) + "' is not iterable");
        }
    }

    /// <summary><c>for x in value</c>: list items, dict keys, or the code points of a str.</summary>
    public static IEnumerable<object?> Iterate(object? value)
    {
        switch (Normalize(value))
        {
            case JsonObject dict:
                return dict.Select(pair => (object?)pair.Key).ToList();
            case JsonArray or IReadOnlyList<object?>:
                return Items(value);
            case string text:
                return PyText.CodePointStrings(text);
            default:
                throw new PyTypeError("'" + TypeName(value) + "' object is not iterable");
        }
    }

    /// <summary><c>a == b</c> (<c>PyObject_RichCompare(a, b, Py_EQ)</c>): NaN is unequal to itself here.</summary>
    public static bool Eq(object? left, object? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        switch (a)
        {
            case null:
                return b is null;
            case string sa:
                return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
            case bool or BigInteger or double:
                return b is bool or BigInteger or double && PyNumbers.Compare(a, b) == 0;
            case JsonObject da:
                if (b is not JsonObject db || da.Count != db.Count)
                {
                    return false;
                }

                foreach (var (key, value) in da)
                {
                    if (!db.TryGetPropertyValue(key, out var other) || !EqBool(value, other))
                    {
                        return false;
                    }
                }

                return true;
            default:
                if (b is not (JsonArray or IReadOnlyList<object?>))
                {
                    return false;
                }

                var la = Items(a);
                var lb = Items(b);
                if (la.Count != lb.Count)
                {
                    return false;
                }

                for (var i = 0; i < la.Count; i++)
                {
                    if (!EqBool(la[i], lb[i]))
                    {
                        return false;
                    }
                }

                return true;
        }
    }

    /// <summary>
    /// <c>PyObject_RichCompareBool(a, b, Py_EQ)</c>, used by <c>in</c>, container equality and dict lookups:
    /// identical objects are equal (so the shared JSON NaN equals itself).
    /// </summary>
    public static bool EqBool(object? left, object? right)
    {
        if (ReferenceEquals(left, right) && left is not null)
        {
            return true;
        }

        var a = Normalize(left);
        var b = Normalize(right);
        if (a is double x && b is double y && double.IsNaN(x) && double.IsNaN(y))
        {
            return true;
        }

        return Eq(a, b);
    }

    /// <summary><c>a != b</c>.</summary>
    public static bool Ne(object? left, object? right) => !Eq(left, right);

    /// <summary><c>a &lt; b</c>; <c>TypeError</c> for unorderable types.</summary>
    public static bool Lt(object? left, object? right) => Compare(left, right, CompareOp.Lt, "<");

    /// <summary><c>a &lt;= b</c>.</summary>
    public static bool Le(object? left, object? right) => Compare(left, right, CompareOp.Le, "<=");

    /// <summary><c>a &gt; b</c>.</summary>
    public static bool Gt(object? left, object? right) => Compare(left, right, CompareOp.Gt, ">");

    /// <summary><c>a &gt;= b</c>.</summary>
    public static bool Ge(object? left, object? right) => Compare(left, right, CompareOp.Ge, ">=");

    private enum CompareOp
    {
        Lt,
        Le,
        Gt,
        Ge,
    }

    private static bool Compare(object? left, object? right, CompareOp op, string symbol)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a is bool or BigInteger or double && b is bool or BigInteger or double)
        {
            var order = PyNumbers.Compare(a, b);
            return order is { } o && Holds(o, op);
        }

        if (a is string sa && b is string sb)
        {
            return Holds(PyRepr.CompareStrings(sa, sb), op);
        }

        if (a is JsonArray or IReadOnlyList<object?> && b is JsonArray or IReadOnlyList<object?>)
        {
            // list_richcompare: the first index whose items differ decides, with the same operator.
            var la = Items(a);
            var lb = Items(b);
            var i = 0;
            while (i < la.Count && i < lb.Count && EqBool(la[i], lb[i]))
            {
                i++;
            }

            if (i >= la.Count || i >= lb.Count)
            {
                return Holds(la.Count.CompareTo(lb.Count), op);
            }

            return Compare(la[i], lb[i], op, symbol);
        }

        throw new PyTypeError("'" + symbol + "' not supported between instances of '" + TypeName(a) + "' and '"
                              + TypeName(b) + "'");
    }

    private static bool Holds(int order, CompareOp op) => op switch
    {
        CompareOp.Lt => order < 0,
        CompareOp.Le => order <= 0,
        CompareOp.Gt => order > 0,
        _ => order >= 0,
    };

    /// <summary>
    /// <c>a &lt; b</c> for tuples (<c>tuplerichcompare</c>, as <c>list.sort</c> applies it to tuple keys): the
    /// first position whose items are not equal decides; equal prefixes compare by length.
    /// </summary>
    public static bool TupleLt(IReadOnlyList<object?> left, IReadOnlyList<object?> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var i = 0;
        while (i < left.Count && i < right.Count && EqBool(left[i], right[i]))
        {
            i++;
        }

        if (i >= left.Count || i >= right.Count)
        {
            return left.Count < right.Count;
        }

        return Lt(left[i], right[i]);
    }

    /// <summary><c>str(x)</c>.</summary>
    public static string Str(object? value) => Normalize(value) is string text ? text : Repr(value);

    /// <summary><c>repr(x)</c>.</summary>
    public static string Repr(object? value)
    {
        var builder = new StringBuilder();
        AppendRepr(builder, value);
        return builder.ToString();
    }

    private static void AppendRepr(StringBuilder builder, object? value)
    {
        switch (Normalize(value))
        {
            case null:
                builder.Append("None");
                break;
            case bool flag:
                builder.Append(flag ? "True" : "False");
                break;
            case BigInteger integer:
                builder.Append(PyJson.IntStr(integer));
                break;
            case double number:
                builder.Append(PyJson.FloatRepr(number));
                break;
            case string text:
                builder.Append(PyRepr.Str(text));
                break;
            case JsonObject dict:
                builder.Append('{');
                var first = true;
                foreach (var (key, item) in dict)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    builder.Append(PyRepr.Str(key)).Append(": ");
                    AppendRepr(builder, item);
                }

                builder.Append('}');
                break;
            default:
                builder.Append('[');
                var items = Items(value);
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    AppendRepr(builder, items[i]);
                }

                builder.Append(']');
                break;
        }
    }

    /// <summary><c>int(x)</c>.</summary>
    public static BigInteger Int(object? value)
    {
        switch (Normalize(value))
        {
            case bool flag:
                return flag ? BigInteger.One : BigInteger.Zero;
            case BigInteger integer:
                return integer;
            case double number:
                if (double.IsNaN(number))
                {
                    throw new PyValueError("cannot convert float NaN to integer");
                }

                if (double.IsInfinity(number))
                {
                    throw new PyOverflowError("cannot convert float infinity to integer");
                }

                return new BigInteger(Math.Truncate(number));
            case string text:
                return PyArgumentParser.ParsePyInt(text);
            default:
                throw new PyTypeError("int() argument must be a string, a bytes-like object or a real number, not '"
                                      + TypeName(value) + "'");
        }
    }

    /// <summary>A read-only list view of a <see cref="JsonArray"/>.</summary>
    private sealed class JsonArrayView(JsonArray array) : IReadOnlyList<object?>
    {
        public int Count => array.Count;

        public object? this[int index] => array[index];

        public IEnumerator<object?> GetEnumerator()
        {
            foreach (var item in array)
            {
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>
/// A dict key with Python's hash/equality: <c>True == 1 == 1.0</c>, <c>-0.0 == 0</c>, the shared JSON NaN
/// equals itself, strings compare ordinally; lists and dicts are unhashable.
/// </summary>
public readonly record struct PyKey
{
    public const byte NoneTag = 0;
    public const byte IntTag = 1;
    public const byte FloatTag = 2;
    public const byte NanTag = 3;
    public const byte StrTag = 4;

    private PyKey(byte tag, BigInteger integer, double number, string? text)
    {
        Tag = tag;
        Int = integer;
        Float = number;
        Str = text;
    }

    public byte Tag { get; }

    public BigInteger Int { get; }

    public double Float { get; }

    public string? Str { get; }

    /// <summary>The key of a value; <c>TypeError: unhashable type</c> for lists and dicts.</summary>
    public static PyKey Of(object? value) => PyObj.Normalize(value) switch
    {
        null => new PyKey(NoneTag, BigInteger.Zero, 0, null),
        bool flag => new PyKey(IntTag, flag ? BigInteger.One : BigInteger.Zero, 0, null),
        BigInteger integer => new PyKey(IntTag, integer, 0, null),
        double number when double.IsNaN(number) => new PyKey(NanTag, BigInteger.Zero, 0, null),
        double number when double.IsFinite(number) && Math.Floor(number) == number =>
            new PyKey(IntTag, new BigInteger(number), 0, null),
        double number => new PyKey(FloatTag, BigInteger.Zero, number, null),
        string text => new PyKey(StrTag, BigInteger.Zero, 0, text),
        _ => throw new PyTypeError("unhashable type: '" + PyObj.TypeName(value) + "'"),
    };
}

/// <summary>
/// An insertion-ordered Python <c>dict</c> keyed by <see cref="PyKey"/>: assigning to an existing key keeps
/// its position and its original key object; deleting and re-adding appends.
/// </summary>
public sealed class PyDict<TValue>
{
    private readonly List<Entry?> _entries = [];
    private readonly Dictionary<PyKey, int> _index = [];

    public int Count => _index.Count;

    public bool TryGetValue(object? key, out TValue value)
    {
        if (_index.TryGetValue(PyKey.Of(key), out var position))
        {
            value = _entries[position]!.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary><c>d[key] = value</c>.</summary>
    public void Set(object? key, TValue value)
    {
        var hashed = PyKey.Of(key);
        if (_index.TryGetValue(hashed, out var position))
        {
            _entries[position]!.Value = value;
            return;
        }

        _index[hashed] = _entries.Count;
        _entries.Add(new Entry(key, value));
    }

    /// <summary><c>d.setdefault(key, value)</c>.</summary>
    public TValue SetDefault(object? key, TValue value)
    {
        if (TryGetValue(key, out var existing))
        {
            return existing;
        }

        Set(key, value);
        return value;
    }

    /// <summary><c>del d[key]</c> (the key must exist).</summary>
    public void Remove(object? key)
    {
        var hashed = PyKey.Of(key);
        var position = _index[hashed];
        _index.Remove(hashed);
        _entries[position] = null;
    }

    /// <summary><c>list(d.items())</c>: a snapshot in insertion order.</summary>
    public List<KeyValuePair<object?, TValue>> Items() =>
        _entries.Where(entry => entry is not null).Select(entry => new KeyValuePair<object?, TValue>(entry!.Key, entry.Value)).ToList();

    private sealed class Entry(object? key, TValue value)
    {
        public object? Key { get; } = key;

        public TValue Value { get; set; } = value;
    }
}
