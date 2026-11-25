using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Margo.Cloud.WorkIQ;

/// <summary>
/// Traces and metrics for Work IQ calls: one activity per <c>tools/call</c> and <c>tools/list</c>, a call-duration
/// histogram, and counters for sessions and throttling retries. Tags hold the tool name, whether it is a write, the
/// outcome and the HTTP status: never arguments, paths, results or tokens.
/// </summary>
public static class WorkIQTelemetry
{
    /// <summary>The <see cref="ActivitySource"/> and <see cref="Meter"/> name to subscribe to.</summary>
    public const string Name = "Margo.Cloud.WorkIQ";

    /// <summary>The MCP C# SDK's own activity source (its client spans sit under Margo's).</summary>
    public const string SdkSourceName = "Experimental.ModelContextProtocol";

    internal static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    internal static readonly Histogram<double> CallDuration = Meter.CreateHistogram<double>(
        "workiq.call.duration", unit: "ms", description: "Work IQ tools/call duration, session set-up included.");

    internal static readonly Counter<long> SessionsOpened = Meter.CreateCounter<long>(
        "workiq.sessions.opened", description: "MCP sessions opened (one per identity for reads; one per write).");

    internal static readonly Counter<long> ThrottleRetries = Meter.CreateCounter<long>(
        "workiq.throttle.retries", description: "Reads sent once more after a 429 or 503 with a short Retry-After.");

    /// <summary>The outcome tag for an exception the transport raised.</summary>
    internal static string Outcome(Exception? error) => error switch
    {
        null => "ok",
        TransportTimeoutException => "timeout",
        TransportResponseTooLargeException => "too_large",
        TransportException { StatusCode: not null } => "http_error",
        TransportException => "error",
        OperationCanceledException => "cancelled",
        _ => "unknown",
    };
}
