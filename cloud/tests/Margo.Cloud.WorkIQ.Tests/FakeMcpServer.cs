using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Margo.Cloud.WorkIQ.Tests;

/// <summary>
/// The Python tests' <c>_McpHandler</c> on a real loopback socket: <c>initialize</c> opens
/// <c>session-N</c>, <c>notifications/initialized</c> is accepted with 202, and each <c>tools/call</c> takes the
/// next mode from <see cref="Modes"/> (default <c>json</c>). Extra modes and the init/notify queues cover
/// the transport paths the Python tests do not reach. Fictional data only.
/// </summary>
internal sealed class FakeMcpServer : IAsyncDisposable
{
    private readonly LoopbackHttpServer _http;
    private int _sessions;

    public FakeMcpServer()
    {
        _http = new LoopbackHttpServer(HandleAsync);
    }

    /// <summary>Modes for <c>tools/call</c>, consumed in order (Python <c>server.mode</c>).</summary>
    public ConcurrentQueue<string> Modes { get; } = new();

    /// <summary>Modes for <c>initialize</c>: <c>ok</c>, <c>no_session</c>, <c>error</c>, <c>http_500</c>,
    /// <c>version:JSON</c>, <c>result:JSON</c>.</summary>
    public ConcurrentQueue<string> InitModes { get; } = new();

    /// <summary>Modes for <c>notifications/initialized</c>: <c>ok</c> (202), <c>404</c>, <c>500</c>.</summary>
    public ConcurrentQueue<string> NotifyModes { get; } = new();

    /// <summary>How long <c>slow</c> waits before answering, and how long <c>stall_body</c> stalls.</summary>
    public TimeSpan SlowDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public int Sessions => Volatile.Read(ref _sessions);

    public string Url => _http.Url();

    public IReadOnlyList<SeenRequest> Seen => _http.Seen;

    public IReadOnlyList<string?> Methods => [.. Seen.Select(request => (string?)request.Json["method"])];

    public ValueTask DisposeAsync() => _http.DisposeAsync();

    public McpHttpTransport Transport(TimeSpan? timeout = null) => new(Url, timeout, allowLoopbackHttp: true);

    private static string Next(ConcurrentQueue<string> queue, string fallback) =>
        queue.TryDequeue(out var mode) ? mode : fallback;

    private async Task HandleAsync(SeenRequest request, LoopbackHttpServer.Exchange exchange, CancellationToken cancellationToken)
    {
        var body = request.Json;
        var method = (string?)body["method"];
        var id = body["id"]?.DeepClone();
        if (method == "initialize")
        {
            var number = Interlocked.Increment(ref _sessions);
            var mode = Next(InitModes, "ok");
            var session = new[] { new KeyValuePair<string, string>("Mcp-Session-Id", "session-" + number.ToString(CultureInfo.InvariantCulture)) };
            JsonNode? result = new JsonObject { ["protocolVersion"] = "2025-06-18" };
            if (mode == "http_500")
            {
                await exchange.SendAsync(500, null, null, cancellationToken);
                return;
            }

            if (mode == "error")
            {
                var refusal = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32600, ["message"] = "fictional refusal" } };
                await exchange.SendJsonAsync(refusal, session, cancellationToken);
                return;
            }

            if (mode.StartsWith("version:", StringComparison.Ordinal))
            {
                result = new JsonObject { ["protocolVersion"] = JsonNode.Parse(mode["version:".Length..]) };
            }
            else if (mode.StartsWith("result:", StringComparison.Ordinal))
            {
                result = JsonNode.Parse(mode["result:".Length..]);
            }

            var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            await exchange.SendJsonAsync(answer, mode == "no_session" ? null : session, cancellationToken);
            return;
        }

        if (method == "notifications/initialized")
        {
            var status = Next(NotifyModes, "ok") switch
            {
                "404" => 404,
                "500" => 500,
                _ => 202,
            };
            await exchange.SendAsync(status, null, null, cancellationToken);
            return;
        }

        await CallAsync(Next(Modes, "json"), id, exchange, cancellationToken);
    }

    private async Task CallAsync(string mode, JsonNode? id, LoopbackHttpServer.Exchange exchange, CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case "expired":
                await exchange.SendAsync(404, null, null, cancellationToken);
                return;
            case "server_error":
                await exchange.SendAsync(503, null, null, cancellationToken);
                return;
            case "close":
                return; // hang up without an answer, after reading the whole request
            case "garbage":
                await exchange.Stream.WriteAsync(Encoding.ASCII.GetBytes("this is not http\r\n\r\n"), cancellationToken);
                return;
            case "slow":
                await Task.Delay(SlowDelay, cancellationToken);
                break;
        }

        JsonNode result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = new JsonObject { ["statusCode"] = 200, ["data"] = new JsonObject { ["ok"] = true } }.ToJsonString(),
            }),
        };
        if (mode == "tool_error")
        {
            result = new JsonObject { ["isError"] = true, ["structuredContent"] = new JsonObject { ["statusCode"] = 403 } };
        }

        var message = mode == "rpc_error"
            ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "fictional" } }
            : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        var data = Encoding.UTF8.GetBytes(message.ToJsonString());
        switch (mode)
        {
            case "sse":
                var events = Encoding.UTF8.GetBytes("event: message\ndata: " + message.ToJsonString() + "\n\n");
                await exchange.SendAsync(200, Headers("text/event-stream", events.Length), events, cancellationToken);
                return;
            case "truncated":
                await exchange.WriteHeadAsync(200, Headers("application/json", data.Length + 100), cancellationToken);
                await exchange.Stream.WriteAsync(data.AsMemory(0, 10), cancellationToken);
                return;
            case "stall_body":
                await exchange.WriteHeadAsync(200, Headers("application/json", data.Length), cancellationToken);
                await exchange.Stream.WriteAsync(data.AsMemory(0, 10), cancellationToken);
                await exchange.Stream.FlushAsync(cancellationToken);
                await Task.Delay(SlowDelay, cancellationToken);
                await exchange.Stream.WriteAsync(data.AsMemory(10), cancellationToken);
                return;
            case "trickle":
                // Five pieces, 100 ms apart: longer in total than a 300 ms timeout, never idle that long.
                await exchange.WriteHeadAsync(200, Headers("application/json", data.Length), cancellationToken);
                var piece = (data.Length + 4) / 5;
                for (var offset = 0; offset < data.Length; offset += piece)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                    await exchange.Stream.WriteAsync(data.AsMemory(offset, Math.Min(piece, data.Length - offset)), cancellationToken);
                    await exchange.Stream.FlushAsync(cancellationToken);
                }

                return;
            default:
                await exchange.SendAsync(200, Headers("application/json", data.Length), data, cancellationToken);
                return;
        }
    }

    private static KeyValuePair<string, string>[] Headers(string contentType, int length) =>
    [
        new("Content-Type", contentType),
        new("Content-Length", length.ToString(CultureInfo.InvariantCulture)),
    ];
}
