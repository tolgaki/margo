using System.Text.Json.Nodes;
using Xunit;

namespace Margo.Cloud.WorkIQ.Tests;

/// <summary>
/// What the port does beyond the Python transport (PORTING.md, deviations 10 to 12): pooled connections for reads,
/// fresh ones for writes, compressed answers, a hard bound on the response body and a bounded session cache.
/// </summary>
public sealed class ConnectionAndBoundsTests : IAsyncLifetime
{
    private readonly KeepAliveMcpServer _server = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private static JsonObject FetchMe() => new() { ["entityUrls"] = new JsonArray("/me") };

    private static JsonObject Send() => new() { ["actionUrl"] = "/me/messages/draft-1/send" };

    // -- connections -------------------------------------------------------------------------

    [Fact]
    public async Task Read_calls_share_one_connection()
    {
        using var client = _server.Transport();
        foreach (var tool in new[] { "fetch", "fetch", "call_function", "retrieve", "ask", "get_schema" })
        {
            Assert.Equal(200, (int)(await client.CallAsync(tool, FetchMe(), "token-a", Token))["statusCode"]!);
        }

        // initialize, the notification and six calls: eight requests, one TCP handshake (Python: eight).
        Assert.Equal(8, _server.Seen.Count);
        Assert.Equal(1, _server.Connections);
        Assert.All(_server.Seen, seen =>
        {
            Assert.Null(seen.Request.Header("Connection"));
            Assert.Equal("gzip, deflate, br", seen.Request.Header("Accept-Encoding"));
        });
    }

    [Fact]
    public async Task Concurrent_reads_use_a_few_connections_not_one_each()
    {
        using var client = _server.Transport();
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        for (var round = 0; round < 5; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.CallAsync("fetch", FetchMe(), "token-a", Token)));
        }

        Assert.Equal(43, _server.Seen.Count);
        Assert.InRange(_server.Connections, 1, 8);
    }

    [Theory]
    [InlineData("create_entity")]
    [InlineData("update_entity")]
    [InlineData("delete_entity")]
    [InlineData("do_action")]
    [InlineData("a_tool_added_later")]
    public async Task A_write_never_travels_on_a_reused_connection(string tool)
    {
        using var client = _server.Transport();
        await client.CallAsync("fetch", FetchMe(), "token-a", Token); // leaves a pooled connection and a session
        await client.CallAsync(tool, Send(), "token-a", Token);
        await client.CallAsync(tool, Send(), "token-b", Token); // its handshake is not pooled either

        var requests = _server.Seen;
        Assert.Equal(7, requests.Count);
        Assert.Equal([1, 1, 1], requests.Take(3).Select(seen => seen.Connection));
        Assert.Equal([2, 3, 4, 5], requests.Skip(3).Select(seen => seen.Connection)); // one connection each
        Assert.All(requests.Skip(3), seen => Assert.Equal("close", seen.Request.Header("Connection")));
        Assert.Equal(new string?[] { "tools/call", "initialize", "notifications/initialized", "tools/call" },
                     requests.Skip(3).Select(seen => (string?)seen.Request.Json["method"]));

        await client.CallAsync("fetch", FetchMe(), "token-a", Token); // reads go back to the pooled connection
        Assert.Equal(1, _server.Seen[^1].Connection);
    }

    [Fact]
    public async Task Reuse_can_be_turned_off()
    {
        using var client = _server.Transport(new McpTransportOptions { ReuseConnections = false });
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal(4, _server.Seen.Count);
        Assert.Equal(4, _server.Connections);
        Assert.All(_server.Seen, seen => Assert.Equal("close", seen.Request.Header("Connection")));
    }

    [Fact]
    public async Task A_request_that_died_on_a_reused_connection_is_never_resent()
    {
        using var client = _server.Transport();
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        _server.HangUpOnCall = 2; // the server reads the second call whole, then closes without a word
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.IsNotAssignableFrom<TransportException>(failure); // the request was delivered: the guard records "unknown"
        Assert.IsNotAssignableFrom<OperationCanceledException>(failure);

        var calls = _server.Calls;
        Assert.Equal(2, calls.Count); // not three: neither the transport nor the HTTP stack sent it again
        Assert.Equal(1, calls[1].Connection);
        Assert.Equal(1, _server.Connections);

        Assert.Equal(200, (int)(await client.CallAsync("fetch", FetchMe(), "token-a", Token))["statusCode"]!); // and it recovers
        Assert.Equal(2, _server.Connections);
    }

    // -- compression and the response bound ------------------------------------------------------

    [Fact]
    public async Task Compressed_answers_are_read()
    {
        _server.Compress = true;
        _server.PayloadCharacters = 200_000;
        using var client = _server.Transport();
        var result = await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal(200_000, ((string)result["data"]!).Length);
        Assert.Equal(1, _server.Connections);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_response_beyond_the_limit_fails_the_call_instead_of_being_buffered(bool compress, bool chunked)
    {
        // 3 MiB of "a" (a few kilobytes when compressed) against a 1 MiB limit.
        _server.Compress = compress;
        _server.Chunked = chunked;
        using var client = _server.Transport(new McpTransportOptions { MaxResponseBytes = 1024 * 1024 });
        Assert.Equal(200, (int)(await client.CallAsync("fetch", FetchMe(), "token-a", Token))["statusCode"]!);

        _server.PayloadCharacters = 3 * 1024 * 1024;
        var failure = await Assert.ThrowsAsync<TransportResponseTooLargeException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal("Work IQ response too large", failure.Message);
        Assert.Null(failure.StatusCode);
        Assert.IsAssignableFrom<TransportException>(failure); // a read: "error"; a write: outcome unknown
        Assert.Equal(2, _server.Calls.Count); // not asked again

        _server.PayloadCharacters = 0;
        Assert.Equal(200, (int)(await client.CallAsync("fetch", FetchMe(), "token-a", Token))["statusCode"]!);
    }

    [Fact]
    public async Task The_limit_is_on_the_decompressed_size_and_exact()
    {
        _server.Compress = true;
        _server.PayloadCharacters = 100_000;
        using var probe = _server.Transport();
        await probe.CallAsync("fetch", FetchMe(), "token-a", Token);
        var answer = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["result"] = new JsonObject { ["structuredContent"] = new JsonObject { ["statusCode"] = 200, ["data"] = new string('a', 100_000) } },
        };
        var size = answer.ToJsonString().Length;

        using var exact = _server.Transport(new McpTransportOptions { MaxResponseBytes = size });
        Assert.Equal(200, (int)(await exact.CallAsync("fetch", FetchMe(), "token-b", Token))["statusCode"]!);
        using var oneLess = _server.Transport(new McpTransportOptions { MaxResponseBytes = size - 1 });
        await Assert.ThrowsAsync<TransportResponseTooLargeException>(() => oneLess.CallAsync("fetch", FetchMe(), "token-c", Token));
    }

    [Fact]
    public void Defaults_and_validation()
    {
        using var client = new McpHttpTransport();
        Assert.Same(McpTransportOptions.Default, client.Options);
        Assert.Equal(8L * 1024 * 1024, client.Options.MaxResponseBytes);
        Assert.Equal(32, client.Options.MaxSessions);
        Assert.Equal(TimeSpan.FromHours(1), client.Options.SessionIdleTimeout);
        Assert.True(client.Options.ReuseConnections);
        Assert.Equal(TimeSpan.FromMinutes(2), client.Options.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(1), client.Options.PooledConnectionIdleTimeout);
        Assert.Equal(["ask", "call_function", "fetch", "fetch_blob", "get_schema", "list_agents", "retrieve", "search_paths"],
                     client.Options.PooledTools.Order(StringComparer.Ordinal));

        foreach (var bad in new[]
                 {
                     new McpTransportOptions { MaxResponseBytes = 0 }, new McpTransportOptions { MaxSessions = 0 },
                     new McpTransportOptions { SessionIdleTimeout = TimeSpan.Zero },
                     new McpTransportOptions { PooledConnectionLifetime = TimeSpan.Zero },
                     new McpTransportOptions { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(-1) },
                 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new McpHttpTransport(options: bad));
        }
    }

    // -- sessions ------------------------------------------------------------------------------

    [Fact]
    public async Task Sessions_are_bounded_and_the_least_recently_used_goes_first()
    {
        using var client = _server.Transport(new McpTransportOptions { MaxSessions = 2 });
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        await client.CallAsync("fetch", FetchMe(), "token-b", Token);
        await client.CallAsync("fetch", FetchMe(), "token-a", Token); // a is now the most recent
        await client.CallAsync("fetch", FetchMe(), "token-c", Token); // b goes
        Assert.Equal((2, 3), (client.SessionCount, _server.Sessions));

        await client.CallAsync("fetch", FetchMe(), "token-a", Token); // still known
        Assert.Equal(3, _server.Sessions);
        Assert.Equal("session-1", _server.Seen[^1].Request.Header("Mcp-Session-Id"));

        await client.CallAsync("fetch", FetchMe(), "token-b", Token); // forgotten: a new handshake, and c goes
        Assert.Equal((2, 4), (client.SessionCount, _server.Sessions));

        for (var i = 0; i < 40; i++)
        {
            await client.CallAsync("fetch", FetchMe(), "rotating-token-" + i, Token);
        }

        Assert.Equal(2, client.SessionCount);
    }

    [Fact]
    public async Task Idle_sessions_are_dropped()
    {
        var clock = new ManualClock();
        using var client = _server.Transport(new McpTransportOptions { SessionIdleTimeout = TimeSpan.FromMinutes(30), TimeProvider = clock });
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        await client.CallAsync("fetch", FetchMe(), "token-b", Token);
        clock.Advance(TimeSpan.FromMinutes(20));
        await client.CallAsync("fetch", FetchMe(), "token-a", Token); // keeps a alive
        clock.Advance(TimeSpan.FromMinutes(20));
        await client.CallAsync("fetch", FetchMe(), "token-c", Token); // b has been idle for 40 minutes
        Assert.Equal((2, 3), (client.SessionCount, _server.Sessions));
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal(3, _server.Sessions);
        clock.Advance(TimeSpan.FromMinutes(31));
        await client.CallAsync("fetch", FetchMe(), "token-a", Token); // idle too long: a new session
        Assert.Equal((1, 4), (client.SessionCount, _server.Sessions));
    }

    [Fact]
    public void The_cache_keeps_a_newer_session_when_an_old_one_is_reported_gone()
    {
        var cache = new McpSessionCache(4, TimeSpan.FromHours(1), TimeProvider.System);
        var old = new McpSession("session-1", "2025-06-18");
        var replacement = new McpSession("session-2", "2025-06-18");
        cache.Set("key", old);
        cache.Set("key", replacement); // another call re-initialised first
        cache.Remove("key", old);
        Assert.Same(replacement, cache.Get("key"));
        cache.Remove("key", replacement);
        Assert.Null(cache.Get("key"));

        cache.Set("a", old);
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new McpSessionCache(0, TimeSpan.FromHours(1), TimeProvider.System));
    }

    [Fact]
    public async Task Disposing_the_transport_forgets_its_sessions()
    {
        var client = _server.Transport();
        await client.CallAsync("fetch", FetchMe(), "token-a", Token);
        Assert.Equal(1, client.SessionCount);
        client.Dispose();
        Assert.Equal(0, client.SessionCount);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
