using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Xunit;

namespace Margo.Cloud.WorkIQ.Tests;

/// <summary>
/// <c>tests/test_autopilot_runtime.py</c> <c>TransportTests</c> and <c>RedirectTests</c>, against a real
/// loopback HTTP server, plus the transport paths those tests do not reach.
/// </summary>
public sealed class TransportTests : IAsyncLifetime
{
    private readonly FakeMcpServer _server = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private static JsonObject FetchMe() => new() { ["entityUrls"] = new JsonArray("/me") };

    // -- ported from TransportTests -------------------------------------------------------

    [Fact]
    public void Requires_https_except_explicit_loopback()
    {
        Assert.Equal("Work IQ transport requires https", Assert.Throws<ArgumentException>(() => new McpHttpTransport("http://example.com/mcp")).Message);
        Assert.Throws<ArgumentException>(() => new McpHttpTransport("http://127.0.0.1:1/mcp"));
        using var standard = new McpHttpTransport();
        Assert.Equal(WorkIQEndpoints.McpUrl, standard.Endpoint);
        Assert.Equal("https://workiq.svc.cloud.microsoft/mcp", standard.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(60), standard.Timeout);
    }

    [Fact]
    public void Loopback_exception_is_exact_and_explicit()
    {
        Assert.Throws<ArgumentException>(() => new McpHttpTransport("http://example.com/mcp", allowLoopbackHttp: true));
        Assert.Throws<ArgumentException>(() => new McpHttpTransport("http://127.0.0.1/mcp", allowLoopbackHttp: true)); // no port: no prefix match
        Assert.Throws<ArgumentException>(() => new McpHttpTransport("HTTPS://workiq.svc.cloud.microsoft/mcp")); // case-sensitive, as in Python
        Assert.Throws<ArgumentException>(() => new McpHttpTransport("http://[::1]:1/mcp", allowLoopbackHttp: true));
        using var localhost = new McpHttpTransport("http://localhost:1/mcp", allowLoopbackHttp: true);
        Assert.Equal("http://localhost:1/mcp", localhost.Endpoint);
    }

    [Fact]
    public async Task Session_handshake_json_and_sse()
    {
        using var client = _server.Transport();
        _server.Modes.Enqueue("json");
        _server.Modes.Enqueue("sse");
        Assert.Equal(200, (int)(await client.CallAsync("fetch", FetchMe(), "token-a", Token))["statusCode"]!);
        var second = await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["ok"] = true }, second["data"]));
        Assert.Equal(new string?[] { "initialize", "notifications/initialized", "tools/call", "tools/call" }, _server.Methods);
        var call = _server.Seen[2];
        Assert.Equal("session-1", call.Header("Mcp-Session-Id"));
        Assert.Equal("Bearer token-a", call.Header("Authorization"));
    }

    [Fact]
    public async Task Separate_session_per_token()
    {
        using var client = _server.Transport();
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        await client.CallAsync("fetch", FetchMe(), "token-b", Token);
        Assert.Equal(2, _server.Sessions);
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal(2, _server.Sessions);
        Assert.Equal("session-1", _server.Seen[^1].Header("Mcp-Session-Id"));
    }

    [Fact]
    public async Task Expired_session_reinitialises_once()
    {
        using var client = _server.Transport();
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        _server.Modes.Enqueue("expired");
        _server.Modes.Enqueue("json");
        Assert.Equal(200, (int)(await client.CallAsync("fetch", FetchMe(), "token-a", Token))["statusCode"]!);
        Assert.Equal(2, _server.Sessions);
        Assert.Equal(
            new string?[] { "initialize", "notifications/initialized", "tools/call", "tools/call", "initialize", "notifications/initialized", "tools/call" },
            _server.Methods);
        // The resend is the same request, now on the new session.
        Assert.Equal(_server.Seen[3].BodyText, _server.Seen[6].BodyText);
        Assert.Equal("session-2", _server.Seen[6].Header("Mcp-Session-Id"));
    }

    [Fact]
    public async Task Errors_map_to_guard_transport_errors()
    {
        using var client = _server.Transport(TimeSpan.FromMilliseconds(200));
        _server.Modes.Enqueue("server_error");
        var server = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal(503, server.StatusCode);
        Assert.Equal("Work IQ HTTP 503", server.Message);

        _server.Modes.Enqueue("tool_error");
        var tool = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal(403, tool.StatusCode);
        Assert.Equal("Work IQ tool error", tool.Message);

        _server.Modes.Enqueue("slow");
        var slow = await Assert.ThrowsAsync<TransportTimeoutException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal("Work IQ timed out", slow.Message);
        Assert.Null(slow.StatusCode);
    }

    [Fact]
    public async Task Redirects_are_never_followed_with_the_token()
    {
        await using var sink = new FakeMcpServer();
        await using var source = new LoopbackHttpServer((_, exchange, cancellationToken) =>
            exchange.SendAsync(302, [new("Location", sink.Url.Replace("/mcp", "/steal", StringComparison.Ordinal)), new("Content-Length", "0")], null, cancellationToken));
        using var client = new McpHttpTransport(source.Url(), allowLoopbackHttp: true);
        var caught = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "secret-token", Token));
        Assert.Equal(302, caught.StatusCode);
        Assert.Empty(sink.Seen);
    }

    // -- request shapes ---------------------------------------------------------------------

    [Fact]
    public async Task Requests_have_the_python_shapes_and_headers()
    {
        using var client = _server.Transport();
        var arguments = new JsonObject { ["entityUrls"] = new JsonArray("/me/messages?$top=5") };
        await client.CallAsync("fetch", arguments, "token-a", Token);
        var (init, notify, call) = (_server.Seen[0], _server.Seen[1], _server.Seen[2]);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"margo-autopilot","version":"0.1.0"}}}"""),
            init.Json));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""), notify.Json));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"fetch","arguments":{"entityUrls":["/me/messages?$top=5"]}}}"""),
            call.Json));
        Assert.Equal(new[] { "jsonrpc", "id", "method", "params" }, call.Json.Select(pair => pair.Key));

        foreach (var request in _server.Seen)
        {
            Assert.Equal("POST", request.Method);
            Assert.Equal("/mcp", request.Path);
            Assert.Equal("application/json", request.Header("Content-Type"));
            Assert.Equal("application/json, text/event-stream", request.Header("Accept"));
            Assert.Equal("Bearer token-a", request.Header("Authorization"));
            Assert.Equal("close", request.Header("Connection"));
            Assert.Equal("identity", request.Header("Accept-Encoding"));
            Assert.Equal(request.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Header("Content-Length"));
            Assert.All(request.Body, b => Assert.True(b < 0x80)); // ensure_ascii
        }

        Assert.Null(init.Header("Mcp-Session-Id"));
        Assert.Null(init.Header("MCP-Protocol-Version"));
        Assert.Equal("session-1", notify.Header("Mcp-Session-Id"));
        Assert.Equal("2025-06-18", notify.Header("MCP-Protocol-Version"));
        Assert.Equal("2025-06-18", call.Header("MCP-Protocol-Version"));
    }

    [Fact]
    public async Task Arguments_are_sent_unchanged_and_never_taken_over()
    {
        using var client = _server.Transport();
        var owner = new JsonObject { ["arguments"] = new JsonObject { ["question"] = "O\u00F9 est la r\u00E9union? \U0001F600 <b>&</b>", ["n"] = 1.5 } };
        var arguments = owner["arguments"]!.AsObject();
        await client.CallAsync("ask", arguments, "token-a", Token);
        Assert.Same(owner, arguments.Parent);
        Assert.True(JsonNode.DeepEquals(arguments, _server.Seen[2].Json["params"]!["arguments"]));
    }

    [Fact]
    public async Task Server_protocol_version_is_sent_on_later_requests()
    {
        using var client = _server.Transport();
        _server.InitModes.Enqueue("version:\"2025-03-26\"");
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal("2025-03-26", _server.Seen[1].Header("MCP-Protocol-Version"));
        Assert.Equal("2025-03-26", _server.Seen[2].Header("MCP-Protocol-Version"));
    }

    [Theory]
    [InlineData("result:{}")]
    [InlineData("result:[]")]
    [InlineData("result:null")]
    [InlineData("result:0")]
    public async Task Missing_or_falsy_initialize_result_assumes_our_version(string mode)
    {
        using var client = _server.Transport();
        _server.InitModes.Enqueue(mode);
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal("2025-06-18", _server.Seen[2].Header("MCP-Protocol-Version"));
    }

    [Theory]
    [InlineData("version:5", "5")]
    [InlineData("version:true", "True")]
    public async Task Integer_versions_are_sent_as_python_renders_them(string mode, string header)
    {
        using var client = _server.Transport();
        _server.InitModes.Enqueue(mode);
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal(header, _server.Seen[1].Header("MCP-Protocol-Version"));
    }

    [Theory]
    [InlineData("version:null")]
    [InlineData("version:1.5")]
    [InlineData("version:{}")]
    [InlineData("version:\"2025\\n06\"")]
    [InlineData("result:[1]")]
    [InlineData("result:\"text\"")]
    public async Task Unsendable_versions_fail_before_the_notification_and_cache_nothing(string mode)
    {
        using var client = _server.Transport();
        _server.InitModes.Enqueue(mode);
        var failure = await Record.ExceptionAsync(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.IsType<TransportFaultException>(failure);
        Assert.Equal(new string?[] { "initialize" }, _server.Methods);
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal(2, _server.Sessions);
    }

    // -- session handling ---------------------------------------------------------------------

    [Fact]
    public async Task Refused_session_is_an_error_and_not_cached()
    {
        using var client = _server.Transport();
        _server.InitModes.Enqueue("error");
        var refused = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal("Work IQ refused the MCP session", refused.Message);
        Assert.Null(refused.StatusCode);
        Assert.Equal(new string?[] { "initialize" }, _server.Methods);
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal(2, _server.Sessions);
    }

    [Fact]
    public async Task Initialize_http_error_carries_its_status()
    {
        using var client = _server.Transport();
        _server.InitModes.Enqueue("http_500");
        var failed = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal(500, failed.StatusCode);
        Assert.Equal("Work IQ HTTP 500", failed.Message);
    }

    [Fact]
    public async Task Without_a_session_id_no_header_is_sent_and_404_is_not_retried()
    {
        using var client = _server.Transport();
        _server.InitModes.Enqueue("no_session");
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Null(_server.Seen[1].Header("Mcp-Session-Id"));
        Assert.Null(_server.Seen[2].Header("Mcp-Session-Id"));
        Assert.Equal("2025-06-18", _server.Seen[2].Header("MCP-Protocol-Version"));

        _server.Modes.Enqueue("expired");
        var missing = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal(404, missing.StatusCode);
        Assert.Equal(1, _server.Sessions);
    }

    [Fact]
    public async Task Session_forgotten_twice_escapes_as_an_unknown_failure_after_one_resend()
    {
        using var client = _server.Transport();
        _server.Modes.Enqueue("expired");
        _server.Modes.Enqueue("expired");
        var failure = await Record.ExceptionAsync(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.IsType<McpUnknownSessionException>(failure);
        Assert.IsNotAssignableFrom<TransportException>(failure);
        Assert.Equal(2, _server.Methods.Count(method => method == "tools/call"));
    }

    [Fact]
    public async Task Notification_404_escapes_and_caches_nothing()
    {
        using var client = _server.Transport();
        _server.NotifyModes.Enqueue("404");
        var failure = await Record.ExceptionAsync(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.IsType<McpUnknownSessionException>(failure);
        Assert.Equal(new string?[] { "initialize", "notifications/initialized" }, _server.Methods);
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal(2, _server.Sessions);
    }

    [Fact]
    public async Task Notification_error_is_a_transport_error()
    {
        using var client = _server.Transport();
        _server.NotifyModes.Enqueue("500");
        var failed = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal(500, failed.StatusCode);
    }

    [Fact]
    public async Task Request_ids_count_up_across_sessions()
    {
        using var client = _server.Transport();
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        await client.CallAsync("fetch", FetchMe(), "token-b", Token);
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        var ids = _server.Seen.Select(request => request.Json["id"]?.GetValue<long>()).ToList();
        Assert.Equal(new long?[] { 1, null, 2, 3, null, 4, 5 }, ids);
    }

    [Fact]
    public async Task Concurrent_calls_are_safe()
    {
        using var client = _server.Transport();
        var calls = Enumerable.Range(0, 12).Select(i => client.CallAsync("fetch", FetchMe(), "token-" + (i % 3), Token));
        var results = await Task.WhenAll(calls);
        Assert.All(results, result => Assert.Equal(200, (int)result["statusCode"]!));
        var ids = _server.Seen.Select(request => request.Json["id"]).OfType<JsonNode>().Select(id => id.GetValue<long>()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.InRange(_server.Sessions, 3, 12);
    }

    [Fact]
    public async Task Json_rpc_error_on_a_call()
    {
        using var client = _server.Transport();
        _server.Modes.Enqueue("rpc_error");
        var failed = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal("Work IQ JSON-RPC error", failed.Message);
        Assert.Null(failed.StatusCode);
    }

    [Fact]
    public async Task Returned_payload_is_detached()
    {
        using var client = _server.Transport();
        _server.Modes.Enqueue("tool_error");
        _server.Modes.Enqueue("sse");
        await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        var payload = await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Null(payload.Parent);
        _ = new JsonObject { ["data"] = payload };
    }

    // -- failure classification -------------------------------------------------------------

    [Fact]
    public async Task Unreachable_before_sending_is_a_transport_error()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var client = new McpHttpTransport("http://127.0.0.1:" + port + "/mcp", allowLoopbackHttp: true);
        var failed = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal("Work IQ unreachable", failed.Message);
        Assert.Null(failed.StatusCode);
        Assert.IsType<TransportException>(failed);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("garbage")]
    public async Task Failure_after_the_request_was_sent_is_not_a_transport_error(string mode)
    {
        // Python: RemoteDisconnected or BadStatusLine escape getresponse() unwrapped; the guard says "unknown".
        using var client = _server.Transport();
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        _server.Modes.Enqueue(mode);
        var failure = await Record.ExceptionAsync(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.IsType<HttpRequestException>(failure);
    }

    [Fact]
    public async Task Truncated_body_is_not_a_transport_error()
    {
        // Python: http.client.IncompleteRead escapes response.read() unwrapped.
        using var client = _server.Transport();
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        _server.Modes.Enqueue("truncated");
        var failure = await Record.ExceptionAsync(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.NotNull(failure);
        Assert.IsNotAssignableFrom<TransportException>(failure);
        Assert.IsAssignableFrom<IOException>(failure);
    }

    [Fact]
    public async Task Stalled_body_times_out()
    {
        using var client = _server.Transport(TimeSpan.FromMilliseconds(200));
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        _server.Modes.Enqueue("stall_body");
        await Assert.ThrowsAsync<TransportTimeoutException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
    }

    [Fact]
    public async Task Timeout_bounds_each_wait_not_the_whole_body()
    {
        // Python's socket timeout applies per read: a body that keeps arriving is not cut off.
        using var client = _server.Transport(TimeSpan.FromMilliseconds(300));
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        _server.Modes.Enqueue("trickle");
        var started = DateTime.UtcNow;
        var result = await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal(200, (int)result["statusCode"]!);
        Assert.True(DateTime.UtcNow - started > TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_timeout()
    {
        using var client = _server.Transport(TimeSpan.FromSeconds(30));
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        _server.SlowDelay = TimeSpan.FromSeconds(5);
        _server.Modes.Enqueue("slow");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));
        var failure = await Record.ExceptionAsync(() => client.CallAsync("fetch", FetchMe(), "token-a", cancel.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
    }

    [Theory]
    [InlineData("token\nX-Injected: 1")]
    [InlineData("token\r")]
    [InlineData("tok\u0101en")]
    public async Task Unsendable_tokens_fail_before_anything_is_sent(string bearer)
    {
        using var client = _server.Transport();
        var failure = await Record.ExceptionAsync(() => client.CallAsync("fetch", FetchMe(), bearer, Token));
        Assert.IsType<TransportFaultException>(failure);
        Assert.Empty(_server.Seen);
    }
}
