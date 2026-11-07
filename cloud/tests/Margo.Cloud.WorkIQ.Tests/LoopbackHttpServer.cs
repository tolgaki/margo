using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Margo.Cloud.WorkIQ.Tests;

/// <summary>One request as the loopback server saw it.</summary>
internal sealed record SeenRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public JsonObject Json => JsonNode.Parse(Body)!.AsObject();

    public string BodyText => Encoding.UTF8.GetString(Body);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A tiny HTTP/1.x server on 127.0.0.1, the equivalent of the <c>ThreadingHTTPServer</c> the Python tests
/// run: one request per connection, answered HTTP/1.0 style and closed, like <c>BaseHTTPRequestHandler</c>.
/// Working at the socket level lets a test answer slowly, stall, truncate or hang up.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<SeenRequest, Exchange, CancellationToken, Task> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<SeenRequest> _seen = [];
    private readonly Lock _lock = new();
    private readonly Task _accepting;

    public LoopbackHttpServer(Func<SeenRequest, Exchange, CancellationToken, Task> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    public int Port { get; }

    public string Url(string path = "/mcp") => "http://127.0.0.1:" + Port + path;

    public IReadOnlyList<SeenRequest> Seen
    {
        get
        {
            lock (_lock)
            {
                return [.. _seen];
            }
        }
    }

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

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, _stop.Token);
                if (request is null)
                {
                    return;
                }

                lock (_lock)
                {
                    _seen.Add(request);
                }

                await _handler(request, new Exchange(stream), _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch (Exception exc) when (exc is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The client gave up (a timeout test) or the server is stopping; like handle_error = no-op.
            }
        }
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

        var length = headers.TryGetValue("Content-Length", out var value) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return new SeenRequest(requestLine[0], requestLine[1], headers, body);
    }

    /// <summary>Writes the answer to one request.</summary>
    internal sealed class Exchange(NetworkStream stream)
    {
        public NetworkStream Stream { get; } = stream;

        /// <summary><c>send_response</c> + headers + <c>end_headers</c> + body, as BaseHTTPRequestHandler writes them.</summary>
        public async Task SendAsync(int status, IEnumerable<KeyValuePair<string, string>>? headers, byte[]? body, CancellationToken cancellationToken)
        {
            await WriteHeadAsync(status, headers, cancellationToken);
            if (body is not null)
            {
                await Stream.WriteAsync(body, cancellationToken);
            }
        }

        public async Task WriteHeadAsync(int status, IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken cancellationToken)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.0 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
            head.Append("Server: loopback-test\r\n");
            foreach (var (name, value) in headers ?? [])
            {
                head.Append(name).Append(": ").Append(value).Append("\r\n");
            }

            head.Append("\r\n");
            await Stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), cancellationToken);
            await Stream.FlushAsync(cancellationToken);
        }

        public async Task SendJsonAsync(JsonNode payload, IEnumerable<KeyValuePair<string, string>>? extra, CancellationToken cancellationToken)
        {
            var data = Encoding.UTF8.GetBytes(payload.ToJsonString());
            var headers = new List<KeyValuePair<string, string>>
            {
                new("Content-Type", "application/json"),
                new("Content-Length", data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            headers.AddRange(extra ?? []);
            await SendAsync(200, headers, data, cancellationToken);
        }

        private static string Reason(int status) => status switch
        {
            200 => "OK",
            202 => "Accepted",
            302 => "Found",
            404 => "Not Found",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "Status",
        };
    }
}
