global using Xunit;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Margo.Cloud.Engine;
using Margo.Cloud.Host.Authentication;
using Margo.Cloud.Orchestrator;
using Margo.Cloud.State.Core;
using Margo.Cloud.WorkIQ;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Authentication.Msal.Model;
using Microsoft.Agents.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

// The host and its state are Linux-only (POSIX modes, the margo-state process).
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Margo.Cloud.Host.Tests;

/// <summary>A clock the tests move by hand: wall time and the monotonic timestamp move together.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now = new(2026, 11, 8, 9, 30, 0, TimeSpan.Zero);
    private long _ticks = 1_000_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _ticks;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
            _ticks += by.Ticks;
        }
    }
}

/// <summary>The repository root (holds <c>agents/</c>, <c>skills/</c> and the Python specification).</summary>
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "agents", "margo.agent.md"))
                && File.Exists(Path.Combine(directory.FullName, "cloud", "Margo.Cloud.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// A private temporary directory, with the environment override the <c>margo-state</c> CLI needs for a state directory
/// under the world-writable /tmp (as the orchestrator tests do). Create it inside the test method, before the host.
/// </summary>
internal sealed class TempState : IDisposable
{
    private readonly IDisposable _environment;

    public TempState()
    {
        Path = Directory.CreateTempSubdirectory("margo-host-test-").FullName;
        File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var home = Combine("home");
        Directory.CreateDirectory(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = home,
            ["MARGO_ALLOW_UNSAFE_STATE_DIR"] = "1",
            ["MARGO_CONFIG"] = Combine("missing-config.json"),
        };
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } dotnetRoot)
        {
            environment["DOTNET_ROOT"] = dotnetRoot;
        }

        _environment = PyRuntime.Override(environment);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>A minimal Margo database for <paramref name="account"/> holding one note.</summary>
    public static void WriteDatabase(string path, string account, string note)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS margo_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
            + "INSERT OR IGNORE INTO margo_meta VALUES ('account', $account), ('store_version', '1');"
            + "CREATE TABLE IF NOT EXISTS notes (text TEXT); INSERT INTO notes VALUES ($note);";
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$note", note);
        command.ExecuteNonQuery();
    }

    public static string ReadNote(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM notes ORDER BY rowid LIMIT 1";
        return (string)command.ExecuteScalar()!;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        _environment.Dispose();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Locally generated RSA keys and tokens signed with them (as the router's tests). Fictional identifiers only.</summary>
internal static class Tokens
{
    public const string ClientId = "fictional-blueprint-client";

    /// <summary>Made per run, so no tenant identifier is written in the repository.</summary>
    public static readonly string TenantId = Guid.NewGuid().ToString();

    public static readonly string OtherTenantId = Guid.NewGuid().ToString();

    public static readonly RSA Key = RSA.Create(2048);

    public static readonly RSA Stranger = RSA.Create(2048);

    public static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string B64(string text) => B64(Encoding.UTF8.GetBytes(text));

    public static byte[] KeySet()
    {
        var parameters = Key.ExportParameters(includePrivateParameters: false);
        var jwk = new JsonObject { ["kty"] = "RSA", ["use"] = "sig", ["kid"] = "k1", ["n"] = B64(parameters.Modulus!), ["e"] = B64(parameters.Exponent!) };
        return Encoding.UTF8.GetBytes(new JsonObject { ["keys"] = new JsonArray(jwk) }.ToJsonString());
    }

    /// <summary>A token as Agent 365 sends one to the blueprint, with <paramref name="change"/> applied.</summary>
    public static string Valid(Action<JsonObject>? change = null, RSA? key = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new JsonObject
        {
            ["aud"] = ClientId,
            ["iss"] = "https://sts.windows.net/" + TenantId + "/",
            ["tid"] = TenantId,
            ["appid"] = "fictional-channel-app",
            ["ver"] = "1.0",
            ["serviceurl"] = Activities.ServiceUrl,
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 3600,
        };
        change?.Invoke(claims);
        var signingInput = B64(new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = "k1" }.ToJsonString()) + "." + B64(claims.ToJsonString());
        var signature = (key ?? Key).SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + B64(signature);
    }
}

/// <summary>Serves the local key set and records which endpoints were read.</summary>
internal sealed class FakeJwksSource : IJwksSource
{
    public ConcurrentQueue<string> Fetched { get; } = new();

    public Task<byte[]> FetchAsync(string uri, CancellationToken cancellationToken)
    {
        Fetched.Enqueue(uri);
        return Task.FromResult(Tokens.KeySet());
    }
}

/// <summary>Activity JSON as Agent 365 sends it to an agentic user. Fictional identifiers only.</summary>
internal static class Activities
{
    public const string ServiceUrl = "https://smba.example.com/teams/";
    public const string AgentIdentity = "agent-identity-1";
    public const string AgentUser = "margo-user-1";
    public const string Manager = "principal-1";

    public static JsonObject Recipient() => new()
    {
        ["id"] = "28:" + AgentUser,
        ["name"] = "Margo",
        ["role"] = "agenticUser",
        ["agenticAppId"] = AgentIdentity,
        ["agenticUserId"] = AgentUser,
        ["tenantId"] = Tokens.TenantId,
    };

    public static JsonObject Message(string text, string sender = Manager, string conversation = "chat-principal-1", string conversationType = "personal",
                                     string id = "activity-1", string channel = "msteams") => new()
    {
        ["type"] = "message",
        ["id"] = id,
        ["channelId"] = channel,
        ["serviceUrl"] = ServiceUrl,
        ["timestamp"] = "2026-11-08T09:30:00Z",
        ["from"] = new JsonObject { ["id"] = "29:" + sender, ["name"] = sender == Manager ? "Dana" : "Rafa", ["aadObjectId"] = sender },
        ["recipient"] = Recipient(),
        ["conversation"] = new JsonObject { ["id"] = conversation, ["conversationType"] = conversationType, ["tenantId"] = Tokens.TenantId },
        ["text"] = text,
    };

    /// <summary>An Agent 365 mail: a <c>message</c> on <c>agents:email</c> with the Outlook reference.</summary>
    public static JsonObject Mail(string text, string sender = "colleague-1", string? mailId = "outlook-message-1", string type = "message")
    {
        var activity = Message(text, sender, conversation: "mail-conversation-transport", conversationType: "personal", id: "transport-1", channel: "agents:email");
        activity["type"] = type;
        if (type == "event")
        {
            activity["name"] = "emailNotification";
        }
        var reference = new JsonObject { ["type"] = "emailNotification", ["conversationId"] = "outlook-conversation-1" };
        if (mailId is not null)
        {
            reference["id"] = mailId;
        }

        activity["entities"] = new JsonArray(reference);
        return activity;
    }

    public static JsonObject Lifecycle(string valueType) => new()
    {
        ["type"] = "event",
        ["name"] = "agentLifecycle",
        ["valueType"] = valueType,
        ["id"] = "lifecycle-1",
        ["channelId"] = "agents",
        ["serviceUrl"] = ServiceUrl,
        ["from"] = new JsonObject { ["id"] = "platform" },
        ["recipient"] = Recipient(),
        ["conversation"] = new JsonObject { ["id"] = "lifecycle-conversation" },
        ["value"] = new JsonObject(),
    };

    public static JsonObject Installation(string action) => new()
    {
        ["type"] = "installationUpdate",
        ["action"] = action,
        ["id"] = "install-1",
        ["channelId"] = "msteams",
        ["serviceUrl"] = ServiceUrl,
        ["from"] = new JsonObject { ["id"] = "29:" + Manager, ["aadObjectId"] = Manager },
        ["recipient"] = Recipient(),
        ["conversation"] = new JsonObject { ["id"] = "chat-principal-1", ["conversationType"] = "personal" },
    };
}

/// <summary>A scripted model: each step is a response object or an exception.</summary>
internal sealed class FakeModel(params object?[] script) : IModelClient
{
    private readonly Queue<object?> _script = new(script);
    private readonly List<JsonObject> _requests = [];

    public IReadOnlyList<JsonObject> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public void Add(object step)
    {
        lock (_requests)
        {
            _script.Enqueue(step);
        }
    }

    public Task<JsonNode?> CreateResponseAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        object? step;
        lock (_requests)
        {
            _requests.Add(PyNode.CopyObject(request));
            step = _script.Count > 0 ? _script.Dequeue() : new InvalidOperationException("the fake model has no more answers");
        }

        return step switch
        {
            Exception error => Task.FromException<JsonNode?>(error),
            JsonNode node => Task.FromResult<JsonNode?>(PyNode.Copy(node)),
            _ => Task.FromResult<JsonNode?>(null),
        };
    }

    public static JsonObject Final(string text, string responseId = "r-final") => new()
    {
        ["id"] = responseId,
        ["output"] = new JsonArray(new JsonObject
        {
            ["type"] = "message",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }),
        }),
    };
}

/// <summary>Work IQ: answers Margo's <c>/me/manager</c> lookup with Dana; records every call and its bearer.</summary>
internal sealed class FakeWorkIQ : IWorkIQTransport
{
    private readonly List<(string Tool, JsonObject Arguments, string Bearer)> _calls = [];

    public string Endpoint => WorkIQEndpoints.McpUrl;

    public IReadOnlyList<(string Tool, JsonObject Arguments, string Bearer)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default)
    {
        lock (_calls)
        {
            _calls.Add((tool, PyNode.CopyObject(arguments), bearer));
        }

        if (tool == "fetch" && PyNode.AsString(arguments["entityUrls"]?[0])?.StartsWith("/me/manager", StringComparison.Ordinal) == true)
        {
            return Task.FromResult(new JsonObject
            {
                ["results"] = new JsonArray(new JsonObject
                {
                    ["statusCode"] = 200,
                    ["data"] = new JsonObject { ["id"] = Activities.Manager, ["displayName"] = "Dana" },
                }),
            });
        }

        return Task.FromResult((JsonObject)PyJson.Loads("""{"statusCode": 200, "data": {"value": []}}""")!);
    }
}

/// <summary>
/// The SDK connection (in place of MSAL): Margo's agentic user token per scope set, an on-behalf-of exchange, and the
/// channel token for replies. Records each request (never a real token: these are fixed fictional strings).
/// </summary>
internal sealed class FakeConnection : IAccessTokenProvider, IAgenticTokenProvider, IOBOExchange
{
    private readonly ConcurrentQueue<string> _requests = new();

    public ImmutableConnectionSettings ConnectionSettings { get; } = new(new ConnectionSettings
    {
        ClientId = Tokens.ClientId,
        TenantId = Tokens.TenantId,
        Scopes = ["api://fictional-agents-service/.default"],
    });

    public IReadOnlyList<string> Requests => [.. _requests];

    public bool FailAgentic { get; set; }

    public Task<string> GetAccessTokenAsync(string resourceUrl, IList<string> scopes, bool forceRefresh = false)
    {
        _requests.Enqueue("channel");
        return Task.FromResult("channel-token");
    }

    public TokenCredential GetTokenCredential() => throw new NotSupportedException();

    public Task<string> GetAgenticApplicationTokenAsync(string tenantId, string agentAppInstanceId, CancellationToken cancellationToken = default) =>
        Task.FromResult("agentic-app-token");

    public Task<string> GetAgenticInstanceTokenAsync(string tenantId, string agentAppInstanceId, CancellationToken cancellationToken = default) =>
        Task.FromResult("agentic-instance-token");

    public Task<string> GetAgenticInstanceTokenAsync(string tenantId, string agentAppInstanceId, IList<string> scopes, CancellationToken cancellationToken = default) =>
        Task.FromResult("agentic-instance-token");

    public Task<string> GetAgenticUserTokenAsync(string tenantId, string agentAppInstanceId, string upn, IList<string> scopes, CancellationToken cancellationToken = default)
    {
        var scope = string.Join(" ", scopes ?? []);
        _requests.Enqueue("agentic-user " + tenantId + " " + agentAppInstanceId + " " + upn + " " + scope);
        if (FailAgentic)
        {
            throw new InvalidOperationException("agentic token refused (fake)");
        }

        return Task.FromResult(scope == HostSettings.WorkIQScope ? "margo-workiq-token" : "margo-other-token");
    }

    public Task<TokenResponse> AcquireTokenOnBehalfOf(IEnumerable<string> scopes, string token)
    {
        _requests.Enqueue("obo " + string.Join(" ", scopes));
        return Task.FromResult(new TokenResponse { Token = "principal-voice-token" });
    }
}

/// <summary>The channel's connector service: records the activities the host sends (replies, typing).</summary>
internal sealed class FakeChannel : HttpMessageHandler, IHttpClientFactory
{
    private readonly ConcurrentQueue<(string Url, JsonObject Activity, string? Authorization)> _sent = new();

    public IReadOnlyList<(string Url, JsonObject Activity, string? Authorization)> Sent => [.. _sent];

    public IReadOnlyList<JsonObject> Messages => [.. Sent.Where(item => (string?)item.Activity["type"] == "message").Select(item => item.Activity)];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        _sent.Enqueue((request.RequestUri!.ToString(), (JsonObject)JsonNode.Parse(body)!, request.Headers.Authorization?.ToString()));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\": \"sent-1\"}", Encoding.UTF8, "application/json") };
    }
}

/// <summary>Collects log entries.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message, Exception? Exception)> _entries = new();

    public IReadOnlyList<(string Category, LogLevel Level, string Message, Exception? Exception)> Entries => [.. _entries];

    public string All => string.Join("\n", Entries.Select(entry => entry.Category + " " + entry.Level + " " + entry.Message + " " + entry.Exception));

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider._entries.Enqueue((category, logLevel, formatter(state, exception), exception));
    }
}
