using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Margo.Cloud.State.Core;

/// <summary>The Python type a JSON tree node corresponds to.</summary>
public enum PyKind
{
    None,
    Bool,
    Int,
    Float,
    Str,
    List,
    Dict,
}

/// <summary>Python <c>json.JSONDecodeError</c>: <c>msg: line L column C (char P)</c>, positions in code points.</summary>
public sealed class PyJsonDecodeError : PyValueError
{
    public PyJsonDecodeError(string msg, string doc, int utf16Position)
        : base(Format(msg, doc, utf16Position, out var line, out var column, out var position))
    {
        Msg = msg;
        Pos = position;
        LineNo = line;
        ColNo = column;
    }

    public string Msg { get; }

    public int Pos { get; }

    public int LineNo { get; }

    public int ColNo { get; }

    public override string PythonType => "JSONDecodeError";

    private static string Format(string msg, string doc, int index, out int line, out int column, out int position)
    {
        position = PyRepr.CodePointIndex(doc, index);
        var newlines = 0;
        var last = -1;
        for (var i = 0; i < Math.Min(index, doc.Length); i++)
        {
            if (doc[i] == '\n')
            {
                newlines++;
                last = i;
            }
        }

        line = newlines + 1;
        column = position - (last < 0 ? -1 : PyRepr.CodePointIndex(doc, last));
        return string.Format(CultureInfo.InvariantCulture, "{0}: line {1} column {2} (char {3})", msg, line, column, position);
    }
}

/// <summary>Options of <c>json.loads</c> that the state helpers use.</summary>
public sealed record PyJsonLoadOptions
{
    /// <summary>
    /// <c>object_pairs_hook</c>: receives the pairs of each object when it closes. It stands for a Python function, so
    /// calling it costs C recursion budget (see <see cref="MaxDepth"/>).
    /// </summary>
    public Func<IReadOnlyList<KeyValuePair<string, JsonNode?>>, JsonNode?>? ObjectPairsHook { get; init; }

    /// <summary>
    /// <c>parse_constant</c>: receives <c>NaN</c>, <c>Infinity</c> or <c>-Infinity</c>. It stands for a Python function,
    /// so calling it costs C recursion budget (see <see cref="MaxDepth"/>).
    /// </summary>
    public Func<string, JsonNode?>? ParseConstant { get; init; }

    /// <summary>
    /// The C recursion budget of the call site: the deepest container nesting it decodes. <see cref="PyJson.MaxDepth"/>
    /// (9998) is <c>json.loads</c> called from Python code that no C function called (a script's <c>main()</c>,
    /// <c>parse_json</c>); every C-to-Python re-entry on the way (a generator resumed from C, a callback) costs two.
    /// </summary>
    public int MaxDepth { get; init; } = PyJson.MaxDepth;
}

/// <summary>
/// Python <c>json.dumps</c>/<c>json.loads</c> (CPython 3.13 C accelerator) over <see cref="JsonNode"/>.
/// <para>Value mapping: <c>None</c> = C# <c>null</c>; <c>bool</c> = <see cref="JsonValue"/> of <see cref="bool"/>;
/// <c>int</c> = <see cref="JsonValue"/> of <see cref="long"/> (or <see cref="BigInteger"/> beyond 64 bits);
/// <c>float</c> = <see cref="JsonValue"/> of <see cref="double"/>; <c>str</c> = <see cref="JsonValue"/> of
/// <see cref="string"/>; <c>list</c> = <see cref="JsonArray"/>; <c>dict</c> = <see cref="JsonObject"/>
/// (insertion ordered). <c>1</c> and <c>1.0</c> stay distinct, as in Python.</para>
/// </summary>
public static class PyJson
{
    /// <summary>Deepest container nesting CPython 3.13 decodes or encodes before <c>RecursionError</c>.</summary>
    public const int MaxDepth = 9998;

    /// <summary><c>sys.get_int_max_str_digits()</c> default.</summary>
    public const int MaxIntDigits = 4300;

    private const char ByteOrderMark = (char)0xFEFF;

    // ---------------------------------------------------------------------------------- dumps

    /// <summary>
    /// <c>json.dumps(value, indent=..., separators=..., sort_keys=..., ensure_ascii=..., allow_nan=...)</c>.
    /// When <paramref name="itemSeparator"/>/<paramref name="keySeparator"/> are null they default as in
    /// Python: <c>', '</c> (or <c>','</c> with an indent) and <c>': '</c>.
    /// </summary>
    public static string Dumps(JsonNode? value, int? indent = null, string? itemSeparator = null,
                               string? keySeparator = null, bool sortKeys = false, bool ensureAscii = true,
                               bool allowNan = true)
    {
        var indentText = indent is null ? null : new string(' ', Math.Max(0, indent.Value));
        return Dumps(value, indentText, itemSeparator, keySeparator, sortKeys, ensureAscii, allowNan);
    }

    /// <summary><c>json.dumps</c> with a string indent (<c>indent="\t"</c>).</summary>
    public static string Dumps(JsonNode? value, string? indent, string? itemSeparator, string? keySeparator,
                               bool sortKeys, bool ensureAscii, bool allowNan)
    {
        itemSeparator ??= indent is null ? ", " : ",";
        keySeparator ??= ": ";
        var output = new StringBuilder();
        var encoder = new Encoder(output, indent, itemSeparator, keySeparator, sortKeys, ensureAscii, allowNan);
        encoder.Encode(value);
        return output.ToString();
    }

    /// <summary>Encodes a string as a JSON string literal (<c>encode_basestring[_ascii]</c>).</summary>
    public static string EncodeString(string value, bool ensureAscii = true)
    {
        var builder = new StringBuilder(value.Length + 2);
        AppendString(builder, value, ensureAscii);
        return builder.ToString();
    }

    /// <summary><c>float.__repr__</c>: shortest round-trip digits in Python's layout (<c>1e-05</c>, <c>1e+16</c>, <c>2.0</c>).</summary>
    public static string FloatRepr(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = text[0] == '-';
        if (negative)
        {
            text = text[1..];
        }

        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var integerPart = dot >= 0 ? text[..dot] : text;
        var fraction = dot >= 0 ? text[(dot + 1)..] : string.Empty;
        var digits = (integerPart + fraction).TrimStart('0');
        var leadingZeros = (integerPart + fraction).Length - digits.Length;
        // value = 0.DIGITS x 10^decpt
        var decpt = integerPart.Length - leadingZeros + exponent;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }

        var builder = new StringBuilder();
        if (negative)
        {
            builder.Append('-');
        }

        if (decpt <= -4 || decpt > 16)
        {
            builder.Append(digits[0]);
            if (digits.Length > 1)
            {
                builder.Append('.').Append(digits, 1, digits.Length - 1);
            }

            var exp = decpt - 1;
            builder.Append('e').Append(exp < 0 ? '-' : '+')
                   .Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (decpt <= 0)
        {
            builder.Append("0.").Append('0', -decpt).Append(digits);
        }
        else if (decpt >= digits.Length)
        {
            builder.Append(digits).Append('0', decpt - digits.Length).Append(".0");
        }
        else
        {
            builder.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
        }

        return builder.ToString();
    }

    /// <summary><c>str(int)</c>, including CPython's 4300-digit conversion limit.</summary>
    public static string IntStr(BigInteger value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        var digits = text.Length - (value.Sign < 0 ? 1 : 0);
        if (digits > MaxIntDigits)
        {
            throw new PyValueError("Exceeds the limit (4300 digits) for integer string conversion; use sys.set_int_max_str_digits() to increase the limit");
        }

        return text;
    }

    // ---------------------------------------------------------------------------------- loads

    /// <summary><c>json.loads(text)</c> with the default hooks.</summary>
    public static JsonNode? Loads(string text) => Loads(text, null);

    /// <summary>
    /// <c>json.loads(text, object_pairs_hook=..., parse_constant=...)</c>, including what CPython 3.13 does where its C
    /// recursion budget runs out. With B = <see cref="PyJsonLoadOptions.MaxDepth"/> and n the number of containers open
    /// where something happens, measured with CPython 3.13.16 (<c>tools</c>: see State.Core's PORTING.md):
    /// <list type="bullet">
    /// <item>a container opens while n + 1 &lt;= B, else <c>RecursionError</c> "... while decoding a JSON array/object
    /// from a unicode string";</item>
    /// <item>every call the scanner makes needs levels beyond n: the default <c>parse_constant</c> (a C method) one; a
    /// Python hook (<c>parse_constant</c>, <c>object_pairs_hook</c> at the closing object's own level) two, and a third
    /// when it raises (creating the exception calls its class); a decode error built in C (<c>raise_errmsg</c> calls
    /// <c>JSONDecodeError</c>, whose <c>__init__</c> is Python code calling <c>ValueError.__init__</c>) four;</item>
    /// <item>"Expecting value" (a <c>StopIteration</c> that <c>raw_decode</c> turns into the <c>JSONDecodeError</c>) and
    /// the 4300-digit <c>ValueError</c> need one to be created; failing that, CPython raises <c>RecursionError</c> with the
    /// note "Normalization failed: type=... args=&lt;unknown&gt;", which the traceback prints last (it is part of
    /// <see cref="Exception.Message"/> here);</item>
    /// <item>the <c>RecursionError</c> text names the call that ran out: " while calling a Python object" for calls
    /// through <c>_PyObject_MakeTpCall</c> or to C methods, nothing for entering a Python frame (two levels);</item>
    /// <item>errors raised by Python code after the scan (<c>Extra data</c>, the BOM check) are unaffected.</item>
    /// </list>
    /// The first of these in document order wins, as in CPython.
    /// </summary>
    public static JsonNode? Loads(string text, PyJsonLoadOptions? options)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == ByteOrderMark)
        {
            throw new PyJsonDecodeError("Unexpected UTF-8 BOM (decode using utf-8-sig)", text, 0);
        }

        return LoadsDecoded(text, options);
    }

    /// <summary>
    /// <c>json.loads(data)</c> for <c>bytes</c>: <c>data.decode(json.detect_encoding(data), "surrogatepass")</c>
    /// (<see cref="PyIO.DecodeJsonBytes"/>), then the decoder. Unlike a <c>str</c>, decoded bytes get no BOM check:
    /// <c>utf-8-sig</c> and <c>utf-16</c>/<c>utf-32</c> strip one BOM, and a second one is "Expecting value".
    /// </summary>
    public static JsonNode? LoadsBytes(ReadOnlySpan<byte> data, PyJsonLoadOptions? options = null) =>
        LoadsDecoded(PyIO.DecodeJsonBytes(data), options);

    private static JsonNode? LoadsDecoded(string text, PyJsonLoadOptions? options)
    {
        var decoder = new Decoder(text, options ?? new PyJsonLoadOptions());
        var index = SkipWhitespace(text, 0);
        var (value, end) = decoder.Decode(index);
        end = SkipWhitespace(text, end);
        if (end != text.Length)
        {
            throw new PyJsonDecodeError("Extra data", text, end);
        }

        return value;
    }

    // ---------------------------------------------------------------------------------- values

    /// <summary>The Python type of a node.</summary>
    public static PyKind KindOf(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return PyKind.None;
            case JsonObject:
                return PyKind.Dict;
            case JsonArray:
                return PyKind.List;
            case JsonValue value:
                var scalar = Scalar(value);
                return scalar switch
                {
                    null => PyKind.None,
                    bool => PyKind.Bool,
                    string => PyKind.Str,
                    BigInteger => PyKind.Int,
                    double => PyKind.Float,
                    _ => throw new PyTypeError("Object of type " + scalar.GetType().Name + " is not JSON serializable"),
                };
            default:
                throw new PyTypeError("Object of type " + node.GetType().Name + " is not JSON serializable");
        }
    }

    /// <summary>Python type name (<c>dict</c>, <c>list</c>, <c>str</c>, <c>int</c>, <c>float</c>, <c>bool</c>, <c>NoneType</c>).</summary>
    public static string TypeName(JsonNode? node) => KindOf(node) switch
    {
        PyKind.None => "NoneType",
        PyKind.Bool => "bool",
        PyKind.Int => "int",
        PyKind.Float => "float",
        PyKind.Str => "str",
        PyKind.List => "list",
        _ => "dict",
    };

    /// <summary><c>type(x) is int</c> (bool excluded).</summary>
    public static bool IsInt(JsonNode? node) => node is JsonValue && KindOf(node) == PyKind.Int;

    /// <summary><c>isinstance(x, int)</c>: true for bool as well, as in Python.</summary>
    public static bool IsIntOrBool(JsonNode? node) => node is JsonValue && KindOf(node) is PyKind.Int or PyKind.Bool;

    /// <summary><c>type(x) is float</c>.</summary>
    public static bool IsFloat(JsonNode? node) => node is JsonValue && KindOf(node) == PyKind.Float;

    /// <summary><c>isinstance(x, bool)</c>.</summary>
    public static bool IsBool(JsonNode? node) => node is JsonValue && KindOf(node) == PyKind.Bool;

    /// <summary><c>isinstance(x, str)</c>.</summary>
    public static bool IsStr(JsonNode? node) => node is JsonValue && KindOf(node) == PyKind.Str;

    /// <summary>Returns the Python int value (bool counts as 0/1, like <c>int(True)</c>).</summary>
    public static BigInteger AsInt(JsonNode? node) => Scalar(node as JsonValue ?? throw new PyTypeError("not an int")) switch
    {
        BigInteger integer => integer,
        bool flag => flag ? BigInteger.One : BigInteger.Zero,
        _ => throw new PyTypeError("not an int"),
    };

    /// <summary>Returns a numeric node as a double (int or float), like <c>float(x)</c>.</summary>
    public static double AsDouble(JsonNode? node) => Scalar(node as JsonValue ?? throw new PyTypeError("not a number")) switch
    {
        double number => number,
        BigInteger integer => (double)integer,
        bool flag => flag ? 1 : 0,
        _ => throw new PyTypeError("not a number"),
    };

    /// <summary>Creates a Python int node, stored as <see cref="long"/> when it fits.</summary>
    public static JsonValue Int(BigInteger value) =>
        value >= long.MinValue && value <= long.MaxValue ? JsonValue.Create((long)value) : CreateBig(value);

    /// <summary>Creates a Python float node.</summary>
    public static JsonValue Float(double value) => JsonValue.Create(value);

    /// <summary>Python truthiness of a JSON value.</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonObject obj => obj.Count > 0,
        JsonArray array => array.Count > 0,
        JsonValue value => Scalar(value) switch
        {
            null => false,
            bool flag => flag,
            string text => text.Length > 0,
            BigInteger integer => !integer.IsZero,
            double number => number != 0,
            _ => true,
        },
        _ => true,
    };

    /// <summary>
    /// Python <c>==</c> for JSON values: <c>1 == 1.0 == True</c>, dicts compare without order, NaN is
    /// never equal to itself (but the same object compares equal, as in Python containers).
    /// </summary>
    public static bool PyEquals(JsonNode? left, JsonNode? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        switch (left)
        {
            case JsonObject a when right is JsonObject b:
                if (a.Count != b.Count)
                {
                    return false;
                }

                foreach (var (key, value) in a)
                {
                    if (!b.TryGetPropertyValue(key, out var other) || !PyEquals(value, other))
                    {
                        return false;
                    }
                }

                return true;
            case JsonArray a when right is JsonArray b:
                if (a.Count != b.Count)
                {
                    return false;
                }

                for (var i = 0; i < a.Count; i++)
                {
                    if (!PyEquals(a[i], b[i]))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValue a when right is JsonValue b:
                var x = Scalar(a);
                var y = Scalar(b);
                if (x is string sx)
                {
                    return y is string sy && string.Equals(sx, sy, StringComparison.Ordinal);
                }

                if (y is string || x is null || y is null)
                {
                    return x is null && y is null;
                }

                return NumericEquals(x, y);
            default:
                return false;
        }
    }

    /// <summary>
    /// The scalar behind a <see cref="JsonValue"/>, normalised to <see cref="bool"/>, <see cref="string"/>,
    /// <see cref="BigInteger"/> (any integral CLR type) or <see cref="double"/> (any floating CLR type).
    /// JSON values parsed by System.Text.Json (<see cref="JsonElement"/>) are classified by their raw text.
    /// </summary>
    public static object? Scalar(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue value)
        {
            throw new PyTypeError("not a scalar");
        }

        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => ParseNumberText(element.GetRawText()),
                _ => throw new PyTypeError("unsupported JSON element"),
            };
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        if (value.TryGetValue<long>(out var int64))
        {
            return new BigInteger(int64);
        }

        if (value.TryGetValue<int>(out var int32))
        {
            return new BigInteger(int32);
        }

        if (value.TryGetValue<double>(out var float64))
        {
            return float64;
        }

        if (value.TryGetValue<BigInteger>(out var big))
        {
            return big;
        }

        if (value.TryGetValue<short>(out var int16))
        {
            return new BigInteger(int16);
        }

        if (value.TryGetValue<byte>(out var uint8))
        {
            return new BigInteger(uint8);
        }

        if (value.TryGetValue<sbyte>(out var int8))
        {
            return new BigInteger(int8);
        }

        if (value.TryGetValue<ushort>(out var uint16))
        {
            return new BigInteger(uint16);
        }

        if (value.TryGetValue<uint>(out var uint32))
        {
            return new BigInteger(uint32);
        }

        if (value.TryGetValue<ulong>(out var uint64))
        {
            return new BigInteger(uint64);
        }

        if (value.TryGetValue<float>(out var float32))
        {
            return (double)float32;
        }

        if (value.TryGetValue<decimal>(out var dec))
        {
            return (double)dec;
        }

        if (value.TryGetValue<char>(out var character))
        {
            return character.ToString();
        }

        if (value.GetValueKind() == JsonValueKind.Number)
        {
            return ParseNumberText(value.ToJsonString());
        }

        var raw = value.GetValue<object>();
        throw new PyTypeError("Object of type " + raw.GetType().Name + " is not JSON serializable");
    }

    // ---------------------------------------------------------------------------------- internals

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "BigInteger values are only written by this encoder, never by System.Text.Json serialization.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "BigInteger values are only written by this encoder, never by System.Text.Json serialization.")]
    private static JsonValue CreateBig(BigInteger value) => JsonValue.Create(value)!;

    private static object ParseNumberText(string text)
    {
        if (text.IndexOfAny(['.', 'e', 'E']) >= 0)
        {
            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        return BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    private static bool NumericEquals(object x, object y)
    {
        static (bool isFloat, BigInteger integer, double number) Normalize(object value) => value switch
        {
            bool flag => (false, flag ? BigInteger.One : BigInteger.Zero, 0),
            BigInteger integer => (false, integer, 0),
            double number => (true, BigInteger.Zero, number),
            _ => throw new PyTypeError("not a number"),
        };

        var (af, ai, ad) = Normalize(x);
        var (bf, bi, bd) = Normalize(y);
        if (!af && !bf)
        {
            return ai == bi;
        }

        if (af && bf)
        {
            return ad == bd;
        }

        // int vs float: exact comparison, as Python does.
        var (integer, number) = af ? (bi, ad) : (ai, bd);
        if (double.IsNaN(number) || double.IsInfinity(number) || Math.Floor(number) != number)
        {
            return false;
        }

        return new BigInteger(number) == integer;
    }

    internal static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && IsJsonWhitespace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsJsonWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r';

    private static void AppendString(StringBuilder builder, string value, bool ensureAscii)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c <= 0x1f || (ensureAscii && c > '~'))
                    {
                        // UTF-16 units of astral characters come out as the surrogate pair, as in CPython.
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private sealed class Encoder(StringBuilder output, string? indent, string itemSeparator, string keySeparator,
                                 bool sortKeys, bool ensureAscii, bool allowNan)
    {
        private sealed class Frame
        {
            public required IEnumerator<KeyValuePair<string?, JsonNode?>> Items { get; init; }
            public required bool IsObject { get; init; }
            public bool First { get; set; } = true;
        }

        public void Encode(JsonNode? root)
        {
            var stack = new Stack<Frame>();
            if (!Open(root, stack))
            {
                return;
            }

            while (stack.Count > 0)
            {
                var frame = stack.Peek();
                if (!frame.Items.MoveNext())
                {
                    frame.Items.Dispose();
                    stack.Pop();
                    if (indent is not null)
                    {
                        output.Append('\n').Append(Repeat(indent, stack.Count));
                    }

                    output.Append(frame.IsObject ? '}' : ']');
                    continue;
                }

                if (frame.First)
                {
                    frame.First = false;
                }
                else
                {
                    output.Append(itemSeparator);
                    if (indent is not null)
                    {
                        output.Append('\n').Append(Repeat(indent, stack.Count));
                    }
                }

                var (key, value) = frame.Items.Current;
                if (frame.IsObject)
                {
                    AppendString(output, key!, ensureAscii);
                    output.Append(keySeparator);
                }

                Open(value, stack);
            }
        }

        /// <summary>Writes a scalar, or opens a container (returns true when a frame was pushed).</summary>
        private bool Open(JsonNode? node, Stack<Frame> stack)
        {
            switch (node)
            {
                case JsonObject obj:
                    // CPython enters a recursion level for every container, empty ones included.
                    Enter(stack);
                    if (obj.Count == 0)
                    {
                        output.Append("{}");
                        return false;
                    }

                    output.Append('{');
                    IEnumerable<KeyValuePair<string, JsonNode?>> pairs = obj;
                    if (sortKeys)
                    {
                        pairs = obj.OrderBy(pair => pair.Key, PyRepr.StringComparer).ToList();
                    }

                    stack.Push(new Frame
                    {
                        IsObject = true,
                        Items = pairs.Select(pair => new KeyValuePair<string?, JsonNode?>(pair.Key, pair.Value)).GetEnumerator(),
                    });
                    break;
                case JsonArray array:
                    Enter(stack);
                    if (array.Count == 0)
                    {
                        output.Append("[]");
                        return false;
                    }

                    output.Append('[');
                    stack.Push(new Frame
                    {
                        IsObject = false,
                        Items = array.Select(item => new KeyValuePair<string?, JsonNode?>(null, item)).GetEnumerator(),
                    });
                    break;
                default:
                    WriteScalar(node);
                    return false;
            }

            if (indent is not null)
            {
                output.Append('\n').Append(Repeat(indent, stack.Count));
            }

            return true;
        }

        private static void Enter(Stack<Frame> stack)
        {
            if (stack.Count + 1 > MaxDepth)
            {
                throw new PyRecursionError("maximum recursion depth exceeded while encoding a JSON object");
            }
        }

        private void WriteScalar(JsonNode? node)
        {
            switch (Scalar(node))
            {
                case null:
                    output.Append("null");
                    break;
                case bool flag:
                    output.Append(flag ? "true" : "false");
                    break;
                case string text:
                    AppendString(output, text, ensureAscii);
                    break;
                case BigInteger integer:
                    output.Append(IntStr(integer));
                    break;
                case double number:
                    if (double.IsNaN(number) || double.IsInfinity(number))
                    {
                        if (!allowNan)
                        {
                            throw new PyValueError("Out of range float values are not JSON compliant: " + FloatRepr(number));
                        }

                        output.Append(double.IsNaN(number) ? "NaN" : number > 0 ? "Infinity" : "-Infinity");
                    }
                    else
                    {
                        output.Append(FloatRepr(number));
                    }

                    break;
            }
        }

        private static string Repeat(string text, int count)
        {
            if (count <= 0 || text.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length * count);
            for (var i = 0; i < count; i++)
            {
                builder.Append(text);
            }

            return builder.ToString();
        }
    }

    private sealed class Decoder(string text, PyJsonLoadOptions options)
    {
        // C recursion levels each kind of call needs beyond the open containers: 'c' a level whose exhaustion
        // reads " while calling a Python object", '_' one that reads "" (entering a Python frame costs two).
        private const string CMethod = "c";
        private const string PythonHook = "__";
        private const string PythonHookRaising = "__c";
        private const string RaiseErrmsg = "c__c";

        private readonly int _end = text.Length - 1;
        private readonly Stack<Frame> _stack = new();

        private sealed class Frame
        {
            public required bool IsObject { get; init; }
            public List<KeyValuePair<string, JsonNode?>>? Pairs { get; init; }
            public JsonArray? Items { get; init; }
            public string? Key { get; set; }
        }

        private sealed class StopIteration(int index) : Exception
        {
            public int Index { get; } = index;
        }

        public (JsonNode? Value, int End) Decode(int index)
        {
            try
            {
                return Run(index);
            }
            catch (StopIteration stop)
            {
                // raw_decode turns the StopIteration into the JSONDecodeError in Python, after the scanner returned.
                throw new PyJsonDecodeError("Expecting value", text, stop.Index);
            }
        }

        /// <summary>Raises <c>RecursionError</c> when a call needing <paramref name="levels"/> beyond the open containers cannot be made.</summary>
        private void Spend(string levels, string note = "", int open = -1)
        {
            open = open < 0 ? _stack.Count : open;
            if (open + levels.Length > options.MaxDepth)
            {
                var first = Math.Max(0, options.MaxDepth - open);
                throw new PyRecursionError("maximum recursion depth exceeded"
                                           + (levels[first] == 'c' ? " while calling a Python object" : "") + note);
            }
        }

        /// <summary>A decode error built in C by <c>raise_errmsg</c>.</summary>
        private PyJsonDecodeError Error(string message, int position)
        {
            Spend(RaiseErrmsg);
            return new PyJsonDecodeError(message, text, position);
        }

        /// <summary>The <c>StopIteration</c> behind "Expecting value".</summary>
        private StopIteration Stop(int position)
        {
            Spend(CMethod, "\nNormalization failed: type=StopIteration args=<unknown>");
            return new StopIteration(position);
        }

        /// <summary>Calls a Python-level hook at <paramref name="open"/> open containers.</summary>
        private JsonNode? CallHook<T>(Func<T, JsonNode?> hook, T argument, int open)
        {
            Spend(PythonHook, open: open);
            try
            {
                return hook(argument);
            }
            catch (PyException ex) when (ex is not PyRecursionError)
            {
                Spend(PythonHookRaising, open: open);
                throw;
            }
        }

        private (JsonNode? Value, int End) Run(int index)
        {
            var stack = _stack;
            while (true)
            {
                // Parse one value at index: a scalar, or the start of a container.
                JsonNode? value;
                int next;
                if (index >= text.Length)
                {
                    throw Stop(index);
                }

                var c = text[index];
                if (c is '{' or '[')
                {
                    if (stack.Count + 1 > options.MaxDepth)
                    {
                        throw new PyRecursionError("maximum recursion depth exceeded while decoding a JSON "
                                                   + (c == '{' ? "object" : "array") + " from a unicode string");
                    }

                    var frame = c == '{'
                        ? new Frame { IsObject = true, Pairs = [] }
                        : new Frame { IsObject = false, Items = [] };
                    index = SkipWhitespace(text, index + 1);
                    var close = c == '{' ? '}' : ']';
                    if (index <= _end && text[index] == close)
                    {
                        value = Finish(frame, stack.Count + 1);
                        next = index + 1;
                    }
                    else
                    {
                        stack.Push(frame);
                        if (frame.IsObject)
                        {
                            index = ReadKey(frame, index);
                        }

                        continue;
                    }
                }
                else
                {
                    (value, next) = ScanScalar(index);
                }

                // Deliver the value to the enclosing containers, closing as many as end here.
                while (true)
                {
                    if (stack.Count == 0)
                    {
                        return (value, next);
                    }

                    var frame = stack.Peek();
                    if (frame.IsObject)
                    {
                        frame.Pairs!.Add(new KeyValuePair<string, JsonNode?>(frame.Key!, value));
                    }
                    else
                    {
                        frame.Items!.Add(value);
                    }

                    index = SkipWhitespace(text, next);
                    var close = frame.IsObject ? '}' : ']';
                    if (index <= _end && text[index] == close)
                    {
                        stack.Pop();
                        value = Finish(frame, stack.Count + 1);
                        next = index + 1;
                        continue;
                    }

                    if (index > _end || text[index] != ',')
                    {
                        throw Error("Expecting ',' delimiter", index);
                    }

                    var comma = index;
                    index = SkipWhitespace(text, index + 1);
                    if (index <= _end && text[index] == close)
                    {
                        throw Error(
                            "Illegal trailing comma before end of " + (frame.IsObject ? "object" : "array"), comma);
                    }

                    if (frame.IsObject)
                    {
                        index = ReadKey(frame, index);
                    }

                    break;
                }
            }
        }

        /// <summary>Reads <c>"key" :</c> and the whitespace after it; returns the index of the value.</summary>
        private int ReadKey(Frame frame, int index)
        {
            if (index > _end || text[index] != '"')
            {
                throw Error("Expecting property name enclosed in double quotes", index);
            }

            (frame.Key, index) = ScanString(index + 1);
            index = SkipWhitespace(text, index);
            if (index > _end || text[index] != ':')
            {
                throw Error("Expecting ':' delimiter", index);
            }

            return SkipWhitespace(text, index + 1);
        }

        private JsonNode? Finish(Frame frame, int depth)
        {
            if (!frame.IsObject)
            {
                return frame.Items;
            }

            if (options.ObjectPairsHook is { } hook)
            {
                // The hook runs inside the object's own level (_parse_object_unicode).
                return CallHook(hook, (IReadOnlyList<KeyValuePair<string, JsonNode?>>)frame.Pairs!, depth);
            }

            var result = new JsonObject();
            foreach (var (key, value) in frame.Pairs!)
            {
                // dict(pairs): a repeated key keeps its first position and takes the last value.
                result[key] = value;
            }

            return result;
        }

        private (JsonNode? Value, int End) ScanScalar(int index)
        {
            var length = text.Length;
            switch (text[index])
            {
                case '"':
                    var (str, end) = ScanString(index + 1);
                    return (JsonValue.Create(str), end);
                case 'n' when index + 3 < length && text[index + 1] == 'u' && text[index + 2] == 'l' && text[index + 3] == 'l':
                    return (null, index + 4);
                case 't' when index + 3 < length && text[index + 1] == 'r' && text[index + 2] == 'u' && text[index + 3] == 'e':
                    return (JsonValue.Create(true), index + 4);
                case 'f' when index + 4 < length && string.CompareOrdinal(text, index + 1, "alse", 0, 4) == 0:
                    return (JsonValue.Create(false), index + 5);
                case 'N' when index + 2 < length && text[index + 1] == 'a' && text[index + 2] == 'N':
                    return (Constant("NaN"), index + 3);
                case 'I' when index + 7 < length && string.CompareOrdinal(text, index + 1, "nfinity", 0, 7) == 0:
                    return (Constant("Infinity"), index + 8);
                case '-' when index + 8 < length && string.CompareOrdinal(text, index + 1, "Infinity", 0, 8) == 0:
                    return (Constant("-Infinity"), index + 9);
            }

            return MatchNumber(index);
        }

        private JsonNode? Constant(string name)
        {
            if (options.ParseConstant is { } parse)
            {
                return CallHook(parse, name, _stack.Count);
            }

            Spend(CMethod); // _CONSTANTS.__getitem__
            return JsonValue.Create(name switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                _ => double.NegativeInfinity,
            });
        }

        private (JsonNode? Value, int End) MatchNumber(int start)
        {
            var index = start;
            if (text[index] == '-')
            {
                index++;
                if (index > _end)
                {
                    throw Stop(start);
                }
            }

            if (text[index] is >= '1' and <= '9')
            {
                index++;
                while (index <= _end && text[index] is >= '0' and <= '9')
                {
                    index++;
                }
            }
            else if (text[index] == '0')
            {
                index++;
            }
            else
            {
                throw Stop(start);
            }

            var isFloat = false;
            if (index < _end && text[index] == '.' && text[index + 1] is >= '0' and <= '9')
            {
                isFloat = true;
                index += 2;
                while (index <= _end && text[index] is >= '0' and <= '9')
                {
                    index++;
                }
            }

            if (index < _end && text[index] is 'e' or 'E')
            {
                var exponentStart = index;
                index++;
                if (index < _end && text[index] is '-' or '+')
                {
                    index++;
                }

                while (index <= _end && text[index] is >= '0' and <= '9')
                {
                    index++;
                }

                if (text[index - 1] is >= '0' and <= '9')
                {
                    isFloat = true;
                }
                else
                {
                    index = exponentStart;
                }
            }

            var number = text[start..index];
            if (isFloat)
            {
                return (JsonValue.Create(double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture)), index);
            }

            var digits = number.Length - (number[0] == '-' ? 1 : 0);
            if (digits > MaxIntDigits)
            {
                Spend(CMethod, "\nNormalization failed: type=ValueError args=<unknown>");
                throw new PyValueError(string.Format(CultureInfo.InvariantCulture,
                    "Exceeds the limit (4300 digits) for integer string conversion: value has {0} digits; use sys.set_int_max_str_digits() to increase the limit",
                    digits));
            }

            if (long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var small))
            {
                return (JsonValue.Create(small), index);
            }

            return (Int(BigInteger.Parse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)), index);
        }

        /// <summary><c>scanstring_unicode</c> with <c>strict=True</c>; <paramref name="end"/> is just after the quote.</summary>
        private (string Value, int End) ScanString(int end)
        {
            var begin = end - 1;
            var length = text.Length;
            StringBuilder? builder = null;
            while (true)
            {
                var next = end;
                var c = '\0';
                for (; next < length; next++)
                {
                    c = text[next];
                    if (c is '"' or '\\')
                    {
                        break;
                    }

                    if (c <= 0x1f)
                    {
                        throw Error("Invalid control character at", next);
                    }
                }

                if (next >= length || (c != '"' && c != '\\'))
                {
                    throw Error("Unterminated string starting at", begin);
                }

                if (c == '"' && builder is null)
                {
                    return (text[end..next], next + 1);
                }

                builder ??= new StringBuilder();
                builder.Append(text, end, next - end);
                next++;
                if (c == '"')
                {
                    end = next;
                    break;
                }

                if (next == length)
                {
                    throw Error("Unterminated string starting at", begin);
                }

                c = text[next];
                if (c != 'u')
                {
                    end = next + 1;
                    var unescaped = c switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => '\0',
                    };
                    if (unescaped == '\0')
                    {
                        throw Error("Invalid \\escape", end - 2);
                    }

                    builder.Append(unescaped);
                    continue;
                }

                next++;
                end = next + 4;
                if (end > length)
                {
                    throw Error("Invalid \\uXXXX escape", next - 1);
                }

                var code = Hex4(next, end);
                next = end;
                if (code is >= 0xD800 and <= 0xDBFF && end + 6 < length && text[next] == '\\' && text[next + 1] == 'u')
                {
                    end += 6;
                    var low = Hex4(next + 2, end);
                    if (low is >= 0xDC00 and <= 0xDFFF)
                    {
                        builder.Append((char)code).Append((char)low);
                        continue;
                    }

                    end -= 6;
                }

                builder.Append((char)code);
            }

            return (builder.ToString(), end);
        }

        private int Hex4(int start, int end)
        {
            var value = 0;
            for (var i = start; i < end; i++)
            {
                var digit = text[i];
                value <<= 4;
                value |= digit switch
                {
                    >= '0' and <= '9' => digit - '0',
                    >= 'a' and <= 'f' => digit - 'a' + 10,
                    >= 'A' and <= 'F' => digit - 'A' + 10,
                    _ => throw Error("Invalid \\uXXXX escape", end - 5),
                };
            }

            return value;
        }
    }
}
