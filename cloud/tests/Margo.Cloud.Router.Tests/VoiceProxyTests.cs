using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Margo.Cloud.Core;
using Margo.Cloud.Router.Tests.TestSupport;
using Xunit;

namespace Margo.Cloud.Router.Tests;

/// <summary>
/// The voice proxy, on loopback ports with real WebSockets. Expected outcomes were observed by driving
/// the Python router (aiohttp) the same way: raw request targets, an aiohttp WebSocket client and a
/// fake instance.
/// </summary>
public sealed class VoiceProxyTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string Key(string user = RouterUnderTest.User) => RouterUnderTest.Key(user);

    private static async Task<(FakeInstance Instance, RouterUnderTest Router)> StartAsync()
    {
        var instance = await FakeInstance.StartAsync();
        var router = await RouterUnderTest.StartAsync(RouterUnderTest.Table(
            (RouterUnderTest.User, RouteStates.Ready, instance.Url),
            ("margo-user-p", RouteStates.Provisioning, null),
            ("margo-user-s", RouteStates.Suspended, null),
            ("margo-user-down", RouteStates.Ready, "http://127.0.0.1:9")), kestrel: true);
        return (instance, router);
    }

    /// <summary>One request with a raw target, so no client normalises the path.</summary>
    private static async Task<(int Status, string Head, string Body)> RawGet(RouterUnderTest router, string target, string method = "GET", params string[] headers)
    {
        var uri = new Uri(router.Address!);
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port, Cancel);
        await using var stream = client.GetStream();
        var request = method + " " + target + " HTTP/1.1\r\nHost: router.example.com\r\nConnection: close\r\n" + string.Concat(headers.Select(h => h + "\r\n")) + "\r\n";
        await stream.WriteAsync(Encoding.Latin1.GetBytes(request), Cancel);
        using var reader = new StreamReader(stream, Encoding.Latin1);
        var response = await reader.ReadToEndAsync(Cancel);
        var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = response[..split];
        return (int.Parse(head.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture), head, response[(split + 4)..]);
    }

    private static string? Header(string head, string name) =>
        head.Split("\r\n").Skip(1).Where(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(name.Length + 1)..].Trim()).FirstOrDefault();

    [Fact]
    public async Task The_page_is_fetched_without_query_or_caller_headers()
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        var page = await RawGet(router, "/i/" + Key() + "/voice/app.js?code=fictional-code", "GET", "Cookie: s=1", "Authorization: Bearer x");
        Assert.Equal((200, "<html>voice</html>"), (page.Status, page.Body));
        Assert.Equal("text/html; charset=utf-8", Header(page.Head, "Content-Type"));
        Assert.Equal("no-store, max-age=0", Header(page.Head, "Cache-Control"));
        Assert.Equal("default-src 'self'", Header(page.Head, "Content-Security-Policy"));
        Assert.Null(Header(page.Head, "X-Other"));
        Assert.Null(Header(page.Head, "Set-Cookie"));
        var seen = Assert.Single(instance.Seen);
        Assert.Equal(("GET", "/voice/app.js"), (seen.Method, seen.PathAndQuery));
        Assert.DoesNotContain("Cookie", seen.HeaderNames);
        Assert.DoesNotContain("Authorization", seen.HeaderNames);
        Assert.DoesNotContain(router.Logs.Lines, line => line.Contains("fictional-code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Voice_statuses()
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        var waiting = await RawGet(router, "/i/" + Key("margo-user-p") + "/voice/");
        Assert.Equal((503, "30", string.Empty), (waiting.Status, Header(waiting.Head, "Retry-After"), waiting.Body));
        Assert.Equal(404, (await RawGet(router, "/i/" + Key("margo-user-s") + "/voice/")).Status);
        Assert.Equal(404, (await RawGet(router, "/i/" + Key("margo-user-new") + "/voice/")).Status);
        var wrongMethod = await RawGet(router, "/i/" + Key() + "/voice/", "POST", "Content-Length: 0");
        Assert.Equal((405, "GET", "405: Method Not Allowed"), (wrongMethod.Status, Header(wrongMethod.Head, "Allow"), wrongMethod.Body));
        var head = await RawGet(router, "/i/" + Key() + "/voice/", "HEAD");
        Assert.Equal((405, "GET"), (head.Status, Header(head.Head, "Allow")));
        var down = await RawGet(router, "/i/" + Key("margo-user-down") + "/voice/");
        Assert.Equal((500, "500 Internal Server Error\n\nServer got itself in trouble"), (down.Status, down.Body));
        Assert.Empty(instance.Seen);
    }

    [Theory]
    [InlineData("/i/K/voice", 200, "/voice/")]
    [InlineData("/i/K/voice/", 200, "/voice/")]
    [InlineData("/i/K/voice/x/../y", 404, null)]
    [InlineData("/i/K/voice/./y", 200, "/voice/y")]
    [InlineData("/i/K/voice/%2e/y", 200, "/voice/y")]
    [InlineData("/i/K/voice/a+b", 404, null)]
    [InlineData("/i/K/voice/%7E", 404, null)]
    [InlineData("/i/K/voice/%41", 200, "/voice/A")]
    [InlineData("/i/K/voice/%C3%A9", 404, null)]
    [InlineData("/i/K/voice/%ZZ", 404, null)]
    [InlineData("/i/K/voice/%252F", 404, null)]
    [InlineData("/i/K/voice/a%2fb", 200, "/voice/a/b")]
    [InlineData("/i/K/voice/%2E%2E", 404, null)]
    [InlineData("/i/K/voice/..", 404, null)]
    [InlineData("/i/K/voice/.", 200, "/voice/")]
    [InlineData("/i/K/voice/./", 200, "/voice/")]
    [InlineData("/i/K/voice/a/./b", 200, "/voice/a/b")]
    [InlineData("/i/K/voice/a/../b", 404, null)]
    [InlineData("/i/K/voice/a//b", 404, null)]
    [InlineData("/i/K/voice///", 404, null)]
    [InlineData("/i/K/voice?x=1", 200, "/voice/")]
    [InlineData("/i/K/voice/a?b=../c", 200, "/voice/a")]
    [InlineData("/i/K/voice/%0A", 404, null)]
    [InlineData("/i/K/voice/A-Z_a.z", 200, "/voice/A-Z_a.z")]
    [InlineData("/i/K/voice/~", 404, null)]
    [InlineData("/i/K/voice/a;b", 404, null)]
    [InlineData("http://router.example.com/i/K/voice/abs", 200, "/voice/abs")]
    [InlineData("/i/K/voice/%2F", 404, null)]
    [InlineData("/i/K/./voice/", 404, null)]
    [InlineData("/i/K/x/../voice/", 404, null)]
    [InlineData("//i/K/voice/", 404, null)]
    [InlineData("/i/K/voice/%E2%82%AC", 404, null)]
    [InlineData("/i/K/voicex", 404, null)]
    [InlineData("/i/K/voice/../admin", 404, null)]
    [InlineData("/i/K/voice/%2E%2E/admin", 404, null)]
    [InlineData("/i/K/voice/a%20b", 404, null)]
    [InlineData("/i/K/voice//x", 404, null)]
    public async Task Raw_targets_reach_the_instance_as_python_let_them(string target, int status, string? instancePath)
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        var result = await RawGet(router, target.Replace("K", Key(), StringComparison.Ordinal));
        Assert.Equal(status, result.Status);
        Assert.Equal(instancePath is null ? [] : [instancePath], instance.Seen.Select(seen => seen.PathAndQuery));
    }

    [Fact]
    public async Task A_percent_encoded_key_is_decoded_for_routing()
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        var key = Key();
        Assert.Equal(200, (await RawGet(router, "/i/%" + ((int)key[0]).ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + key[1..] + "/voice/")).Status);
    }

    private static async Task<ClientWebSocket> ConnectAsync(RouterUnderTest router, string user = RouterUnderTest.User)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", "s=1");
        await socket.ConnectAsync(new Uri(router.Address!.Replace("http://", "ws://", StringComparison.Ordinal) + "/i/" + Key(user) + "/voice/ws?code=fictional-code"), Cancel);
        return socket;
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[1024 * 1024];
        var count = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(count), Cancel);
            count += result.Count;
        }
        while (!result.EndOfMessage);
        return (result.MessageType, buffer[..count]);
    }

    private static async Task<WebSocketCloseStatus?> ExpectCloseAsync(WebSocket socket)
    {
        var (type, _) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Close, type);
        var status = socket.CloseStatus;
        if (socket.State == WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, Cancel);
        }

        return status;
    }

    private static async Task<WebSocketCloseStatus?> InstanceCloseAsync(FakeInstance instance)
    {
        for (var i = 0; i < 200 && instance.SocketCloses.IsEmpty; i++)
        {
            await Task.Delay(25, Cancel);
        }

        return Assert.Single(instance.SocketCloses);
    }

    [Fact]
    public async Task Messages_are_bridged_both_ways_without_query_or_headers()
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        using var socket = await ConnectAsync(router);
        await socket.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, Cancel);
        var text = await ReceiveAsync(socket);
        Assert.Equal((WebSocketMessageType.Text, "echo:hello"), (text.Type, Encoding.UTF8.GetString(text.Data)));
        await socket.SendAsync("abc"u8.ToArray(), WebSocketMessageType.Binary, true, Cancel);
        var binary = await ReceiveAsync(socket);
        Assert.Equal((WebSocketMessageType.Binary, "cba"), (binary.Type, Encoding.UTF8.GetString(binary.Data)));

        // The largest message aiohttp let through: one byte under the limit (its reader refuses a size
        // that reaches the limit; exactly the limit was observed to be refused).
        await socket.SendAsync("largest"u8.ToArray(), WebSocketMessageType.Text, true, Cancel);
        Assert.Equal(VoiceBridge.MaxFrameBytes - 1, (await ReceiveAsync(socket)).Data.Length);

        var seen = Assert.Single(instance.Seen);
        Assert.Equal("/voice/ws", seen.PathAndQuery);
        Assert.DoesNotContain("Cookie", seen.HeaderNames);
        Assert.DoesNotContain(router.Logs.Lines, line => line.Contains("fictional-code", StringComparison.Ordinal));

        // Python: the client's close (any code) is answered with 1000, and the instance gets 1000.
        await socket.CloseAsync((WebSocketCloseStatus)4001, null, Cancel);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await InstanceCloseAsync(instance));
    }

    [Fact]
    public async Task A_message_from_the_instance_at_the_limit_closes_both()
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        using var socket = await ConnectAsync(router);
        await socket.SendAsync("big"u8.ToArray(), WebSocketMessageType.Text, true, Cancel);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await ExpectCloseAsync(socket));
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await InstanceCloseAsync(instance));
    }

    [Theory]
    [InlineData(VoiceBridge.MaxFrameBytes + 1)]
    [InlineData(VoiceBridge.MaxFrameBytes)]
    public async Task A_message_from_the_browser_at_the_limit_closes_both(int size)
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        using var socket = await ConnectAsync(router);
        await socket.SendAsync(new byte[size], WebSocketMessageType.Binary, true, Cancel);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await ExpectCloseAsync(socket));
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await InstanceCloseAsync(instance));
    }

    [Fact]
    public async Task The_instance_closing_closes_the_browser_with_1000()
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        using var socket = await ConnectAsync(router);
        await socket.SendAsync("close:4002"u8.ToArray(), WebSocketMessageType.Text, true, Cancel);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await ExpectCloseAsync(socket));
    }

    [Fact]
    public async Task An_unreachable_instance_closes_the_accepted_socket()
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        using var socket = await ConnectAsync(router, "margo-user-down");
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await ExpectCloseAsync(socket));
        Assert.Contains(router.Logs.Lines, line => line.EndsWith("voice connection to the instance failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_waiting_instance_refuses_the_handshake()
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() =>
            socket.ConnectAsync(new Uri(router.Address!.Replace("http://", "ws://", StringComparison.Ordinal) + "/i/" + Key("margo-user-p") + "/voice/ws"), Cancel));
        Assert.Equal(503, (int)socket.HttpStatusCode);
    }

    [Fact]
    public async Task An_upgrade_header_without_a_handshake_is_a_bad_request()
    {
        // Python answered 400 too, with aiohttp's text "No CONNECTION upgrade hdr: close" (PORTING.md).
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        Assert.Equal(400, (await RawGet(router, "/i/" + Key() + "/voice/ws", "GET", "Upgrade: websocket")).Status);
        Assert.Empty(instance.Seen);
    }
}
