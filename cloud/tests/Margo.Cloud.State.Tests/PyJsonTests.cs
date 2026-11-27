using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.State.Tests;

public sealed class PyJsonTests
{
    public static TheoryData<string, int?, string?, string?, bool, bool, bool, string> DumpsCases => PythonFixtures.Dumps;

    public static TheoryData<string, string> LoadsCases => PythonFixtures.Loads;

    public static TheoryData<string, string> ParseJsonCases => PythonFixtures.ParseJson;

    public static TheoryData<long, string> FloatCases => PythonFixtures.FloatRepr;

    [Theory]
    [MemberData(nameof(DumpsCases))]
    public void Dumps_matches_cpython(string input, int? indent, string? itemSeparator, string? keySeparator, bool sortKeys,
                                      bool ensureAscii, bool allowNan, string expected)
    {
        string actual;
        try
        {
            actual = PyJson.Dumps(PyJson.Loads(input), indent, itemSeparator, keySeparator, sortKeys, ensureAscii, allowNan);
        }
        catch (PyValueError ex)
        {
            actual = "!" + ex.PythonType + ": " + ex.Message;
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(LoadsCases))]
    public void Loads_matches_cpython_values_and_error_positions(string input, string expected)
    {
        string actual;
        try
        {
            actual = PyJson.Dumps(PyJson.Loads(input));
        }
        catch (PyValueError ex)
        {
            actual = "!" + ex.PythonType + ": " + ex.Message;
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(ParseJsonCases))]
    public void Parse_json_and_canonical_json_match_margo_store(string input, string expected)
    {
        string actual;
        try
        {
            actual = StateStore.CanonicalJson(StateStore.ParseJson(input));
        }
        catch (StateError ex)
        {
            actual = "!" + ex.PythonType + ": " + ex.Message;
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(FloatCases))]
    public void Float_repr_is_shortest_round_trip_in_python_layout(long bits, string expected) =>
        Assert.Equal(expected, PyJson.FloatRepr(BitConverter.Int64BitsToDouble(bits)));

    [Fact]
    public void Int_and_float_stay_distinct()
    {
        Assert.Equal(PyKind.Int, PyJson.KindOf(PyJson.Loads("1")));
        Assert.Equal(PyKind.Float, PyJson.KindOf(PyJson.Loads("1.0")));
        Assert.Equal(PyKind.Float, PyJson.KindOf(PyJson.Loads("1e2")));
        Assert.Equal("[1, 1.0, 100.0, -0.0, 0]", PyJson.Dumps(PyJson.Loads("[1, 1.0, 1e2, -0.0, -0]")));
        Assert.Equal(BigInteger.Parse("123456789012345678901234567890"), PyJson.AsInt(PyJson.Loads("123456789012345678901234567890")));
    }

    [Fact]
    public void Clr_values_and_system_text_json_values_are_classified_like_python()
    {
        var obj = new JsonObject
        {
            ["int"] = 5,
            ["long"] = 5L,
            ["double"] = 5.0,
            ["float"] = 1.5f,
            ["bool"] = true,
            ["text"] = "x",
            ["none"] = null,
            ["big"] = PyJson.Int(BigInteger.Pow(10, 20)),
        };
        Assert.Equal("{\"int\": 5, \"long\": 5, \"double\": 5.0, \"float\": 1.5, \"bool\": true, \"text\": \"x\", \"none\": null, \"big\": 100000000000000000000}",
                     PyJson.Dumps(obj));
        var parsed = JsonNode.Parse("{\"a\":1,\"b\":1.0,\"c\":100000000000000000000,\"d\":\"s\",\"e\":false}");
        Assert.Equal("{\"a\": 1, \"b\": 1.0, \"c\": 100000000000000000000, \"d\": \"s\", \"e\": false}", PyJson.Dumps(parsed));
    }

    [Fact]
    public void Duplicate_keys_keep_first_position_and_last_value_by_default()
    {
        Assert.Equal("{\"a\": 3, \"b\": 2}", PyJson.Dumps(PyJson.Loads("{\"a\": 1, \"b\": 2, \"a\": 3}")));
        var error = Assert.Throws<StateError>(() => StateStore.ParseJson("{\"id\":\"one\",\"id\":\"two\"}"));
        Assert.Equal("duplicate JSON object key", error.Message);
    }

    [Fact]
    public void Sort_keys_orders_by_code_point_not_utf16()
    {
        var obj = new JsonObject { ["\U0001F600"] = 1, ["\uFFFF"] = 2, ["a"] = 3 };
        Assert.Equal("{\"a\":3,\"\uFFFF\":2,\"\U0001F600\":1}", StateStore.CanonicalJson(obj));
    }

    [Fact]
    public void Ensure_ascii_escapes_astral_characters_as_surrogate_pairs_and_del()
    {
        Assert.Equal("\"\\ud83d\\ude00\\u007f\\u00e9\"", PyJson.Dumps(JsonValue.Create("\U0001F600\u007f\u00E9")));
        Assert.Equal("\"\U0001F600\u007f\u00E9\"", PyJson.Dumps(JsonValue.Create("\U0001F600\u007f\u00E9"), ensureAscii: false));
    }

    [Fact]
    public void Non_finite_numbers_follow_allow_nan()
    {
        Assert.Equal("[NaN, Infinity, -Infinity]",
                     PyJson.Dumps(new JsonArray(double.NaN, double.PositiveInfinity, double.NegativeInfinity)));
        var error = Assert.Throws<PyValueError>(() => PyJson.Dumps(JsonValue.Create(double.NaN), allowNan: false));
        Assert.Equal("Out of range float values are not JSON compliant: nan", error.Message);
        Assert.Throws<StateError>(() => StateStore.CanonicalJson(new JsonObject { ["value"] = double.PositiveInfinity }));
        Assert.Equal("non-finite JSON number", Assert.Throws<StateError>(() => StateStore.ParseJson("{\"number\":NaN}")).Message);
        Assert.Equal("value must contain finite JSON data", Assert.Throws<StateError>(() => StateStore.ParseJson("[1e400]")).Message);
    }

    [Fact]
    public void Nesting_limits_raise_recursion_error_like_cpython()
    {
        var ok = new string('[', PyJson.MaxDepth) + new string(']', PyJson.MaxDepth);
        Assert.Equal(ok, PyJson.Dumps(PyJson.Loads(ok)));
        var deep = new string('[', PyJson.MaxDepth + 1) + new string(']', PyJson.MaxDepth + 1);
        Assert.Equal("maximum recursion depth exceeded while decoding a JSON array from a unicode string",
                     Assert.Throws<PyRecursionError>(() => PyJson.Loads(deep)).Message);
        JsonNode node = new JsonArray();
        for (var i = 0; i < PyJson.MaxDepth; i++)
        {
            node = new JsonArray(node);
        }

        Assert.Throws<PyRecursionError>(() => PyJson.Dumps(node));
        var hooked = string.Concat(Enumerable.Repeat("{\"a\":", 9997)) + "1" + new string('}', 9997);
        Assert.Throws<PyRecursionError>(() => StateStore.ParseJson(hooked));
        Assert.NotNull(PyJson.Loads(hooked));
    }

    [Fact]
    public void Integer_digit_limit_matches_cpython()
    {
        var error = Assert.Throws<PyValueError>(() => PyJson.Loads(new string('1', 4301)));
        Assert.StartsWith("Exceeds the limit (4300 digits) for integer string conversion: value has 4301 digits", error.Message, StringComparison.Ordinal);
        Assert.Throws<PyValueError>(() => PyJson.Dumps(PyJson.Int(BigInteger.Pow(10, 4300))));
    }

    [Fact]
    public void Python_equality_and_truthiness()
    {
        Assert.True(PyJson.PyEquals(PyJson.Loads("1"), PyJson.Loads("1.0")));
        Assert.True(PyJson.PyEquals(PyJson.Loads("true"), PyJson.Loads("1")));
        Assert.True(PyJson.PyEquals(PyJson.Loads("{\"a\":1,\"b\":[1,2]}"), PyJson.Loads("{\"b\":[1,2.0],\"a\":1}")));
        Assert.False(PyJson.PyEquals(PyJson.Loads("[1,2]"), PyJson.Loads("[2,1]")));
        Assert.False(PyJson.PyEquals(PyJson.Loads("NaN"), PyJson.Loads("NaN")));
        Assert.False(PyJson.PyEquals(PyJson.Loads("\"1\""), PyJson.Loads("1")));
        Assert.False(PyJson.Truthy(PyJson.Loads("0.0")));
        Assert.False(PyJson.Truthy(PyJson.Loads("{}")));
        Assert.True(PyJson.Truthy(PyJson.Loads("\" \"")));
        Assert.Equal("bool", PyJson.TypeName(PyJson.Loads("false")));
        Assert.True(PyJson.IsIntOrBool(PyJson.Loads("false")));
        Assert.False(PyJson.IsInt(PyJson.Loads("false")));
    }
}
