using System.Net;
using System.Net.WebSockets;

namespace Margo.Cloud.Router;

/// <summary>
/// The router's connections to instances: Python's one <c>aiohttp.ClientSession</c> with
/// <c>ClientTimeout(total=300, connect=10)</c> and the default connector.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>At most <see cref="ConnectionLimit"/> requests and voice sockets at once, as aiohttp's
/// connector allowed; a request that cannot get a slot within <see cref="ConnectTimeout"/> fails.</item>
/// <item>Responses compressed with gzip or deflate are decompressed (aiohttp asked for them).</item>
/// <item>Redirects are followed, at most 10 (aiohttp's default; the instance does not redirect these).</item>
/// <item>No cookie jar: aiohttp kept one for the whole session, but the instance sets no cookies.</item>
/// </list>
/// </remarks>
public sealed class InstanceConnector : IDisposable
{
    /// <summary>aiohttp's <c>TCPConnector(limit=100)</c>.</summary>
    public const int ConnectionLimit = 100;

    /// <summary>Python: <c>FORWARD_TIMEOUT.connect</c>.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Python: <c>FORWARD_TIMEOUT.total</c>.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(300);

    /// <summary>Python: <c>heartbeat=20</c>; a pong is awaited for half of it.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _slots = new(ConnectionLimit, ConnectionLimit);
    private readonly HttpClient _http;
    private readonly HttpMessageInvoker _sockets;

    public InstanceConnector()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = false,
        })
        {
            Timeout = TotalTimeout,
        };
        _sockets = new HttpMessageInvoker(new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AllowAutoRedirect = false,
            UseCookies = false,
        });
    }

    /// <summary>Wait for a connection slot.</summary>
    /// <exception cref="TimeoutException">None became free within <see cref="ConnectTimeout"/>.</exception>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _slots.WaitAsync(ConnectTimeout, cancellationToken))
        {
            throw new TimeoutException("no connection to the instance became free");
        }

        return new Slot(_slots);
    }

    /// <summary>Send a request and read the whole response, within <see cref="TotalTimeout"/>.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        return _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    /// <summary>Open a WebSocket to the instance: no headers, no subprotocol, heartbeats every
    /// <see cref="Heartbeat"/> (Python: <c>ws_connect(url, heartbeat=20, ...)</c>).</summary>
    public async Task<WebSocket> ConnectWebSocketAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = Heartbeat;
        socket.Options.KeepAliveTimeout = Heartbeat / 2;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TotalTimeout);
        try
        {
            await socket.ConnectAsync(uri, _sockets, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _sockets.Dispose();
        _slots.Dispose();
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
