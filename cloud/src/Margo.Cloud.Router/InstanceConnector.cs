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
/// <item>At most <see cref="ConnectionLimit"/> activity forwards at once, and at most
/// <see cref="PerInstanceLimit"/> to one instance; voice pages and sockets have their own, smaller pool
/// (<see cref="VoiceLimit"/>, <see cref="PerInstanceVoiceLimit"/> per instance). A request that cannot get a slot
/// within <see cref="ConnectTimeout"/> fails. Deliberate deviation from aiohttp's one pool of 100 (PORTING.md): one
/// instance that does not answer, or voice sockets opened by anyone who knows an instance's address, could hold
/// every slot and stop the activities of every other instance.</item>
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
    /// <summary>Activity forwards at once, over all instances (aiohttp's <c>TCPConnector(limit=100)</c> covered
    /// voice too).</summary>
    public const int ConnectionLimit = 100;

    /// <summary>Activity forwards at once to one instance.</summary>
    public const int PerInstanceLimit = 8;

    /// <summary>Voice pages and sockets at once, over all instances.</summary>
    public const int VoiceLimit = 24;

    /// <summary>Voice pages and sockets at once to one instance (one manager, a few tabs).</summary>
    public const int PerInstanceVoiceLimit = 4;

    /// <summary>Python: <c>FORWARD_TIMEOUT.connect</c>.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long an instance has to answer. Python: <c>FORWARD_TIMEOUT.total</c> was 300 s; the instance
    /// accepts an activity at once and works in the background, so this only has to outlast a cold start, and a
    /// shorter wait frees the slots of an instance that never starts sooner.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Python: <c>heartbeat=20</c>; a pong is awaited for half of it.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _voiceSlots;
    private readonly int _perInstance;
    private readonly int _perInstanceVoice;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _instanceSlots = new(StringComparer.Ordinal);
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
        var voiceLimit = Math.Min(VoiceLimit, connectionLimit);
        _voiceSlots = new SemaphoreSlim(voiceLimit, voiceLimit);
        _perInstance = Math.Min(PerInstanceLimit, connectionLimit);
        _perInstanceVoice = Math.Min(PerInstanceVoiceLimit, connectionLimit);
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

    /// <summary>Wait for an activity slot that belongs to no instance (tests, and callers without a key).</summary>
    /// <exception cref="InstanceTimeoutException">None became free within <see cref="ConnectTimeout"/>.</exception>
    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken) => AcquireAsync(null, voice: false, cancellationToken);

    /// <summary>Wait for a slot to one instance: first the instance's own, then the shared pool for the kind.</summary>
    /// <param name="instanceKey">The instance's key; <see langword="null"/> takes only a shared slot.</param>
    /// <param name="voice">A voice page or socket, not an activity.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="InstanceTimeoutException">None became free within <see cref="ConnectTimeout"/>.</exception>
    public async Task<IDisposable> AcquireAsync(string? instanceKey, bool voice, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        SemaphoreSlim? own = null;
        if (instanceKey is not null)
        {
            var limit = voice ? _perInstanceVoice : _perInstance;
            own = _instanceSlots.GetOrAdd((voice ? "v:" : "a:") + instanceKey, _ => new SemaphoreSlim(limit, limit));
            if (!await own.WaitAsync(_connectTimeout, cancellationToken))
            {
                throw new InstanceTimeoutException("the instance has too many requests waiting on it");
            }
        }

        var pool = voice ? _voiceSlots : _slots;
        var left = _connectTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
        bool acquired;
        try
        {
            acquired = await pool.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, cancellationToken);
        }
        catch
        {
            own?.Release();
            throw;
        }

        if (!acquired)
        {
            own?.Release();
            throw new InstanceTimeoutException("no connection to the instance became free");
        }

        return new Slot(pool, own);
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
        _voiceSlots.Dispose();
        foreach (var own in _instanceSlots.Values)
        {
            own.Dispose();
        }
    }

    private sealed class Slot(SemaphoreSlim pool, SemaphoreSlim? own) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                pool.Release();
                own?.Release();
            }
        }
    }
}
