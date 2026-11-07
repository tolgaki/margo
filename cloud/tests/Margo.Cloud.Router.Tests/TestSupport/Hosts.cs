using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using Margo.Cloud.Core;
using Margo.Cloud.Router.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Router.Tests.TestSupport;

/// <summary>What the fake instance received.</summary>
internal sealed record SeenRequest(string Method, string PathAndQuery, IReadOnlyList<string> HeaderNames, string? Authorization, string? ContentType, int BodyLength);

/// <summary>
/// A fictional instance container on a loopback port: <c>POST /activity/messages</c> and
/// <c>/voice/...</c> (page and WebSocket), recording what reaches it.
/// </summary>
internal sealed class FakeInstance : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeInstance(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public ConcurrentQueue<SeenRequest> Seen { get; } = new();

    public ConcurrentQueue<WebSocketCloseStatus?> SocketCloses { get; } = new();

    public static async Task<FakeInstance> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        FakeInstance? instance = null;
        app.Run(context => instance!.HandleAsync(context));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        instance = new FakeInstance(app, address.TrimEnd('/'));
        return instance;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var memory = new MemoryStream();
        await context.Request.Body.CopyToAsync(memory);
        var body = Encoding.UTF8.GetString(memory.ToArray());
        Seen.Enqueue(new SeenRequest(context.Request.Method, context.Request.Path + context.Request.QueryString,
            context.Request.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            context.Request.Headers.Authorization.FirstOrDefault(), context.Request.Headers.ContentType.FirstOrDefault(), memory.ToArray().Length));
        if (context.Request.Path == "/activity/messages")
        {
            await MessagesAsync(context, body);
        }
        else if (context.WebSockets.IsWebSocketRequest)
        {
            await EchoAsync(context);
        }
        else
        {
            if (context.Request.Path.Value!.EndsWith("/slow", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted).ContinueWith(_ => { }, TaskScheduler.Default);
            }

            context.Response.Headers.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store, max-age=0";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'";
            context.Response.Headers["X-Other"] = "not forwarded";
            context.Response.Headers.SetCookie = "session=fictional";
            await context.Response.WriteAsync("<html>voice</html>");
        }
    }

    private static async Task MessagesAsync(HttpContext context, string body)
    {
        if (body.Contains("\"slow\"", StringComparison.Ordinal))
        {
            await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        if (body.Contains("\"empty\"", StringComparison.Ordinal))
        {
            context.Response.StatusCode = 202;
            return;
        }

        if (body.Contains("\"nocontent\"", StringComparison.Ordinal))
        {
            context.Response.StatusCode = 204;
            return;
        }

        if (body.Contains("\"plain\"", StringComparison.Ordinal))
        {
            context.Response.StatusCode = 201;
            context.Response.Headers.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers["X-Extra"] = "1";
            context.Response.Headers.RetryAfter = "5";
            await context.Response.WriteAsync("hi");
            return;
        }

        if (body.Contains("\"untyped\"", StringComparison.Ordinal))
        {
            await context.Response.Body.WriteAsync("raw"u8.ToArray());
            return;
        }

        if (body.Contains("\"gzip\"", StringComparison.Ordinal))
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write("{\"compressed\": true}"u8);
            }

            context.Response.Headers.ContentType = "application/json";
            context.Response.Headers.ContentEncoding = "gzip";
            await context.Response.Body.WriteAsync(compressed.ToArray());
            return;
        }

        context.Response.Headers.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync("{\"ok\": true}");
    }

    private async Task EchoAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[2 * 1024 * 1024];
        try
        {
            while (true)
            {
                var count = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                    count += result.Count;
                }
                while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    SocketCloses.Enqueue(socket.CloseStatus);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var reversed = buffer.AsSpan(0, count).ToArray();
                    Array.Reverse(reversed);
                    await socket.SendAsync(reversed, WebSocketMessageType.Binary, true, CancellationToken.None);
                    continue;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, count);
                if (text == "big")
                {
                    await socket.SendAsync(new byte[VoiceBridge.MaxFrameBytes + 1], WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                else if (text == "largest")
                {
                    await socket.SendAsync(new byte[VoiceBridge.MaxFrameBytes - 1], WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                else if (text.StartsWith("close:", StringComparison.Ordinal))
                {
                    await socket.CloseAsync((WebSocketCloseStatus)int.Parse(text[6..], System.Globalization.CultureInfo.InvariantCulture), null, CancellationToken.None);
                    SocketCloses.Enqueue(socket.CloseStatus);
                    return;
                }
                else
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes("echo:" + text), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }
        catch (WebSocketException)
        {
            SocketCloses.Enqueue(socket.CloseStatus);
        }
    }
}

/// <summary>The router with fake storage, a local key set and a hand-moved clock, on TestServer or on a
/// loopback port (for WebSockets).</summary>
internal sealed class RouterUnderTest : IAsyncDisposable
{
    public const string Identity = "agent-identity-1";
    public const string User = "margo-user-1";

    private RouterUnderTest(WebApplication app, HttpClient client, string? address)
    {
        App = app;
        Client = client;
        Address = address;
    }

    public static ManualTime NewTime() => new();

    public WebApplication App { get; }

    public HttpClient Client { get; }

    /// <summary>The loopback address when started on Kestrel.</summary>
    public string? Address { get; }

    public ManualTime Time { get; private init; } = null!;

    public FakeRouteSource Routes { get; private init; } = null!;

    public FakeProvisioningSink Sink { get; private init; } = null!;

    public FakeJwksSource Jwks { get; private init; } = null!;

    public ListLoggerProvider Logs { get; private init; } = null!;

    public static string Key(string user = User) => Instances.InstanceKey(Identity, user);

    /// <summary>A route table with these instances (all for <see cref="Identity"/>).</summary>
    public static string Table(params (string User, string State, string? Url)[] routes)
    {
        var table = new RouteTable();
        foreach (var (user, state, url) in routes)
        {
            table.Put(Key(user), Identity, user, state, url);
        }

        return table.Dumps();
    }

    public static async Task<RouterUnderTest> StartAsync(string routes = "", bool kestrel = false, InstanceConnector? connector = null)
    {
        var time = new ManualTime();
        var source = new FakeRouteSource { Text = routes };
        var sink = new FakeProvisioningSink();
        var jwks = new FakeJwksSource();
        var logs = new ListLoggerProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        if (kestrel)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }
        else
        {
            builder.WebHost.UseTestServer();
        }

        builder.Services.AddMargoRouter(new AgentAuthSettings(Tokens.ClientId, Tokens.TenantId), source, sink, jwks, time);
        if (connector is not null)
        {
            builder.Services.AddSingleton(connector); // the last registration wins
        }

        var app = builder.Build();
        app.UseMargoRouter();
        await app.StartAsync();
        string? address = null;
        HttpClient client;
        if (kestrel)
        {
            address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
            client = new HttpClient { BaseAddress = new Uri(address) };
        }
        else
        {
            client = app.GetTestClient();
        }

        return new RouterUnderTest(app, client, address) { Time = time, Routes = source, Sink = sink, Jwks = jwks, Logs = logs };
    }

    public string Token(Action<System.Text.Json.Nodes.JsonObject>? change = null) => Tokens.Valid(Time.GetUtcNow(), change);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
