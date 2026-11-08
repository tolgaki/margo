using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Voice;

/// <summary>Opens Voice Live sessions. The service uses <see cref="VoiceLiveConnector"/>; tests use a fake.</summary>
public interface IVoiceLiveConnector
{
    /// <summary><c>azure.ai.voicelive.aio.connect(credential=..., endpoint=endpoint, model=model)</c>.</summary>
    /// <exception cref="Exception">Any failure: the voice server ends the call with its generic message.</exception>
    Task<IVoiceLiveConnection> ConnectAsync(string endpoint, string model, CancellationToken cancellationToken);
}

/// <summary>One open Voice Live session (<c>VoiceLiveConnection</c>).</summary>
public interface IVoiceLiveConnection : IAsyncDisposable
{
    /// <summary><c>connection.send(event)</c>: one client event.</summary>
    Task SendAsync(JsonObject clientEvent, CancellationToken cancellationToken);

    /// <summary>The next server event (<c>event.as_dict()</c>: the event exactly as received), or
    /// <see langword="null"/> when the session has ended (<c>async for event in connection</c> stops).</summary>
    /// <exception cref="Exception">A message that is not a JSON object: in Python the iteration raises and the call
    /// ends.</exception>
    Task<JsonObject?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The Voice Live WebSocket protocol as <c>azure-ai-voicelive</c> 1.3.0 speaks it (<c>aio/_patch.py</c>,
/// <c>_VoiceLiveConnectionManager</c> and <c>VoiceLiveConnection</c>), over <see cref="ClientWebSocket"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>URL: the endpoint with <c>https</c> as <c>wss</c> (any other <c>http...</c> scheme as <c>ws</c>), its path
/// without trailing slashes plus <c>/voice-live/realtime</c>, and the query <c>api-version=2026-07-15&amp;model=MODEL</c>
/// followed by the endpoint's own non-blank query parameters not already set (<see cref="RealtimeUrl"/>).</item>
/// <item>Authentication: <c>Authorization: Bearer TOKEN</c> for <c>https://ai.azure.com/.default</c>, from a
/// <see cref="ManagedIdentityCredential"/> for <c>AZURE_CLIENT_ID</c> when it is set, else
/// <see cref="DefaultAzureCredential"/>, made per session as the Python server did.</item>
/// <item>Events: each client event is one text message, <c>json.dumps(event)</c> (<see cref="PyJson.Dumps(JsonNode?, int?, string?, string?, bool, bool, bool)"/>);
/// each server message, text or binary, is UTF-8 JSON. An empty message, a close or a socket failure ends the
/// session; a message that is not a JSON object raises.</item>
/// <item>Limits: 4 MiB messages and a ping every 30 seconds (the SDK's <c>max_msg_size</c> and <c>heartbeat</c>
/// defaults); a close waits up to 10 seconds for the service's reply.</item>
/// </list>
/// </remarks>
public sealed class VoiceLiveConnector : IVoiceLiveConnector
{
    /// <summary>The SDK's default <c>api_version</c>.</summary>
    public const string ApiVersion = "2026-07-15";

    /// <summary>The SDK's default <c>credential_scopes</c>.</summary>
    public const string Scope = "https://ai.azure.com/.default";

    /// <summary>The SDK's default <c>max_msg_size</c>.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>The SDK's default <c>heartbeat</c>.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);

    private readonly Func<TokenCredential> _credential;

    /// <param name="credential">Makes the credential for one session; null for the Python server's choice
    /// (<see cref="DefaultCredential"/>).</param>
    public VoiceLiveConnector(Func<TokenCredential>? credential = null)
    {
        _credential = credential ?? DefaultCredential;
    }

    /// <summary><c>ManagedIdentityCredential(client_id=AZURE_CLIENT_ID)</c> when it is set (and not empty), else
    /// <c>DefaultAzureCredential()</c>.</summary>
    public static TokenCredential DefaultCredential()
    {
        var clientId = PyRuntime.GetEnv("AZURE_CLIENT_ID");
        return string.IsNullOrEmpty(clientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }

    public async Task<IVoiceLiveConnection> ConnectAsync(string endpoint, string model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        var url = RealtimeUrl(endpoint, model);
        var token = await _credential().GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken).ConfigureAwait(false);
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token.Token);
            socket.Options.KeepAliveInterval = Heartbeat;
            socket.Options.KeepAliveTimeout = Heartbeat / 2;
            await socket.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new Connection(new MessageSocket(socket, MaxMessageBytes));
    }

    /// <summary><c>_VoiceLiveConnectionManager._prepare_url()</c> with no agent configuration and no extra query
    /// (Python's <c>urlparse</c>, <c>parse_qs</c>, <c>urlencode</c> and <c>urlunparse</c>).</summary>
    public static string RealtimeUrl(string endpoint, string? model)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var (scheme, netloc, path, parameters, query, fragment) = UrlParse(endpoint);
        var wsScheme = scheme.StartsWith("http", StringComparison.Ordinal) ? (scheme == "https" ? "wss" : "ws") : scheme;
        var values = new List<(string Key, string Value)> { ("api-version", ApiVersion) };
        if (model is not null)
        {
            values.Add(("model", model));
        }

        foreach (var (key, value) in ParseQueryFirst(query))
        {
            if (!values.Any(existing => existing.Key == key))
            {
                values.Add((key, value));
            }
        }

        var encoded = string.Join("&", values.Select(pair => QuotePlus(pair.Key) + "=" + QuotePlus(pair.Value)));
        return UrlUnparse(wsScheme, netloc, path.TrimEnd('/') + "/voice-live/realtime", parameters, encoded, fragment);
    }

    private static readonly string[] UsesParams =
        ["", "ftp", "hdl", "prospero", "http", "imap", "https", "shttp", "rtsp", "rtsps", "rtspu", "sip", "sips", "mms", "sftp", "tel"];

    private static readonly string[] UsesNetloc =
        ["", "ftp", "http", "gopher", "nntp", "telnet", "imap", "wais", "file", "mms", "https", "shttp", "snews", "prospero", "rtsp",
         "rtsps", "rtspu", "rsync", "svn", "svn+ssh", "sftp", "nfs", "git", "git+ssh", "ws", "wss", "itms-services"];

    /// <summary>Python <c>urllib.parse.urlparse</c> (3.13).</summary>
    private static (string Scheme, string Netloc, string Path, string Params, string Query, string Fragment) UrlParse(string url)
    {
        url = url.TrimStart(UrlStrip).Replace("\t", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        var scheme = "";
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && char.IsAsciiLetter(url[0]) && url[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            scheme = url[..colon].ToLowerInvariant();
            url = url[(colon + 1)..];
        }

        var netloc = "";
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            var end = url.IndexOfAny(['/', '?', '#'], 2);
            end = end < 0 ? url.Length : end;
            netloc = url[2..end];
            url = url[end..];
        }

        var fragment = "";
        var hash = url.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = url[(hash + 1)..];
            url = url[..hash];
        }

        var query = "";
        var question = url.IndexOf('?', StringComparison.Ordinal);
        if (question >= 0)
        {
            query = url[(question + 1)..];
            url = url[..question];
        }

        var parameters = "";
        if (UsesParams.Contains(scheme) && url.Contains(';', StringComparison.Ordinal))
        {
            var semicolon = url.Contains('/', StringComparison.Ordinal)
                ? url.IndexOf(';', url.LastIndexOf('/'))
                : url.IndexOf(';', StringComparison.Ordinal);
            if (semicolon >= 0)
            {
                parameters = url[(semicolon + 1)..];
                url = url[..semicolon];
            }
        }

        return (scheme, netloc, url, parameters, query, fragment);
    }

    /// <summary>The C0 controls and space that <c>urlsplit</c> strips from the start.</summary>
    private static readonly char[] UrlStrip = [.. Enumerable.Range(0, 33).Select(code => (char)code)];

    /// <summary>Python <c>urlunparse</c>.</summary>
    private static string UrlUnparse(string scheme, string netloc, string path, string parameters, string query, string fragment)
    {
        var url = parameters.Length > 0 ? path + ";" + parameters : path;
        if (netloc.Length > 0 || (scheme.Length > 0 && UsesNetloc.Contains(scheme) && !url.StartsWith("//", StringComparison.Ordinal)))
        {
            if (url.Length > 0 && url[0] != '/')
            {
                url = "/" + url;
            }

            url = "//" + netloc + url;
        }

        if (scheme.Length > 0)
        {
            url = scheme + ":" + url;
        }

        if (query.Length > 0)
        {
            url += "?" + query;
        }

        if (fragment.Length > 0)
        {
            url += "#" + fragment;
        }

        return url;
    }

    /// <summary><c>parse_qs(query)</c> (blank values dropped, <c>&amp;</c> separated, <c>+</c> as space, percent-decoded
    /// as UTF-8 with replacement), keeping each name's first value in first-seen order.</summary>
    private static List<(string Key, string Value)> ParseQueryFirst(string query)
    {
        var result = new List<(string, string)>();
        foreach (var piece in query.Split('&'))
        {
            if (piece.Length == 0)
            {
                continue;
            }

            var equals = piece.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                continue;
            }

            var value = piece[(equals + 1)..];
            if (value.Length == 0)
            {
                continue;
            }

            var key = Unquote(piece[..equals].Replace('+', ' '));
            if (!result.Any(pair => pair.Item1 == key))
            {
                result.Add((key, Unquote(value.Replace('+', ' '))));
            }
        }

        return result;
    }

    /// <summary>Python <c>unquote</c> (UTF-8, errors replaced).</summary>
    private static string Unquote(string text)
    {
        if (!text.Contains('%', StringComparison.Ordinal))
        {
            return text;
        }

        var bytes = new List<byte>();
        var builder = new StringBuilder();
        void Flush()
        {
            if (bytes.Count > 0)
            {
                builder.Append(Encoding.UTF8.GetString([.. bytes]));
                bytes.Clear();
            }
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '%' && index + 2 < text.Length && Uri.IsHexDigit(text[index + 1]) && Uri.IsHexDigit(text[index + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(index + 1, 2), 16));
                index += 2;
                continue;
            }

            Flush();
            builder.Append(text[index]);
        }

        Flush();
        return builder.ToString();
    }

    /// <summary>Python <c>quote_plus(text)</c>: UTF-8, letters, digits and <c>_.-~</c> kept, space as <c>+</c>.</summary>
    private static string QuotePlus(string text)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~')
            {
                builder.Append((char)b);
            }
            else if (b == (byte)' ')
            {
                builder.Append('+');
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private sealed class Connection(MessageSocket socket) : IVoiceLiveConnection
    {
        public Task SendAsync(JsonObject clientEvent, CancellationToken cancellationToken) =>
            socket.SendTextAsync(PyJson.Dumps(clientEvent), cancellationToken);

        public async Task<JsonObject?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var message = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            string text;
            switch (message.Kind)
            {
                case SocketMessageKind.Text:
                    text = message.Text!;
                    break;
                case SocketMessageKind.Binary:
                    text = Jwt.Utf8Strict.Encoding.GetString(message.Data!); // UnicodeDecodeError: the iteration raises
                    break;
                default:
                    return null;
            }

            if (text.Length == 0)
            {
                return null; // ConnectionClosed(1006, "Empty WebSocket frame"): the iteration stops
            }

            return PyJson.Loads(text) as JsonObject
                ?? throw new InvalidOperationException("a Voice Live message was not a JSON object");
        }

        public ValueTask DisposeAsync() => socket.DisposeAsync();
    }
}
