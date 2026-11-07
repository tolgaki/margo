using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Margo.Cloud.WorkIQ;

/// <summary>
/// Minimal MCP client for the Work IQ endpoint (streamable HTTP, JSON-RPC 2.0); the port of
/// <c>autopilot/src/margo_autopilot/transport.py</c>.
/// </summary>
/// <remarks>
/// <para>It does one thing: send a <c>tools/call</c> to <see cref="WorkIQEndpoints.McpUrl"/> with the bearer
/// token the guard chose, and return a plain object. It never chooses tokens, never retries a call that may
/// have been processed, and maps failures onto the guard's transport errors so the guard can record truthful
/// outcomes.</para>
/// <para>The only retry is the one the MCP specification requires: if the server answers 404 for a session it
/// no longer knows, that request was not processed, so the client starts a new session and sends it once more.</para>
/// <para>One MCP session per bearer token; thread-safe. Every request is sent on a fresh connection
/// (<c>Connection: close</c>, as Python's urllib does), so the HTTP stack can never resend a request that a
/// reused connection may already have delivered. Redirects are never followed: they would carry the bearer
/// token to another URL.</para>
/// </remarks>
public sealed class McpHttpTransport : IWorkIQTransport, IDisposable
{
    /// <summary>The MCP protocol version offered in <c>initialize</c> and assumed when the server names none.</summary>
    public const string ProtocolVersion = "2025-06-18";

    /// <summary><c>clientInfo.name</c> sent in <c>initialize</c>.</summary>
    public const string ClientName = "margo-autopilot";

    /// <summary><c>clientInfo.version</c> sent in <c>initialize</c>.</summary>
    public const string ClientVersion = "0.1.0";

    internal const string RequiresHttps = "Work IQ transport requires https";
    internal const string JsonRpcError = "Work IQ JSON-RPC error";
    internal const string SessionRefused = "Work IQ refused the MCP session";
    internal const string TimedOut = "Work IQ timed out";
    internal const string Unreachable = "Work IQ unreachable";

    private const string Accept = "application/json, text/event-stream";
    private const string UserAgent = ClientName + "/" + ClientVersion;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly HttpClient _client;
    private readonly Dictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _lastId;

    /// <summary>Create a transport for <paramref name="url"/>.</summary>
    /// <param name="url">The MCP endpoint. Must be https, except <c>http://127.0.0.1:</c> or
    /// <c>http://localhost:</c> when <paramref name="allowLoopbackHttp"/> is set (local tests).</param>
    /// <param name="timeout">How long any single wait may take: for the response headers, and then for each
    /// read of the body. Defaults to 60 seconds; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> waits
    /// forever (Python <c>timeout=None</c>).</param>
    /// <param name="allowLoopbackHttp">Permit plain http to a loopback address.</param>
    /// <param name="handler">The HTTP handler; <c>null</c> builds one that never follows redirects. An injected
    /// handler must not follow redirects either.</param>
    /// <param name="disposeHandler">Whether disposing the transport disposes an injected handler.</param>
    /// <exception cref="ArgumentException">The URL is not https (Python <c>ValueError</c>), or the handler
    /// follows redirects.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive.</exception>
    public McpHttpTransport(
        string url = WorkIQEndpoints.McpUrl,
        TimeSpan? timeout = null,
        bool allowLoopbackHttp = false,
        HttpMessageHandler? handler = null,
        bool disposeHandler = false)
    {
        ArgumentNullException.ThrowIfNull(url);
        var insecureOk = allowLoopbackHttp
            && (url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) || url.StartsWith("http://localhost:", StringComparison.Ordinal));
        if (!url.StartsWith("https://", StringComparison.Ordinal) && !insecureOk)
        {
            throw new ArgumentException(RequiresHttps);
        }

        var wait = timeout ?? DefaultTimeout;
        if (wait != System.Threading.Timeout.InfiniteTimeSpan && (wait <= TimeSpan.Zero || wait > MaxTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be positive and at most 24.8 days, or infinite");
        }

        if (handler is not null && FollowsRedirects(handler))
        {
            throw new ArgumentException("the HTTP handler must not follow redirects: they would carry the bearer token to another URL", nameof(handler));
        }

        Endpoint = url;
        Timeout = wait;
        _client = handler is null
            ? new HttpClient(CreateHandler(), disposeHandler: true)
            : new HttpClient(handler, disposeHandler);
        _client.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // bounded per wait below, as Python's socket timeout is
    }

    /// <inheritdoc />
    public string Endpoint { get; }

    /// <summary>The per-wait timeout (Python <c>timeout</c>).</summary>
    public TimeSpan Timeout { get; }

    /// <inheritdoc />
    public async Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(bearer);

        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bearer)));
        var session = await SessionAsync(key, bearer, cancellationToken).ConfigureAwait(false);
        var id = NextId();
        var request = McpMessages.ToolsCall(id, tool, arguments);
        JsonObject message;
        try
        {
            message = (await PostAsync(request, id, bearer, session, capture: false, cancellationToken).ConfigureAwait(false)).Message!;
        }
        catch (McpUnknownSessionException)
        {
            lock (_lock)
            {
                _sessions.Remove(key);
            }

            var fresh = await SessionAsync(key, bearer, cancellationToken).ConfigureAwait(false);
            message = (await PostAsync(request, id, bearer, fresh, capture: false, cancellationToken).ConfigureAwait(false)).Message!;
        }

        if (message.ContainsKey("error"))
        {
            throw new TransportException(JsonRpcError);
        }

        return McpMessages.Normalise(message["result"]);
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    // -- internals ----------------------------------------------------------------------

    private long NextId() => Interlocked.Increment(ref _lastId);

    private async Task<McpSession> SessionAsync(string key, string bearer, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(key, out var known))
            {
                return known;
            }
        }

        var id = NextId();
        var init = McpMessages.Initialize(id, ProtocolVersion, ClientName, ClientVersion);
        var answer = await PostAsync(init, id, bearer, null, capture: true, cancellationToken).ConfigureAwait(false);
        var message = answer.Message!;
        if (message.ContainsKey("error"))
        {
            throw new TransportException(SessionRefused);
        }

        var session = new McpSession(answer.SessionId, NegotiatedVersion(message["result"]));
        await PostAsync(McpMessages.Initialized(), null, bearer, session, capture: false, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _sessions[key] = session;
        }

        return session;
    }

    /// <summary>
    /// <c>(message.get("result") or {}).get("protocolVersion", PROTOCOL_VERSION)</c>, rendered as Python's
    /// http.client would render it in a header: a string as is, an int (or bool) through <c>str()</c>; any
    /// other value fails before anything is sent, as it does in Python.
    /// </summary>
    private static string NegotiatedVersion(JsonNode? result)
    {
        if (!PythonJson.IsTruthy(result))
        {
            return ProtocolVersion;
        }

        if (result is not JsonObject fields)
        {
            throw new TransportFaultException("the initialize result is not an object");
        }

        if (!fields.TryGetPropertyValue("protocolVersion", out var version))
        {
            return ProtocolVersion;
        }

        if (PythonJson.TryGetString(version, out var text))
        {
            return text;
        }

        switch (version?.GetValueKind())
        {
            case JsonValueKind.True:
                return "True";
            case JsonValueKind.False:
                return "False";
        }

        if (PythonJson.TryGetInteger(version, out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        throw new TransportFaultException("the negotiated protocol version cannot be sent as a header");
    }

    /// <summary>One POST (<c>transport.py</c> <c>_post</c>). <paramref name="requestId"/> is null for a notification.</summary>
    private async Task<PostResult> PostAsync(byte[] body, long? requestId, string bearer, McpSession? session, bool capture, CancellationToken cancellationToken)
    {
        // urllib finds a URL without a host, and http.client a bad host:port, before the header values are checked.
        RefuseUserInfo(Endpoint);
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri))
        {
            throw new TransportException(Unreachable);
        }

        var authorization = "Bearer " + bearer;
        CheckHeaderValue(authorization);
        if (session is not null)
        {
            CheckHeaderValue(session.Version);
            if (!string.IsNullOrEmpty(session.Id))
            {
                CheckHeaderValue(session.Id);
            }
        }

        var content = new SentTrackingContent(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = content,
        };
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Accept", Accept);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (session is not null)
        {
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", session.Version);
            if (!string.IsNullOrEmpty(session.Id))
            {
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session.Id);
            }
        }

        // What urllib adds by itself: no compression, one request per connection, a client name.
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.ConnectionClose = true;
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var timer = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        Arm(timer);
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exc) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TransportTimeoutException(TimedOut, exc);
        }
        catch (HttpRequestException exc) when (!cancellationToken.IsCancellationRequested)
        {
            if (timer.IsCancellationRequested || IsTimeout(exc))
            {
                throw new TransportTimeoutException(TimedOut, exc);
            }

            // urllib wraps only connect-and-send failures as URLError ("unreachable"). Once the request is
            // out, a reset or a malformed status line escapes getresponse() as is, and the guard records an
            // unknown transport failure: the request may have landed.
            if (!content.Sent)
            {
                throw new TransportException(Unreachable, exc);
            }

            throw;
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is < 200 or > 299)
            {
                if (status == 404 && session is not null && !string.IsNullOrEmpty(session.Id))
                {
                    throw new McpUnknownSessionException();
                }

                throw new TransportException(string.Format(CultureInfo.InvariantCulture, "Work IQ HTTP {0}", status), status);
            }

            var sessionId = capture ? LastValue(response.Headers, "Mcp-Session-Id") : null;
            var raw = await ReadBodyAsync(response, timer, linked.Token, cancellationToken).ConfigureAwait(false);
            if (requestId is not long id)
            {
                return new PostResult(null, sessionId);
            }

            var contentType = FirstValue(response.Content.Headers, "Content-Type");
            return new PostResult(McpMessages.ParseBody(raw, contentType, id), sessionId);
        }
    }

    /// <summary>Read the whole body; each read may wait up to the timeout, as each socket read does in Python.</summary>
    private async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationTokenSource timer, CancellationToken linked, CancellationToken cancellationToken)
    {
        try
        {
            Arm(timer);
            var stream = await response.Content.ReadAsStreamAsync(linked).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                while (true)
                {
                    Arm(timer);
                    var read = await stream.ReadAsync(chunk, linked).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return buffer.ToArray();
                    }

                    buffer.Write(chunk, 0, read);
                }
            }
        }
        catch (Exception exc) when (exc is OperationCanceledException or IOException
                                    && timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TransportTimeoutException(TimedOut, exc);
        }
        catch (Exception exc) when (exc is not OperationCanceledException && IsTimeout(exc) && !cancellationToken.IsCancellationRequested)
        {
            // An OS-level ETIMEDOUT from response.read() is TimeoutError in Python, which _post catches too.
            throw new TransportTimeoutException(TimedOut, exc);
        }

        // Any other failure while reading the body (reset, truncated body) propagates as is: Python lets it
        // escape too, and the guard records an unknown transport failure because the request may have landed.
    }

    private void Arm(CancellationTokenSource timer)
    {
        if (Timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            timer.CancelAfter(Timeout);
        }
    }

    /// <summary>
    /// A socket timeout or an OS-level <c>ETIMEDOUT</c>, at any stage, is a timeout in Python
    /// (<c>TimeoutError</c>, directly or as a <c>URLError</c> reason).
    /// </summary>
    private static bool IsTimeout(Exception exc)
    {
        for (Exception? inner = exc; inner is not null; inner = inner.InnerException)
        {
            if (inner is TimeoutException or SocketException { SocketErrorCode: SocketError.TimedOut })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// urllib hands http.client the whole percent-decoded authority as the host and never strips a user-info
    /// part, so a URL with an '@' there sends nothing in Python: when the text after the last colon is not a
    /// port, http.client raises <c>InvalidURL</c> (not a transport error); otherwise "user@host" is looked up as
    /// a host name, which fails ("Work IQ unreachable"). <see cref="Uri"/> would send the request, bearer token
    /// included, to the host after the '@' (in plain http under the loopback exemption).
    /// </summary>
    private static void RefuseUserInfo(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return;
        }

        var rest = url[(scheme + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = Uri.UnescapeDataString(end < 0 ? rest : rest[..end]);
        if (!authority.Contains('@', StringComparison.Ordinal))
        {
            return;
        }

        var colon = authority.LastIndexOf(':');
        if (colon > authority.LastIndexOf(']'))
        {
            var port = authority[(colon + 1)..];
            if (port.Length > 0 && !IsPythonInt(port))
            {
                throw new TransportFaultException("the URL has a non-numeric port");
            }
        }

        throw new TransportException(Unreachable);
    }

    /// <summary>Whether Python's <c>int(text)</c> accepts the text (whitespace, a sign, digits with single underscores).</summary>
    private static bool IsPythonInt(string text)
    {
        var digits = PythonJson.Strip(text);
        if (digits.Length > 0 && digits[0] is '+' or '-')
        {
            digits = digits[1..];
        }

        var afterDigit = false;
        foreach (var c in digits)
        {
            if (char.IsDigit(c))
            {
                afterDigit = true;
            }
            else if (c == '_' && afterDigit)
            {
                afterDigit = false;
            }
            else
            {
                return false;
            }
        }

        return afterDigit;
    }

    /// <summary>
    /// http.client refuses a header value that is not Latin-1 or that holds a bare line break
    /// (<c>\n</c> not followed by space or tab, <c>\r</c> not followed by space, tab or <c>\n</c>), before
    /// anything is sent; Python raises <c>ValueError</c>, which the guard records as an unknown failure.
    /// </summary>
    private static void CheckHeaderValue(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var next = i + 1 < value.Length ? value[i + 1] : '\0';
            if (c > '\u00FF'
                || (c == '\n' && next is not (' ' or '\t'))
                || (c == '\r' && next is not (' ' or '\t' or '\n')))
            {
                throw new TransportFaultException("invalid header value");
            }
        }
    }

    private static string? FirstValue(HttpHeaders headers, string name) =>
        headers.NonValidated.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string? LastValue(HttpHeaders headers, string name) =>
        headers.NonValidated.TryGetValues(name, out var values) ? values.LastOrDefault() : null;

    private static bool FollowsRedirects(HttpMessageHandler handler)
    {
        var current = handler;
        while (current is DelegatingHandler delegating)
        {
            current = delegating.InnerHandler;
        }

        return current switch
        {
            SocketsHttpHandler sockets => sockets.AllowAutoRedirect,
            HttpClientHandler client => client.AllowAutoRedirect,
            _ => false,
        };
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.Zero,
        // http.client sends header values as Latin-1.
        RequestHeaderEncodingSelector = static (_, _) => Encoding.Latin1,
    };

    private sealed record McpSession(string? Id, string Version);

    /// <summary>
    /// The JSON body, noting when it has been handed to the connection and flushed: Python's
    /// <c>h.request()</c> has returned by then, so later failures are no longer "unreachable".
    /// </summary>
    private sealed class SentTrackingContent(byte[] body) : HttpContent
    {
        private volatile bool _sent;

        public bool Sent => _sent;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _sent = true;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }

    private sealed record PostResult(JsonObject? Message, string? SessionId);
}
