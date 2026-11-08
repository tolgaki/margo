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
public sealed partial class VoiceLiveConnector : IVoiceLiveConnector
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
            if (netloc.Contains('[', StringComparison.Ordinal) != netloc.Contains(']', StringComparison.Ordinal))
            {
                throw new PyValueError("Invalid IPv6 URL");
            }

            if (netloc.Contains('[', StringComparison.Ordinal))
            {
                CheckBracketedNetloc(netloc);
            }
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

    /// <summary>Python 3.13 <c>_check_bracketed_netloc</c>: nothing before the bracket, nothing after it but a port, and
    /// the bracketed host an IPvFuture literal or an IPv6 address (<c>ipaddress.ip_address</c>; an IPv4 address is
    /// refused). Python raises <c>ValueError</c>, which the SDK's <c>connect</c> turns into a connection error.</summary>
    private static void CheckBracketedNetloc(string netloc)
    {
        var at = netloc.LastIndexOf('@');
        var hostAndPort = at < 0 ? netloc : netloc[(at + 1)..];
        var open = hostAndPort.IndexOf('[', StringComparison.Ordinal);
        string hostname;
        if (open >= 0)
        {
            if (open > 0)
            {
                throw new PyValueError("Invalid IPv6 URL");
            }

            var bracketed = hostAndPort[(open + 1)..];
            var close = bracketed.IndexOf(']', StringComparison.Ordinal);
            hostname = close < 0 ? bracketed : bracketed[..close];
            var port = close < 0 ? "" : bracketed[(close + 1)..];
            if (port.Length > 0 && port[0] != ':')
            {
                throw new PyValueError("Invalid IPv6 URL");
            }
        }
        else
        {
            var colon = hostAndPort.IndexOf(':', StringComparison.Ordinal);
            hostname = colon < 0 ? hostAndPort : hostAndPort[..colon];
        }

        if (hostname.StartsWith('v') || hostname.StartsWith('V'))
        {
            if (!IpvFuture().IsMatch(hostname))
            {
                throw new PyValueError("IPvFuture address is invalid");
            }

            return;
        }

        if (IsIPv4Address(hostname))
        {
            throw new PyValueError("An IPv4 address cannot be in brackets");
        }

        if (!IsIPv6Address(hostname))
        {
            throw new PyValueError("'" + hostname + "' does not appear to be an IPv4 or IPv6 address");
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\A[vV][a-fA-F0-9]+\..+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex IpvFuture();

    /// <summary>Python <c>ipaddress.IPv4Address(text)</c> succeeds: four dot-separated ASCII decimal octets of at most
    /// three digits, no leading zeros, at most 255, and no <c>/</c>.</summary>
    private static bool IsIPv4Address(string text)
    {
        if (text.Length == 0 || text.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        var octets = text.Split('.');
        return octets.Length == 4 && octets.All(octet =>
            octet.Length is > 0 and <= 3 && octet.All(char.IsAsciiDigit) && (octet == "0" || octet[0] != '0')
            && int.Parse(octet, System.Globalization.CultureInfo.InvariantCulture) <= 255);
    }

    /// <summary>Python <c>ipaddress.IPv6Address(text)</c> succeeds (3.13 <c>_split_scope_id</c> and
    /// <c>_ip_int_from_string</c>).</summary>
    private static bool IsIPv6Address(string text)
    {
        if (text.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        var percent = text.IndexOf('%', StringComparison.Ordinal);
        if (percent >= 0)
        {
            var scope = text[(percent + 1)..];
            if (scope.Length == 0 || scope.Contains('%', StringComparison.Ordinal))
            {
                return false;
            }

            text = text[..percent];
        }

        if (text.Length is 0 or > 45)
        {
            return false;
        }

        const int hextets = 8;
        var parts = SplitMax(text, ':', hextets + 1);
        if (parts.Count < 3)
        {
            return false;
        }

        if (parts[^1].Contains('.', StringComparison.Ordinal))
        {
            var last = parts[^1];
            parts.RemoveAt(parts.Count - 1);
            if (!IsIPv4Address(last))
            {
                return false;
            }

            parts.Add("0");
            parts.Add("0");
        }

        if (parts.Count > hextets + 1)
        {
            return false;
        }

        int? skip = null;
        for (var index = 1; index < parts.Count - 1; index++)
        {
            if (parts[index].Length == 0)
            {
                if (skip is not null)
                {
                    return false;
                }

                skip = index;
            }
        }

        int high, low;
        if (skip is { } skipIndex)
        {
            high = skipIndex;
            low = parts.Count - skipIndex - 1;
            if (parts[0].Length == 0 && --high != 0)
            {
                return false;
            }

            if (parts[^1].Length == 0 && --low != 0)
            {
                return false;
            }

            if (hextets - (high + low) < 1)
            {
                return false;
            }
        }
        else
        {
            if (parts.Count != hextets || parts[0].Length == 0 || parts[^1].Length == 0)
            {
                return false;
            }

            high = parts.Count;
            low = 0;
        }

        return parts.Take(high).Concat(parts.Skip(parts.Count - low)).All(part => part.Length <= 4 && part.All(char.IsAsciiHexDigit));
    }

    /// <summary>Python <c>str.split(separator, maxsplit)</c>.</summary>
    private static List<string> SplitMax(string text, char separator, int maxSplit)
    {
        var parts = new List<string>();
        var start = 0;
        while (parts.Count < maxSplit)
        {
            var next = text.IndexOf(separator, start);
            if (next < 0)
            {
                break;
            }

            parts.Add(text[start..next]);
            start = next + 1;
        }

        parts.Add(text[start..]);
        return parts;
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

            var serverEvent = PyJson.Loads(text) as JsonObject
                ?? throw new InvalidOperationException("a Voice Live message was not a JSON object");

            // ServerEvent._deserialize looks the class up by type: a list or object there is unhashable (TypeError).
            if (serverEvent.TryGetPropertyValue("type", out var type) && type is JsonArray or JsonObject)
            {
                throw new InvalidOperationException("a Voice Live event's type was not hashable");
            }

            return serverEvent;
        }

        public ValueTask DisposeAsync() => socket.DisposeAsync();
    }
}
