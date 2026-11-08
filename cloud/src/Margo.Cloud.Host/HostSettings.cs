using Margo.Cloud.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Host;

/// <summary>The host cannot start; the message is printed and the process exits with 1 (Python raised at start-up).</summary>
public sealed class HostStartupException : Exception
{
    public HostStartupException()
    {
    }

    public HostStartupException(string message)
        : base(message)
    {
    }

    public HostStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The host's own configuration: the same environment variables <c>host/app.py</c> read and
/// <c>autopilot/infra/instance.bicep</c> and the Dockerfile set, read through <see cref="IConfiguration"/> (an
/// environment variable <c>A__B</c> is the key <c>A:B</c>; keys ignore case). The Agents SDK reads its own sections
/// (<c>CONNECTIONS</c>, <c>CONNECTIONSMAP</c>, <c>AGENTAPPLICATION</c>) from the same configuration.
/// </summary>
public sealed record HostSettings
{
    /// <summary>The Work IQ scope every token is asked for (Python <c>WORKIQ_SCOPE</c>).</summary>
    public const string WorkIQScope = "api://workiq.svc.cloud.microsoft/.default";

    /// <summary>Python <c>HEALTH_PATHS</c>.</summary>
    public static readonly IReadOnlyList<string> HealthPaths = ["/", "/liveness", "/readiness"];

    /// <summary>Python <c>ACTIVITY_PATHS</c>: the router forwards to <c>/activity/messages</c>.</summary>
    public static readonly IReadOnlyList<string> ActivityPaths = ["/api/messages", "/activity/messages"];

    /// <summary>Python's message when the blueprint's identity is missing.</summary>
    public const string NoBlueprintMessage = "the agent blueprint client ID and tenant are required to check activity tokens";

    private const string Connection = "CONNECTIONS:SERVICE_CONNECTION:SETTINGS:";

    /// <summary><c>MARGO_HOSTING</c>: <c>instance</c> when this container serves one hired Margo (default <c>single</c>).</summary>
    public string Hosting { get; init; } = "single";

    /// <summary>Whether <see cref="Hosting"/> is <c>instance</c>.</summary>
    public bool IsInstance => Hosting == "instance";

    /// <summary><c>AUTH_HANDLER_NAME</c> (default <c>AGENTIC</c>): Margo's own token.</summary>
    public string AgenticHandler { get; init; } = "AGENTIC";

    /// <summary><c>PRINCIPAL_AUTH_HANDLER</c> (default empty): the manager's on-behalf-of token.</summary>
    public string PrincipalHandler { get; init; } = string.Empty;

    /// <summary><c>CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID</c>: the agent blueprint.</summary>
    public required string ClientId { get; init; }

    /// <summary><c>CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID</c>.</summary>
    public required string TenantId { get; init; }

    /// <summary><c>MARGO_MODEL_DEPLOYMENT</c>.</summary>
    public required string ModelDeployment { get; init; }

    /// <summary><c>MARGO_STATE_BLOB_URL</c>: the state account's Blob endpoint.</summary>
    public required string StateBlobUrl { get; init; }

    /// <summary><c>AZURE_OPENAI_ENDPOINT</c>.</summary>
    public required string ModelEndpoint { get; init; }

    /// <summary><c>AZURE_CLIENT_ID</c>: this instance's own managed identity; null means <c>DefaultAzureCredential</c>.</summary>
    public string? ManagedIdentityClientId { get; init; }

    /// <summary><c>MARGO_REPO_ROOT</c> (default <c>/app</c>).</summary>
    public string RepoRoot { get; init; } = "/app";

    /// <summary><c>MARGO_TIMEZONE</c> (default <c>America/Los_Angeles</c>).</summary>
    public string TimeZone { get; init; } = "America/Los_Angeles";

    /// <summary><c>MARGO_STATE_DIR</c>, else <c>~/margo/state</c>.</summary>
    public required string StateRoot { get; init; }

    /// <summary><c>MARGO_PRINCIPAL_ACCESS</c> (default <c>obo</c>).</summary>
    public string PrincipalAccess { get; init; } = "obo";

    /// <summary>Per-instance hosting: <c>MARGO_INSTANCE_AGENT_ID</c> and <c>MARGO_INSTANCE_USER_ID</c>.</summary>
    public AgentInstance? Instance { get; init; }

    /// <summary>Per-instance hosting: <c>MARGO_STATE_CONTAINER</c>.</summary>
    public string? StateContainer { get; init; }

    /// <summary><c>MARGO_PUBLIC_BASE_URL/i/MARGO_INSTANCE_KEY/voice/</c> when per-instance hosting has voice.</summary>
    public string? VoiceUrl { get; init; }

    /// <summary>Voice is served (per-instance hosting and <c>VOICE_LIVE_MODEL</c> set).</summary>
    public bool VoiceEnabled { get; init; }

    /// <summary><c>MARGO_TENANT_ID</c> (voice only).</summary>
    public string? VoiceTenantId { get; init; }

    /// <summary><c>MARGO_VOICE_SCOPE</c> (default <c>Voice.Talk</c>).</summary>
    public string VoiceScope { get; init; } = "Voice.Talk";

    /// <summary><c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>.</summary>
    public string? ApplicationInsights { get; init; }

    /// <summary><c>HOST</c> (default <c>0.0.0.0</c>).</summary>
    public string ListenHost { get; init; } = "0.0.0.0";

    /// <summary><c>PORT</c> (default 8088).</summary>
    public int Port { get; init; } = 8088;

    /// <summary><c>LOG_LEVEL</c> (default <c>INFO</c>).</summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// Read and check the configuration in the order <c>MargoHost()</c> and <c>web_app()</c> failed: the model
    /// deployment, the state URL, the model endpoint, the instance's variables, voice, then the blueprint's identity.
    /// </summary>
    /// <exception cref="HostStartupException">Something required is missing or malformed.</exception>
    public static HostSettings Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? Get(string key) => configuration[key];
        string Required(string key) => Get(key) ?? throw new HostStartupException(key + " is required"); // Python: KeyError

        var deployment = Get("MARGO_MODEL_DEPLOYMENT");
        if (string.IsNullOrEmpty(deployment))
        {
            throw new HostStartupException("MARGO_MODEL_DEPLOYMENT is required");
        }

        var blobUrl = Get("MARGO_STATE_BLOB_URL");
        if (string.IsNullOrEmpty(blobUrl))
        {
            throw new HostStartupException("MARGO_STATE_BLOB_URL is required: state must not live only in one session");
        }

        var endpoint = Get("AZURE_OPENAI_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new HostStartupException("AZURE_OPENAI_ENDPOINT is required (https://ACCOUNT.openai.azure.com)");
        }

        var hosting = Get("MARGO_HOSTING") ?? "single";
        var voiceModel = Get("VOICE_LIVE_MODEL");
        AgentInstance? instance = null;
        string? container = null;
        string? voiceUrl = null;
        if (hosting == "instance")
        {
            instance = new AgentInstance(Required("MARGO_INSTANCE_AGENT_ID"), Required("MARGO_INSTANCE_USER_ID"));
            container = Required("MARGO_STATE_CONTAINER");
            var publicBase = (Get("MARGO_PUBLIC_BASE_URL") ?? string.Empty).TrimEnd('/');
            if (publicBase.Length > 0 && !string.IsNullOrEmpty(voiceModel))
            {
                voiceUrl = publicBase + "/i/" + Required("MARGO_INSTANCE_KEY") + "/voice/";
            }
        }

        var voice = hosting == "instance" && !string.IsNullOrEmpty(voiceModel);
        string? voiceTenant = null;
        if (voice)
        {
            voiceTenant = Required("MARGO_TENANT_ID");
            Required(Connection + "CLIENTID");
        }

        var clientId = Get(Connection + "CLIENTID");
        var tenantId = Get(Connection + "TENANTID");
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tenantId))
        {
            throw new HostStartupException(NoBlueprintMessage);
        }

        var portText = Get("PORT") ?? "8088";
        if (!int.TryParse(portText.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var port)
            || port is < 0 or > 65535)
        {
            throw new HostStartupException("PORT is not a port number: " + portText); // Python: ValueError from int()
        }

        var stateDir = Get("MARGO_STATE_DIR");
        var managedIdentity = Get("AZURE_CLIENT_ID");
        return new HostSettings
        {
            Hosting = hosting,
            AgenticHandler = Get("AUTH_HANDLER_NAME") ?? "AGENTIC",
            PrincipalHandler = Get("PRINCIPAL_AUTH_HANDLER") ?? string.Empty,
            ClientId = clientId,
            TenantId = tenantId,
            ModelDeployment = deployment,
            StateBlobUrl = blobUrl,
            ModelEndpoint = endpoint,
            ManagedIdentityClientId = string.IsNullOrEmpty(managedIdentity) ? null : managedIdentity,
            RepoRoot = Get("MARGO_REPO_ROOT") ?? "/app",
            TimeZone = Get("MARGO_TIMEZONE") ?? "America/Los_Angeles",
            StateRoot = string.IsNullOrEmpty(stateDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "margo", "state")
                : stateDir,
            PrincipalAccess = Get("MARGO_PRINCIPAL_ACCESS") ?? "obo",
            Instance = instance,
            StateContainer = container,
            VoiceUrl = voiceUrl,
            VoiceEnabled = voice,
            VoiceTenantId = voiceTenant,
            VoiceScope = Get("MARGO_VOICE_SCOPE") ?? "Voice.Talk",
            ApplicationInsights = Get("APPLICATIONINSIGHTS_CONNECTION_STRING") is { Length: > 0 } insights ? insights : null,
            ListenHost = Get("HOST") ?? "0.0.0.0",
            Port = port,
            LogLevel = ParseLogLevel(Get("LOG_LEVEL") ?? "INFO"),
        };
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

    /// <summary>
    /// <c>_state_root()</c>: create the state root and its parent with mode 0700 (and set it on existing ones).
    /// </summary>
    public static string EnsureStateRoot(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var full = Path.GetFullPath(root);
        foreach (var directory in new[] { Path.GetDirectoryName(full) ?? full, full })
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return root;
    }
}
