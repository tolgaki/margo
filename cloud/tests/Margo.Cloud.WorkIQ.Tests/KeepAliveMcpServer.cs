using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Margo.Cloud.WorkIQ.Tests;

/// <summary>
/// An HTTP/1.1 MCP server on 127.0.0.1 that keeps connections open, as a real front end does (the other loopback
/// server answers HTTP/1.0 style and closes). It counts connections, compresses when asked to, and can answer a
/// <c>tools/call</c> with a body of any size or hang up on one. Fictional data only.
/// </summary>
internal sealed class KeepAliveMcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(int Connection, SeenRequest Request)> _seen = [];
    private readonly Lock _lock = new();
    private readonly Task _accepting;
    private int _connections;
    private int _sessions;

    public KeepAliveMcpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/mcp";
        _accepting = AcceptAsync();
    }

    public string Url { get; }

    /// <summary>Connections accepted so far.</summary>
    public int Connections => Volatile.Read(ref _connections);

    public int Sessions => Volatile.Read(ref _sessions);

    /// <summary>Compress answers with gzip when the request accepts it.</summary>
    public bool Compress { get; set; }

    /// <summary>The <c>data</c> text of each <c>tools/call</c> answer is this many characters long (0: a small answer).</summary>
    public int PayloadCharacters { get; set; }

    /// <summary>Send the answer chunked, without a <c>Content-Length</c>.</summary>
    public bool Chunked { get; set; }

    /// <summary>Read this <c>tools/call</c> (counted from 1) whole, then hang up without answering.</summary>
    public int HangUpOnCall { get; set; }

    /// <summary>Every request, with the number of the connection it arrived on (from 1).</summary>
    public IReadOnlyList<(int Connection, SeenRequest Request)> Seen
    {
        get
        {
            lock (_lock)
            {
                return [.. _seen];
            }
        }
    }

    public IReadOnlyList<(int Connection, SeenRequest Request)> Calls =>
        [.. Seen.Where(seen => (string?)seen.Request.Json["method"] == "tools/call")];

    public McpHttpTransport Transport(McpTransportOptions? options = null) =>
        new(Url, TimeSpan.FromSeconds(20), allowLoopbackHttp: true, options: options);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (Exception exc) when (exc is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exc) when (exc is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            client.NoDelay = true; // the head and the body are written separately
            var number = Interlocked.Increment(ref _connections);
            _ = Task.Run(() => ServeAsync(client, number));
        }
    }

    private async Task ServeAsync(TcpClient client, int connection)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                while (await ReadRequestAsync(stream, _stop.Token) is { } request)
                {
                    int calls;
                    lock (_lock)
                    {
                        _seen.Add((connection, request));
                        calls = _seen.Count(seen => (string?)seen.Request.Json["method"] == "tools/call");
                    }

                    var isCall = (string?)request.Json["method"] == "tools/call";
                    if (isCall && calls == HangUpOnCall)
                    {
                        return;
                    }

                    await AnswerAsync(stream, request, _stop.Token);
                    if (string.Equals(request.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch (Exception exc) when (exc is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The client hung up (a size-limit test) or the server is stopping.
            }
        }
    }

    private async Task AnswerAsync(NetworkStream stream, SeenRequest request, CancellationToken cancellationToken)
    {
        var body = request.Json;
        var id = body["id"]?.DeepClone();
        var close = string.Equals(request.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
        switch ((string?)body["method"])
        {
            case "initialize":
                var session = "session-" + Interlocked.Increment(ref _sessions).ToString(CultureInfo.InvariantCulture);
                var hello = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["protocolVersion"] = "2025-06-18" } };
                await WriteAsync(stream, 200, Encoding.UTF8.GetBytes(hello.ToJsonString()), "Mcp-Session-Id: " + session + "\r\n", close, request, cancellationToken);
                return;
            case "notifications/initialized":
                await WriteAsync(stream, 202, [], string.Empty, close, request, cancellationToken);
                return;
            default:
                var payload = new JsonObject { ["statusCode"] = 200, ["data"] = new string('a', PayloadCharacters) };
                var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["structuredContent"] = payload } };
                await WriteAsync(stream, 200, Encoding.UTF8.GetBytes(answer.ToJsonString()), string.Empty, close, request, cancellationToken);
                return;
        }
    }

    private async Task WriteAsync(NetworkStream stream, int status, byte[] body, string extraHeaders, bool close, SeenRequest request,
                                  CancellationToken cancellationToken)
    {
        var head = new StringBuilder("HTTP/1.1 ").Append(status).Append(status == 200 ? " OK" : " Accepted").Append("\r\n")
            .Append("Content-Type: application/json\r\n").Append(extraHeaders);
        if (Compress && body.Length > 0 && (request.Header("Accept-Encoding") ?? string.Empty).Contains("gzip", StringComparison.Ordinal))
        {
            using var packed = new MemoryStream();
            await using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            {
                await gzip.WriteAsync(body, cancellationToken);
            }

            body = packed.ToArray();
            head.Append("Content-Encoding: gzip\r\n");
        }

        if (close)
        {
            head.Append("Connection: close\r\n");
        }

        if (Chunked && body.Length > 0)
        {
            head.Append("Transfer-Encoding: chunked\r\n\r\n");
            await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), cancellationToken);
            const int piece = 64 * 1024;
            for (var offset = 0; offset < body.Length; offset += piece)
            {
                var count = Math.Min(piece, body.Length - offset);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(count.ToString("x", CultureInfo.InvariantCulture) + "\r\n"), cancellationToken);
                await stream.WriteAsync(body.AsMemory(offset, count), cancellationToken);
                await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
            }

            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken);
        }
        else
        {
            head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n\r\n");
            await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<SeenRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 4 || !(head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
            {
                return null;
            }

            head.Add(one[0]);
        }

        var lines = Encoding.Latin1.GetString([.. head]).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }

        var body = new byte[int.Parse(headers.GetValueOrDefault("Content-Length", "0"), CultureInfo.InvariantCulture)];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return new SeenRequest(requestLine[0], requestLine[1], headers, body);
    }
}
