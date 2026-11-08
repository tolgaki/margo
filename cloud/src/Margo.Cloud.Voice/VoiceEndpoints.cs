using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Margo.Cloud.Voice;

/// <summary>
/// <c>mount(app, service)</c>: the voice routes of the instance's web application.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><c>GET /voice/config</c></term><description>The sign-in config JSON (<c>Cache-Control: no-store</c>).</description></item>
/// <item><term><c>GET /voice/ws</c></term><description>The session's WebSocket.</description></item>
/// <item><term><c>GET /voice</c></term><description>301 to <c>voice/</c>.</description></item>
/// <item><term><c>GET /voice/{file:[A-Za-z0-9._-]*}</c></term><description>The page's files (<see cref="Files"/>), with
/// the content security policy, <c>no-store</c> and <c>nosniff</c>; any other name is an empty 404.</description></item>
/// </list>
/// As aiohttp's <c>add_get</c>, each route also answers <c>HEAD</c>; another method is 405, and a path no route
/// matches is 404. The Host must call <c>app.UseWebSockets()</c> before mapping, and keep the Agents SDK's token check
/// off <c>/voice</c> and <c>/voice/...</c>: the page authenticates the person with their own sign-in.
/// </remarks>
public static partial class VoiceEndpoints
{
    /// <summary><c>CSP</c>: the page's content security policy.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' https://login.microsoftonline.com; "
        + "img-src 'self' data:; media-src 'self'; worker-src 'self'; base-uri 'none'; form-action 'none'; "
        + "frame-ancestors 'none'";

    /// <summary><c>FILES</c>: the page's files by request name, with their content types.</summary>
    public static IReadOnlyDictionary<string, (string File, string ContentType)> Files { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [""] = ("index.html", "text/html"),
            ["index.html"] = ("index.html", "text/html"),
            ["voice.css"] = ("voice.css", "text/css"),
            ["voice.js"] = ("voice.js", "text/javascript"),
            ["voice-core.mjs"] = ("voice-core.mjs", "text/javascript"),
            ["pcm-worklets.js"] = ("pcm-worklets.js", "text/javascript"),
        };

    private static readonly ConcurrentDictionary<string, byte[]> Contents = new(StringComparer.Ordinal);

    /// <summary>Adds the voice routes for <paramref name="service"/> under <c>/voice</c>.</summary>
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints, VoiceService service)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(service);
        return endpoints.Map("/voice/{**rest}", context => HandleAsync(context, service));
    }

    /// <summary>The contents of one of the page's files (embedded from <c>autopilot/src/margo_autopilot/voice/static/</c>).</summary>
    public static byte[] FileContents(string file) => Contents.GetOrAdd(file, static name =>
    {
        using var stream = typeof(VoiceEndpoints).Assembly.GetManifestResourceStream("voice/" + name)
            ?? throw new FileNotFoundException("the voice page file is not embedded", name);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    private static Task HandleAsync(HttpContext context, VoiceService service)
    {
        var path = context.Request.Path.Value ?? "";
        string? route;
        if (path == "/voice")
        {
            route = "redirect";
        }
        else if (path.StartsWith("/voice/", StringComparison.Ordinal))
        {
            var rest = path["/voice/".Length..];
            route = rest is "config" or "ws" ? rest : FileName().IsMatch(rest) ? "file" : null;
        }
        else
        {
            route = null;
        }

        if (route is null)
        {
            return PlainAsync(context, StatusCodes.Status404NotFound, "404: Not Found");
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Headers.Allow = "GET,HEAD";
            return PlainAsync(context, StatusCodes.Status405MethodNotAllowed, "405: Method Not Allowed");
        }

        return route switch
        {
            "redirect" => RedirectAsync(context),
            "config" => ConfigAsync(context, service),
            "ws" => SocketAsync(context, service),
            _ => PageAsync(context, path["/voice/".Length..]),
        };
    }

    private static Task RedirectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
        context.Response.Headers.Location = "voice/";
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    /// <summary><c>config</c>: <c>json_response</c> (<c>json.dumps</c>, <c>application/json; charset=utf-8</c>).</summary>
    private static Task ConfigAsync(HttpContext context, VoiceService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        return WriteAsync(context, StatusCodes.Status200OK, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(PyJson.Dumps(service.Config())));
    }

    /// <summary><c>page</c>: one of the page's files, or an empty 404.</summary>
    private static Task PageAsync(HttpContext context, string name)
    {
        if (!Files.TryGetValue(name, out var file))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentLength = 0;
            return Task.CompletedTask;
        }

        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        return WriteAsync(context, StatusCodes.Status200OK, file.ContentType, FileContents(file.File));
    }

    /// <summary><c>socket</c>: <c>WebSocketResponse(max_msg_size=MAX_FRAME, heartbeat=20)</c>, then the session.</summary>
    private static async Task SocketAsync(HttpContext context, VoiceService service)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            // aiohttp's handshake refusal for a request that does not ask to upgrade.
            var upgrade = context.Request.Headers.Upgrade.Count > 0 ? context.Request.Headers.Upgrade[0] : null;
            await PlainAsync(context, StatusCodes.Status400BadRequest,
                             "No WebSocket UPGRADE hdr: " + (upgrade ?? "None") + "\n Can \"Upgrade\" only to \"WebSocket\".").ConfigureAwait(false);
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = VoiceService.Heartbeat,
            KeepAliveTimeout = VoiceService.Heartbeat / 2,
        }).ConfigureAwait(false);
        var ws = new MessageSocket(socket, VoiceService.MaxFrame);
        await using (ws.ConfigureAwait(false))
        {
            await service.RunAsync(ws, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static Task PlainAsync(HttpContext context, int status, string text) =>
        WriteAsync(context, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private static Task WriteAsync(HttpContext context, int status, string contentType, byte[] body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = body.Length;
        return HttpMethods.IsHead(context.Request.Method) ? Task.CompletedTask : context.Response.Body.WriteAsync(body).AsTask();
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();
}
