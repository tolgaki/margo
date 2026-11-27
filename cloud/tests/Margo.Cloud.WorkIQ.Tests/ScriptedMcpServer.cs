using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Margo.Cloud.WorkIQ.Tests;

/// <summary>
/// A scripted Work IQ: a minimal streamable-HTTP MCP server behind an <see cref="HttpMessageHandler"/>. It answers
/// <c>initialize</c> with a session ID, <c>tools/list</c> from <see cref="Tools"/> and <c>tools/call</c> through
/// <see cref="OnCall"/>, and records every request.
/// </summary>
internal sealed class ScriptedMcpServer
{
    private int _sessions;

    /// <summary>What a <c>tools/call</c> answers: an HTTP response for the request (tool, arguments, session).</summary>
    public Func<string, JsonObject, string, Task<HttpResponseMessage>> OnCall { get; set; } =
        (_, _, _) => Task.FromResult(Result(new JsonObject { ["statusCode"] = 200, ["data"] = new JsonObject() }));

    /// <summary>The tools <c>tools/list</c> returns, in pages of <see cref="PageSize"/>.</summary>
    public List<JsonObject> Tools { get; } = [];

    public int PageSize { get; set; } = 100;

    /// <summary>The protocol revisions clients asked for in <c>initialize</c>.</summary>
    public ConcurrentQueue<string> ProtocolVersions { get; } = new();

    /// <summary>Sessions the server has forgotten: a request in one is answered 404.</summary>
    public ConcurrentDictionary<string, bool> Forgotten { get; } = new(StringComparer.Ordinal);

    /// <summary>Every request: method (JSON-RPC method, or the HTTP method when there is no body), session, bearer.</summary>
    public ConcurrentQueue<(string Method, string? Session, string? Bearer, bool Pooled)> Requests { get; } = new();

    /// <summary>The number of HTTP handlers created (one per MCP session).</summary>
    public int Handlers;

    public IEnumerable<string> Methods(string method) => Requests.Where(request => request.Method == method).Select(request => request.Method);

    public HttpMessageHandler Handler(bool pooled)
    {
        Interlocked.Increment(ref Handlers);
        return new Scripted(this, pooled);
    }

    public static HttpResponseMessage Result(JsonObject payload, bool isError = false, JsonArray? extra = null)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() });
        foreach (var item in extra ?? [])
        {
            content.Add(item!.DeepClone());
        }

        return Json(new JsonObject { ["content"] = content, ["isError"] = isError });
    }

    public static HttpResponseMessage Json(JsonObject result) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 0, ["result"] = result }.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Error(int code, string message) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 0, ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request, bool pooled, CancellationToken cancellationToken)
    {
        var session = request.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.First() : null;
        var bearer = request.Headers.Authorization?.Parameter;
        if (request.Content is null)
        {
            Requests.Enqueue((request.Method.Method, session, bearer, pooled));
            return new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.OK : HttpStatusCode.MethodNotAllowed);
        }

        var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        var method = (string)body["method"]!;
        Requests.Enqueue((method, session, bearer, pooled));
        if (session is not null && Forgotten.ContainsKey(session))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        HttpResponseMessage response;
        switch (method)
        {
            case "initialize":
                ProtocolVersions.Enqueue((string)body["params"]!["protocolVersion"]!);
                response = Json(new JsonObject
                {
                    ["protocolVersion"] = body["params"]!["protocolVersion"]!.DeepClone(),
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "workiq-fake", ["version"] = "1.0.0" },
                });
                response.Headers.Add("Mcp-Session-Id", "session-" + Interlocked.Increment(ref _sessions));
                break;
            case "tools/list":
                var start = int.TryParse((string?)body["params"]?["cursor"], out var cursor) ? cursor : 0;
                var page = new JsonArray([.. Tools.Skip(start).Take(PageSize).Select(tool => (JsonNode)tool.DeepClone())]);
                var result = new JsonObject { ["tools"] = page };
                if (start + PageSize < Tools.Count)
                {
                    result["nextCursor"] = (start + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                response = Json(result);
                break;
            case "tools/call":
                response = await OnCall((string)body["params"]!["name"]!, body["params"]!["arguments"]!.AsObject(), session ?? "");
                break;
            default:
                if (body["id"] is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.Accepted); // a notification
                }

                response = Error(-32601, "Method not found");
                break;
        }

        // Echo the request's ID, as JSON-RPC requires.
        if (response.Content is StringContent && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            answer["id"] = body["id"]!.DeepClone();
            var replaced = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json");
            response.Content = replaced;
        }

        return response;
    }

    private sealed class Scripted(ScriptedMcpServer server, bool pooled) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            server.AnswerAsync(request, pooled, cancellationToken);
    }
}
