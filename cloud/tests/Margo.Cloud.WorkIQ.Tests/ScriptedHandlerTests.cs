using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Margo.Cloud.WorkIQ.Tests;

/// <summary>
/// A handler that answers from a script, for failure classes a loopback socket cannot produce on demand.
/// It reads (sends) the request body first unless <paramref name="readBody"/> is false, which stands for a
/// failure before anything was sent.
/// </summary>
internal sealed class ScriptedHandler(Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> respond, bool readBody = true) : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly List<JsonObject> _bodies = [];

    public IReadOnlyList<JsonObject> Bodies
    {
        get
        {
            lock (_lock)
            {
                return [.. _bodies];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!readBody)
        {
            return await respond(request, null, cancellationToken);
        }

        var body = JsonNode.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken))!.AsObject();
        lock (_lock)
        {
            _bodies.Add(body);
        }

        return await respond(request, (string?)body["method"], cancellationToken);
    }

    /// <summary>Answer <c>initialize</c> and the notification like Work IQ; everything else through <paramref name="call"/>.</summary>
    public static ScriptedHandler Mcp(Func<HttpRequestMessage, JsonObject, CancellationToken, Task<HttpResponseMessage>> call, IEnumerable<string>? sessionIds = null)
    {
        ScriptedHandler? handler = null;
        handler = new ScriptedHandler(async (request, method, cancellationToken) =>
        {
            var body = handler!.Bodies[^1];
            if (method == "initialize")
            {
                var response = Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["result"] = new JsonObject() });
                foreach (var id in sessionIds ?? ["session-1"])
                {
                    response.Headers.TryAddWithoutValidation("Mcp-Session-Id", id);
                }

                return response;
            }

            if (method == "notifications/initialized")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            return await call(request, body, cancellationToken);
        });
        return handler;
    }

    public static HttpResponseMessage Json(JsonNode payload, string? contentType = "application/json")
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

public sealed class ScriptedHandlerTests
{
    private const string Url = "https://workiq.svc.cloud.microsoft/mcp";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static JsonObject FetchMe() => new() { ["entityUrls"] = new JsonArray("/me") };

    [Fact]
    public async Task Failure_before_sending_is_unreachable()
    {
        var handler = new ScriptedHandler((_, _, _) => throw new HttpRequestException("fictional DNS failure"), readBody: false);
        using var client = new McpHttpTransport(Url, handler: handler);
        var failed = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal("Work IQ unreachable", failed.Message);
        Assert.Null(failed.StatusCode);
        Assert.IsType<HttpRequestException>(failed.InnerException);
    }

    [Fact]
    public async Task Os_level_timeout_is_a_timeout()
    {
        var handler = new ScriptedHandler((_, _, _) =>
            throw new HttpRequestException("fictional", new SocketException((int)SocketError.TimedOut)), readBody: false);
        using var client = new McpHttpTransport(Url, handler: handler);
        await Assert.ThrowsAsync<TransportTimeoutException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));

        var after = ScriptedHandler.Mcp((_, _, _) => throw new HttpRequestException("fictional", new TimeoutException()));
        using var sent = new McpHttpTransport(Url, handler: after);
        await Assert.ThrowsAsync<TransportTimeoutException>(() => sent.CallAsync("fetch", FetchMe(), "token-a", Token));
    }

    [Fact]
    public async Task Failure_after_sending_propagates_unwrapped()
    {
        var handler = ScriptedHandler.Mcp((_, _, _) => throw new HttpRequestException("fictional reset"));
        using var client = new McpHttpTransport(Url, handler: handler);
        var failure = await Record.ExceptionAsync(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.IsType<HttpRequestException>(failure);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(301)]
    [InlineData(304)]
    public async Task Every_non_success_status_is_carried(int status)
    {
        var handler = ScriptedHandler.Mcp((_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var client = new McpHttpTransport(Url, handler: handler);
        var failed = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal(status, failed.StatusCode);
        Assert.Equal("Work IQ HTTP " + status, failed.Message);
    }

    [Fact]
    public async Task Initialize_404_has_no_session_to_forget()
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new McpHttpTransport(Url, handler: handler);
        var failed = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal(404, failed.StatusCode);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Last_session_header_wins()
    {
        var seen = new List<string?>();
        var handler = ScriptedHandler.Mcp((request, body, _) =>
        {
            seen.Add(request.Headers.TryGetValues("Mcp-Session-Id", out var values) ? string.Join("|", values) : null);
            return Task.FromResult(ScriptedHandler.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["result"] = new JsonObject() }));
        }, ["session-old", "session-new"]);
        using var client = new McpHttpTransport(Url, handler: handler);
        Assert.Empty(await client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal(new string?[] { "session-new" }, seen);
    }

    [Fact]
    public async Task Sse_without_content_type_is_parsed_as_json()
    {
        var handler = ScriptedHandler.Mcp((_, body, _) =>
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["result"] = new JsonObject() };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("data: " + message.ToJsonString() + "\n\n")),
            };
            return Task.FromResult(response);
        });
        using var client = new McpHttpTransport(Url, handler: handler);
        var failed = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Equal("Work IQ returned a non-JSON body", failed.Message);
    }

    [Fact]
    public async Task Tool_error_with_an_out_of_range_status_has_no_status()
    {
        var handler = ScriptedHandler.Mcp((_, body, _) => Task.FromResult(ScriptedHandler.Json(JsonNode.Parse(
            "{\"jsonrpc\":\"2.0\",\"id\":" + body["id"]!.ToJsonString() + ",\"result\":{\"isError\":true,\"structuredContent\":{\"statusCode\":100000000000000000000}}}")!)));
        using var client = new McpHttpTransport(Url, handler: handler);
        var failed = await Assert.ThrowsAsync<TransportException>(() => client.CallAsync("fetch", FetchMe(), "token-a", Token));
        Assert.Null(failed.StatusCode);
    }

    [Fact]
    public void Handlers_that_follow_redirects_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new McpHttpTransport(Url, handler: new HttpClientHandler()));
        Assert.Throws<ArgumentException>(() => new McpHttpTransport(Url, handler: new SocketsHttpHandler()));
        Assert.Throws<ArgumentException>(() => new McpHttpTransport(Url, handler: new PassThrough(new SocketsHttpHandler())));
        using var allowed = new McpHttpTransport(Url, handler: new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true);
        Assert.Equal(Url, allowed.Endpoint);
    }

    [Fact]
    public void Timeout_must_be_positive_or_infinite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new McpHttpTransport(Url, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new McpHttpTransport(Url, TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new McpHttpTransport(Url, TimeSpan.FromDays(30)));
        using var forever = new McpHttpTransport(Url, Timeout.InfiniteTimeSpan);
        Assert.Equal(Timeout.InfiniteTimeSpan, forever.Timeout);
    }

    [Fact]
    public void Exception_hierarchy_matches_what_the_guard_distinguishes()
    {
        Assert.True(typeof(TransportException).IsAssignableFrom(typeof(TransportTimeoutException)));
        Assert.False(typeof(TransportException).IsAssignableFrom(typeof(McpUnknownSessionException)));
        Assert.False(typeof(TransportException).IsAssignableFrom(typeof(TransportFaultException)));
        Assert.Equal(503, new TransportException("unavailable", 503).StatusCode);
        Assert.Null(new TransportTimeoutException("slow").StatusCode);
    }

    private sealed class PassThrough(HttpMessageHandler inner) : DelegatingHandler(inner);
}
