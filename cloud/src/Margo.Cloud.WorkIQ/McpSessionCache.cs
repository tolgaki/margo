namespace Margo.Cloud.WorkIQ;

/// <summary>One MCP session: the server's session ID (none when it gave none) and the negotiated protocol version.</summary>
internal sealed record McpSession(string? Id, string Version);

/// <summary>
/// The transport's sessions, one per bearer-token hash, bounded in number and in idle time. Python kept every
/// session for the life of the process; tokens rotate about hourly, so that grew without limit.
/// </summary>
/// <remarks>
/// Thread-safe. A dropped session holds nothing to release on this side: it is two strings, and the transport
/// never opens a stream for it. The server-side session is left to expire, as it always was: ending it needs a
/// DELETE signed with its bearer token, and the transport deliberately keeps only the token's hash.
/// </remarks>
internal sealed class McpSessionCache
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly int _capacity;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _time;
    private long _uses;

    public McpSessionCache(int capacity, TimeSpan idleTimeout, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _idleTimeout = idleTimeout;
        _time = time;
    }

    /// <summary>The number of sessions kept, idle ones included until the next lookup or store.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The live session for a token hash, marking it used.</summary>
    public McpSession? Get(string key)
    {
        lock (_lock)
        {
            Prune();
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            entry.Touch(_time.GetTimestamp(), ++_uses);
            return entry.Session;
        }
    }

    /// <summary>Store (or replace) the session for a token hash, dropping the least recently used beyond the capacity.</summary>
    public void Set(string key, McpSession session)
    {
        lock (_lock)
        {
            Prune();
            var entry = new Entry(session);
            entry.Touch(_time.GetTimestamp(), ++_uses);
            _entries[key] = entry;
            while (_entries.Count > _capacity)
            {
                _entries.Remove(_entries.MinBy(pair => pair.Value.Use).Key);
            }
        }
    }

    /// <summary>
    /// Forget <paramref name="session"/> (the server no longer knows it). A newer session another call stored for
    /// the same token meanwhile is kept.
    /// </summary>
    public void Remove(string key, McpSession session)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && ReferenceEquals(entry.Session, session))
            {
                _entries.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private void Prune()
    {
        var now = _time.GetTimestamp();
        List<string>? idle = null;
        foreach (var (key, entry) in _entries)
        {
            if (_time.GetElapsedTime(entry.UsedAt, now) > _idleTimeout)
            {
                (idle ??= []).Add(key);
            }
        }

        foreach (var key in idle ?? [])
        {
            _entries.Remove(key);
        }
    }

    private sealed class Entry(McpSession session)
    {
        public McpSession Session { get; } = session;

        public long UsedAt { get; private set; }

        public long Use { get; private set; }

        public void Touch(long timestamp, long use)
        {
            UsedAt = timestamp;
            Use = use;
        }
    }
}
