using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Router.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Router;

/// <summary>
/// The router service: one public endpoint in front of every hired Margo's own container. Python:
/// <c>router/app.py</c> (<c>Router</c> and <c>build_app</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>POST /api/messages</c> (and <c>/activity/messages</c>): the blueprint's Agent 365
/// messaging endpoint. The platform token is validated here (<see cref="AgentTokenValidator"/>), then
/// the activity is forwarded unchanged, with its token, to the instance, which validates it again.
/// Lifecycle notifications become provisioning messages.</item>
/// <item><c>/i/{key}/voice/...</c>: the voice page and its WebSocket, forwarded to the instance. The
/// instance authenticates the person; the router only forwards bytes.</item>
/// <item><c>/</c>, <c>/liveness</c>, <c>/readiness</c>: health.</item>
/// </list>
/// The router never logs bodies, message text, tokens or query strings. Routing follows aiohttp's:
/// paths match exactly (no dot-segment or trailing-slash normalisation), a known path with another
/// method is 405 with <c>Allow</c>, anything else 404, and an unexpected failure 500.
/// </remarks>
public sealed class RouterService
{
    internal const string NotFoundText = "404: Not Found";
    internal const string MethodNotAllowedText = "405: Method Not Allowed";
    internal const string ServerErrorText = "500 Internal Server Error\n\nServer got itself in trouble";

    /// <summary>aiohttp's 500 body for a request whose <c>Accept</c> mentions <c>text/html</c>.</summary>
    internal const string ServerErrorHtml = "<html><head><title>500 Internal Server Error</title></head><body>\n"
        + "<h1>500 Internal Server Error</h1>\nServer got itself in trouble\n</body></html>\n";

    internal const string HealthJson = "{\"status\": \"ok\"}";

    private static readonly HashSet<string> HealthPaths = new(StringComparer.Ordinal) { "/", "/liveness", "/readiness" };
    private static readonly HashSet<string> ActivityPaths = new(StringComparer.Ordinal) { "/api/messages", "/activity/messages" };

    /// <summary>aiohttp's HTTP parser refused a request that repeated one of these (its spelling of
    /// each name, in its message).</summary>
    private static readonly string[] SingletonHeaders =
        ["Content-Length", "Content-Location", "Content-Range", "Content-Type", "Etag", "Host", "Max-Forwards", "Server", "Transfer-Encoding", "User-Agent"];

    private readonly RouteTableCache _routes;
    private readonly IProvisioningSink _queue;
    private readonly AgentTokenValidator _validator;
    private readonly InstanceConnector _instances;
    private readonly VoiceBridge _voice;
    private readonly ILogger _logger;

    public RouterService(RouteTableCache routes, IProvisioningSink queue, AgentTokenValidator validator, InstanceConnector instances, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(logger);
        _routes = routes;
        _queue = queue;
        _validator = validator;
        _instances = instances;
        _logger = logger;
        _voice = new VoiceBridge(instances, logger);
    }

    /// <summary>Handle one request: the whole application.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await DispatchAsync(context);
        }
        catch (InstanceTimeoutException) when (!context.Response.HasStarted)
        {
            // aiohttp answered a handler that raised asyncio.TimeoutError (its client timeouts are
            // ones) with an empty 504, logging no exception.
            _logger.LogError("Error handling request");
            context.Response.Clear();
            context.Response.StatusCode = 504;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength = 0;
        }
        catch (Exception exc) when (!context.Response.HasStarted)
        {
            // aiohttp: "Error handling request", then a 500: HTML when the caller accepts text/html.
            _logger.LogError(exc, "Error handling request");
            context.Response.Clear();
            if (FirstHeader(context, "Accept") is { } accept && accept.Contains("text/html", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "text/html; charset=utf-8";
                await WriteBodyAsync(context, Encoding.UTF8.GetBytes(ServerErrorHtml));
            }
            else
            {
                await WriteTextAsync(context, 500, ServerErrorText);
            }
        }
    }

    private async Task DispatchAsync(HttpContext context)
    {
        // aiohttp's HTTP parser refused these before any routing.
        if (ParserError(context.Request.Headers) is { } parserError)
        {
            await WriteTextAsync(context, 400, parserError);
            return;
        }

        var path = RequestPath.FromRawTarget(RawTarget(context));
        var method = context.Request.Method;

        // aiohttp's default Expect handler ran for every route, before the middleware.
        if (UnknownExpectation(context) is { } expectation)
        {
            await WriteTextAsync(context, 417, "Unknown Expect: " + expectation);
            return;
        }

        // The SDK middleware ran for these paths whatever the method, before routing.
        if (ActivityPaths.Contains(path.Decoded))
        {
            var auth = await _validator.AuthorizeAsync(FirstHeader(context, "Authorization"), context.RequestAborted);
            if (!auth.Succeeded)
            {
                await WriteJsonAsync(context, auth.StatusCode, auth.ErrorJson!);
                return;
            }
        }

        if (ActivityPaths.Contains(path.Safe))
        {
            if (!HttpMethods.IsPost(method))
            {
                await MethodNotAllowedAsync(context, "POST");
                return;
            }

            await MessagesAsync(context);
        }
        else if (HealthPaths.Contains(path.Safe))
        {
            if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            {
                await MethodNotAllowedAsync(context, "GET,HEAD");
                return;
            }

            await WriteJsonAsync(context, 200, HealthJson);
        }
        else if (IsVoiceRoute(path.Safe))
        {
            if (!HttpMethods.IsGet(method))
            {
                await MethodNotAllowedAsync(context, "GET");
                return;
            }

            await VoiceAsync(context, path.Decoded);
        }
        else
        {
            await WriteTextAsync(context, 404, NotFoundText);
        }
    }

    /// <summary>Python: <c>Router.messages</c>.</summary>
    private async Task MessagesAsync(HttpContext context)
    {
        byte[] body;
        try
        {
            body = await ReadActivityAsync(context, context.RequestAborted);
        }
        catch (BadHttpRequestException exc) when (exc.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel's own limit (a declared length over 30 MB) is beyond this router's limit too.
            await WriteEmptyAsync(context, 413);
            return;
        }

        if (body.Length > RouterCore.MaxActivityBytes)
        {
            await WriteEmptyAsync(context, 413);
            return;
        }

        JsonNode? activity;
        try
        {
            activity = PythonJsonBytes.Loads(body);
        }
        catch (JsonException)
        {
            await WriteEmptyAsync(context, 400);
            return;
        }

        var decision = RouterCore.Decide(activity, await _routes.GetAsync(context.RequestAborted));
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("activity kind={Kind} key={Key}", decision.Kind, decision.Key ?? "None");
        }
        if (decision.Kind is RouterDecisionKinds.Lifecycle or RouterDecisionKinds.Provision)
        {
            await _queue.SendAsync(decision.Message!.ToJson(), context.RequestAborted);
        }

        if (decision.Kind != RouterDecisionKinds.Forward)
        {
            await WriteEmptyAsync(context, decision.Status, decision.RetryAfter);
            return;
        }

        using var slot = await _instances.AcquireAsync(context.RequestAborted);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(decision.Url + "/activity/messages"))
        {
            Content = new ByteArrayContent(body),
        };
        if (FirstHeader(context, "Authorization") is { } authorization)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        // aiohttp sent bytes as application/octet-stream unless the caller's Content-Type was passed on.
        request.Content.Headers.TryAddWithoutValidation("Content-Type", FirstHeader(context, "Content-Type") ?? "application/octet-stream");
        using var upstream = await _instances.SendAsync(request, context.RequestAborted);
        var payload = await upstream.Content.ReadAsByteArrayAsync(context.RequestAborted);
        context.Response.StatusCode = (int)upstream.StatusCode;
        if (payload.Length > 0)
        {
            context.Response.ContentType = MediaType(FirstValue(upstream.Content.Headers.NonValidated, "Content-Type"));
        }

        await WriteBodyAsync(context, payload);
    }

    /// <summary>Python: <c>Router.voice</c>.</summary>
    private async Task VoiceAsync(HttpContext context, string decodedPath)
    {
        var target = RouterCore.VoiceTargetFor(decodedPath, await _routes.GetAsync(context.RequestAborted));
        if (target.Status != 200)
        {
            await WriteEmptyAsync(context, target.Status, target.RetryAfter);
            return;
        }

        // No query string is forwarded: the page's sign-in redirect carries a one-time code that only
        // the browser needs.
        if (FirstHeader(context, "Upgrade") is { } upgrade && PythonCompat.LowerEquals(upgrade, "websocket"))
        {
            if (HandshakeError(context.Request.Headers) is { } handshakeError)
            {
                await WriteTextAsync(context, 400, handshakeError);
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                // aiohttp accepted this handshake (protocol version 7 or 8, or a key with stray
                // characters); ASP.NET Core cannot (PORTING.md).
                await WriteTextAsync(context, 400, "400: Bad Request");
                return;
            }

            await _voice.RunAsync(context, target.Url!);
            return;
        }

        using var slot = await _instances.AcquireAsync(context.RequestAborted);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(target.Url!));
        using var upstream = await _instances.SendAsync(request, context.RequestAborted);
        var payload = await upstream.Content.ReadAsByteArrayAsync(context.RequestAborted);
        context.Response.StatusCode = (int)upstream.StatusCode;
        if (FirstValue(upstream.Content.Headers.NonValidated, "Content-Type") is { } contentType)
        {
            context.Response.Headers.ContentType = contentType;
        }
        else if (payload.Length > 0)
        {
            context.Response.Headers.ContentType = "application/octet-stream";
        }

        foreach (var name in (string[])["Cache-Control", "Content-Security-Policy"])
        {
            if (FirstValue(upstream.Headers.NonValidated, name) is { } value)
            {
                context.Response.Headers[name] = value;
            }
        }

        await WriteBodyAsync(context, payload);
    }

    /// <summary>aiohttp's route <c>/i/{key:[0-9a-f]{20}}/voice{rest:.*}</c>: a full match in which
    /// <c>.</c> does not match a newline.</summary>
    private static bool IsVoiceRoute(string safePath)
    {
        if (safePath.Length < 29 || !safePath.StartsWith("/i/", StringComparison.Ordinal) || !Instances.IsInstanceKey(safePath[3..23])
            || !safePath.AsSpan(23).StartsWith("/voice", StringComparison.Ordinal))
        {
            return false;
        }

        return !safePath.AsSpan(29).Contains('\n');
    }

    /// <summary>aiohttp's <c>content_type</c>: the media type, lower-cased, without parameters;
    /// <c>application/octet-stream</c> when absent or malformed.</summary>
    internal static string MediaType(string? raw)
    {
        if (raw is null)
        {
            return "application/octet-stream";
        }

        var value = raw.ToLowerInvariant();
        var semicolon = value.IndexOf(';', StringComparison.Ordinal);
        var type = (semicolon < 0 ? value : value[..semicolon]).Trim();
        return type.Count(c => c == '/') == 1 ? type : "application/octet-stream";
    }

    private static string RawTarget(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        return context.Request.PathBase.ToUriComponent() + context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
    }

    private static string? FirstHeader(HttpContext context, string name) =>
        context.Request.Headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    private static string? FirstValue(System.Net.Http.Headers.HttpHeadersNonValidated headers, string name)
    {
        if (headers.TryGetValues(name, out var values))
        {
            foreach (var value in values)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>aiohttp's HTTP parser errors that this router reproduces: a repeated singleton header,
    /// then a <c>Content-Encoding</c> it had no decoder for (it had no Brotli or zstd module).</summary>
    internal static string? ParserError(IHeaderDictionary headers)
    {
        foreach (var name in SingletonHeaders)
        {
            if (headers.TryGetValue(name, out var values) && values.Count > 1)
            {
                return "Duplicate '" + name + "' header found.";
            }
        }

        return RequestContentEncoding(headers) switch
        {
            "br" => "Can not decode content-encoding: brotli (br). Please install `Brotli`",
            "zstd" => "Can not decode content-encoding: zstandard (zstd). Please install `backports.zstd`",
            _ => null,
        };
    }

    /// <summary>The body encoding aiohttp decoded: the last <c>Content-Encoding</c> header, when it is
    /// ASCII and, lower-cased, exactly <c>gzip</c>, <c>deflate</c>, <c>br</c> or <c>zstd</c>.</summary>
    internal static string? RequestContentEncoding(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue("Content-Encoding", out var values) || values.Count == 0 || values[^1] is not { } value || !Ascii.IsValid(value))
        {
            return null;
        }

        var lowered = value.ToLowerInvariant();
        return lowered is "gzip" or "deflate" or "br" or "zstd" ? lowered : null;
    }

    /// <summary>aiohttp's <c>_default_expect_handler</c>: on HTTP/1.1, a first <c>Expect</c> value other
    /// than <c>100-continue</c> (any case) is refused with 417.</summary>
    private static string? UnknownExpectation(HttpContext context)
    {
        if (!HttpProtocol.IsHttp11(context.Request.Protocol) || FirstHeader(context, "Expect") is not { Length: > 0 } expect)
        {
            return null;
        }

        return PythonCompat.LowerEquals(expect, "100-continue") ? null : expect;
    }

    /// <summary>aiohttp's <c>WebSocketResponse._handshake</c> checks, in its order, for a request whose
    /// <c>Upgrade</c> is <c>websocket</c>: the message of the 400 it raised, or <see langword="null"/>.</summary>
    /// <exception cref="InvalidOperationException">The key is not ASCII (Python: <c>ValueError</c> from
    /// <c>base64.b64decode</c>, which aiohttp did not catch; the request failed with 500).</exception>
    internal static string? HandshakeError(IHeaderDictionary headers)
    {
        // llhttp's upgrade flag: a Connection header with the token "upgrade".
        var upgradable = headers.TryGetValue("Connection", out var connection) && connection.Any(value =>
            value is not null && value.Split(',').Any(token => token.Trim(' ', '\t').Equals("upgrade", StringComparison.OrdinalIgnoreCase)));
        if (!upgradable)
        {
            return "No CONNECTION upgrade hdr: " + (First(headers, "Connection") ?? "None");
        }

        var version = First(headers, "Sec-WebSocket-Version") ?? string.Empty;
        if (version is not ("13" or "8" or "7"))
        {
            return "Unsupported version: " + version;
        }

        var key = First(headers, "Sec-WebSocket-Key");
        if (string.IsNullOrEmpty(key))
        {
            return "Handshake error: " + (key is null ? "None" : "''");
        }

        if (!Ascii.IsValid(key))
        {
            throw new InvalidOperationException("the WebSocket key is not ASCII");
        }

        return PythonBase64.TryA2b(Encoding.ASCII.GetBytes(key), out var decoded) && decoded.Length == 16
            ? null
            : "Handshake error: " + PythonRepr(key);
    }

    /// <summary>Python's <c>repr()</c> of an ASCII string.</summary>
    internal static string PythonRepr(string text)
    {
        var quote = text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var builder = new StringBuilder(text.Length + 2).Append(quote);
        foreach (var c in text)
        {
            _ = c switch
            {
                '\\' => builder.Append("\\\\"),
                '\t' => builder.Append("\\t"),
                '\n' => builder.Append("\\n"),
                '\r' => builder.Append("\\r"),
                _ when c == quote => builder.Append('\\').Append(c),
                < ' ' or (char)0x7f => builder.Append("\\x").Append(((int)c).ToString("x2", System.Globalization.CultureInfo.InvariantCulture)),
                _ => builder.Append(c),
            };
        }

        return builder.Append(quote).ToString();
    }

    private static string? First(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    /// <summary>The activity body, decoded as aiohttp decoded it: a <c>gzip</c> or <c>deflate</c>
    /// <c>Content-Encoding</c> is decompressed (deflate with or without its zlib header, chosen by the
    /// first byte), and at most the limit plus one byte is read. Data that does not decompress throws
    /// (Python: <c>RequestPayloadError</c> from <c>read</c>, a 500).</summary>
    private static async Task<byte[]> ReadActivityAsync(HttpContext context, CancellationToken cancellationToken)
    {
        const int limit = RouterCore.MaxActivityBytes + 1;
        var reader = context.Request.BodyReader;
        var encoding = RequestContentEncoding(context.Request.Headers);
        await using var raw = reader.AsStream(leaveOpen: true);
        if (encoding == "gzip")
        {
            await using var gzip = new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true);
            return await ReadBodyAsync(gzip, limit, cancellationToken);
        }

        if (encoding != "deflate")
        {
            return await ReadBodyAsync(raw, limit, cancellationToken);
        }

        // aiohttp: "bits 0..3 = CM = 0b1000 = 8 = deflate"; any other first byte is raw deflate data.
        var peek = await reader.ReadAsync(cancellationToken);
        var buffer = peek.Buffer;
        var first = new byte[1];
        var empty = buffer.IsEmpty;
        if (!empty)
        {
            buffer.Slice(0, 1).CopyTo(first);
        }

        reader.AdvanceTo(buffer.Start, buffer.Start);
        if (empty)
        {
            return [];
        }

        await using Stream deflate = (first[0] & 0x0F) == 8
            ? new ZLibStream(raw, CompressionMode.Decompress, leaveOpen: true)
            : new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true);
        return await ReadBodyAsync(deflate, limit, cancellationToken);
    }

    /// <summary>Read at most <paramref name="limit"/> bytes of the body.</summary>
    private static async Task<byte[]> ReadBodyAsync(Stream body, int limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(limit, 64 * 1024)];
        using var collected = new MemoryStream();
        while (collected.Length < limit)
        {
            var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - collected.Length)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            collected.Write(buffer, 0, read);
        }

        return collected.ToArray();
    }

    private static Task MethodNotAllowedAsync(HttpContext context, string allow)
    {
        context.Response.Headers.Allow = allow;
        return WriteTextAsync(context, 405, MethodNotAllowedText);
    }

    private static Task WriteEmptyAsync(HttpContext context, int status, int? retryAfter = null)
    {
        context.Response.StatusCode = status;
        if (retryAfter is { } seconds)
        {
            context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    private static Task WriteJsonAsync(HttpContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return WriteBodyAsync(context, Encoding.UTF8.GetBytes(json));
    }

    private static Task WriteTextAsync(HttpContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return WriteBodyAsync(context, Encoding.UTF8.GetBytes(text));
    }

    private static async Task WriteBodyAsync(HttpContext context, byte[] body)
    {
        if (context.Response.StatusCode is < 200 or 204 or 304)
        {
            return; // no body and no Content-Length, as aiohttp sent these
        }

        context.Response.ContentLength = body.Length;
        if (body.Length > 0 && !HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }
    }
}
