using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Proactive;

/// <summary>
/// Python <c>AttributeError</c> (for example <c>'list' object has no attribute 'update'</c>). proactive_state.py
/// never catches it, so it escapes as an uncaught exception (exit status 1), as in Python.
/// Belongs in <c>Margo.Cloud.State.Core</c> (PyErrors.cs); kept here because this project may not edit Core.
/// </summary>
public sealed class PyAttributeError : PyException
{
    public PyAttributeError(string message) : base(message)
    {
    }

    public override string PythonType => "AttributeError";
}

/// <summary>
/// Python operations on the two value models the port mixes: JSON trees (<see cref="JsonNode"/>, the Python
/// dicts and lists built from JSON input) and SQLite values read through <see cref="PyConnection"/>
/// (<see cref="long"/>, <see cref="double"/>, <see cref="string"/>, <see cref="byte"/>[] or null). Errors follow
/// Python's exception classes (<c>TypeError</c>, <c>KeyError</c>, <c>AttributeError</c>) so the CLI maps them
/// to the same exit status and message as proactive_state.py.
/// Candidates for <c>Margo.Cloud.State.Core</c>; kept here because this project may not edit Core.
/// </summary>
internal static class Py
{
    private static readonly object NotAString = new();

    // ------------------------------------------------------------------------------------ dict access

    /// <summary><c>d.get(key)</c>: None (null) when absent.</summary>
    public static JsonNode? Get(JsonObject obj, string key) => obj.TryGetPropertyValue(key, out var value) ? value : null;

    /// <summary><c>d.get(key, fallback)</c>: the fallback only when the key is absent (a present None stays None).</summary>
    public static JsonNode? Get(JsonObject obj, string key, JsonNode? fallback) =>
        obj.TryGetPropertyValue(key, out var value) ? value : fallback;

    /// <summary><c>d[key]</c>: <c>KeyError</c> when absent.</summary>
    public static JsonNode? Index(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var value) ? value : throw new PyKeyError(key);

    /// <summary><c>value is None</c> for a JSON node.</summary>
    public static bool IsNone(JsonNode? node) => node is null || (node is JsonValue && PyJson.KindOf(node) == PyKind.None);

    /// <summary><c>isinstance(value, str)</c>, returning the text.</summary>
    public static bool IsStr(JsonNode? node, out string text)
    {
        if (node is JsonValue && PyJson.IsStr(node))
        {
            text = (string)PyJson.Scalar(node)!;
            return true;
        }

        text = "";
        return false;
    }

    /// <summary><c>value == "literal"</c> for a JSON node (only a str can equal a str).</summary>
    public static bool StrEquals(JsonNode? node, string expected) => IsStr(node, out var text) && text == expected;

    /// <summary><c>value in ("a", "b", ...)</c> for a JSON node.</summary>
    public static bool StrIn(JsonNode? node, params string[] options) => IsStr(node, out var text) && options.Contains(text, StringComparer.Ordinal);

    /// <summary><c>dict.pop(key, None)</c> applied to a JSON value, with Python's errors for non-dicts.</summary>
    public static void Pop(JsonNode? payload, string key)
    {
        switch (payload)
        {
            case JsonObject obj:
                obj.Remove(key);
                break;
            case JsonArray:
                throw new PyTypeError("pop expected at most 1 argument, got 2");
            default:
                throw new PyAttributeError("'" + TypeName(payload) + "' object has no attribute 'pop'");
        }
    }

    /// <summary>The Python type name of a JSON value.</summary>
    public static string TypeName(JsonNode? node) => PyJson.TypeName(node);

    /// <summary>
    /// A deep copy of a JSON value (Python's <c>dict(...)</c>/comprehension copies keep the same values).
    /// Never use <see cref="JsonNode.DeepClone"/> on Python values: it re-serializes a <see cref="BigInteger"/>
    /// value (a Python int beyond 64 bits) through System.Text.Json reflection, turning <c>10**30</c> into
    /// <c>{"IsEven":true,"IsOne":false,...}</c>.
    /// </summary>
    public static JsonNode? Copy(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[key] = Copy(value);
                }

                return copy;
            }

            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var value in array)
                {
                    copy.Add(Copy(value));
                }

                return copy;
            }

            case JsonValue value when value.TryGetValue<BigInteger>(out var big):
                return PyJson.Int(big);
            default:
                return node.DeepClone();
        }
    }

    /// <summary>
    /// <c>margo_store.validate_timestamp(value)</c> for any JSON value. <see cref="StateStore.ValidateTimestamp"/>
    /// unwraps <see cref="JsonNode"/> scalars, but a dict or list must take the "not a str" path, as in Python.
    /// </summary>
    public static string Timestamp(JsonNode? value) =>
        StateStore.ValidateTimestamp(value is JsonObject or JsonArray ? NotAString : value);

    // ------------------------------------------------------------------------------------ SQLite values

    /// <summary>A value read from SQLite as a JSON node (<c>dict(row)</c> keeps Python ints, floats and strs).</summary>
    public static JsonNode? Node(object? value) => value switch
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
    public static JsonObject Dict(PyRow row)
    {
        var result = new JsonObject();
        foreach (var (key, value) in row.ToDict())
        {
            result[key] = Node(value);
        }

        return result;
    }

    /// <summary><c>json.loads(value)</c> for a value read from SQLite (str, or UTF-8 bytes).</summary>
    public static JsonNode? Loads(object? value) => value switch
    {
        string text => PyJson.Loads(text),
        byte[] bytes => PyJson.Loads(PyIO.DecodeUtf8(bytes)),
        _ => throw new PyTypeError("the JSON object must be str, bytes or bytearray, not " + SqlTypeName(value)),
    };

    /// <summary>A str read from SQLite; anything else raises <c>TypeError</c> (as str concatenation would).</summary>
    public static string Str(object? value) => value as string
        ?? throw new PyTypeError("can only concatenate str (not \"" + SqlTypeName(value) + "\") to str");

    /// <summary><c>int(x)</c> of a float: exact and unbounded; NaN and infinities raise as in Python.</summary>
    public static BigInteger Trunc(double value) =>
        double.IsNaN(value) ? throw new PyValueError("cannot convert float NaN to integer")
        : double.IsInfinity(value) ? throw new PyOverflowError("cannot convert float infinity to integer")
        : new BigInteger(Math.Truncate(value));

    /// <summary>
    /// Python <c>value + n</c> for a value read from SQLite: an int stays an int (a sum beyond 64 bits is a
    /// <see cref="BigInteger"/>, which sqlite3 binding refuses with <c>OverflowError</c>, as in Python), a float
    /// (REAL in a hand-edited INTEGER column) stays a float; str, bytes and None raise Python's <c>TypeError</c>.
    /// </summary>
    public static object Add(object? value, long n)
    {
        switch (value)
        {
            case long integer:
                var sum = (BigInteger)integer + n;
                return sum >= long.MinValue && sum <= long.MaxValue ? (object)(long)sum : sum;
            case BigInteger big:
                return big + n;
            case double number:
                return number + n;
            case string:
                throw new PyTypeError("can only concatenate str (not \"int\") to str");
            case byte[]:
                throw new PyTypeError("can't concat int to bytes");
            default:
                throw new PyTypeError("unsupported operand type(s) for +: '" + SqlTypeName(value) + "' and 'int'");
        }
    }

    /// <summary>
    /// <c>timedelta(seconds=value)</c> for a value read from SQLite: ints exactly; floats as CPython's
    /// <c>delta_new</c>/<c>accum</c> do (integral part exact, fraction scaled to microseconds, the leftover rounded
    /// half to even); infinities, out-of-range days and other types raise Python's <c>OverflowError</c>/<c>TypeError</c>.
    /// </summary>
    public static PyTimeDelta TimedeltaSeconds(object? value)
    {
        BigInteger micros;
        switch (value)
        {
            case long integer:
                micros = (BigInteger)integer * 1_000_000;
                break;
            case BigInteger big:
                micros = big * 1_000_000;
                break;
            case double number:
            {
                if (double.IsNaN(number))
                {
                    throw new PyValueError("cannot convert float NaN to integer");
                }

                if (double.IsInfinity(number))
                {
                    throw new PyOverflowError("cannot convert float infinity to integer");
                }

                var whole = Math.Truncate(number); // modf
                var fraction = number - whole;
                micros = new BigInteger(whole) * 1_000_000;
                var leftover = 0.0;
                if (fraction != 0.0)
                {
                    var scaled = 1_000_000.0 * fraction;
                    var scaledWhole = Math.Truncate(scaled);
                    micros += new BigInteger(scaledWhole);
                    leftover = scaled - scaledWhole;
                }

                if (leftover != 0.0)
                {
                    var rounded = Math.Round(leftover, MidpointRounding.AwayFromZero); // C round()
                    if (Math.Abs(rounded - leftover) == 0.5)
                    {
                        var odd = micros.IsEven ? 0 : 1;
                        rounded = (2.0 * Math.Round((leftover + odd) * 0.5, MidpointRounding.AwayFromZero)) - odd;
                    }

                    micros += new BigInteger(rounded);
                }

                break;
            }

            default:
                throw new PyTypeError("unsupported type for timedelta seconds component: " + SqlTypeName(value));
        }

        // microseconds_to_delta_ex: days must fit a C int before the 999999999-day range check.
        var days = BigInteger.Divide(micros - (micros.Sign < 0 ? 86_400_000_000 - 1 : 0), 86_400_000_000);
        if (days < int.MinValue || days > int.MaxValue)
        {
            throw new PyOverflowError("Python int too large to convert to C int");
        }

        if (BigInteger.Abs(days) > 999_999_999)
        {
            throw new PyOverflowError("days=" + days.ToString(CultureInfo.InvariantCulture) + "; must have magnitude <= 999999999");
        }

        return PyTimeDelta.FromMicroseconds((Int128)micros);
    }

    /// <summary>Python truthiness of a SQLite value.</summary>
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        long integer => integer != 0,
        double number => number != 0,
        string text => text.Length > 0,
        byte[] bytes => bytes.Length > 0,
        bool flag => flag,
        JsonNode node => PyJson.Truthy(node),
        _ => true,
    };

    /// <summary>Python <c>==</c> between SQLite values, Python strings and JSON scalars.</summary>
    public static bool Eq(object? left, object? right)
    {
        left = Normalize(left);
        right = Normalize(right);
        return (left, right) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            (JsonNode a, JsonNode b) => PyJson.PyEquals(a, b),
            (JsonNode, _) or (_, JsonNode) => false,
            _ when IsNumber(left) && IsNumber(right) => CompareNumbers(left, right) == 0,
            _ => false,
        };
    }

    /// <summary>Python ordering (<c>&lt;</c>, <c>&gt;=</c>, ...): str with str (code points), numbers with numbers, else <c>TypeError</c>.</summary>
    public static int Compare(object? left, object? right, string op)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a is string x && b is string y)
        {
            return PyRepr.CompareStrings(x, y);
        }

        if (a is byte[] p && b is byte[] q)
        {
            return p.AsSpan().SequenceCompareTo(q);
        }

        if (a is not null && b is not null && IsNumber(a) && IsNumber(b))
        {
            return CompareNumbers(a, b);
        }

        throw new PyTypeError("'" + op + "' not supported between instances of '" + SqlTypeName(a) + "' and '" + SqlTypeName(b) + "'");
    }

    /// <summary>Python's name for a SQLite or scalar value's type.</summary>
    public static string SqlTypeName(object? value) => value switch
    {
        null => "NoneType",
        string => "str",
        long or int or BigInteger => "int",
        double => "float",
        bool => "bool",
        byte[] => "bytes",
        JsonNode node => PyJson.TypeName(node),
        _ => value.GetType().Name,
    };

    private static object? Normalize(object? value) => value switch
    {
        JsonValue node => PyJson.Scalar(node),
        int integer => new BigInteger(integer),
        long integer => new BigInteger(integer),
        _ => value,
    };

    private static bool IsNumber(object value) => value is bool or BigInteger or double;

    private static int CompareNumbers(object left, object right)
    {
        static BigInteger AsInteger(object value) => value switch
        {
            bool flag => flag ? BigInteger.One : BigInteger.Zero,
            _ => (BigInteger)value,
        };

        if (left is double x && right is double y)
        {
            return x.CompareTo(y);
        }

        if (left is double d)
        {
            return CompareFloatInt(d, AsInteger(right));
        }

        if (right is double e)
        {
            return -CompareFloatInt(e, AsInteger(left));
        }

        return AsInteger(left).CompareTo(AsInteger(right));
    }

    // Exact float/int comparison, as CPython's float_richcompare.
    private static int CompareFloatInt(double value, BigInteger integer)
    {
        if (double.IsNaN(value))
        {
            return 1; // only used for ordering of finite values here; NaN never reaches the store
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? 1 : -1;
        }

        var floor = Math.Floor(value);
        var whole = new BigInteger(floor);
        var result = whole.CompareTo(integer);
        if (result != 0)
        {
            return result;
        }

        return value > floor ? 1 : 0;
    }

    // ------------------------------------------------------------------------------------ str() and repr()

    /// <summary><c>str(value)</c> of a JSON value, as <c>str.format("{}", value)</c> and <c>print</c> show it.</summary>
    public static string Format(JsonNode? node) => IsStr(node, out var text) ? text : Repr(node);

    /// <summary><c>repr(value)</c> of a JSON value (dicts as <c>{'key': value}</c>, lists, None/True/False, numbers).</summary>
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

    // ------------------------------------------------------------------------------------ arithmetic

    /// <summary>
    /// <c>float // float</c> (CPython's <c>float_floor_div</c>/<c>_float_div_mod</c>): the remainder from
    /// <c>fmod</c>, then the quotient snapped to the nearest integral value.
    /// </summary>
    public static double FloorDivide(double vx, double wx)
    {
        if (wx == 0)
        {
            throw new PyZeroDivisionError("float floor division by zero");
        }

        var mod = vx % wx; // C# % on doubles is C fmod: the remainder has the dividend's sign
        var div = (vx - mod) / wx;
        if (mod != 0)
        {
            if ((wx < 0) != (mod < 0))
            {
                div -= 1.0;
            }
        }

        double floordiv;
        if (div != 0)
        {
            floordiv = Math.Floor(div);
            if (div - floordiv > 0.5)
            {
                floordiv += 1.0;
            }
        }
        else
        {
            floordiv = Math.CopySign(0.0, vx / wx);
        }

        return floordiv;
    }
}

/// <summary>Python <c>ZeroDivisionError</c> (never caught by proactive_state.py: exit status 1).</summary>
public sealed class PyZeroDivisionError : PyException
{
    public PyZeroDivisionError(string message) : base(message)
    {
    }

    public override string PythonType => "ZeroDivisionError";
}
