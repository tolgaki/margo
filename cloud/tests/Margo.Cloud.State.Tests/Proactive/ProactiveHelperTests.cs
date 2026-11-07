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
        Assert.Equal(expected, Py.FloorDivide(left, right));
    }

    [Fact]
    public void Floor_division_keeps_signed_zero_and_refuses_zero_divisors()
    {
        Assert.True(double.IsNegative(Py.FloorDivide(-0.0, 5.0)));
        Assert.True(double.IsNegative(Py.FloorDivide(0.0, -5.0)));
        Assert.Equal("ZeroDivisionError", Assert.Throws<PyZeroDivisionError>(() => Py.FloorDivide(1.0, 0.0)).PythonType);
    }

    [Fact]
    public void Text_view_formats_values_like_str()
    {
        var value = StateStore.ParseJson("{\"nested\":[1,\"two\",null,false,1.5e16,{\"k\":\"v's\"}],\"x\":0.1,\"big\":100000000000000000000,\"e\":\"\\u00e9\\u0007\"}");
        Assert.Equal("{'nested': [1, 'two', None, False, 1.5e+16, {'k': \"v's\"}], 'x': 0.1, 'big': 100000000000000000000, 'e': 'é\\x07'}",
                     Py.Format(value));
        Assert.Equal("plain text", Py.Format("plain text"));
        Assert.Equal("None", Py.Format(null));
        Assert.Equal("True", Py.Format(true));
        Assert.Equal("12", Py.Format(12));
        Assert.Equal("7.25", Py.Format(7.25));
    }

    [Fact]
    public void Ordering_and_equality_follow_python_across_value_models()
    {
        Assert.True(Py.Compare("2026-01-01T00:00:00.000000+00:00", "2026-01-02T00:00:00.000000+00:00", "<") < 0);
        Assert.True(Py.Compare("￿", "\U0001F600", "<") < 0); // code points, not UTF-16 units
        Assert.True(Py.Compare(1.5, 1L, ">") > 0);
        Assert.Equal(0, Py.Compare(3600.0, 3600L, ">="));
        Assert.Equal("'<=' not supported between instances of 'bytes' and 'str'",
                     Assert.Throws<PyTypeError>(() => Py.Compare(new byte[] { 0x35 }, "2026", "<=")).Message);
        Assert.Throws<PyTypeError>(() => Py.Compare("x", null, "<"));
        Assert.True(Py.Eq(1L, JsonValue.Create(1.0)));
        Assert.True(Py.Eq("leased", JsonValue.Create("leased")));
        Assert.False(Py.Eq(1L, "1"));
        Assert.False(Py.Eq(null, ""));
        Assert.True(Py.Truthy(1L));
        Assert.False(Py.Truthy(""));
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
                     Assert.Throws<StateError>(() => Py.Timestamp(new JsonObject { ["at"] = 1 })).Message);
        Assert.Equal("2026-01-01T00:00:00.000000+00:00", Py.Timestamp(JsonValue.Create("2026-01-01T09:00:00+09:00")));
        Assert.Equal(PyIds.Sha256Hex("[\"legacy\",\"{}\",\"one\",\"v1\"]"), ProactiveState.Digest(new JsonArray("legacy", "{}", "one", "v1")));
    }

    [Fact]
    public void Non_dict_payloads_raise_python_exception_classes()
    {
        Assert.Equal("AttributeError", Assert.Throws<PyAttributeError>(() => Py.Pop(JsonValue.Create("text"), "ts")).PythonType);
        Assert.Throws<PyTypeError>(() => Py.Pop(new JsonArray(1), "ts"));
        Assert.Throws<PyTypeError>(() => Py.Loads(5L));
        Assert.Throws<PyJsonDecodeError>(() => Py.Loads("{broken"));
        Assert.Equal("{'a': 1}", Py.Repr(Py.Loads("{\"a\":1}"u8.ToArray())));
        Assert.Throws<PyTypeError>(() => Py.Node(new byte[] { 1 }));
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
