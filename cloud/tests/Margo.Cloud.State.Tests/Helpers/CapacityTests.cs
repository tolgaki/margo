using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Helpers;
using Xunit;

namespace Margo.Cloud.State.Tests.Helpers;

/// <summary>tests/test_capacity.py, ported case for case, plus the command line and Python number rules.</summary>
public sealed class CapacityTests
{
    private static JsonObject Interval(string start, string end) => new() { ["start"] = start, ["end"] = end };

    private static JsonObject Base() => new()
    {
        ["coverage"] = "complete",
        ["working"] = new JsonArray(Interval("2026-09-07T09:00:00-07:00", "2026-09-07T17:00:00-07:00")),
        ["busy"] = new JsonArray(),
    };

    private static double Number(JsonObject result, string key) => PyJson.AsDouble(result[key]);

    [Fact]
    public void Overlap_clipping_and_protected_not_double_subtracted()
    {
        var data = Base();
        data["busy"] = new JsonArray(
            Interval("2026-09-07T08:00:00-07:00", "2026-09-07T10:00:00-07:00"),
            Interval("2026-09-07T09:30:00-07:00", "2026-09-07T11:00:00-07:00"),
            Interval("2026-09-07T16:00:00-07:00", "2026-09-07T18:00:00-07:00"));
        data["protected"] = new JsonArray(Interval("2026-09-07T10:00:00-07:00", "2026-09-07T12:00:00-07:00"));
        var result = Capacity.Calculate(data);
        Assert.Equal(180, Number(result, "busy_minutes"));
        Assert.Equal(300, Number(result, "available_minutes"));
        Assert.Equal(60, Number(result, "protected_available_minutes"));
        Assert.Equal(60, Number(result, "protected_conflict_minutes"));
    }

    [Fact]
    public void Dst_uses_elapsed_time()
    {
        var data = new JsonObject
        {
            ["coverage"] = "complete",
            ["busy"] = new JsonArray(),
            ["working"] = new JsonArray(Interval("2026-11-01T00:00:00-07:00", "2026-11-01T04:00:00-08:00")),
        };
        Assert.Equal(300, Number(Capacity.Calculate(data), "working_minutes"));
    }

    [Fact]
    public void Unknown_and_partial_are_not_feasible()
    {
        var data = Base();
        data["coverage"] = "partial";
        Assert.Null(Capacity.Calculate(data)["fits_total_capacity"]);
        data["coverage"] = "complete";
        data["estimates"] = new JsonArray(new JsonObject { ["id"] = "outcome-1", ["minutes"] = null });
        Assert.Null(Capacity.Calculate(data)["fits_total_capacity"]);
    }

    [Fact]
    public void Overflow_and_exact_focus_threshold()
    {
        var data = Base();
        data["busy"] = new JsonArray(Interval("2026-09-07T10:30:00-07:00", "2026-09-07T17:00:00-07:00"));
        data["estimates"] = new JsonArray(new JsonObject { ["id"] = "outcome-1", ["minutes"] = 120 });
        var result = Capacity.Calculate(data);
        Assert.False((bool)result["fits_total_capacity"]!);
        Assert.Equal(30, Number(result, "minimum_overflow_minutes"));
        Assert.Single(result["focus_blocks"]!.AsArray());
        Assert.Equal("not_allocated", (string)result["allocation_status"]!);
    }

    [Theory]
    [InlineData("2026-09-07T09:00:00", "2026-09-07T10:00:00")]
    [InlineData("2026-09-07T10:00:00Z", "2026-09-07T09:00:00Z")]
    public void No_naive_or_reversed_intervals(string start, string end)
    {
        var data = Base();
        data["busy"] = new JsonArray(Interval(start, end));
        Assert.ThrowsAny<PyValueError>(() => Capacity.Calculate(data));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Invalid_numbers(string value)
    {
        var data = (JsonObject)PyJson.Loads(
            "{\"coverage\": \"complete\", \"working\": [], \"busy\": [], \"estimates\": [{\"id\": \"x\", \"minutes\": " + value + "}]}")!;
        Assert.ThrowsAny<PyValueError>(() => Capacity.Calculate(data));
    }

    [Fact]
    public void Duplicate_estimates()
    {
        var data = Base();
        data["estimates"] = new JsonArray(new JsonObject { ["id"] = "x", ["minutes"] = 1 }, new JsonObject { ["id"] = "x", ["minutes"] = 1 });
        var error = Assert.ThrowsAny<PyValueError>(() => Capacity.Calculate(data));
        Assert.Equal("duplicate effort estimate: x", error.Message);
    }

    [Fact]
    public void Working_union_and_whole_day_leave()
    {
        var data = Base();
        data["working"] = new JsonArray(Interval("2026-09-07T09:00:00-07:00", "2026-09-07T17:00:00-07:00"),
                                        Interval("2026-09-07T09:00:00-07:00", "2026-09-07T17:00:00-07:00"));
        data["busy"] = new JsonArray(Interval("2026-09-07T00:00:00-07:00", "2026-09-08T00:00:00-07:00"));
        var result = Capacity.Calculate(data);
        Assert.Equal(480, Number(result, "working_minutes"));
        Assert.Equal(0, Number(result, "available_minutes"));
    }

    [Fact]
    public void Effort_stays_an_int_until_a_float_joins()
    {
        var data = Base();
        data["estimates"] = new JsonArray(new JsonObject { ["id"] = "a", ["minutes"] = 30 }, new JsonObject { ["id"] = "b", ["minutes"] = 60 });
        Assert.True(PyJson.IsInt(Capacity.Calculate(data)["known_effort_minutes"]));
        Assert.True(PyJson.IsInt(Capacity.Calculate(data)["minimum_overflow_minutes"])); // max(0, negative) is the int 0
        data["estimates"]!.AsArray().Add(new JsonObject { ["id"] = "c", ["minutes"] = 0.5 });
        Assert.True(PyJson.IsFloat(Capacity.Calculate(data)["known_effort_minutes"]));
    }

    [Fact]
    public void Ints_beyond_the_float_range_raise_overflow_error()
    {
        var huge = BigInteger.Pow(10, 308);
        Assert.Equal(huge, Capacity.Number(PyJson.Int(huge), "x"));
        Assert.Throws<PyOverflowError>(() => Capacity.Number(PyJson.Int(huge * 10), "x"));
        var data = Base();
        data["estimates"] = new JsonArray(new JsonObject { ["id"] = "a", ["minutes"] = PyJson.Int(huge) },
                                          new JsonObject { ["id"] = "b", ["minutes"] = PyJson.Int(huge) });
        Assert.Throws<PyOverflowError>(() => Capacity.Calculate(data)); // known - free_minutes
    }

    [Fact]
    public void Minutes_use_exact_total_seconds_and_compensated_sums()
    {
        var span = new CapacityInterval(Capacity.Instant("0001-01-02T00:00:00Z"), Capacity.Instant("9999-12-30T00:00:00.000001Z"));
        Assert.Equal("5258960640.0", PyJson.FloatRepr(Capacity.Minutes([span])));
        var second = new CapacityInterval(Capacity.Instant("2026-09-07T09:00:00.000001Z"), Capacity.Instant("2026-09-07T09:00:59.999999Z"));
        Assert.Equal("2.9999998999999997", PyJson.FloatRepr(Capacity.Minutes([second, second, second])));
        Assert.Equal("0.0", PyJson.FloatRepr(Capacity.Minutes([])));
    }

    [Fact]
    public void Instant_requires_an_offset_and_stays_in_range()
    {
        Assert.Equal("2026-09-07T16:00:00+00:00", Capacity.Instant("2026-09-07T09:00:00-07:00").Isoformat());
        Assert.Equal("Invalid isoformat string: '2026-09-07T09:00:00+00:00+00:00'",
                     Assert.ThrowsAny<PyValueError>(() => Capacity.Instant("2026-09-07T09:00:00ZZ")).Message);
        Assert.Equal("interval endpoints require an explicit UTC offset",
                     Assert.ThrowsAny<PyValueError>(() => Capacity.Instant("2026-09-07T09:00:00")).Message);
        Assert.Equal("interval endpoints must be ISO-8601 strings", Assert.ThrowsAny<PyValueError>(() => Capacity.Instant(5L)).Message);
        Assert.Throws<PyOverflowError>(() => Capacity.Instant("0001-01-01T00:00:00+00:01"));
    }

    [Fact]
    public void Command_line_prints_the_report()
    {
        const string input = "{\"coverage\": \"complete\", \"working\": [{\"start\": \"2026-09-07T09:00:00-07:00\", \"end\": \"2026-09-07T17:00:00-07:00\"}], "
                             + "\"busy\": [{\"start\": \"2026-09-07T10:30:00-07:00\", \"end\": \"2026-09-07T17:00:00-07:00\"}], "
                             + "\"estimates\": [{\"id\": \"outcome-1\", \"minutes\": 120}]}";
        const string expected = """
            {
              "coverage": "complete",
              "working_minutes": 480.0,
              "busy_minutes": 390.0,
              "available_minutes": 90.0,
              "protected_available_minutes": 0.0,
              "protected_conflict_minutes": 0.0,
              "focus_blocks": [
                {
                  "start": "2026-09-07T16:00:00+00:00",
                  "end": "2026-09-07T17:30:00+00:00",
                  "minutes": 90.0
                }
              ],
              "available_blocks": [
                {
                  "start": "2026-09-07T16:00:00+00:00",
                  "end": "2026-09-07T17:30:00+00:00",
                  "minutes": 90.0
                }
              ],
              "known_effort_minutes": 120,
              "unestimated_items": [],
              "minimum_overflow_minutes": 30.0,
              "fits_total_capacity": false,
              "allocation_status": "not_allocated",
              "warnings": []
            }

            """;
        var (code, stdout, stderr) = ScriptRunner.Run(new CapacityScript(), ["--input", "-"], input);
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Equal(expected, stdout);
    }

    [Fact]
    public void Command_line_errors_exit_two_with_python_messages()
    {
        var (code, stdout, stderr) = ScriptRunner.Run(new CapacityScript(), ["--input", "-"], "[1] x");
        Assert.Equal((2, ""), (code, stdout));
        Assert.Equal("ERROR: Extra data: line 1 column 5 (char 4)\n", stderr);
        (code, _, stderr) = ScriptRunner.Run(new CapacityScript(), ["--input", "./no-such-dir//capacity.json"]);
        Assert.Equal(2, code);
        Assert.Equal("ERROR: [Errno 2] No such file or directory: 'no-such-dir/capacity.json'\n", stderr);
        (code, _, stderr) = ScriptRunner.Run(new CapacityScript(), []);
        Assert.Equal(2, code);
        Assert.EndsWith("error: the following arguments are required: --input\n", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Stdin_decode_errors_name_the_reason_and_absolute_position()
    {
        var bytes = Enumerable.Repeat((byte)' ', 20000).Concat(new byte[] { 0xE2, 0x82 }).ToArray();
        using var reader = PyIO.StdinReader(new MemoryStream(bytes)); // margo-state's stdin
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = new CapacityScript().Run(["--input", "-"], reader, stdout, stderr);
        Assert.Equal(2, code);
        Assert.Equal("ERROR: 'utf-8' codec can't decode bytes in position 20000-20001: unexpected end of data\n", stderr.ToString());
    }
}
