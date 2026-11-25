using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Margo.Cloud.WorkIQ.Tests;

/// <summary>Work IQ through the MCP SDK: sessions per identity, writes never resent, bounded calls, plain results.</summary>
public sealed class WorkIQMcpTransportTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static (WorkIQMcpTransport Transport, ScriptedMcpServer Server) Create(McpTransportOptions? options = null)
    {
        var server = new ScriptedMcpServer();
        var transport = new WorkIQMcpTransport(options: (options ?? McpTransportOptions.Default) with { HandlerFactory = server.Handler });
        return (transport, server);
    }

    private static JsonObject Fetch(string path = "/me/messages?$top=1") => new() { ["entityUrls"] = new JsonArray(path) };

    [Fact]
    public async Task A_read_returns_the_tool_payload_and_sends_the_token_it_was_given()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        var payload = await transport.CallAsync("fetch", Fetch(), "token-a", Cancel);
        Assert.Equal(200, (int)payload["statusCode"]!);
        Assert.All(server.Requests, request => Assert.Equal("token-a", request.Bearer));
        // No server/discover probe: the protocol revision is pinned.
        Assert.Equal(["initialize", "notifications/initialized", "tools/call"], server.Requests.Select(r => r.Method));
        Assert.Equal("2025-06-18", server.ProtocolVersions.Single());
    }

    [Fact]
    public async Task Reads_share_one_session_per_token()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        await transport.CallAsync("fetch", Fetch(), "token-a", Cancel);
        await transport.CallAsync("ask", new JsonObject { ["question"] = "What changed?" }, "token-a", Cancel);
        await transport.CallAsync("fetch", Fetch(), "token-b", Cancel);
        Assert.Equal(2, server.Methods("initialize").Count());
        Assert.Equal(2, transport.SessionCount);
        var calls = server.Requests.Where(r => r.Method == "tools/call").ToList();
        Assert.Equal(calls[0].Session, calls[1].Session);
        Assert.NotEqual(calls[0].Session, calls[2].Session);
        Assert.All(calls, call => Assert.True(call.Pooled));
    }

    [Fact]
    public async Task A_write_gets_a_session_and_connection_of_its_own_and_closes_it()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        await transport.CallAsync("fetch", Fetch(), "token-a", Cancel);
        await transport.CallAsync("do_action", new JsonObject { ["actionUrl"] = "/me/sendMail", ["jsonBody"] = "{}" }, "token-a", Cancel);
        var write = Assert.Single(server.Requests, r => r.Method == "tools/call" && !r.Pooled);
        Assert.NotEqual(server.Requests.First(r => r.Method == "tools/call").Session, write.Session);
        await WaitFor(() => server.Requests.Any(r => r.Method == "DELETE" && r.Session == write.Session));
        Assert.Equal(1, transport.SessionCount); // the write's session is not kept
    }

    [Fact]
    public async Task A_read_whose_session_was_forgotten_is_sent_once_more_on_a_new_session()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        await transport.CallAsync("fetch", Fetch(), "token-a", Cancel);
        server.Forgotten[server.Requests.Last().Session!] = true;
        var payload = await transport.CallAsync("fetch", Fetch(), "token-a", Cancel);
        Assert.Equal(200, (int)payload["statusCode"]!);
        Assert.Equal(2, server.Methods("initialize").Count());
        Assert.Equal(3, server.Methods("tools/call").Count());
    }

    [Fact]
    public async Task A_write_is_never_resent()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        server.OnCall = (_, _, session) =>
        {
            server.Forgotten[session] = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        };
        var error = await Assert.ThrowsAsync<TransportException>(() =>
            transport.CallAsync("create_entity", new JsonObject { ["parentUrl"] = "/me/events", ["jsonBody"] = "{}" }, "token-a", Cancel));
        Assert.Equal(502, error.StatusCode);
        Assert.Single(server.Methods("tools/call"));
    }

    [Fact]
    public async Task A_tool_error_carries_its_status_code()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        server.OnCall = (_, _, _) => Task.FromResult(ScriptedMcpServer.Result(new JsonObject { ["statusCode"] = 403 }, isError: true));
        var error = await Assert.ThrowsAsync<TransportException>(() => transport.CallAsync("fetch", Fetch(), "token-a", Cancel));
        Assert.Equal((WorkIQMcpTransport.ToolError, 403), (error.Message, error.StatusCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    public async Task An_http_error_carries_its_status(HttpStatusCode status, int expected)
    {
        var (transport, server) = Create();
        await using var _ = transport;
        server.OnCall = (_, _, _) => Task.FromResult(new HttpResponseMessage(status));
        var error = await Assert.ThrowsAsync<TransportException>(() => transport.CallAsync("fetch", Fetch(), "token-a", Cancel));
        Assert.Equal(expected, error.StatusCode);
    }

    [Fact]
    public async Task A_json_rpc_error_keeps_the_server_message_for_the_model()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        server.OnCall = (_, _, _) => Task.FromResult(ScriptedMcpServer.Error(-32602, "entityUrls must be an array"));
        var error = await Assert.ThrowsAsync<TransportException>(() => transport.CallAsync("fetch", Fetch(), "token-a", Cancel));
        Assert.Null(error.StatusCode);
        Assert.Contains("-32602", error.Message, StringComparison.Ordinal);
        Assert.Equal("entityUrls must be an array", error.ServerMessage);
        Assert.DoesNotContain("entityUrls", error.Message, StringComparison.Ordinal); // the message may be logged
    }

    [Fact]
    public async Task A_call_that_takes_too_long_is_a_timeout()
    {
        var (transport, server) = Create(McpTransportOptions.Default with { CallTimeout = TimeSpan.FromMilliseconds(300) });
        await using var _ = transport;
        server.OnCall = async (_, _, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
            return ScriptedMcpServer.Result([]);
        };
        await Assert.ThrowsAsync<TransportTimeoutException>(() => transport.CallAsync("ask", new JsonObject { ["question"] = "?" }, "token-a", Cancel));
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_a_timeout()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        using var cancel = new CancellationTokenSource();
        server.OnCall = async (_, _, _) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
            return ScriptedMcpServer.Result([]);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.CallAsync("fetch", Fetch(), "token-a", cancel.Token));
    }

    [Fact]
    public async Task An_answer_larger_than_the_bound_is_not_read_on()
    {
        var (transport, server) = Create(McpTransportOptions.Default with { MaxResponseBytes = 4096 });
        await using var _ = transport;
        server.OnCall = (_, _, _) => Task.FromResult(ScriptedMcpServer.Result(new JsonObject { ["statusCode"] = 200, ["data"] = new string('x', 10_000) }));
        await Assert.ThrowsAsync<TransportResponseTooLargeException>(() => transport.CallAsync("fetch", Fetch(), "token-a", Cancel));
    }

    [Fact]
    public async Task Resource_links_are_kept()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        server.OnCall = (_, _, _) => Task.FromResult(ScriptedMcpServer.Result(new JsonObject { ["statusCode"] = 200 }, extra:
            [new JsonObject { ["type"] = "resource_link", ["uri"] = "https://contoso.example.com/doc.docx", ["name"] = "Plan.docx", ["mimeType"] = "application/msword" }]));
        var payload = await transport.CallAsync("do_action", new JsonObject { ["actionUrl"] = "/office-agent-mcp/generate_artifact" }, "token-a", Cancel);
        Assert.Equal("""[{"uri":"https://contoso.example.com/doc.docx","name":"Plan.docx","mimeType":"application/msword"}]""",
                     payload["resource_links"]!.ToJsonString());
    }

    [Fact]
    public async Task Text_that_is_not_json_is_kept_as_text()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        server.OnCall = (_, _, _) => Task.FromResult(ScriptedMcpServer.Json(new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "The review moved to Thursday." }),
        }));
        var payload = await transport.CallAsync("ask", new JsonObject { ["question"] = "?" }, "token-a", Cancel);
        Assert.Equal("The review moved to Thursday.", (string?)payload["text"]);
    }

    [Fact]
    public async Task The_server_describes_its_tools_and_the_list_is_kept_with_the_session()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        server.PageSize = 1;
        server.Tools.Add(new JsonObject
        {
            ["name"] = "fetch",
            ["description"] = "Fetch Microsoft 365 resources by path.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["entityUrls"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
                ["required"] = new JsonArray("entityUrls"),
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        });
        server.Tools.Add(new JsonObject { ["name"] = "ask", ["inputSchema"] = new JsonObject { ["type"] = "object" } });
        var tools = await transport.ListToolsAsync("token-a", Cancel);
        Assert.Equal(["fetch", "ask"], tools.Select(tool => tool.Name));
        Assert.Equal(("Fetch Microsoft 365 resources by path.", true), (tools[0].Description, tools[0].ReadOnly));
        Assert.Equal("array", (string?)tools[0].InputSchema["properties"]!["entityUrls"]!["type"]);
        Assert.Equal(("", null), (tools[1].Description, tools[1].ReadOnly));
        Assert.Same(tools, await transport.ListToolsAsync("token-a", Cancel));
        Assert.Equal(2, server.Methods("tools/list").Count());
    }

    [Fact]
    public async Task A_tool_list_that_fails_is_empty_and_not_kept()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        server.Forgotten["session-1"] = true; // every request after initialize is refused
        Assert.Empty(await transport.ListToolsAsync("token-a", Cancel));
    }

    [Fact]
    public async Task The_least_recently_used_session_is_closed_beyond_the_bound()
    {
        var (transport, server) = Create(McpTransportOptions.Default with { MaxSessions = 2 });
        await using var _ = transport;
        foreach (var token in (string[])["token-a", "token-b", "token-c"])
        {
            await transport.CallAsync("fetch", Fetch(), token, Cancel);
        }

        Assert.Equal(2, transport.SessionCount);
        await WaitFor(() => server.Requests.Any(r => r.Method == "DELETE" && r.Bearer == "token-a"));
    }

    [Fact]
    public void Only_https_endpoints_are_accepted()
    {
        Assert.Throws<ArgumentException>(() => new WorkIQMcpTransport("http://workiq.example.com/mcp"));
        Assert.Equal(WorkIQEndpoints.McpUrl, new WorkIQMcpTransport().Endpoint);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, Cancel);
        }

        Assert.True(condition());
    }

    private static HttpResponseMessage Throttled(TimeSpan wait)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        return response;
    }

    [Fact]
    public async Task A_throttled_read_waits_its_short_retry_after_once()
    {
        var (transport, server) = Create(McpTransportOptions.Default with { MaxRetryAfter = TimeSpan.FromSeconds(2) });
        await using var _ = transport;
        var calls = 0;
        server.OnCall = (_, _, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? Throttled(TimeSpan.Zero)
            : ScriptedMcpServer.Result(new JsonObject { ["statusCode"] = 200 }));
        var payload = await transport.CallAsync("fetch", Fetch(), "token-a", Cancel);
        Assert.Equal((200, 2), ((int)payload["statusCode"]!, calls));
    }

    [Fact]
    public async Task A_long_retry_after_or_a_second_refusal_is_the_error()
    {
        var (transport, server) = Create(McpTransportOptions.Default with { MaxRetryAfter = TimeSpan.FromSeconds(2) });
        await using var _ = transport;
        var calls = 0;
        server.OnCall = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Throttled(TimeSpan.FromSeconds(30)));
        };
        Assert.Equal(429, (await Assert.ThrowsAsync<TransportException>(() => transport.CallAsync("fetch", Fetch(), "token-a", Cancel))).StatusCode);
        Assert.Equal(1, calls);

        calls = 0;
        server.OnCall = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Throttled(TimeSpan.Zero));
        };
        Assert.Equal(429, (await Assert.ThrowsAsync<TransportException>(() => transport.CallAsync("fetch", Fetch(), "token-a", Cancel))).StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_throttled_write_is_never_resent()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        var calls = 0;
        server.OnCall = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Throttled(TimeSpan.Zero));
        };
        var error = await Assert.ThrowsAsync<TransportException>(() =>
            transport.CallAsync("do_action", new JsonObject { ["actionUrl"] = "/me/sendMail", ["jsonBody"] = "{}" }, "token-a", Cancel));
        Assert.Equal((429, 1), (error.StatusCode, calls));
    }

    [Fact]
    public async Task Each_call_is_traced_without_its_arguments()
    {
        var (transport, server) = Create();
        await using var _ = transport;
        var seen = new List<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == WorkIQTelemetry.Name,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (seen) { seen.Add(activity); } },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        await transport.CallAsync("fetch", Fetch("/me/messages?$search=secret-project"), "token-a", Cancel);
        var activity = Assert.Single(seen, a => a.OperationName == "workiq fetch");
        Assert.Equal(("fetch", false, "ok"), ((string?)activity.GetTagItem("workiq.tool"), (bool?)activity.GetTagItem("workiq.write"), (string?)activity.GetTagItem("workiq.outcome")));
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Value?.ToString()?.Contains("secret-project", StringComparison.Ordinal) == true);
    }
}
