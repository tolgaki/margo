using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.Orchestrator;
using Margo.Cloud.State.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Margo.Cloud.Voice.Tests;

/// <summary>
/// One instance's voice endpoint in a test server: the real orchestrator (fake Work IQ directory, in-memory leases,
/// real temporary state and the real <c>margo-state</c> CLI), fake tokens, keys made in the test and a fake Voice Live.
/// The test method creates the temporary state and disposes it after the host.
/// </summary>
internal sealed class VoiceHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private VoiceHost(WebApplication app, TempState state, VoiceService service)
    {
        _app = app;
        State = state;
        Service = service;
        Server = app.GetTestServer();
    }

    public TempState State { get; }

    public VoiceService Service { get; }

    public TestServer Server { get; }

    public DirectoryTransport Transport { get; private init; } = new();

    public FakeTokens Tokens { get; private init; } = new();

    public FakeVoiceLive VoiceLive { get; private init; } = new();

    public TestKeys Keys { get; private init; } = new();

    public CapturingLogger Logger { get; private init; } = new();

    /// <param name="state">Temporary state made by the test method itself (its environment override is per async flow).</param>
    public static async Task<VoiceHost> StartAsync(TempState state, VoiceSettings? settings = null, string principalAccess = "obo",
                                                   TimeProvider? time = null)
    {
        var transport = new DirectoryTransport();
        var tokens = new FakeTokens();
        var voiceLive = new FakeVoiceLive();
        var keys = new TestKeys();
        var logger = new CapturingLogger();
        var stores = new Dictionary<string, InMemoryLeaseStore>(StringComparer.Ordinal);
        var engine = new Margo.Cloud.Engine.Engine(new UnusedModel(), "fictional-model", Fixtures.Root);
        var orchestrator = new Margo.Cloud.Orchestrator.Orchestrator(engine, transport, state.Path, container =>
        {
            lock (stores)
            {
                if (!stores.TryGetValue(container, out var store))
                {
                    store = new InMemoryLeaseStore();
                    stores[container] = store;
                }

                return store;
            }
        }, new OrchestratorOptions { PrincipalAccess = principalAccess, StateCliCommand = Fixtures.StateCli, Instance = Fixtures.Instance });
        var service = new VoiceService(orchestrator, tokens, Fixtures.Instance, Fixtures.Tenant, Fixtures.Client, new VoiceServiceOptions
        {
            Settings = settings ?? new VoiceSettings("fictional-realtime"),
            Endpoint = "https://voice.example.com/",
            VoiceLive = voiceLive,
            Keys = keys,
            Logger = logger,
            Time = time,
        });

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer(options => options.PreserveExecutionContext = true);
        var app = builder.Build();
        app.UseWebSockets();
        VoiceEndpoints.Map(app, service);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new VoiceHost(app, state, service)
        {
            Transport = transport,
            Tokens = tokens,
            VoiceLive = voiceLive,
            Keys = keys,
            Logger = logger,
        };
    }

    public HttpClient Client() => Server.CreateClient();

    /// <summary>A valid sign-in by <paramref name="oid"/> (the manager by default).</summary>
    public string SignIn(string oid = Fixtures.Manager) => Keys.Mint(TestKeys.Claims(TestKeys.Now(), oid));

    public async Task<PageSocket> OpenAsync()
    {
        var client = Server.CreateWebSocketClient();
        var socket = await client.ConnectAsync(new Uri(Server.BaseAddress, "voice/ws"), TestContext.Current.CancellationToken);
        return new PageSocket(socket);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
        Service.Dispose();
        Keys.Dispose();
    }
}

/// <summary>The browser's side of the voice socket.</summary>
internal sealed class PageSocket(WebSocket socket) : IDisposable
{
    public WebSocket Socket => socket;

    public Task SendAsync(string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

    public Task SendJsonAsync(JsonObject message) => SendAsync(PyJson.Dumps(message));

    public Task HelloAsync(string token) => SendJsonAsync(new JsonObject { ["type"] = "hello", ["token"] = token });

    /// <summary>The next text message, or null when the server closed.</summary>
    public async Task<string?> ReceiveTextAsync()
    {
        var buffer = new byte[1024 * 1024];
        var count = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(count), TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            count += result.Count;
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(buffer, 0, count);
            }
        }
    }

    public async Task<JsonObject> ReceiveJsonAsync() => (JsonObject)PyJson.Loads(await ReceiveTextAsync() ?? throw new InvalidOperationException("the socket closed"))!;

    /// <summary>Every remaining message until the server closes.</summary>
    public async Task<List<string>> DrainAsync()
    {
        var messages = new List<string>();
        while (await ReceiveTextAsync() is { } text)
        {
            messages.Add(text);
        }

        return messages;
    }

    public void Dispose() => socket.Dispose();
}
