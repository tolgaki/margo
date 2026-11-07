using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Margo.Cloud.Core.Tests.Fixtures;

namespace Margo.Cloud.Core.Tests;

/// <summary>The json.loads/json.dumps behaviour the shared blob and queue formats rely on. Expected
/// strings come from Python 3.13.</summary>
public sealed class PythonJsonTests
{
    [Fact]
    public void Floats_print_like_python_repr()
    {
        const string input = "[1.0, 1e16, 1e15, 0.0001, 0.00001, -0.0, 1e400, -1e400, 5e-324, 123456789012345680, 1.5e300, 0.1, "
            + "2.5e-7, 12345678901234567890123, -0, 1E2, 1.7976931348623157e308, 100.0, 1e22, 9007199254740993.0, "
            + "1234567890123456.7, 12345678901234567.0]";
        const string expected = "[1.0, 1e+16, 1000000000000000.0, 0.0001, 1e-05, -0.0, Infinity, -Infinity, 5e-324, 123456789012345680, "
            + "1.5e+300, 0.1, 2.5e-07, 12345678901234567890123, 0, 100.0, 1.7976931348623157e+308, 100.0, 1e+22, 9007199254740992.0, "
            + "1234567890123456.8, 1.2345678901234568e+16]";
        Assert.Equal(expected, PythonJson.Dumps(PythonJson.Loads(input)));
    }

    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(0.5, "0.5")]
    [InlineData(-2.0, "-2.0")]
    [InlineData(1e-4, "0.0001")]
    [InlineData(-1.5e-10, "-1.5e-10")]
    [InlineData(1e16, "1e+16")]
    [InlineData(9999999999999998.0, "9999999999999998.0")]
    [InlineData(123.456, "123.456")]
    public void Format_float(double value, string expected)
    {
        Assert.Equal(expected, PythonJson.FormatFloat(value));
    }

    [Fact]
    public void Indented_sorted_output_matches_python()
    {
        var value = new JsonObject
        {
            ["b"] = new JsonArray(),
            ["a"] = new JsonObject(),
            ["c"] = new JsonArray(1, new JsonObject { ["z"] = null, ["y"] = true }),
            ["d"] = "caf" + U(0xe9) + " " + U(0x1f600) + " " + U(0x7f) + " " + U(0x01) + " \t\"\\/",
        };
        const string expected = """
            {
             "a": {},
             "b": [],
             "c": [
              1,
              {
               "y": true,
               "z": null
              }
             ],
             "d": "caf\u00e9 \ud83d\ude00 \u007f \u0001 \t\"\\/"
            }
            """;
        Assert.Equal(Lf(expected), PythonJson.Dumps(value, sortKeys: true, indent: 1));
    }

    [Fact]
    public void Without_ensure_ascii_only_controls_quotes_and_backslashes_are_escaped()
    {
        var value = new JsonArray("caf" + U(0xe9) + " " + U(0x1f600) + " " + U(0x7f) + " " + U(0x01) + " \t\"\\/", null);
        var expected = "[\"caf" + U(0xe9) + " " + U(0x1f600) + " " + U(0x7f) + " \\u0001 \\t\\\"\\\\/\", null]";
        Assert.Equal(expected, PythonJson.Dumps(value, ensureAscii: false));
        Assert.Equal("\"\\b\\f\\n\\r\\u001f\"", PythonJson.Dumps(JsonValue.Create("\b\f\n\r" + U(0x1f))));
    }

    [Fact]
    public void Keys_sort_by_code_point_not_utf16_unit()
    {
        // Python: sorted(["\ue000", "\U0001f600", "a", "Z"]) puts U+E000 before U+1F600; UTF-16 order would not.
        var value = new JsonObject { [U(0x1f600)] = 1, [U(0xe000)] = 2, ["a"] = 3, ["Z"] = 4 };
        Assert.Equal("{\"Z\": 4, \"a\": 3, \"\\ue000\": 2, \"\\ud83d\\ude00\": 1}", PythonJson.Dumps(value, sortKeys: true));
        Assert.Equal("{\"\\ud83d\\ude00\": 1, \"\\ue000\": 2, \"a\": 3, \"Z\": 4}", PythonJson.Dumps(value));
    }

    [Fact]
    public void Repeated_keys_keep_the_first_position_and_last_value()
    {
        Assert.Equal("""{"a": 3, "b": 2}""", PythonJson.Dumps(PythonJson.Loads("""{"a":1,"b":2,"a":3}""")));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("[Infinity]")]
    [InlineData("\"\\ud800\"")]
    [InlineData("{\"a\": 1,}")]
    [InlineData("")]
    [InlineData("1 2")]
    public void Unsupported_or_invalid_text_is_a_json_exception(string text)
    {
        Assert.ThrowsAny<JsonException>(() => PythonJson.Loads(text));
    }

    [Fact]
    public void Raw_lone_surrogates_are_a_json_exception_like_escaped_ones()
    {
        // Documented deviation: Python accepts both; System.Text.Json refuses to transcode a raw one
        // (an ArgumentException) and to materialise an escaped one. Callers catch JsonException only.
        Assert.ThrowsAny<JsonException>(() => PythonJson.Loads("[\"" + LoneSurrogate + "\"]"));
        Assert.ThrowsAny<JsonException>(() => PythonJson.Loads("{\"" + LoneSurrogate + "\": 1}"));
        Assert.ThrowsAny<JsonException>(() => PythonJson.Loads("{\"\\ud800\": 1}"));
        Assert.Throws<ArgumentNullException>(() => PythonJson.Loads(null!));
    }

    [Fact]
    public void Integers_beyond_pythons_digit_limit_are_refused_like_python()
    {
        // Python 3.13 limits int/str conversion to 4300 digits (sys.get_int_max_str_digits()):
        // json.loads raises ValueError for a longer int literal, json.dumps for a longer int.
        var limit = new string('9', 4300);
        Assert.Equal(limit, PythonJson.Dumps(PythonJson.Loads(limit)));
        Assert.Equal("[-" + limit + "]", PythonJson.Dumps(PythonJson.Loads("[-" + limit + "]")));
        Assert.ThrowsAny<JsonException>(() => PythonJson.Loads("1" + limit));
        Assert.ThrowsAny<JsonException>(() => PythonJson.Loads("{\"n\": [-1" + limit + "]}"));
        Assert.Equal("Infinity", PythonJson.Dumps(PythonJson.Loads("1" + limit + ".0"))); // a float: no digit limit
        Assert.Equal("Infinity", PythonJson.Dumps(PythonJson.Loads("1" + limit + "e0")));
        var error = Assert.Throws<ArgumentException>(() => PythonJson.Dumps(JsonNode.Parse("[1" + limit + "]")));
        Assert.StartsWith("Exceeds the limit (4300 digits) for integer string conversion", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => PythonJson.Dumps(JsonNode.Parse("-1" + new string('0', 4300))));
    }

    [Fact]
    public void Int_of_text_has_pythons_digit_limit()
    {
        // Python: int() of text with more than 4300 digits raises ValueError; underscores, signs and
        // surrounding spaces are not digits, leading zeros are.
        var limit = new string('1', 4300);
        Assert.True(PythonValues.TryParseInt(limit, out var parsed));
        Assert.Equal(limit, parsed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(PythonValues.TryParseInt(" -" + limit + new string(' ', 5000), out _));
        Assert.False(PythonValues.TryParseInt(limit + "1", out _));
        Assert.False(PythonValues.TryParseInt("0" + limit, out _));
        Assert.False(PythonValues.TryParseInt(string.Concat(Enumerable.Repeat("1_", 4300)) + "1", out _));
        Assert.True(PythonValues.TryParseInt(string.Concat(Enumerable.Repeat("1_", 4299)) + "1", out _));
    }

    [Fact]
    public void Deep_nesting_up_to_the_limit_is_accepted()
    {
        var depth = PythonJson.MaxDepth;
        var text = new string('[', depth) + new string(']', depth);
        Assert.Equal(text, PythonJson.Dumps(PythonJson.Loads(text))); // one item per list: no separators
        Assert.ThrowsAny<JsonException>(() => PythonJson.Loads("[" + text + "]"));
    }

    [Fact]
    public void Scalars_and_python_ints_round_trip()
    {
        Assert.Null(PythonJson.Loads("null"));
        Assert.Equal("\"x\"", PythonJson.Dumps(PythonJson.Loads(" \"x\" ")));
        Assert.Equal("-12345678901234567890", PythonJson.Dumps(PythonJson.Loads("-12345678901234567890")));
        Assert.Equal("[true, false, null, 5, 2.5]", PythonJson.Dumps(new JsonArray(true, false, null, 5L, 2.5)));
    }
}
