using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Margo.Cloud.WorkIQ.Tests;

/// <summary>The Python value rules the transport relies on, including where System.Text.Json differs.</summary>
public sealed class PythonJsonTests
{
    [Fact]
    public void Duplicate_keys_keep_the_last_value_at_the_first_position()
    {
        var parsed = PythonJson.Loads("""{"a":1,"b":2,"a":{"c":3}}""")!.AsObject();
        Assert.Equal(new[] { "a", "b" }, parsed.Select(pair => pair.Key));
        Assert.Equal(3, (int)parsed["a"]!["c"]!);
    }

    [Fact]
    public void Lone_surrogate_escapes_survive_as_in_python()
    {
        var text = (string)PythonJson.Loads("""["\ud800x", "\uD83D\uDE00", "\u00E9\n\/\"\\"]""")![0]!;
        Assert.Equal("\ud800x", text);
        var array = PythonJson.Loads("""["\ud800x", "\uD83D\uDE00", "\u00E9\n\/\"\\"]""")!.AsArray();
        Assert.Equal("\U0001F600", (string)array[1]!);
        Assert.Equal("\u00E9\n/\"\\", (string)array[2]!);
    }

    [Theory]
    [InlineData("\uFEFF{}")]
    [InlineData("")]
    [InlineData("{} {}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("/* c */ {}")]
    [InlineData("NaN")] // Python accepts NaN and Infinity; System.Text.Json cannot (see PORTING.md)
    [InlineData("'a'")]
    public void Invalid_documents_are_json_errors(string text) =>
        Assert.ThrowsAny<JsonException>(() => PythonJson.Loads(text));

    [Fact]
    public void Scalars_parse_at_top_level()
    {
        Assert.Null(PythonJson.Loads("null"));
        Assert.Equal("1.50e2", PythonJson.NumberText((JsonValue)PythonJson.Loads(" 1.50e2 ")!));
        Assert.True((bool)PythonJson.Loads("true")!);
        Assert.Equal("x", (string)PythonJson.Loads("\"x\"")!);
    }

    [Fact]
    public void Nesting_is_bounded_and_not_a_json_error()
    {
        var deepest = new string('[', PythonJson.MaxNesting) + new string(']', PythonJson.MaxNesting);
        Assert.IsType<JsonArray>(PythonJson.Loads(deepest));
        var tooDeep = new string('[', PythonJson.MaxNesting + 1) + new string(']', PythonJson.MaxNesting + 1);
        Assert.Throws<TransportFaultException>(() => PythonJson.Loads(tooDeep));

        // Python's RecursionError is not a ValueError: neither the JSON nor the SSE path absorbs it.
        var body = Encoding.UTF8.GetBytes("{\"id\":1,\"result\":" + tooDeep + "}");
        Assert.Throws<TransportFaultException>(() => McpMessages.ParseBody(body, "application/json", 1));
        var events = Encoding.UTF8.GetBytes("data: " + tooDeep + "\n\n");
        Assert.Throws<TransportFaultException>(() => McpMessages.ParseBody(events, "text/event-stream", 1));
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("-0", false)]
    [InlineData("0.0", false)]
    [InlineData("0e10", false)]
    [InlineData("1e-400", false)]
    [InlineData("1e400", true)]
    [InlineData("-1", true)]
    [InlineData("100000000000000000000000000000", true)]
    [InlineData("\"\"", false)]
    [InlineData("\"0\"", true)]
    [InlineData("[]", false)]
    [InlineData("[0]", true)]
    [InlineData("{}", false)]
    [InlineData("{\"a\":null}", true)]
    public void Truthiness_follows_python(string json, bool truthy) =>
        Assert.Equal(truthy, PythonJson.IsTruthy(PythonJson.Loads(json)));

    [Theory]
    [InlineData("403", true, 403)]
    [InlineData("-0", true, 0)]
    [InlineData("true", true, 1)]
    [InlineData("false", true, 0)]
    [InlineData("403.0", false, 0)]
    [InlineData("4.03e2", false, 0)]
    [InlineData("\"403\"", false, 0)]
    [InlineData("null", false, 0)]
    [InlineData("[403]", false, 0)]
    public void Integers_follow_python_isinstance_int(string json, bool isInt, long value)
    {
        Assert.Equal(isInt, PythonJson.TryGetInteger(PythonJson.Loads(json), out var parsed));
        if (isInt)
        {
            Assert.Equal(value, (long)parsed);
        }
    }

    [Theory]
    [InlineData("7", 7, true)]
    [InlineData("7.0", 7, true)]
    [InlineData("7e0", 7, true)]
    [InlineData("7.5", 7, false)]
    [InlineData("true", 1, true)]
    [InlineData("true", 7, false)]
    [InlineData("\"7\"", 7, false)]
    [InlineData("null", 7, false)]
    [InlineData("[7]", 7, false)]
    public void Equality_with_an_int_follows_python(string json, long id, bool equal) =>
        Assert.Equal(equal, PythonJson.EqualsInteger(PythonJson.Loads(json), id));

    [Fact]
    public void Whitespace_follows_python_str_isspace()
    {
        foreach (var c in " \t\n\r\v\f\x1c\x1d\x1e\x1f\x85\xa0\u1680\u2000\u200A\u2028\u2029\u202F\u205F\u3000")
        {
            Assert.True(PythonJson.IsSpace(c), "U+" + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var c in "a\u200B\uFEFF\u180E\0")
        {
            Assert.False(PythonJson.IsSpace(c), "U+" + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal("x \u00A0y", PythonJson.Strip("\x1c\u3000x \u00A0y\x85 "));
        Assert.Equal("x ", PythonJson.LStrip("\t\u00A0x "));
    }
}
