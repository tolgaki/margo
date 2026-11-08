using System.Numerics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Router;

/// <summary>The router cannot start; the message is printed and the process exits with 1. Python:
/// <c>SystemExit(message)</c> in <c>main</c>.</summary>
public sealed class RouterStartupException : Exception
{
    public RouterStartupException()
    {
    }

    public RouterStartupException(string message)
        : base(message)
    {
    }

    public RouterStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The router's configuration, from the environment variables the Python router read and
/// <c>cloud/infra/main.bicep</c> sets (read through <see cref="IConfiguration"/>, where
/// <c>A__B</c> is the key <c>A:B</c>).
/// </summary>
/// <param name="ClientId"><c>CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID</c>: the agent blueprint.</param>
/// <param name="TenantId"><c>CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID</c>.</param>
/// <param name="RoutesUrl"><c>MARGO_ROUTES_URL</c>: the route table blob.</param>
/// <param name="ProvisionQueueUrl"><c>MARGO_PROVISION_QUEUE_URL</c>: the provisioning queue.</param>
/// <param name="ManagedIdentityClientId"><c>AZURE_CLIENT_ID</c>: the router's user-assigned identity;
/// <see langword="null"/> means <c>DefaultAzureCredential</c>.</param>
/// <param name="Host"><c>HOST</c>, default <c>0.0.0.0</c>.</param>
/// <param name="Port"><c>PORT</c>, default 8088.</param>
/// <param name="LogLevel"><c>LOG_LEVEL</c>, default <c>INFO</c>, as a minimum level.</param>
public sealed record RouterSettings(
    string ClientId,
    string TenantId,
    string RoutesUrl,
    string ProvisionQueueUrl,
    string? ManagedIdentityClientId,
    string Host,
    int Port,
    LogLevel LogLevel)
{
    /// <summary>Python's message when the blueprint is not configured.</summary>
    public const string NoBlueprintMessage = "the agent blueprint client ID is not set; see docs/autopilot-deployment.md";

    /// <summary>Read and check the configuration, in Python's order: the blueprint first (without it
    /// there is no audience to check tokens against, so nothing would be accepted), then the storage
    /// addresses, then the listening address.</summary>
    /// <exception cref="RouterStartupException">Something required is missing or malformed.</exception>
    public static RouterSettings Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var clientId = configuration["CONNECTIONS:SERVICE_CONNECTION:SETTINGS:CLIENTID"] ?? string.Empty;
        var tenantId = configuration["CONNECTIONS:SERVICE_CONNECTION:SETTINGS:TENANTID"] ?? string.Empty;
        if (clientId.Length == 0 || tenantId.Length == 0)
        {
            throw new RouterStartupException(NoBlueprintMessage);
        }

        var managedIdentity = configuration["AZURE_CLIENT_ID"];
        var routesUrl = configuration["MARGO_ROUTES_URL"] ?? throw new RouterStartupException("MARGO_ROUTES_URL is not set");
        var queueUrl = configuration["MARGO_PROVISION_QUEUE_URL"] ?? throw new RouterStartupException("MARGO_PROVISION_QUEUE_URL is not set");
        var host = configuration["HOST"] ?? "0.0.0.0";
        var portText = configuration["PORT"] ?? "8088";
        if (!PythonCompat.TryParseInt(portText, out var port) || port < BigInteger.Zero || port > 65535)
        {
            throw new RouterStartupException("PORT is not a port number: " + portText);
        }

        return new RouterSettings(clientId, tenantId, routesUrl, queueUrl, string.IsNullOrEmpty(managedIdentity) ? null : managedIdentity,
            host, (int)port, ParseLogLevel(configuration["LOG_LEVEL"] ?? "INFO"));
    }

    /// <summary>Python: <c>getattr(logging, LOG_LEVEL.upper(), logging.INFO)</c>.</summary>
    public static LogLevel ParseLogLevel(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToUpperInvariant() switch
        {
            "NOTSET" => LogLevel.Trace,
            "DEBUG" => LogLevel.Debug,
            "WARNING" or "WARN" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            "CRITICAL" or "FATAL" => LogLevel.Critical,
            _ => LogLevel.Information,
        };
    }
}
