using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Margo.Cloud.WorkIQ;

/// <summary>
/// Work IQ loaded as an MCP server, through the official MCP C# SDK (<c>ModelContextProtocol.Core</c>): the client
/// the guard sends every Work IQ call through.
/// </summary>
/// <remarks>
/// <para>What Margo adds on top of the SDK is only what an agent acting for a person needs:</para>
/// <list type="bullet">
/// <item><b>One MCP session per identity.</b> Work IQ answers as the subject of the bearer token, so each token gets
/// its own <see cref="McpClient"/> (a bounded cache that keeps only the token's hash as its key). The guard chooses
/// the token; this class never does.</item>
/// <item><b>Writes are never resent.</b> Read tools share their session's pooled connections, and a read whose session
/// the server forgot is sent once more on a new session (the server did not process it). Any other tool gets a
/// session and a connection of its own, closed after the call, and is never resent: a write that may have landed is
/// reported, not repeated.</item>
/// <item><b>Bounded.</b> Every call has a timeout (<see cref="McpTransportOptions.CallTimeout"/>), and no answer is read
/// beyond <see cref="McpTransportOptions.MaxResponseBytes"/>. Redirects are never followed: they would carry the token
/// to another URL.</item>
/// <item><b>Plain results.</b> A tool result becomes the JSON payload the guard reads (<c>statusCode</c>,
/// <c>results</c>), with <c>resource_link</c> items kept as <c>resource_links</c>; an <c>isError</c> result, an HTTP
/// error and a JSON-RPC error become a <see cref="TransportException"/>.</item>
/// </list>
/// <para>Thread-safe. Dispose closes every session.</para>
/// </remarks>
public sealed class WorkIQMcpTransport : IWorkIQTransport, IAsyncDisposable, IDisposable
{
    /// <summary>The client name sent in <c>initialize</c>.</summary>
    public const string ClientName = "margo-cloud";

    internal const string TimedOut = "Work IQ timed out";
    internal const string TooLarge = "Work IQ response too large";
    internal const string ToolError = "Work IQ tool error";
    internal const int MaxToolPages = 10;

    private const int MaxErrorChars = 300;

    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <summary>Create a client for <paramref name="endpoint"/> (Work IQ's endpoint by default).</summary>
    public WorkIQMcpTransport(string endpoint = WorkIQEndpoints.McpUrl, McpTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Work IQ transport requires https", nameof(endpoint));
        }

        Endpoint = endpoint;
        Options = (options ?? McpTransportOptions.Default).Validated();
    }

    /// <inheritdoc/>
    public string Endpoint { get; }

    /// <summary>The bounds and connection handling in use.</summary>
    public McpTransportOptions Options { get; }

    /// <summary>The number of open sessions (tests).</summary>
    public int SessionCount
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Count;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var write = !Options.PooledTools.Contains(tool);
        using var activity = WorkIQTelemetry.Source.StartActivity("workiq " + tool, System.Diagnostics.ActivityKind.Client);
        activity?.SetTag("workiq.tool", tool);
        activity?.SetTag("workiq.write", write);
        var started = Options.TimeProvider.GetTimestamp();
        Exception? failure = null;
        try
        {
            return await CallCoreAsync(tool, arguments, bearer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            failure = exc;
            throw;
        }
        finally
        {
            var outcome = WorkIQTelemetry.Outcome(failure);
            var status = (failure as TransportException)?.StatusCode;
            activity?.SetTag("workiq.outcome", outcome);
            if (status is not null)
            {
                activity?.SetTag("http.response.status_code", status);
            }

            if (failure is not null)
            {
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, outcome);
            }

            WorkIQTelemetry.CallDuration.Record(Options.TimeProvider.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("workiq.tool", tool), new KeyValuePair<string, object?>("workiq.outcome", outcome));
        }
    }

    private async Task<JsonObject> CallCoreAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(bearer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new CallToolRequestParams
        {
            Name = tool,
            Arguments = arguments.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)),
        };
        using var timeout = Deadline(cancellationToken);
        try
        {
            if (!Options.PooledTools.Contains(tool))
            {
                return await WriteAsync(request, bearer, timeout.Token).ConfigureAwait(false);
            }

            for (var attempt = 0; ; attempt++)
            {
                var session = SessionFor(bearer);
                try
                {
                    var client = await session.Client.WaitAsync(timeout.Token).ConfigureAwait(false);
                    var result = await client.CallToolAsync(request, timeout.Token).ConfigureAwait(false);
                    return Normalise(result);
                }
                catch (Exception exc) when (attempt == 0 && session.Established && SessionGone(exc))
                {
                    // The server forgot the session, so it did not process this read: one new session, sent once more.
                    Drop(session);
                }
                catch (Exception) when (!session.Established)
                {
                    Drop(session); // a session that never came up is not kept
                    throw;
                }
            }
        }
        catch (Exception exc) when (exc is not TransportException)
        {
            throw Mapped(exc, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WorkIQTool>> ListToolsAsync(string bearer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bearer);
        using var activity = WorkIQTelemetry.Source.StartActivity("workiq tools/list", System.Diagnostics.ActivityKind.Client);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = SessionFor(bearer);
        if (session.Tools is { } known)
        {
            return known;
        }

        using var timeout = Deadline(cancellationToken);
        try
        {
            var client = await session.Client.WaitAsync(timeout.Token).ConfigureAwait(false);
            var tools = new List<WorkIQTool>();
            string? cursor = null;
            for (var page = 0; page < MaxToolPages; page++)
            {
                var listed = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, timeout.Token).ConfigureAwait(false);
                tools.AddRange(listed.Tools.Select(Describe));
                cursor = listed.NextCursor;
                if (string.IsNullOrEmpty(cursor))
                {
                    break;
                }
            }

            session.Tools = tools;
            return tools;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            if (!session.Established)
            {
                Drop(session);
            }

            return [];
        }
    }

    /// <summary>A tool result as the payload the guard reads (exposed for tests).</summary>
    /// <exception cref="TransportException">The result is marked <c>isError</c>, carrying its <c>statusCode</c>.</exception>
    public static JsonObject Normalise(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var tool = JsonSerializer.SerializeToNode(result, McpJsonUtilities.DefaultOptions) as JsonObject ?? [];
        JsonObject payload;
        if (tool["structuredContent"] is JsonObject structured)
        {
            payload = structured.DeepClone().AsObject();
        }
        else
        {
            var joined = string.Join("\n", (tool["content"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Where(item => Text(item["type"]) == "text" && item["text"] is not null)
                .Select(item => Text(item["text"]) ?? ""));
            try
            {
                var parsed = joined.Length > 0 ? JsonNode.Parse(joined) : new JsonObject();
                payload = parsed as JsonObject ?? new JsonObject { ["value"] = parsed };
            }
            catch (JsonException)
            {
                payload = new JsonObject { ["text"] = joined };
            }
        }

        if (result.IsError == true)
        {
            throw new TransportException(ToolError, StatusCode(payload));
        }

        // Tools that create something (Office documents, Pages) answer with a resource_link: kept for the caller.
        var links = new JsonArray();
        foreach (var item in (tool["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Text(item["type"]) == "resource_link" && Text(item["uri"]) is { Length: > 0 } uri)
            {
                var link = new JsonObject { ["uri"] = uri };
                foreach (var key in (string[])["name", "mimeType"])
                {
                    if (Text(item[key]) is { } value)
                    {
                        link[key] = value;
                    }
                }

                links.Add(link);
            }
        }

        if (links.Count > 0 && !payload.ContainsKey("resource_links"))
        {
            payload["resource_links"] = links;
        }

        return payload;
    }

    /// <summary>Close every session.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Session> sessions;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Close every session.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task<JsonObject> WriteAsync(CallToolRequestParams request, string bearer, CancellationToken cancellationToken)
    {
        // A session and a connection of its own: nothing below can resend this call.
        var session = new Session(this, bearer, pooled: false);
        try
        {
            var client = await session.Client.WaitAsync(cancellationToken).ConfigureAwait(false);
            var result = await client.CallToolAsync(request, cancellationToken).ConfigureAwait(false);
            return Normalise(result);
        }
        finally
        {
            _ = session.CloseAsync();
        }
    }

    private Session SessionFor(string bearer)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer)));
        var now = Options.TimeProvider.GetUtcNow();
        List<Session> closed = [];
        Session session;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (staleKey, stale) in _sessions.Where(pair => now - pair.Value.LastUsed > Options.SessionIdleTimeout).ToList())
            {
                _sessions.Remove(staleKey);
                closed.Add(stale);
            }

            if (!_sessions.TryGetValue(key, out session!))
            {
                while (_sessions.Count >= Options.MaxSessions)
                {
                    var oldest = _sessions.MinBy(pair => pair.Value.LastUsed);
                    _sessions.Remove(oldest.Key);
                    closed.Add(oldest.Value);
                }

                session = new Session(this, bearer, pooled: true) { Key = key };
                _sessions[key] = session;
            }

            session.LastUsed = now;
        }

        foreach (var stale in closed)
        {
            _ = stale.CloseAsync();
        }

        return session;
    }

    private void Drop(Session session)
    {
        lock (_lock)
        {
            if (session.Key is { } key && _sessions.TryGetValue(key, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(key);
            }
        }

        _ = session.CloseAsync();
    }

    private CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(Options.CallTimeout);
        return source;
    }

    /// <summary>The server no longer knows the session: HTTP 404 on an established session, as the MCP specification says.</summary>
    private static bool SessionGone(Exception exc) =>
        Chain(exc).Any(inner => inner is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                                || (inner is McpException && inner.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
                                    && inner.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The guard's view of a failure. Anything not recognised is returned as it is: an unknown outcome.</summary>
    private static Exception Mapped(Exception exc, CancellationToken caller)
    {
        if (caller.IsCancellationRequested && exc is OperationCanceledException)
        {
            return exc;
        }

        var chain = Chain(exc).ToList();
        if (chain.OfType<TransportResponseTooLargeException>().FirstOrDefault() is { } tooLarge)
        {
            return tooLarge;
        }

        if (chain.Any(inner => inner is OperationCanceledException or TimeoutException))
        {
            return new TransportTimeoutException(TimedOut, exc);
        }

        if (chain.OfType<HttpRequestException>().FirstOrDefault(http => http.StatusCode is not null) is { } http)
        {
            return new TransportException("Work IQ answered HTTP " + (int)http.StatusCode!.Value, (int)http.StatusCode.Value, exc);
        }

        if (chain.OfType<McpProtocolException>().FirstOrDefault() is { } protocol)
        {
            // A JSON-RPC error: the server's message helps the model correct its arguments.
            const string Remote = "Request failed (remote): "; // the SDK's prefix for an error the server returned
            var message = protocol.Message.StartsWith(Remote, StringComparison.Ordinal) ? protocol.Message[Remote.Length..] : protocol.Message;
            message = message.Length > MaxErrorChars ? message[..MaxErrorChars] : message;
            return new TransportException("Work IQ refused the request (JSON-RPC " + (int)protocol.ErrorCode + ")", null, exc)
            {
                ServerMessage = message,
            };
        }

        if (chain.OfType<HttpRequestException>().FirstOrDefault() is { } unreachable)
        {
            return new TransportException("Work IQ unreachable or the connection was lost", null, unreachable);
        }

        return exc;
    }

    private static IEnumerable<Exception> Chain(Exception exc)
    {
        for (Exception? current = exc; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }

                yield break;
            }

            yield return current;
        }
    }

    private static WorkIQTool Describe(Tool tool)
    {
        var schema = JsonSerializer.SerializeToNode(tool.InputSchema, McpJsonUtilities.DefaultOptions) as JsonObject
                     ?? new JsonObject { ["type"] = "object" };
        return new WorkIQTool(tool.Name, tool.Description ?? "", schema, tool.Annotations?.ReadOnlyHint);
    }

    private static int? StatusCode(JsonObject payload) =>
        payload["statusCode"] is JsonValue value && value.TryGetValue<int>(out var code) ? code : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private HttpClient NewHttpClient(bool pooled)
    {
        HttpMessageHandler inner = Options.HandlerFactory?.Invoke(pooled) ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = pooled ? Options.PooledConnectionLifetime : TimeSpan.Zero,
            PooledConnectionIdleTimeout = Options.PooledConnectionIdleTimeout,
        };
        if (pooled && Options.MaxRetryAfter > TimeSpan.Zero)
        {
            inner = new RetryAfterHandler(inner, Options.MaxRetryAfter, Options.TimeProvider); // reads only
        }

        WorkIQTelemetry.SessionsOpened.Add(1, new KeyValuePair<string, object?>("workiq.write", !pooled));
        return new HttpClient(new BoundedHandler(inner, Options.MaxResponseBytes)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>One MCP session: an <see cref="McpClient"/> connected with one bearer token.</summary>
    private sealed class Session
    {
        private readonly HttpClient _http;
        private int _closed;

        public Session(WorkIQMcpTransport owner, string bearer, bool pooled)
        {
            _http = owner.NewHttpClient(pooled);
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(owner.Endpoint),
                Name = "workiq",
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = "Bearer " + bearer },
                EnableStandaloneGetStream = false,
                OwnsSession = true,
            }, _http);
            Client = ConnectAsync(transport, owner.Options.ProtocolVersion);
        }

        public string? Key { get; init; }

        public Task<McpClient> Client { get; }

        public bool Established => Client.IsCompletedSuccessfully;

        public DateTimeOffset LastUsed { get; set; }

        public IReadOnlyList<WorkIQTool>? Tools { get; set; }

        public async Task CloseAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
            {
                return;
            }

            try
            {
                if (Client.IsCompletedSuccessfully)
                {
                    await Client.Result.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Ending a session is a courtesy to the server; it expires on its own.
            }
            finally
            {
                _http.Dispose();
            }
        }

        private static Task<McpClient> ConnectAsync(HttpClientTransport transport, string protocolVersion) =>
            McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = ClientName, Version = typeof(WorkIQMcpTransport).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" },
                ProtocolVersion = protocolVersion,
            });
    }
}
