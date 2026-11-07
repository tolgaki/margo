using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Proactive;
using Xunit;

namespace Margo.Cloud.State.Tests.Proactive;

/// <summary>
/// The Python value semantics the proactive port relies on (expected values measured with CPython 3.13):
/// float floor division, <c>str()</c>/<c>repr()</c> of JSON values in the text queue view, ordering and equality
/// across SQLite and JSON values, and the validation helpers' messages.
/// </summary>
public sealed class ProactiveHelperTests
{
    [Theory]
    [InlineData(6600.0, 3600.0, 1.0)]
    [InlineData(-0.5, 3600.0, -1.0)]
    [InlineData(7200.0, 3600.0, 2.0)]
    [InlineData(0.30000000000000004, 0.1, 3.0)]
    [InlineData(1e16, 3.0, 3333333333333333.0)]
    [InlineData(-7.5, 2.0, -4.0)]
    [InlineData(5.0, -3.0, -2.0)]
    [InlineData(1.0, 0.1, 9.0)]
    [InlineData(3599.999999, 3600.0, 0.0)]
    [InlineData(86399.999999, 1.0, 86399.0)]
    public void Float_floor_division_matches_cpython(double left, double right, double expected)
    {
        Assert.Equal(expected, PyNumbers.FloorDivide(left, right));
    }

    [Fact]
    public void Floor_division_keeps_signed_zero_and_refuses_zero_divisors()
    {
        Assert.True(double.IsNegative(PyNumbers.FloorDivide(-0.0, 5.0)));
        Assert.True(double.IsNegative(PyNumbers.FloorDivide(0.0, -5.0)));
        Assert.Equal("ZeroDivisionError", Assert.Throws<PyZeroDivisionError>(() => PyNumbers.FloorDivide(1.0, 0.0)).PythonType);
    }

    // timedelta(seconds=x) // timedelta(microseconds=1), measured with CPython 3.13 (leftover rounded half to even).
    [Theory]
    [InlineData(1.5, 1500000L)]
    [InlineData(0.0000015, 2L)]
    [InlineData(0.0000025, 2L)]
    [InlineData(0.0000035, 4L)]
    [InlineData(0.0000045, 4L)]
    [InlineData(0.0000005, 0L)]
    [InlineData(-0.0000005, 0L)]
    [InlineData(-0.0000015, -2L)]
    [InlineData(-0.0000025, -2L)]
    [InlineData(-2.5, -2500000L)]
    [InlineData(1e-7, 0L)]
    [InlineData(0.1, 100000L)]
    [InlineData(0.3333333333333333, 333333L)]
    [InlineData(2.675, 2675000L)]
    [InlineData(123456.7890125, 123456789012L)]
    [InlineData(-123456.7890125, -123456789012L)]
    [InlineData(86399.9999995, 86399999999L)]
    public void Float_timedelta_seconds_round_like_cpython(double seconds, long microseconds)
    {
        Assert.Equal((Int128)microseconds, PyTimeDelta.FromPySeconds(seconds).TotalMicroseconds);
    }

    [Fact]
    public void Stored_number_arithmetic_raises_python_errors()
    {
        // Review regression: hand-edited REAL/huge values used to raise TypeError or wrap around 64 bits.
        Assert.Equal((Int128)60_000_000, PyTimeDelta.FromPySeconds(60L).TotalMicroseconds);
        Assert.Equal("days=1000000000; must have magnitude <= 999999999",
                     Assert.Throws<PyOverflowError>(() => PyTimeDelta.FromPySeconds(86400.0 * 1000000000)).Message);
        Assert.Equal("Python int too large to convert to C int", Assert.Throws<PyOverflowError>(() => PyTimeDelta.FromPySeconds(1e15)).Message);
        Assert.Equal("cannot convert float infinity to integer",
                     Assert.Throws<PyOverflowError>(() => PyTimeDelta.FromPySeconds(double.NegativeInfinity)).Message);
        Assert.Equal("unsupported type for timedelta seconds component: str", Assert.Throws<PyTypeError>(() => PyTimeDelta.FromPySeconds("60")).Message);
        Assert.Equal(2L, PyNumbers.Add(1L, 1));
        Assert.Equal(new BigInteger(long.MaxValue) + 1, PyNumbers.Add(long.MaxValue, 1));
        Assert.Equal(2.5, PyNumbers.Add(1.5, 1));
        Assert.Equal("can only concatenate str (not \"int\") to str", Assert.Throws<PyTypeError>(() => PyNumbers.Add("1", 1)).Message);
        Assert.Equal("can't concat int to bytes", Assert.Throws<PyTypeError>(() => PyNumbers.Add(new byte[] { 1 }, 1)).Message);
        Assert.Equal("unsupported operand type(s) for +: 'NoneType' and 'int'", Assert.Throws<PyTypeError>(() => PyNumbers.Add(null, 1)).Message);
        Assert.Equal(BigInteger.Parse("3600000000000000122398779418931231681982886974873827863816617781501617083061490207476795509503496375733795"
                                      + "5083705774550140102578466523574119558328648203694292249838899483936583790951896150347715813770710693562680"
                                      + "15576057490939120336433607805879485031047299112132322515277595685754259698210966215046725632",
                                      System.Globalization.CultureInfo.InvariantCulture),
                     PyNumbers.Trunc(PyNumbers.FloorDivide(3600.0, 1e-300)));
        Assert.Equal("cannot convert float NaN to integer", Assert.Throws<PyValueError>(() => PyNumbers.Trunc(double.NaN)).Message);
    }

    [Fact]
    public void Text_view_formats_values_like_str()
    {
        var value = StateStore.ParseJson("{\"nested\":[1,\"two\",null,false,1.5e16,{\"k\":\"v's\"}],\"x\":0.1,\"big\":100000000000000000000,\"e\":\"\\u00e9\\u0007\"}");
        Assert.Equal("{'nested': [1, 'two', None, False, 1.5e+16, {'k': \"v's\"}], 'x': 0.1, 'big': 100000000000000000000, 'e': 'é\\x07'}",
                     PyNode.Str(value));
        Assert.Equal("plain text", PyNode.Str("plain text"));
        Assert.Equal("None", PyNode.Str(null));
        Assert.Equal("True", PyNode.Str(true));
        Assert.Equal("12", PyNode.Str(12));
        Assert.Equal("7.25", PyNode.Str(7.25));
    }

    [Fact]
    public void Ordering_and_equality_follow_python_across_value_models()
    {
        Assert.True(PyObj.Lt("2026-01-01T00:00:00.000000+00:00", "2026-01-02T00:00:00.000000+00:00"));
        Assert.True(PyObj.Lt("￿", "\U0001F600")); // code points, not UTF-16 units
        Assert.True(PyObj.Gt(1.5, 1L));
        Assert.True(PyObj.Ge(3600.0, 3600L) && PyObj.Le(3600.0, 3600L));
        Assert.Equal("'<=' not supported between instances of 'bytes' and 'str'",
                     Assert.Throws<PyTypeError>(() => PyObj.Le(new byte[] { 0x35 }, "2026")).Message);
        Assert.Throws<PyTypeError>(() => PyObj.Lt("x", null));
        Assert.True(PyObj.Eq(1L, JsonValue.Create(1.0)));
        Assert.True(PyObj.Eq("leased", JsonValue.Create("leased")));
        Assert.False(PyObj.Eq(1L, "1"));
        Assert.False(PyObj.Eq(null, ""));
        Assert.True(PyObj.Truthy(1L));
        Assert.False(PyObj.Truthy(""));
    }

    [Fact]
    public void Validation_helpers_raise_the_python_messages()
    {
        Assert.Equal("id must be a nonempty string", Assert.Throws<StateError>(() => ProactiveState.Text(JsonValue.Create("  "), "id")).Message);
        Assert.Equal("id must be a nonempty string", Assert.Throws<StateError>(() => ProactiveState.Text(JsonValue.Create("a\0b"), "id")).Message);
        Assert.Null(ProactiveState.Text((JsonNode?)null, "continuation", optional: true));
        Assert.Equal("page is out of range", Assert.Throws<StateError>(() => ProactiveState.Integer(JsonValue.Create(true), "page", 1)).Message);
        Assert.Equal("page is out of range", Assert.Throws<StateError>(() => ProactiveState.Integer(JsonValue.Create(2.0), "page", 1)).Message);
        Assert.Equal(2147483647L, ProactiveState.Integer(PyJson.Int(2147483647), "page", 1));
        Assert.Equal("JSON input must be an object", Assert.Throws<StateError>(() => ProactiveState.ObjectJson(new JsonArray())).Message);
        Assert.Equal("value must contain finite JSON data",
                     Assert.Throws<StateError>(() => ProactiveState.ObjectJson(new JsonObject { ["x"] = double.PositiveInfinity })).Message);
        // validate_timestamp of a dict or list takes the "not a str" path, as in Python.
        Assert.Equal("timestamp must be an ISO-8601 datetime with an explicit timezone",
                     Assert.Throws<StateError>(() => StateStore.ValidateTimestamp(new JsonObject { ["at"] = 1 })).Message);
        Assert.Equal("2026-01-01T00:00:00.000000+00:00", StateStore.ValidateTimestamp(JsonValue.Create("2026-01-01T09:00:00+09:00")));
        Assert.Equal(PyIds.Sha256Hex("[\"legacy\",\"{}\",\"one\",\"v1\"]"), ProactiveState.Digest(new JsonArray("legacy", "{}", "one", "v1")));
    }

    [Fact]
    public void Non_dict_payloads_raise_python_exception_classes()
    {
        Assert.Equal("AttributeError", Assert.Throws<PyAttributeError>(() => PyNode.Pop(JsonValue.Create("text"), "ts")).PythonType);
        Assert.Throws<PyTypeError>(() => PyNode.Pop(new JsonArray(1), "ts"));
        Assert.Throws<PyTypeError>(() => PyNode.Loads(5L));
        Assert.Throws<PyJsonDecodeError>(() => PyNode.Loads("{broken"));
        Assert.Equal("{'a': 1}", PyNode.Repr(PyNode.Loads("{\"a\":1}"u8.ToArray())));
        Assert.Throws<PyTypeError>(() => PyNode.FromSql(new byte[] { 1 }));
    }

    [Fact]
    public void Schema_statements_name_every_table_once()
    {
        Assert.Equal(14, ProactiveState.Schema.Count);
        Assert.Equal(13, ProactiveState.Schema.Count(statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal)));
        Assert.Contains("binding_unavailable", ProactiveState.ErrorClasses);
        Assert.Equal(11, ProactiveState.ErrorClasses.Count);
    }
}
