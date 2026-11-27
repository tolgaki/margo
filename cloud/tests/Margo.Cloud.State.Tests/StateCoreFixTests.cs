using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.State.Tests;

/// <summary>
/// The State.Core defects the port reviews reported, fixed when the ports' Python-compatibility copies were
/// consolidated. Each test failed on the build before the fix; expected values are CPython 3.13.16 measurements.
/// The parity scenarios <c>core/json_recursion_budget.json</c> and <c>core/int_whitespace.json</c> judge the
/// observable ones through the scripts.
/// </summary>
public sealed class StateCoreFixTests
{
    private const string TimestampError = "timestamp must be an ISO-8601 datetime with an explicit timezone";

    public static TheoryData<string> ContainerTimestamps => ["[]", "[\"2026-01-01T00:00:00Z\"]", "{}", "{\"at\": \"2026-01-01T00:00:00Z\"}"];

    [Theory]
    [MemberData(nameof(ContainerTimestamps))]
    public void Validate_timestamp_refuses_lists_and_dicts_with_the_timestamp_state_error(string json)
    {
        // Review finding (Memory, Proactive): a JSON list or dict raised TypeError "not a scalar"; Python's
        // isinstance(value, str) check raises the StateError, which the scripts print.
        var error = Assert.Throws<StateError>(() => StateStore.ValidateTimestamp(PyJson.Loads(json)));
        Assert.Equal(TimestampError, error.Message);
        Assert.Equal(TimestampError, Assert.Throws<StateError>(() => StateStore.ValidateTimestamp(JsonValue.Create(5))).Message);
        Assert.Equal("2026-01-01T00:00:00.000000+00:00", StateStore.ValidateTimestamp(JsonValue.Create("2026-01-01T00:00:00Z")));
    }

    [Fact]
    public void Timedelta_days_range_is_symmetric()
    {
        // Review finding (Proactive): totals in (-1000000000 days, -999999999 days) were accepted.
        const long day = 86_400_000_000;
        Assert.Equal("-999999999 days, 0:00:00", PyTimeDelta.FromMicroseconds(-(Int128)999_999_999 * day).ToString());
        Assert.Equal("999999999 days, 23:59:59.999999", PyTimeDelta.FromMicroseconds(((Int128)1_000_000_000 * day) - 1).ToString());
        foreach (var micros in new[] { (-(Int128)999_999_999 * day) - 1, (-(Int128)1_000_000_000 * day) + 1 })
        {
            Assert.Equal("days=-1000000000; must have magnitude <= 999999999",
                         Assert.Throws<PyOverflowError>(() => PyTimeDelta.FromMicroseconds(micros)).Message);
        }

        Assert.Equal("days=1000000000; must have magnitude <= 999999999",
                     Assert.Throws<PyOverflowError>(() => PyTimeDelta.FromMicroseconds((Int128)1_000_000_000 * day)).Message);
        Assert.Equal("days=-1000000000; must have magnitude <= 999999999",
                     Assert.Throws<PyOverflowError>(() => PyTimeDelta.FromDays(-999_999_999) - PyTimeDelta.FromMicroseconds(1)).Message);
    }

    public static TheoryData<string, string> StdinDecodeErrors => new()
    {
        { "truncated after 20000", "'utf-8' codec can't decode bytes in position 20000-20001: unexpected end of data" },
        { "0xff after 9000", "'utf-8' codec can't decode byte 0xff in position 9000: invalid start byte" },
        { "bad continuation", "'utf-8' codec can't decode byte 0xe2 in position 2: invalid continuation byte" },
        { "split at 8192", "'utf-8' codec can't decode bytes in position 8191-8193: invalid continuation byte" },
        { "surrogate", "'utf-8' codec can't decode byte 0xed in position 0: invalid continuation byte" },
        { "truncated after astral", "'utf-8' codec can't decode byte 0xc3 in position 8195: unexpected end of data" },
    };

    [Theory]
    [MemberData(nameof(StdinDecodeErrors))]
    public void Stdin_read_reports_cpythons_reason_and_absolute_position(string name, string message)
    {
        // Review finding (Helpers): margo-state's stdin decoded through a StreamReader, which always said "invalid
        // start byte" at a position relative to its buffer. sys.stdin.read() decodes all remaining bytes at once.
        byte[] bytes = name switch
        {
            "truncated after 20000" => [.. Enumerable.Repeat((byte)' ', 20000), 0xE2, 0x82],
            "0xff after 9000" => [.. Enumerable.Repeat((byte)'a', 9000), 0xFF, (byte)'b'],
            "bad continuation" => [(byte)'a', (byte)'b', 0xE2, 0x28, 0xA1],
            "split at 8192" => [.. Enumerable.Repeat((byte)'a', 8191), 0xF0, 0x9F, 0x98, (byte)'x'],
            "surrogate" => [0xED, 0xA0, 0x80],
            _ => [.. Enumerable.Repeat((byte)'a', 8191), 0xF0, 0x9F, 0x98, 0x80, 0xC3],
        };
        using var stdin = PyIO.StdinReader(new MemoryStream(bytes));
        var error = Assert.Throws<PyUnicodeError>(() => PyIO.ReadText(stdin));
        Assert.Equal("UnicodeDecodeError", error.PythonType);
        Assert.Equal(message, error.Message);
        Assert.IsAssignableFrom<PyValueError>(error); // the scripts' ValueError handlers catch it
    }

    [Fact]
    public void Stdin_read_of_n_counts_code_points_and_keeps_the_rest()
    {
        using var stdin = PyIO.StdinReader(new MemoryStream(Encoding.UTF8.GetBytes("a\U0001F600bc\u00e9")));
        Assert.Equal("a\U0001F600", PyIO.ReadText(stdin, 2));
        Assert.Equal("bc\u00e9", PyIO.ReadText(stdin));
        Assert.Equal("", PyIO.ReadText(stdin));
    }

    private static string Nest(int depth, string inner) => new string('[', depth) + inner + new string(']', depth);

    [Fact]
    public void Json_loads_models_the_c_recursion_budget_for_every_caller()
    {
        // Review finding (Helpers): PyJson.Loads modelled the container limit only. Called from Python code the
        // budget is 9998 levels: a decode error built in C needs four beyond the open containers, NaN one.
        Assert.Throws<PyJsonDecodeError>(() => PyJson.Loads(Nest(9994, "1 2")));
        Assert.Equal("maximum recursion depth exceeded while calling a Python object",
                     Assert.Throws<PyRecursionError>(() => PyJson.Loads(Nest(9995, "1 2"))).Message);
        Assert.Equal("maximum recursion depth exceeded", Assert.Throws<PyRecursionError>(() => PyJson.Loads(Nest(9996, "\"abc"))).Message);
        Assert.NotNull(PyJson.Loads(Nest(9997, "NaN")));
        Assert.Throws<PyRecursionError>(() => PyJson.Loads(Nest(9998, "-Infinity")));
        Assert.Throws<PyJsonDecodeError>(() => PyJson.Loads(Nest(9997, ",")));
        Assert.Equal("maximum recursion depth exceeded while calling a Python object\nNormalization failed: type=StopIteration args=<unknown>",
                     Assert.Throws<PyRecursionError>(() => PyJson.Loads(Nest(9998, ","))).Message);
        Assert.StartsWith("Exceeds the limit (4300 digits)",
                          Assert.Throws<PyValueError>(() => PyJson.Loads(Nest(9997, new string('7', 4400)))).Message, StringComparison.Ordinal);
        Assert.EndsWith("\nNormalization failed: type=ValueError args=<unknown>",
                        Assert.Throws<PyRecursionError>(() => PyJson.Loads(Nest(9998, new string('7', 4400)))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_json_hooks_cost_python_call_levels()
    {
        // margo_store.parse_json's object_pairs_hook and parse_constant are Python functions: two levels to call,
        // a third to raise their StateError (measured with CPython 3.13.16).
        Assert.Equal("duplicate JSON object key",
                     Assert.Throws<StateError>(() => StateStore.ParseJson(Nest(9994, "{\"k\": 1, \"k\": 2}"))).Message);
        Assert.Equal("maximum recursion depth exceeded while calling a Python object",
                     Assert.Throws<PyRecursionError>(() => StateStore.ParseJson(Nest(9995, "{\"k\": 1, \"k\": 2}"))).Message);
        Assert.Equal("maximum recursion depth exceeded",
                     Assert.Throws<PyRecursionError>(() => StateStore.ParseJson(Nest(9996, "{\"k\": 1}"))).Message);
        Assert.Equal("non-finite JSON number", Assert.Throws<StateError>(() => StateStore.ParseJson(Nest(9995, "NaN"))).Message);
        Assert.Equal("maximum recursion depth exceeded while calling a Python object",
                     Assert.Throws<PyRecursionError>(() => StateStore.ParseJson(Nest(9996, "NaN"))).Message);
    }

    [Theory]
    [InlineData("\u001c5")]
    [InlineData(" 5\u001f")]
    [InlineData("\u001d-7")]
    [InlineData("1_0\u001e")]
    public void Int_parsing_does_not_strip_information_separators(string text)
    {
        // Review finding (Router, checked here): str.isspace() is true for U+001C..U+001F, but int() strips only
        // ASCII whitespace after mapping non-ASCII spaces to ' ', so they make the literal invalid.
        Assert.Equal("invalid literal for int() with base 10: " + PyRepr.Str(text),
                     Assert.Throws<PyValueError>(() => PyArgumentParser.ParsePyInt(text)).Message);
    }

    [Theory]
    [InlineData(" \t\u000b\u000c5\r\n", 5)]
    [InlineData("\u00855\u3000", 5)]
    [InlineData("\u2028\u0661\u0662", 12)]
    [InlineData("\u00a0-1_000\u2029", -1000)]
    public void Int_parsing_strips_ascii_and_unicode_spaces(string text, long expected)
    {
        Assert.Equal(new BigInteger(expected), PyArgumentParser.ParsePyInt(text));
    }
}
