using Margo.Cloud.Core;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Router;

/// <summary>
/// The route table, reloaded at most every 15 seconds. Python: <c>Router.table()</c>.
/// </summary>
/// <remarks>
/// One load at a time; requests wait for it. A load that fails is logged and the last good table is
/// kept (an empty table until the first load succeeds), and the next request tries again. A missing
/// blob is an empty table. The load is not tied to the request that started it, so a caller that goes
/// away does not abort it for the others.
/// </remarks>
public sealed class RouteTableCache : IDisposable
{
    /// <summary>Python: <c>ROUTES_TTL_SECONDS</c>.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    private readonly IRouteSource _source;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private RouteTable _table = new();
    private long? _loadedAt;

    public RouteTableCache(IRouteSource source, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The current table. The router only reads it.</summary>
    public async Task<RouteTable> GetAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_loadedAt is not { } loadedAt || _time.GetElapsedTime(loadedAt) > Ttl)
            {
                try
                {
                    _table = RouteTable.Loads(await _source.LoadAsync(CancellationToken.None));
                    _loadedAt = _time.GetTimestamp();
                }
                catch (Exception exc)
                {
                    // Keep serving the last good table rather than none.
                    _logger.LogError(exc, "route table could not be loaded");
                }
            }

            return _table;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}
