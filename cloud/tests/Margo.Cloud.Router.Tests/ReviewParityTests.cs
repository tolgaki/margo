using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Router.Authentication;
using Margo.Cloud.Router.Tests.TestSupport;
using Xunit;

namespace Margo.Cloud.Router.Tests;

/// <summary>
/// Differences from the Python router found in review by sending the same raw requests to the Python
/// router (<c>build_app</c> in the venv: aiohttp 3.14.5, Agents SDK 1.10, PyJWT 2.15.1) and to this one.
/// Every expected status, header and body here is what the Python router answered.
/// </summary>
public sealed class ReviewParityTests
{
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static byte[] Activity(string user = RouterUnderTest.User, string text = "Brief me") =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = "message",
            ["recipient"] = new JsonObject { ["agenticAppId"] = RouterUnderTest.Identity, ["agenticUserId"] = user },
            ["text"] = text,
        }.ToJsonString());

    private sealed record RawResponse(int Status, IReadOnlyList<(string Name, string Value)> Headers, byte[] Body)
    {
        public string Text => Encoding.UTF8.GetString(Body);

        public string? Header(string name) => Headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();
    }

    /// <summary>One raw HTTP/1.x exchange with the Kestrel router: the head as given, the body after it.
    /// Reads the response head and its Content-Length body (no waiting for the connection to close).</summary>
    private static async Task<RawResponse> RawAsync(RouterUnderTest router, string head, byte[]? body = null, byte[]? rawHead = null)
    {
        var uri = new Uri(router.Address!);
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port, Cancel);
        await using var stream = client.GetStream();
        await stream.WriteAsync(rawHead ?? Encoding.Latin1.GetBytes(head.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n\r\n"), Cancel);
        if (body is not null)
        {
            await stream.WriteAsync(body, Cancel);
        }

        var received = new List<byte>();
        var buffer = new byte[65536];
        int end;
        while ((end = IndexOf(received, "\r\n\r\n"u8)) < 0)
        {
            var read = await stream.ReadAsync(buffer, Cancel);
            if (read == 0)
            {
                throw new IOException("the router closed the connection without a response");
            }

            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        var lines = Encoding.Latin1.GetString([.. received.Take(end)]).Split("\r\n");
        var headers = lines.Skip(1).Select(line => line.Split(':', 2)).Select(parts => (parts[0], parts[1].Trim())).ToList();
        var status = int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var length = headers.Where(h => h.Item1.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(h => int.Parse(h.Item2, System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
        var content = received.Skip(end + 4).ToList();
        while (content.Count < length)
        {
            var read = await stream.ReadAsync(buffer, Cancel);
            if (read == 0)
            {
                break;
            }

            content.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return new RawResponse(status, headers, [.. content]);
    }

    private static int IndexOf(List<byte> data, ReadOnlySpan<byte> pattern)
    {
        for (var i = 0; i + pattern.Length <= data.Count; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length && match; j++)
            {
                match = data[i + j] == pattern[j];
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static string PostHead(string token, int length, params string[] extra) =>
        "POST /api/messages HTTP/1.1\nHost: router.example.com\nConnection: close\nAuthorization: Bearer " + token
        + "\nContent-Length: " + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + string.Concat(extra.Select(h => "\n" + h));

    private static HttpRequestMessage Post(byte[] body, string token, params (string Name, string Value)[] contentHeaders)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/messages") { Content = new ByteArrayContent(body) };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        foreach (var (name, value) in contentHeaders)
        {
            request.Content.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return request;
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static async Task<(FakeInstance Instance, RouterUnderTest Router)> StartAsync(bool kestrel = true, InstanceConnector? connector = null)
    {
        var instance = await FakeInstance.StartAsync();
        var router = await RouterUnderTest.StartAsync(RouterUnderTest.Table(
            (RouterUnderTest.User, RouteStates.Ready, instance.Url),
            ("margo-user-down", RouteStates.Ready, "http://127.0.0.1:9")), kestrel, connector);
        return (instance, router);
    }

    [Fact]
    public async Task No_trace_context_reaches_the_instance()
    {
        // aiohttp's client sent none; .NET added traceparent (with the caller's trace id) and baggage.
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        var activity = Activity();
        var forwarded = await RawAsync(router, PostHead(router.Token(), activity.Length, "Content-Type: application/json",
            "traceparent: " + TraceParent, "baggage: tenant=fictional"), activity);
        Assert.Equal(200, forwarded.Status);
        var page = await RawAsync(router, "GET /i/" + RouterUnderTest.Key() + "/voice/ HTTP/1.1\nHost: router.example.com\nConnection: close\ntraceparent: " + TraceParent);
        Assert.Equal(200, page.Status);
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        socket.Options.SetRequestHeader("traceparent", TraceParent);
        await socket.ConnectAsync(new Uri(router.Address!.Replace("http://", "ws://", StringComparison.Ordinal) + "/i/" + RouterUnderTest.Key() + "/voice/ws"), Cancel);
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, Cancel);
        Assert.Equal(3, instance.Seen.Count);
        Assert.All(instance.Seen, seen =>
        {
            Assert.DoesNotContain(seen.HeaderNames, name => name.ToLowerInvariant() is "traceparent" or "tracestate" or "baggage" or "request-id" or "correlation-context");
        });
    }

    [Fact]
    public async Task Header_values_beyond_ascii_are_passed_on_as_utf8()
    {
        // "Bearer <token>": Python's str.split() splits at the no-break space, accepts the token and
        // forwards the header as received. .NET refused to write the non-ASCII value (500).
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        var token = router.Token();
        var activity = Activity();
        var head = Encoding.UTF8.GetBytes("POST /api/messages HTTP/1.1\r\nHost: router.example.com\r\nConnection: close\r\nAuthorization: Bearer " + token
            + "\r\nContent-Type: application/json; note=café\r\nContent-Length: " + activity.Length + "\r\n\r\n");
        var response = await RawAsync(router, string.Empty, activity, head);
        Assert.Equal(200, response.Status);
        var seen = Assert.Single(instance.Seen);
        Assert.Equal(("Bearer " + token, "application/json; note=café"), (seen.Authorization, seen.ContentType));
    }

    [Fact]
    public async Task A_failure_is_an_html_page_for_callers_that_accept_html()
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        const string Html = "<html><head><title>500 Internal Server Error</title></head><body>\n<h1>500 Internal Server Error</h1>\nServer got itself in trouble\n</body></html>\n";
        var page = await RawAsync(router, "GET /i/" + RouterUnderTest.Key("margo-user-down") + "/voice/ HTTP/1.1\nHost: router.example.com\nConnection: close\nAccept: text/html,application/xhtml+xml");
        Assert.Equal((500, "text/html; charset=utf-8", Html), (page.Status, page.Header("Content-Type"), page.Text));
        var activity = Activity("margo-user-down");
        var message = await RawAsync(router, PostHead(router.Token(), activity.Length, "Content-Type: application/json", "Accept: text/html"), activity);
        Assert.Equal((500, "text/html; charset=utf-8", Html), (message.Status, message.Header("Content-Type"), message.Text));

        // "text/html" in the first Accept value, case-sensitively, as Python tested it.
        var upper = await RawAsync(router, "GET /i/" + RouterUnderTest.Key("margo-user-down") + "/voice/ HTTP/1.1\nHost: router.example.com\nConnection: close\nAccept: TEXT/HTML");
        Assert.Equal((500, "text/plain; charset=utf-8", RouterService.ServerErrorText), (upper.Status, upper.Header("Content-Type"), upper.Text));
    }

    [Fact]
    public async Task An_instance_that_does_not_answer_in_time_is_a_504()
    {
        // aiohttp's server answered asyncio.TimeoutError (the client's total timeout) with an empty 504.
        var (instance, router) = await StartAsync(kestrel: false, new InstanceConnector(100, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300)));
        await using var _ = instance;
        await using var __ = router;
        var response = await router.Client.SendAsync(Post(Activity(text: "slow"), router.Token()), Cancel);
        Assert.Equal((504, "text/plain", string.Empty, 0L),
            ((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), await response.Content.ReadAsStringAsync(Cancel), response.Content.Headers.ContentLength));
        var page = await router.Client.GetAsync("/i/" + RouterUnderTest.Key() + "/voice/slow", Cancel);
        Assert.Equal((504, "text/plain"), ((int)page.StatusCode, page.Content.Headers.ContentType?.ToString()));
        Assert.Contains(router.Logs.Lines, line => line.EndsWith("Error handling request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_free_connection_within_the_connect_timeout_is_a_504()
    {
        // aiohttp's connect timeout covered the wait for a pooled connection (ConnectionTimeoutError).
        var connector = new InstanceConnector(1, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(300));
        var (instance, router) = await StartAsync(kestrel: false, connector);
        await using var _ = instance;
        await using var __ = router;
        using var held = await connector.AcquireAsync(Cancel);
        var response = await router.Client.SendAsync(Post(Activity(), router.Token()), Cancel);
        Assert.Equal((504, "text/plain"), ((int)response.StatusCode, response.Content.Headers.ContentType?.ToString()));
        Assert.Empty(instance.Seen);
    }

    [Fact]
    public async Task Unknown_expectations_are_refused_before_anything_else()
    {
        await using var router = await RouterUnderTest.StartAsync(kestrel: true);
        var health = await RawAsync(router, "GET /liveness HTTP/1.1\nHost: router.example.com\nConnection: close\nExpect: foo");
        Assert.Equal((417, "text/plain; charset=utf-8", "Unknown Expect: foo"), (health.Status, health.Header("Content-Type"), health.Text));
        var unauthenticated = await RawAsync(router, "POST /api/messages HTTP/1.1\nHost: router.example.com\nConnection: close\nExpect: bar\nContent-Length: 0");
        Assert.Equal((417, "Unknown Expect: bar"), (unauthenticated.Status, unauthenticated.Text));
        var missing = await RawAsync(router, "GET /nope HTTP/1.1\nHost: router.example.com\nConnection: close\nExpect: foo");
        Assert.Equal(417, missing.Status);
        var continued = await RawAsync(router, "GET /liveness HTTP/1.1\nHost: router.example.com\nConnection: close\nExpect: 100-CONTINUE");
        Assert.Equal(200, continued.Status);
        var http10 = await RawAsync(router, "GET /liveness HTTP/1.0\nHost: router.example.com\nExpect: foo");
        Assert.Equal(200, http10.Status); // the handler checked HTTP/1.1 only
    }

    [Fact]
    public async Task A_repeated_singleton_header_is_refused_as_aiohttp_s_parser_refused_it()
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        var agents = await RawAsync(router, "GET /liveness HTTP/1.1\nHost: router.example.com\nConnection: close\nUser-Agent: a\nuser-agent: b");
        Assert.Equal((400, "text/plain; charset=utf-8", "Duplicate 'User-Agent' header found."), (agents.Status, agents.Header("Content-Type"), agents.Text));
        var tags = await RawAsync(router, "GET /liveness HTTP/1.1\nHost: router.example.com\nConnection: close\nETag: a\nETag: b");
        Assert.Equal((400, "Duplicate 'Etag' header found."), (tags.Status, tags.Text));
        var activity = Activity();
        var types = await RawAsync(router, PostHead(router.Token(), activity.Length, "Content-Type: text/plain", "Content-Type: application/json"), activity);
        Assert.Equal((400, "Duplicate 'Content-Type' header found."), (types.Status, types.Text));
        Assert.Empty(instance.Seen);
        Assert.Equal(200, (await RawAsync(router, "GET /liveness HTTP/1.1\nHost: router.example.com\nConnection: close\nAccept: a\nAccept: b")).Status);
    }

    [Fact]
    public async Task Brotli_and_zstd_bodies_are_refused_before_anything_else()
    {
        // aiohttp had no Brotli or zstd module: its parser refused these, for any path, before the token check.
        await using var router = await RouterUnderTest.StartAsync(kestrel: true);
        var brotli = await RawAsync(router, "GET /liveness HTTP/1.1\nHost: router.example.com\nConnection: close\nContent-Encoding: br");
        Assert.Equal((400, "text/plain; charset=utf-8", "Can not decode content-encoding: brotli (br). Please install `Brotli`"),
            (brotli.Status, brotli.Header("Content-Type"), brotli.Text));
        var zstd = await RawAsync(router, "POST /api/messages HTTP/1.1\nHost: router.example.com\nConnection: close\nContent-Encoding: ZSTD\nContent-Length: 0");
        Assert.Equal((400, "Can not decode content-encoding: zstandard (zstd). Please install `backports.zstd`"), (zstd.Status, zstd.Text));
        Assert.Equal(200, (await RawAsync(router, "GET /liveness HTTP/1.1\nHost: router.example.com\nConnection: close\nContent-Encoding: br, gzip")).Status);
    }

    [Fact]
    public async Task Compressed_activities_are_decompressed_as_aiohttp_did()
    {
        var (instance, router) = await StartAsync(kestrel: false);
        await using var _ = instance;
        await using var __ = router;
        var token = router.Token();
        var activity = Activity();
        async Task<int> Status(byte[] body, params (string, string)[] headers) => (int)(await router.Client.SendAsync(Post(body, token, headers), Cancel)).StatusCode;

        Assert.Equal(200, await Status(Gzip(activity), ("Content-Encoding", "GZIP")));
        Assert.Equal(200, await Status(Zlib(activity), ("Content-Encoding", "deflate")));
        Assert.Equal(200, await Status(Zlib(activity)[2..^4], ("Content-Encoding", "deflate"))); // raw deflate, no zlib header
        Assert.Equal(200, await Status(Gzip(activity), ("Content-Encoding", "identity"), ("Content-Encoding", "gzip"))); // the last one counts
        Assert.Equal(3 + 1, instance.Seen.Count);
        Assert.All(instance.Seen, seen => Assert.Equal(activity.Length, seen.BodyLength)); // the instance gets the plain JSON
        Assert.Equal(400, await Status(Gzip(activity), ("Content-Encoding", "gzip, identity"))); // not one aiohttp decoded
        Assert.Equal(500, await Status("not gzip at all"u8.ToArray(), ("Content-Encoding", "gzip"))); // RequestPayloadError
        var padded = new byte[RouterCore.MaxActivityBytes + 1];
        Array.Fill(padded, (byte)' ');
        activity.CopyTo(padded, 0);
        Assert.Equal(413, await Status(Gzip(padded), ("Content-Encoding", "gzip"))); // the limit applies to the decompressed body
        var unauthenticated = Post(Gzip(activity), "x", ("Content-Encoding", "gzip"));
        Assert.Equal(401, (int)(await router.Client.SendAsync(unauthenticated, Cancel)).StatusCode);
    }

    [Fact]
    public async Task A_declared_length_beyond_kestrels_limit_is_413_not_500()
    {
        await using var router = await RouterUnderTest.StartAsync(kestrel: true);
        var activity = Activity("margo-user-new");
        var response = await RawAsync(router, PostHead(router.Token(), 40_000_000, "Content-Type: application/json"), activity);
        Assert.Equal((413, 0), (response.Status, response.Body.Length));
        Assert.Empty(router.Sink.Sent);
    }

    [Fact]
    public async Task The_asterisk_form_is_not_the_root_path()
    {
        await using var router = await RouterUnderTest.StartAsync(kestrel: true);
        var response = await RawAsync(router, "OPTIONS * HTTP/1.1\nHost: router.example.com\nConnection: close");
        Assert.Equal((404, RouterService.NotFoundText), (response.Status, response.Text));
    }

    [Theory]
    [InlineData("Connection: close", "No CONNECTION upgrade hdr: close")]
    [InlineData("Connection: Upgrade, close\nSec-WebSocket-Version: 99\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==", "Unsupported version: 99")]
    [InlineData("Connection: Upgrade, close\nSec-WebSocket-Version: 99\nSec-WebSocket-Version: 13\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==", "Unsupported version: 99")]
    [InlineData("Connection: Upgrade, close\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==", "Unsupported version: ")]
    [InlineData("Connection: Upgrade, close\nSec-WebSocket-Version: 13", "Handshake error: None")]
    [InlineData("Connection: Upgrade, close\nSec-WebSocket-Version: 13\nSec-WebSocket-Key: abc", "Handshake error: 'abc'")]
    [InlineData("Connection: Upgrade, close\nSec-WebSocket-Version: 13\nSec-WebSocket-Key: it's", "Handshake error: \"it's\"")]
    [InlineData("Connection: Upgrade, close\nSec-WebSocket-Version: 13\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25j", "Handshake error: 'dGhlIHNhbXBsZSBub25j'")]
    [InlineData("Connection: Upgrade, close\nSec-WebSocket-Version: 8\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==", "400: Bad Request")] // aiohttp accepted (PORTING.md)
    public async Task A_bad_websocket_handshake_gets_aiohttp_s_message(string headers, string message)
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        var response = await RawAsync(router, "GET /i/" + RouterUnderTest.Key() + "/voice/ws HTTP/1.1\nHost: router.example.com\nUpgrade: websocket\n" + headers);
        Assert.Equal((400, "text/plain; charset=utf-8", message), (response.Status, response.Header("Content-Type"), response.Text));
        Assert.Empty(instance.Seen);
    }

    [Fact]
    public async Task A_websocket_key_beyond_ascii_fails_as_python_s_uncaught_value_error()
    {
        var (instance, router) = await StartAsync();
        await using var _ = instance;
        await using var __ = router;
        var head = Encoding.UTF8.GetBytes("GET /i/" + RouterUnderTest.Key() + "/voice/ws HTTP/1.1\r\nHost: router.example.com\r\nUpgrade: websocket\r\n"
            + "Connection: Upgrade, close\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: éabc\r\n\r\n");
        Assert.Equal(500, (await RawAsync(router, string.Empty, rawHead: head)).Status);
    }

    [Theory]
    [InlineData("AQAB", "010001")]
    [InlineData("AQ=AB", "010001")] // Python 3.13 skips a "=" before the end
    [InlineData("AQAB=", "010001")]
    [InlineData("A-_B", "03efc1")]
    [InlineData("AQéAB", "010001")] // UTF-8 bytes count towards the padding, then are skipped
    [InlineData("AQ.B", null)] // "Incorrect padding": PyJWT skipped the key
    public void Key_numbers_are_read_as_pyjwt_reads_them(string text, string? hex)
    {
        // PyJWT 2.15.1 from_base64url_uint on Python 3.13.16, run in the venv.
        var ok = JwksClient.TryUnsignedFromBase64Url(JsonValue.Create(text), out var value);
        Assert.Equal(hex, ok ? Convert.ToHexStringLower(value) : null);
    }

    [Fact]
    public void A_key_with_an_even_exponent_is_not_usable()
    {
        // cryptography: "e must be odd."; PyJWKSet then had no usable key.
        var jwk = Tokens.Jwk(Tokens.Key1, "k1");
        jwk["e"] = "AQAC";
        Assert.Throws<TokenRejectedException>(() => JwksClient.ParseKeySet(new JsonObject { ["keys"] = new JsonArray(jwk) }));
    }
}
