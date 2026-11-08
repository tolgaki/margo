global using Xunit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Margo.Cloud.Core;
using Margo.Cloud.Engine;
using Margo.Cloud.Orchestrator;
using Margo.Cloud.State.Core;
using Margo.Cloud.Voice.Jwt;
using Margo.Cloud.WorkIQ;
using Microsoft.Extensions.Logging;

// Private state relies on POSIX modes; Margo Cloud runs on Linux containers.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Margo.Cloud.Voice.Tests;

/// <summary>Fictional identities and paths shared by the tests.</summary>
internal static class Fixtures
{
    public const string Tenant = "fictional-tenant";
    public const string Client = "fictional-blueprint-app";
    public const string Manager = "principal-1";

    public static AgentInstance Instance { get; } = new("agent-identity-1", "margo-user-1");

    /// <summary>The repository root (holds <c>agents/</c>, <c>skills/</c> and the Python specification).</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>The real C# <c>margo-state</c> CLI, copied next to this test assembly.</summary>
    public static IReadOnlyList<string> StateCli { get; } =
    [
        Environment.ProcessPath is { } host && Path.GetFileNameWithoutExtension(host) == "dotnet" ? host : "dotnet",
        Path.Combine(AppContext.BaseDirectory, "margo-state.dll"),
    ];

    public static JsonObject Obj(string text) => (JsonObject)PyJson.Loads(text)!;

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

/// <summary>A model that is never asked anything: voice runs its own model (Voice Live), not the engine's.</summary>
internal sealed class UnusedModel : IModelClient
{
    public Task<JsonNode?> CreateResponseAsync(JsonObject request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("the engine's model is not used by voice");
}

/// <summary>Answers Margo's <c>/me/manager</c> lookup like Work IQ and any other read with an empty result; records
/// every call with its bearer token.</summary>
internal sealed class DirectoryTransport : IWorkIQTransport
{
    private readonly List<(string Tool, JsonObject Arguments, string Bearer)> _calls = [];

    public (string Id, string Name)? Manager { get; set; } = (Fixtures.Manager, "Dana");

    public Exception? LookupFailure { get; set; }

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
            if (LookupFailure is { } failure)
            {
                return Task.FromException<JsonObject>(failure);
            }

            if (Manager is not { } manager)
            {
                return Task.FromResult(Fixtures.Obj("""{"results": [{"statusCode": 404, "data": {}}]}"""));
            }

            return Task.FromResult(new JsonObject
            {
                ["results"] = new JsonArray(new JsonObject
                {
                    ["statusCode"] = 200,
                    ["data"] = new JsonObject { ["id"] = manager.Id, ["displayName"] = manager.Name },
                }),
            });
        }

        return Task.FromResult(Fixtures.Obj("""{"statusCode": 200, "data": {"value": []}}"""));
    }
}

/// <summary>
/// A private temporary state root with a complete environment override for the async flow (a private <c>HOME</c>,
/// <c>MARGO_ALLOW_UNSAFE_STATE_DIR=1</c> since temporary directories live under a world-writable /tmp).
/// </summary>
internal sealed class TempState : IDisposable
{
    private readonly IDisposable _environment;

    public TempState()
    {
        Path = Directory.CreateTempSubdirectory("margo-voice-test-").FullName;
        File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var home = System.IO.Path.Combine(Path, "home");
        Directory.CreateDirectory(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = home,
            ["MARGO_ALLOW_UNSAFE_STATE_DIR"] = "1",
            ["MARGO_CONFIG"] = System.IO.Path.Combine(Path, "missing-config.json"),
        };
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } dotnetRoot)
        {
            environment["DOTNET_ROOT"] = dotnetRoot;
        }

        _environment = PyRuntime.Override(environment);
    }

    public string Path { get; }

    public string StateRoot => System.IO.Path.Combine(Path, "state");

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

/// <summary>Records the token requests; answers with fictional tokens.</summary>
internal sealed class FakeTokens : IVoiceTokens
{
    private readonly List<string> _requests = [];

    public string? Margo { get; set; } = "margo-token";

    public string? Principal { get; set; } = "principal-token";

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public Task<string?> GetAgenticUserTokenAsync(string tenantId, string agentIdentityId, string agentUserId, IReadOnlyList<string> scopes,
                                                  CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add($"agentic {tenantId} {agentIdentityId} {agentUserId} {string.Join(",", scopes)}");
        }

        return Task.FromResult(Margo);
    }

    public Task<string?> AcquireTokenOnBehalfOfAsync(IReadOnlyList<string> scopes, string userToken, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add($"obo {string.Join(",", scopes)} {userToken.Length}");
        }

        return Task.FromResult(Principal);
    }
}

/// <summary>A fake Voice Live: each connection records what it was sent and replays what the test queues.</summary>
internal sealed class FakeVoiceLive : IVoiceLiveConnector
{
    private readonly Channel<FakeVoiceLiveConnection> _opened = Channel.CreateUnbounded<FakeVoiceLiveConnection>();

    public Exception? ConnectFailure { get; set; }

    public List<(string Endpoint, string Model)> Connects { get; } = [];

    /// <summary>Called with each new connection before it is returned (to queue its first events).</summary>
    public Action<FakeVoiceLiveConnection>? OnConnect { get; set; }

    public Task<IVoiceLiveConnection> ConnectAsync(string endpoint, string model, CancellationToken cancellationToken)
    {
        lock (Connects)
        {
            Connects.Add((endpoint, model));
        }

        if (ConnectFailure is { } failure)
        {
            return Task.FromException<IVoiceLiveConnection>(failure);
        }

        var connection = new FakeVoiceLiveConnection();
        OnConnect?.Invoke(connection);
        _opened.Writer.TryWrite(connection);
        return Task.FromResult<IVoiceLiveConnection>(connection);
    }

    public async Task<FakeVoiceLiveConnection> NextConnectionAsync() =>
        await _opened.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(60));
}

internal sealed class FakeVoiceLiveConnection : IVoiceLiveConnection
{
    private readonly Channel<JsonObject?> _events = Channel.CreateUnbounded<JsonObject?>();
    private readonly Channel<JsonObject> _sent = Channel.CreateUnbounded<JsonObject>();

    public bool Disposed { get; private set; }

    /// <summary>Queue a server event (null ends the session from the service side).</summary>
    public void Push(JsonObject? serviceEvent) => _events.Writer.TryWrite(serviceEvent);

    /// <summary>The next client event the bridge sent.</summary>
    public async Task<JsonObject> NextSentAsync() =>
        await _sent.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(60));

    public Task SendAsync(JsonObject clientEvent, CancellationToken cancellationToken)
    {
        _sent.Writer.TryWrite(PyNode.CopyObject(clientEvent));
        return Task.CompletedTask;
    }

    public async Task<JsonObject?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _events.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Signing keys and tokens made in the test, served as the tenant's key set.</summary>
internal sealed class TestKeys : IJwksSource, IDisposable
{
    private readonly List<string> _fetches = [];

    public TestKeys(string kid = "test-key-1")
    {
        Rsa = RSA.Create(2048);
        Kid = kid;
        Document = KeySet((Rsa, kid));
    }

    public RSA Rsa { get; }

    public string Kid { get; }

    /// <summary>The key set document served; tests may replace it.</summary>
    public byte[] Document { get; set; }

    public bool Unavailable { get; set; }

    public IReadOnlyList<string> Fetches
    {
        get
        {
            lock (_fetches)
            {
                return [.. _fetches];
            }
        }
    }

    public static byte[] KeySet(params (RSA Key, string Kid)[] keys)
    {
        var members = new JsonArray();
        foreach (var (key, kid) in keys)
        {
            var parameters = key.ExportParameters(false);
            members.Add(new JsonObject
            {
                ["kty"] = "RSA",
                ["use"] = "sig",
                ["kid"] = kid,
                ["n"] = Base64Url(parameters.Modulus!),
                ["e"] = Base64Url(parameters.Exponent!),
            });
        }

        return Encoding.UTF8.GetBytes(new JsonObject { ["keys"] = members }.ToJsonString());
    }

    public Task<byte[]> FetchAsync(string uri, CancellationToken cancellationToken)
    {
        lock (_fetches)
        {
            _fetches.Add(uri);
        }

        return Unavailable ? Task.FromException<byte[]>(new JwksUnavailableException("offline")) : Task.FromResult(Document);
    }

    /// <summary>The claims a valid voice sign-in carries at <paramref name="now"/>.</summary>
    public static JsonObject Claims(double now, string oid = Fixtures.Manager) => new()
    {
        ["aud"] = "api://" + Fixtures.Client,
        ["iss"] = "https://login.microsoftonline.com/" + Fixtures.Tenant + "/v2.0",
        ["iat"] = (long)now - 10,
        ["nbf"] = (long)now - 10,
        ["exp"] = (long)now + 3600,
        ["tid"] = Fixtures.Tenant,
        ["oid"] = oid,
        ["scp"] = "Voice.Talk",
        ["sub"] = "fictional-subject",
    };

    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>A compact RS256 token over <paramref name="payload"/> (written as given) signed with <paramref name="key"/>.</summary>
    public string Mint(JsonObject payload, JsonObject? header = null, RSA? key = null)
    {
        header ??= new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = Kid };
        return MintRaw(Encoding.UTF8.GetBytes(header.ToJsonString()), Encoding.UTF8.GetBytes(payload.ToJsonString()), key ?? Rsa);
    }

    public static string MintRaw(byte[] header, byte[] payload, RSA key)
    {
        var signingInput = Base64Url(header) + "." + Base64Url(payload);
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => Rsa.Dispose();
}

/// <summary>Collects log entries.</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}

/// <summary>A clock whose monotonic time runs <paramref name="speed"/> times faster than the system's (the time limit
/// test); timers are the system's.</summary>
internal sealed class FastClock(double speed) : TimeProvider
{
    private readonly long _origin = System.GetTimestamp();

    public override long GetTimestamp() => _origin + (long)((System.GetTimestamp() - _origin) * speed);
}
