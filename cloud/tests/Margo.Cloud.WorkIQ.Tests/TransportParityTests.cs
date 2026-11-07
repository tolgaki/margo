using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Margo.Cloud.WorkIQ.Tests;

/// <summary>
/// Replays every response shape recorded from <c>transport.py</c> (<c>_parse_body</c> and <c>_normalise</c>)
/// by <c>Parity/transport_cases.py</c> and requires the same outcome: the same object, or the same error.
/// </summary>
public sealed class TransportParityTests
{
    private static readonly Lazy<Dictionary<string, JsonObject>> Cases = new(Load);

    public static TheoryData<string> ParseCases => Names("parse_body");

    public static TheoryData<string> NormaliseCases => Names("normalise");

    [Fact]
    public void Fixture_covers_both_operations()
    {
        Assert.True(ParseCases.Count >= 30);
        Assert.True(NormaliseCases.Count >= 30);
        Assert.All(Cases.Value.Values, c => Assert.Contains((string)c["op"]!, new[] { "parse_body", "normalise" }));
    }

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void Parse_body_matches_python(string name)
    {
        var c = Cases.Value[name];
        var raw = Convert.FromHexString((string)c["body_hex"]!);
        var contentType = (string?)c["content_type"];
        var requestId = c["request_id"]!.GetValue<long>();
        AssertOutcome(c["expect"]!.AsObject(), () => McpMessages.ParseBody(raw, contentType, requestId));
    }

    [Theory]
    [MemberData(nameof(NormaliseCases))]
    public void Normalise_matches_python(string name)
    {
        var c = Cases.Value[name];
        var result = PythonJson.Loads((string)c["result_json"]!);
        AssertOutcome(c["expect"]!.AsObject(), () => McpMessages.Normalise(result));
    }

    private static void AssertOutcome(JsonObject expect, Func<JsonObject> run)
    {
        if (expect.TryGetPropertyValue("ok", out var ok))
        {
            AssertSameJson(ok, run(), "$");
            return;
        }

        var failure = Record.Exception(() => run());
        Assert.NotNull(failure);
        if ((string)expect["error"]! == "other")
        {
            // Python raised something other than TransportError; the guard records an unknown failure.
            Assert.IsType<TransportFaultException>(failure);
            return;
        }

        Assert.Equal("TransportError", (string)expect["error"]!);
        var transport = Assert.IsType<TransportException>(failure);
        Assert.Equal((string)expect["message"]!, transport.Message);
        Assert.Equal(ExpectedStatus(expect["status_code"]), transport.StatusCode);
    }

    /// <summary>Python's status is an int or a bool (<c>True == 1</c>); the port carries the int value.</summary>
    private static int? ExpectedStatus(JsonNode? status) => status?.GetValueKind() switch
    {
        null or JsonValueKind.Null => null,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => status.GetValue<int>(),
    };

    /// <summary>Same keys in the same order, same values; numbers compare by Python type (int or float) and value.</summary>
    internal static void AssertSameJson(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case null:
                Assert.True(actual is null, path + ": expected null, got " + actual?.ToJsonString());
                return;
            case JsonObject expectedObject:
                var actualObject = Assert.IsType<JsonObject>(actual);
                Assert.Equal(expectedObject.Select(pair => pair.Key), actualObject.Select(pair => pair.Key));
                foreach (var (key, value) in expectedObject)
                {
                    AssertSameJson(value, actualObject[key], path + "." + key);
                }

                return;
            case JsonArray expectedArray:
                var actualArray = Assert.IsType<JsonArray>(actual);
                Assert.Equal(expectedArray.Count, actualArray.Count);
                for (var i = 0; i < expectedArray.Count; i++)
                {
                    AssertSameJson(expectedArray[i], actualArray[i], path + "[" + i + "]");
                }

                return;
        }

        var expectedValue = (JsonValue)expected;
        var actualValue = Assert.IsAssignableFrom<JsonValue>(actual);
        Assert.Equal(expectedValue.GetValueKind(), actualValue.GetValueKind());
        switch (expectedValue.GetValueKind())
        {
            case JsonValueKind.String:
                Assert.Equal(expectedValue.GetValue<string>(), actualValue.GetValue<string>());
                break;
            case JsonValueKind.Number:
                var expectedInt = PythonJson.TryGetInteger(expectedValue, out var left);
                var actualInt = PythonJson.TryGetInteger(actualValue, out var right);
                Assert.True(expectedInt == actualInt, path + ": int versus float");
                if (expectedInt)
                {
                    Assert.Equal(left, right);
                }
                else
                {
                    Assert.Equal(Float(expectedValue), Float(actualValue));
                }

                break;
        }
    }

    private static double Float(JsonValue value) =>
        double.Parse(PythonJson.NumberText(value), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static TheoryData<string> Names(string op)
    {
        var data = new TheoryData<string>();
        foreach (var (name, c) in Cases.Value)
        {
            if ((string)c["op"]! == op)
            {
                data.Add(name);
            }
        }

        return data;
    }

    private static Dictionary<string, JsonObject> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Parity", "transport_cases.json");
        var fixture = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var cases = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var c in fixture["cases"]!.AsArray())
        {
            cases.Add((string)c!["name"]!, c.AsObject());
        }

        return cases;
    }
}
