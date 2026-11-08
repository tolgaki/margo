using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Azure.Core;
using Margo.Cloud.State.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Margo.Cloud.Voice.Tests;

/// <summary>
/// <see cref="VoiceLiveConnector"/> speaks the protocol of <c>azure-ai-voicelive</c> 1.3.0 (read from the package):
/// the URL, the bearer token for <c>https://ai.azure.com/.default</c>, one <c>json.dumps</c> text message per client
/// event, and server events parsed as JSON objects. Checked against a local WebSocket server standing in for Voice Live.
/// </summary>
public sealed class VoiceLiveTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Expected values printed by the SDK's own <c>_VoiceLiveConnectionManager._prepare_url()</c> with
    /// <c>model="gpt-realtime model/é"</c>.</summary>
    [Theory]
    [InlineData("https://margo-voice.cognitiveservices.azure.com/",
                "wss://margo-voice.cognitiveservices.azure.com/voice-live/realtime?api-version=2026-07-15&model=gpt-realtime+model%2F%C3%A9")]
    [InlineData("https://margo-voice.services.ai.azure.com",
                "wss://margo-voice.services.ai.azure.com/voice-live/realtime?api-version=2026-07-15&model=gpt-realtime+model%2F%C3%A9")]
    [InlineData("http://localhost:8080/base/",
                "ws://localhost:8080/base/voice-live/realtime?api-version=2026-07-15&model=gpt-realtime+model%2F%C3%A9")]
    [InlineData("wss://x.example.com/a?api-version=old&extra=1&blank=&x+y=a%20b+c&dup=1&dup=2#frag",
                "wss://x.example.com/a/voice-live/realtime?api-version=2026-07-15&model=gpt-realtime+model%2F%C3%A9&extra=1&x+y=a+b+c&dup=1#frag")]
    [InlineData("https://x.example.com/p;q?z=%E2%82%AC",
                "wss://x.example.com/p/voice-live/realtime;q?api-version=2026-07-15&model=gpt-realtime+model%2F%C3%A9&z=%E2%82%AC")]
    [InlineData("HTTPS://X.Example.com:443/",
                "wss://X.Example.com:443/voice-live/realtime?api-version=2026-07-15&model=gpt-realtime+model%2F%C3%A9")]
    public void The_url_is_the_sdk_s(string endpoint, string expected) =>
        Assert.Equal(expected, VoiceLiveConnector.RealtimeUrl(endpoint, "gpt-realtime model/é"));

    [Fact]
    public async Task The_wire_protocol_is_the_sdk_s()
    {
        var received = Channel.CreateUnbounded<string>();
        string? authorization = null, query = null, path = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            authorization = context.Request.Headers.Authorization;
            query = context.Request.QueryString.Value;
            path = context.Request.Path.Value;
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            var first = await socket.ReceiveAsync(buffer, CancellationToken.None);
            await received.Writer.WriteAsync(Encoding.UTF8.GetString(buffer, 0, first.Count));
            await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type": "session.updated", "event_id": "e1", "extra": [1, 2.5]}"""),
                                   WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type": "response.audio.delta", "delta": "AAAA"}"""),
                                   WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.SendAsync(Encoding.UTF8.GetBytes("[1]"), WebSocketMessageType.Text, true, CancellationToken.None);
            var close = await socket.ReceiveAsync(buffer, CancellationToken.None);
            await received.Writer.WriteAsync("close " + close.MessageType + " " + socket.CloseStatus);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        await app.StartAsync(Cancel);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        var credential = new FixedCredential();
        var connector = new VoiceLiveConnector(() => credential);
        var connection = await connector.ConnectAsync(address + "/", "fictional-realtime", Cancel);
        await using (connection)
        {
            await connection.SendAsync(new JsonObject { ["type"] = "session.update", ["session"] = new JsonObject { ["instructions"] = "Bonjour — Margo" } }, Cancel);
            Assert.Equal("{\"type\": \"session.update\", \"session\": {\"instructions\": \"Bonjour \\u2014 Margo\"}}", // ensure_ascii
                         await received.Reader.ReadAsync(Cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Cancel));
            Assert.Equal("""{"type": "session.updated", "event_id": "e1", "extra": [1, 2.5]}""", PyJson.Dumps(await connection.ReceiveAsync(Cancel)));
            Assert.Equal("""{"type": "response.audio.delta", "delta": "AAAA"}""", PyJson.Dumps(await connection.ReceiveAsync(Cancel)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ReceiveAsync(Cancel));
        }

        Assert.Equal("close Close NormalClosure", await received.Reader.ReadAsync(Cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Cancel));
        Assert.Equal("Bearer fictional-voice-live-token", authorization);
        Assert.Equal("/voice-live/realtime", path);
        Assert.Equal("?api-version=2026-07-15&model=fictional-realtime", query);
        Assert.Equal(["https://ai.azure.com/.default"], credential.Scopes);
        await app.StopAsync(Cancel);
    }

    [Fact]
    public async Task The_session_ends_when_the_service_closes()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        await app.StartAsync(Cancel);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var connection = await new VoiceLiveConnector(() => new FixedCredential()).ConnectAsync(address, "m", Cancel);
        await using (connection)
        {
            Assert.Null(await connection.ReceiveAsync(Cancel));
            await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendAsync(new JsonObject { ["type"] = "response.create" }, Cancel));
        }

        await app.StopAsync(Cancel);
    }

    private sealed class FixedCredential : TokenCredential
    {
        public List<string> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.AddRange(requestContext.Scopes);
            return new AccessToken("fictional-voice-live-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
