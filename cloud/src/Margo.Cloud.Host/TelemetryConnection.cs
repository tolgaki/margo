using System.Text.RegularExpressions;

namespace Margo.Cloud.Host;

/// <summary>
/// Whether <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> can be used. Python's <c>configure_azure_monitor</c> rejected a
/// bad one at once (<c>ValueError</c>), and <c>_configure_telemetry</c> logged "Telemetry configuration failed;
/// continuing without it." The .NET exporter only parses it when the host starts, and a failure there stops the host,
/// so the host checks first: Python's rules (its messages), then what the .NET parser would refuse.
/// </summary>
public static partial class TelemetryConnection
{
    /// <summary>The .NET exporter's limit on the connection string's length.</summary>
    public const int MaxLength = 4096;

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex PythonUuid();

    /// <summary>The reason the connection string cannot be used, or null when it can. Never echoes its content.</summary>
    public static string? Problem(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        return PythonProblem(connectionString) ?? DotNetProblem(connectionString);
    }

    /// <summary>
    /// <c>ConnectionStringParser</c> of <c>azure-monitor-opentelemetry-exporter</c>: <c>dict(s.split("=") for s in
    /// cs.split(";"))</c> with lower-cased keys (the last of a repeated key wins), an <c>Authorization</c> other than
    /// <c>ikey</c> refused, and an instrumentation key that is a lower-case UUID (<c>re.match</c> with <c>$</c>, which
    /// also matches before a final newline).
    /// </summary>
    private static string? PythonProblem(string connectionString)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in connectionString.Split(';'))
        {
            var parts = segment.Split('=');
            if (parts.Length != 2)
            {
                return "Invalid connection string";
            }

            pairs[parts[0].ToLowerInvariant()] = parts[1];
        }

        if (pairs.TryGetValue("authorization", out var authorization) && !authorization.Equals("ikey", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid authorization mechanism";
        }

        if (!pairs.TryGetValue("instrumentationkey", out var key) || key.Length == 0)
        {
            return "Instrumentation key cannot be none or empty.";
        }

        var match = key.EndsWith('\n') ? key[..^1] : key;
        return PythonUuid().IsMatch(match) ? null : "Invalid instrumentation key. It should be a valid UUID.";
    }

    /// <summary>What the .NET exporter's parser (Azure.Core's <c>ConnectionString</c>) refuses that Python accepted.</summary>
    private static string? DotNetProblem(string connectionString)
    {
        if (connectionString.Length > MaxLength)
        {
            return "the connection string is longer than " + MaxLength + " characters";
        }

        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in connectionString.Split(';'))
        {
            var separator = segment.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return "the connection string has a value without a keyword";
            }

            if (separator == segment.Length - 1)
            {
                return "the connection string has a keyword with an empty value";
            }

            if (!pairs.TryAdd(segment[..separator].Trim(), segment[(separator + 1)..].Trim()))
            {
                return "the connection string repeats a keyword";
            }
        }

        if (!pairs.ContainsKey("InstrumentationKey"))
        {
            // Python lower-cases keys with Unicode rules (a Kelvin sign is a k); .NET compares them ordinally.
            return "the connection string has no InstrumentationKey keyword";
        }

        foreach (var endpoint in new[] { "IngestionEndpoint", "LiveEndpoint" })
        {
            if (pairs.TryGetValue(endpoint, out var value) && !Uri.TryCreate(value, UriKind.Absolute, out _))
            {
                return "the connection string's " + endpoint + " is not an absolute URI";
            }
        }

        if (pairs.TryGetValue("EndpointSuffix", out var suffix)
            && !(pairs.ContainsKey("IngestionEndpoint") && pairs.ContainsKey("LiveEndpoint")))
        {
            var location = pairs.TryGetValue("Location", out var given) ? given.Trim().TrimEnd('.') : null;
            if (location is not null && !location.All(char.IsLetterOrDigit))
            {
                return "the connection string's Location must contain only letters and digits";
            }

            var host = (string.IsNullOrEmpty(location) ? string.Empty : location + ".") + "dc." + suffix.Trim().TrimStart('.');
            if (!Uri.TryCreate("https://" + host, UriKind.Absolute, out _))
            {
                return "the connection string's EndpointSuffix is not valid";
            }
        }

        return null;
    }
}
