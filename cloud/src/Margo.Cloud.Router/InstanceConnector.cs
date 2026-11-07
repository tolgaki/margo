using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Margo.Cloud.Router;

/// <summary>A request to an instance timed out: no connection slot or TCP connection within the connect
/// timeout, or no whole response within the total timeout. Python: aiohttp's <c>ServerTimeoutError</c>
/// or <c>asyncio.TimeoutError</c>, which aiohttp's server answered with 504.</summary>
public sealed class InstanceTimeoutException : TimeoutException
{
    public InstanceTimeoutException()
    {
    }

    public InstanceTimeoutException(string message)
        : base(message)
    {
    }

    public InstanceTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

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
/// <item>No trace context (<c>traceparent</c>, <c>tracestate</c>, <c>baggage</c>) is added: aiohttp sent
/// none, and .NET would otherwise pass on the caller's.</item>
/// <item>Header values go out as UTF-8, as aiohttp wrote them (.NET refuses non-ASCII by default).</item>
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

    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _connectTimeout;
    private readonly HttpClient _http;
    private readonly HttpMessageInvoker _sockets;

    public InstanceConnector()
        : this(ConnectionLimit, ConnectTimeout, TotalTimeout)
    {
    }

    /// <summary>Other limits, for tests.</summary>
    internal InstanceConnector(int connectionLimit, TimeSpan connectTimeout, TimeSpan totalTimeout)
    {
        _slots = new SemaphoreSlim(connectionLimit, connectionLimit);
        _connectTimeout = connectTimeout;
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = connectTimeout,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = false,
            ActivityHeadersPropagator = null,
            RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8,
        })
        {
            Timeout = totalTimeout,
        };
        _sockets = new HttpMessageInvoker(new SocketsHttpHandler
        {
            ConnectTimeout = connectTimeout,
            AllowAutoRedirect = false,
            UseCookies = false,
            ActivityHeadersPropagator = null,
        });
    }

    /// <summary>Wait for a connection slot.</summary>
    /// <exception cref="InstanceTimeoutException">None became free within <see cref="ConnectTimeout"/>.</exception>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _slots.WaitAsync(_connectTimeout, cancellationToken))
        {
            throw new InstanceTimeoutException("no connection to the instance became free");
        }

        return new Slot(_slots);
    }

    /// <summary>Send a request and read the whole response, within <see cref="TotalTimeout"/>.</summary>
    /// <exception cref="InstanceTimeoutException">The connect or total timeout elapsed.</exception>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException exc) when (exc.InnerException is TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            throw new InstanceTimeoutException("the instance did not answer in time", exc);
        }
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
