namespace Margo.Cloud.Orchestrator;

/// <summary>
/// Shared, leased storage for an instance's state bundle (the <c>store</c> object <c>statesync.py</c> takes:
/// <c>store.lease(name)</c> and <c>store.put(name, bytes)</c>). The Host implements it on Azure Blob Storage; tests and
/// local development use <see cref="InMemoryLeaseStore"/>.
/// </summary>
/// <remarks>
/// <para>Contract, as the Python Azure store keeps it:</para>
/// <list type="bullet">
/// <item><see cref="AcquireAsync"/> takes an exclusive lease on the named blob (creating an empty blob first when there is
/// none), waiting for another holder to release it. When the lease cannot be taken safely (busy past its wait, storage
/// unavailable) it throws <see cref="SyncException"/>.</item>
/// <item>Only one lease on a name is held at a time, across every process and container that shares the store.</item>
/// <item><see cref="PutAsync"/> writes an unleased copy (the daily copies); a failure there is not data loss.</item>
/// </list>
/// </remarks>
public interface ILeaseStore
{
    /// <summary><c>store.lease(name).__enter__()</c>: an exclusive lease on <paramref name="name"/>.</summary>
    /// <exception cref="SyncException">The lease could not be taken safely.</exception>
    Task<IStateLease> AcquireAsync(string name, CancellationToken cancellationToken = default);

    /// <summary><c>store.put(name, data)</c>: an unleased copy, overwriting any earlier one.</summary>
    Task PutAsync(string name, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the unleased copies whose names start with <paramref name="prefix"/> and sort before
    /// <paramref name="firstKept"/> (ordinal), and returns how many: how the daily copies, named by date, expire. The
    /// default deletes nothing, for a store that cannot list. No Python equivalent (daily copies were kept for ever).
    /// </summary>
    Task<int> DeleteBeforeAsync(string prefix, string firstKept, CancellationToken cancellationToken = default) => Task.FromResult(0);
}

/// <summary>
/// One held lease (the object <c>store.lease(name)</c> enters into: <c>read()</c>, <c>write(bytes)</c>, <c>renew()</c>;
/// disposing it is <c>__exit__</c>, which releases the lease).
/// </summary>
/// <remarks>An unreleased lease must expire on its own (Azure: after 60 seconds without renewal), so a crashed holder
/// never blocks the instance for good. Disposal never throws for a release that failed.</remarks>
public interface IStateLease : IAsyncDisposable
{
    /// <summary><c>read()</c>: the blob's content and ETag, or <c>(null, null)</c> when there is no blob. Empty content
    /// means there is no shared bundle.</summary>
    /// <exception cref="SyncException">The blob could not be read.</exception>
    Task<(byte[]? Data, string? ETag)> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="ReadAsync"/> for a caller that already holds the content of <paramref name="knownETag"/>: a store
    /// that can ask conditionally (Azure: <c>If-None-Match</c>) answers <c>(null, knownETag, true)</c> without
    /// downloading when the blob still has that ETag. The default reads the blob and reports it as changed; the caller
    /// compares the ETag itself.
    /// </summary>
    /// <exception cref="SyncException">The blob could not be read.</exception>
    async Task<(byte[]? Data, string? ETag, bool Unchanged)> ReadIfChangedAsync(string? knownETag, CancellationToken cancellationToken = default)
    {
        var (data, etag) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return (data, etag, false);
    }

    /// <summary><c>write(data)</c>: replaces the blob under this lease and returns its new ETag.</summary>
    /// <exception cref="SyncException">The blob could not be written (the lease may have been lost).</exception>
    Task<string?> WriteAsync(byte[] data, CancellationToken cancellationToken = default);

    /// <summary><c>renew()</c>: extends the lease. Any exception means the lease may be lost.</summary>
    Task RenewAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// An in-memory <see cref="ILeaseStore"/> (the Python tests' <c>FakeLeaseStore</c>): one blocking lease per blob, and
/// ETags that change on every write (<c>etag-1</c>, <c>etag-2</c>, ...). For tests and local development; its content
/// lives only as long as the process.
/// </summary>
public sealed class InMemoryLeaseStore : ILeaseStore
{
    private readonly Dictionary<string, (byte[] Data, string ETag)> _blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly List<string> _puts = [];
    private int _writes;
    private int _downloads;
    private readonly Dictionary<string, byte[]> _copies = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _counter;

    /// <summary>The leased blobs' names, in the order they were first written.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate)
            {
                return [.. _blobs.Keys];
            }
        }
    }

    /// <inheritdoc/>
    public Task<int> DeleteBeforeAsync(string prefix, string firstKept, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(firstKept);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var old = _copies.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && string.CompareOrdinal(name, firstKept) < 0).ToList();
            foreach (var name in old)
            {
                _copies.Remove(name);
            }

            return Task.FromResult(old.Count);
        }
    }

    /// <summary>The names <see cref="PutAsync"/> was called with, in order.</summary>
    public IReadOnlyList<string> Puts
    {
        get
        {
            lock (_gate)
            {
                return [.. _puts];
            }
        }
    }

    /// <summary>How many times a leased blob was written (<see cref="IStateLease.WriteAsync"/>).</summary>
    public int Writes => Volatile.Read(ref _writes);

    /// <summary>How many times a leased blob's content was handed out (a conditional read of an unchanged blob is not one).</summary>
    public int Downloads => Volatile.Read(ref _downloads);

    /// <summary>A leased blob's content and ETag, or null.</summary>
    public (byte[] Data, string ETag)? Get(string name)
    {
        lock (_gate)
        {
            return _blobs.TryGetValue(name, out var blob) ? (blob.Data.ToArray(), blob.ETag) : null;
        }
    }

    /// <summary>An unleased copy written by <see cref="PutAsync"/>, or null.</summary>
    public byte[]? GetCopy(string name)
    {
        lock (_gate)
        {
            return _copies.TryGetValue(name, out var data) ? data.ToArray() : null;
        }
    }

    /// <summary>Replaces a blob as an operator or another writer would (tests: tampering).</summary>
    public void Set(string name, byte[] data, string etag)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            _blobs[name] = (data.ToArray(), etag);
        }
    }

    /// <summary>Removes a blob, as an operator removing an instance's data would.</summary>
    public bool Remove(string name)
    {
        lock (_gate)
        {
            return _blobs.Remove(name);
        }
    }

    /// <inheritdoc />
    public async Task<IStateLease> AcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        SemaphoreSlim semaphore;
        lock (_gate)
        {
            if (!_locks.TryGetValue(name, out semaphore!))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _locks[name] = semaphore;
            }
        }

        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this, name, semaphore);
    }

    /// <inheritdoc />
    public Task PutAsync(string name, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _puts.Add(name);
            _copies[name] = data.ToArray();
        }

        return Task.CompletedTask;
    }

    private sealed class Lease(InMemoryLeaseStore store, string name, SemaphoreSlim semaphore) : IStateLease
    {
        private int _released;

        public Task<(byte[]? Data, string? ETag)> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (store._gate)
            {
                Interlocked.Increment(ref store._downloads);
                return Task.FromResult<(byte[]?, string?)>(store._blobs.TryGetValue(name, out var blob) ? (blob.Data.ToArray(), blob.ETag) : (null, null));
            }
        }

        public Task<(byte[]? Data, string? ETag, bool Unchanged)> ReadIfChangedAsync(string? knownETag, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (store._gate)
            {
                if (!string.IsNullOrEmpty(knownETag) && store._blobs.TryGetValue(name, out var known) && known.ETag == knownETag)
                {
                    return Task.FromResult<(byte[]?, string?, bool)>((null, knownETag, true));
                }

                Interlocked.Increment(ref store._downloads);
                return Task.FromResult<(byte[]?, string?, bool)>(store._blobs.TryGetValue(name, out var blob) ? (blob.Data.ToArray(), blob.ETag, false) : (null, null, false));
            }
        }

        public Task<string?> WriteAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            cancellationToken.ThrowIfCancellationRequested();
            lock (store._gate)
            {
                store._counter++;
                Interlocked.Increment(ref store._writes);
                var etag = "etag-" + store._counter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                store._blobs[name] = (data.ToArray(), etag);
                return Task.FromResult<string?>(etag);
            }
        }

        public Task RenewAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
