using Margo.Cloud.Core;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Router;

/// <summary>
/// The route table, reloaded at most every 15 seconds. Python: <c>Router.table()</c>.
/// </summary>
/// <remarks>
/// One load at a time; requests wait for it. A load that fails is logged and the last good table is
/// kept; the next attempt waits <see cref="FailureBackoff"/>, so an outage does not queue every request
/// behind a load that will fail. A missing blob is an empty table. The load is not tied to the request
/// that started it, so a caller that goes away does not abort it for the others.
/// <para>Deliberate deviation from Python (PORTING.md): until the first load succeeds there is no table,
/// and <see cref="GetAsync"/> throws <see cref="RouteTableUnavailableException"/>. Python served an empty
/// table, on which every activity looks like a first activity: a replica that could not read the table
/// asked the provisioner to create instances that exist, including suspended and retired ones.</para>
/// </remarks>
public sealed class RouteTableCache : IDisposable
{
    /// <summary>Python: <c>ROUTES_TTL_SECONDS</c>.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    /// <summary>How long after a failed load the next one is attempted.</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(5);

    private readonly IRouteSource _source;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private RouteTable? _table;
    private long? _loadedAt;
    private long? _failedAt;

    public RouteTableCache(IRouteSource source, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The current table. The router only reads it.</summary>
    /// <exception cref="RouteTableUnavailableException">No load has succeeded yet.</exception>
    public async Task<RouteTable> GetAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var stale = _loadedAt is not { } loadedAt || _time.GetElapsedTime(loadedAt) > Ttl;
            var backingOff = _failedAt is { } failedAt && _time.GetElapsedTime(failedAt) <= FailureBackoff;
            if (stale && !backingOff)
            {
                try
                {
                    _table = RouteTable.Loads(await _source.LoadAsync(CancellationToken.None));
                    _loadedAt = _time.GetTimestamp();
                    _failedAt = null;
                }
                catch (Exception exc)
                {
                    // Keep serving the last good table rather than none.
                    _failedAt = _time.GetTimestamp();
                    _logger.LogError(exc, "route table could not be loaded");
                }
            }

            return _table ?? throw new RouteTableUnavailableException();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}

/// <summary>The route table has never been loaded: the router cannot tell a new instance from an existing one.</summary>
public sealed class RouteTableUnavailableException : Exception
{
    public RouteTableUnavailableException()
        : base("the route table has not been loaded yet")
    {
    }

    public RouteTableUnavailableException(string message)
        : base(message)
    {
    }

    public RouteTableUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
