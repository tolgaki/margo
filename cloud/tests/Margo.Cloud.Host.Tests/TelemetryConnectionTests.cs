using System.Reflection;
using Azure.Monitor.OpenTelemetry.Exporter;

namespace Margo.Cloud.Host.Tests;

/// <summary>
/// <see cref="TelemetryConnection"/> against Python's <c>ConnectionStringParser</c> (verdicts recorded from
/// azure-monitor-opentelemetry-exporter in the reference venv) and against the .NET exporter's own parser.
/// </summary>
public sealed class TelemetryConnectionTests
{
    // A fictional instrumentation key, split so the repository's GUID check does not mistake it for a real one.
    internal const string Key = "00000000-0000-4000-8000" + "-00000000000a";

    /// <summary>Input and Python's <c>ValueError</c> message (null: accepted).</summary>
    public static TheoryData<string, string?> PythonVerdicts => new()
    {
        { "garbage", "Invalid connection string" },
        { "Foo=bar", "Instrumentation key cannot be none or empty." },
        { "InstrumentationKey=not-a-guid", "Invalid instrumentation key. It should be a valid UUID." },
        { "InstrumentationKey=" + Key, null },
        { "InstrumentationKey=" + Key.ToUpperInvariant(), "Invalid instrumentation key. It should be a valid UUID." },
        { "instrumentationkey=" + Key, null },
        { "InstrumentationKey=" + Key + ";", "Invalid connection string" },
        { ";InstrumentationKey=" + Key, "Invalid connection string" },
        { "InstrumentationKey=" + Key + ";IngestionEndpoint=https://westus-0.in.applicationinsights.example.com/;LiveEndpoint=https://westus.livediagnostics.example.com/;ApplicationId=" + Key, null },
        { "InstrumentationKey=" + Key + ";Authorization=aad", "Invalid authorization mechanism" },
        { "InstrumentationKey=" + Key + ";Authorization=iKey", null },
        { "InstrumentationKey=" + Key + ";A=b=c", "Invalid connection string" },
        { " InstrumentationKey=" + Key, "Instrumentation key cannot be none or empty." },
        { "InstrumentationKey= " + Key, "Invalid instrumentation key. It should be a valid UUID." },
        { "InstrumentationKey=x;InstrumentationKey=" + Key, null },
        { "InstrumentationKey=" + Key + "\n", null },
        { string.Empty, "Invalid connection string" },
    };

    [Theory]
    [MemberData(nameof(PythonVerdicts))]
    public void Python_s_refusals_are_refusals_with_its_messages(string connection, string? python)
    {
        var problem = TelemetryConnection.Problem(connection);
        if (python is null)
        {
            // Python accepted it; the host refuses it only when the .NET exporter would stop the host over it.
            Assert.Equal(DotNetParserRefuses(connection), problem is not null);
        }
        else
        {
            Assert.Equal(python, problem);
        }
    }

    /// <summary>
    /// Strings Python accepted: the host refuses exactly those the .NET exporter's parser refuses (so the host never
    /// stops at start-up over the variable), and never repeats the content.
    /// </summary>
    [Theory]
    [InlineData("InstrumentationKey=" + Key + ";Extra=")]
    [InlineData("InstrumentationKey=" + Key + ";=value")]
    [InlineData("InstrumentationKey=" + Key + ";InstrumentationKey=" + Key)]
    [InlineData("InstrumentationKey=" + Key + ";instrumentationKEY=" + Key)]
    [InlineData("InstrumentationKey=" + Key + ";IngestionEndpoint=not a url")]
    [InlineData("InstrumentationKey=" + Key + ";LiveEndpoint=relative/path")]
    [InlineData("InstrumentationKey=" + Key + ";IngestionEndpoint=https://dc.example.com/")]
    [InlineData("InstrumentationKey=" + Key + ";EndpointSuffix=applicationinsights.example.com;Location=chinaeast2")]
    [InlineData("InstrumentationKey=" + Key + ";EndpointSuffix=example.com;Location=bad-location")]
    [InlineData("InstrumentationKey=" + Key + ";EndpointSuffix=exa mple.com")]
    [InlineData("InstrumentationKey=" + Key + ";EndpointSuffix=example.com;Location=westus.")]
    [InlineData("InstrumentationKey=" + Key + ";Extra= padded ")]
    [InlineData("InstrumentationKey=" + Key)]
    public void Strings_python_accepted_are_refused_only_when_the_dotnet_parser_would_fail(string connection)
    {
        Assert.Null(PythonOnly(connection));
        var problem = TelemetryConnection.Problem(connection);
        Assert.Equal(DotNetParserRefuses(connection), problem is not null);
        if (problem is not null)
        {
            Assert.DoesNotContain(Key, problem, StringComparison.Ordinal);
        }
    }

    /// <summary>Generated strings: whatever the host accepts, the .NET exporter parses (so it never stops the host).</summary>
    [Fact]
    public void Generated_strings_the_host_accepts_never_stop_the_exporter()
    {
        string[] keys = ["InstrumentationKey", "instrumentationkey", " InstrumentationKey", "IngestionEndpoint", "LiveEndpoint", "EndpointSuffix",
                         "Location", "Authorization", "ApplicationId", "AADAudience", "", "x"];
        string[] values = [Key, Key + " ", " " + Key, "ikey", "aad", "https://dc.example.com/", "not a url", "example.com", ".example.com", "west-us",
                           "westus", "westus.", "", " ", "a=b", "http://[::1]/", "https://", "ex ample.com"];
        var random = new Random(20261108);
        var accepted = 0;
        for (var i = 0; i < 20000; i++)
        {
            var segments = Enumerable.Range(0, random.Next(1, 5)).Select(_ => keys[random.Next(keys.Length)] + "=" + values[random.Next(values.Length)]);
            var connection = "InstrumentationKey=" + Key + ";" + string.Join(';', segments);
            if (TelemetryConnection.Problem(connection) is null)
            {
                accepted++;
                Assert.False(DotNetParserRefuses(connection), connection);
            }
            else if (PythonOnly(connection) is null)
            {
                Assert.True(DotNetParserRefuses(connection), connection);
            }
        }

        Assert.True(accepted > 1000);
    }

    [Fact]
    public void Overlong_strings_are_refused()
    {
        var connection = "InstrumentationKey=" + Key + ";Extra=" + new string('x', TelemetryConnection.MaxLength);
        Assert.True(DotNetParserRefuses(connection));
        Assert.NotNull(TelemetryConnection.Problem(connection));
    }

    private static string? PythonOnly(string connection)
    {
        var problem = TelemetryConnection.Problem(connection);
        return problem is "Invalid connection string" or "Invalid authorization mechanism" or "Instrumentation key cannot be none or empty."
            or "Invalid instrumentation key. It should be a valid UUID." ? problem : null;
    }

    /// <summary>The .NET exporter's internal parser (test-only reflection: the oracle the host's check must agree with).</summary>
    private static bool DotNetParserRefuses(string connection)
    {
        var parser = typeof(AzureMonitorExporterOptions).Assembly
            .GetType("Azure.Monitor.OpenTelemetry.Exporter.Internals.ConnectionString.ConnectionStringParser", throwOnError: true)!
            .GetMethod("GetValues", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            parser.Invoke(null, [connection]);
            return false;
        }
        catch (TargetInvocationException exc) when (exc.InnerException is InvalidOperationException)
        {
            return true;
        }
    }
}
