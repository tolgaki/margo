using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Margo.Cloud.Orchestrator;

namespace Margo.Cloud.Host;

/// <summary>
/// The Azure Blob implementation of the Orchestrator's leased state store (Python: <c>host/azure_store.py</c>
/// <c>AzureBlobLeaseStore</c>). One private container per instance-and-manager, created on first use.
/// </summary>
/// <remarks>
/// Constructing a store (or the factory) makes no network call. Every call that does is asynchronous; Python ran them
/// on a worker thread. Failures Python caught as <c>AzureError</c> are, here, the Azure SDK's request and credential
/// failures (<see cref="IsAzureError"/>).
/// </remarks>
public sealed class AzureBlobLeaseStore : ILeaseStore, IDisposable
{
    /// <summary><c>LEASE_SECONDS</c>: how long a lease lasts without renewal.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

    /// <summary><c>WAIT_SECONDS</c>: how long another holder's lease is waited for.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(120);

    /// <summary>The pause between attempts while another holder has the lease (Python: <c>time.sleep(1)</c>).</summary>
    public static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private bool _ready;

    /// <param name="service">The account's Blob service client.</param>
    /// <param name="container">The container name.</param>
    /// <param name="time">The monotonic clock for the lease wait (tests).</param>
    /// <param name="delay">The pause between lease attempts (tests).</param>
    public AzureBlobLeaseStore(BlobServiceClient service, string container, TimeProvider? time = null,
                               Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(container);
        Container = service.GetBlobContainerClient(container);
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((span, token) => Task.Delay(span, _time, token));
    }

    /// <summary>The container client (no network call was made to create it).</summary>
    public BlobContainerClient Container { get; }

    /// <summary>
    /// <c>AzureBlobLeaseStore.factory(account_url, credential)</c>: returns <c>store_for(container)</c>, which reuses one
    /// store per container.
    /// </summary>
    public static Func<string, ILeaseStore> Factory(Uri accountUrl, TokenCredential credential, BlobClientOptions? options = null,
                                                    TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(accountUrl);
        ArgumentNullException.ThrowIfNull(credential);
        return Factory(new BlobServiceClient(accountUrl, credential, options), time, delay);
    }

    /// <summary>The same over an existing service client (tests pass one with a fake transport).</summary>
    public static Func<string, ILeaseStore> Factory(BlobServiceClient service, TimeProvider? time = null,
                                                    Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        var stores = new Dictionary<string, AzureBlobLeaseStore>(StringComparer.Ordinal);
        var gate = new Lock();
        return container =>
        {
            lock (gate)
            {
                if (!stores.TryGetValue(container, out var store))
                {
                    store = new AzureBlobLeaseStore(service, container, time, delay);
                    stores[container] = store;
                }

                return store;
            }
        };
    }

    /// <summary>
    /// Whether an exception is one Python's <c>except AzureError</c> caught: a failed storage request (including a
    /// transport failure after retries) or a failed credential.
    /// </summary>
    public static bool IsAzureError(Exception exception) =>
        exception is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException;

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    /// <summary><c>ensure_container()</c>: create the container once (an existing one is fine).</summary>
    public async Task EnsureContainerAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready)
            {
                return;
            }

            try
            {
                await Container.CreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exc) when (exc.Status == 409)
            {
                // ResourceExistsError
            }

            _ready = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary><c>forget_container()</c>: the container was removed since it was created here; create it again on next use.</summary>
    public async Task ForgetContainerAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ready = false;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IStateLease> AcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var lease = new Lease(this, Container.GetBlobClient(name));
        await lease.EnterAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    /// <inheritdoc />
    public async Task PutAsync(string name, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
        await Container.GetBlobClient(name).UploadAsync(BinaryData.FromBytes(data), overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Python: <c>_Lease</c>.</summary>
    private sealed class Lease(AzureBlobLeaseStore store, BlobClient blob) : IStateLease
    {
        private BlobLeaseClient? _lease;
        private int _released;

        /// <summary>The held lease's ID (tests).</summary>
        internal string? LeaseId => _lease?.LeaseId;

        /// <summary><c>__enter__</c>.</summary>
        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            try
            {
                await BootstrapAsync(retry: true, cancellationToken).ConfigureAwait(false);
                var deadline = store._time.GetTimestamp() + (long)(WaitLimit.TotalSeconds * store._time.TimestampFrequency);
                while (true)
                {
                    var client = blob.GetBlobLeaseClient();
                    try
                    {
                        await client.AcquireAsync(LeaseDuration, cancellationToken: cancellationToken).ConfigureAwait(false);
                        _lease = client;
                        return;
                    }
                    catch (RequestFailedException exc) when (exc.Status == 409 && store._time.GetTimestamp() < deadline)
                    {
                        await store._delay(RetryPause, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exc) when (IsAzureError(exc))
            {
                throw new SyncException("instance state is busy or unavailable", exc);
            }
        }

        /// <summary>
        /// <c>_bootstrap</c>: a lease needs an existing blob. While another sandbox holds the lease, Azure can refuse this
        /// create with 412 (no lease ID) instead of 409 (exists); either way the blob exists.
        /// </summary>
        private async Task BootstrapAsync(bool retry, CancellationToken cancellationToken)
        {
            await store.EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await blob.UploadAsync(BinaryData.FromBytes([]), overwrite: false, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exc) when (exc.Status == 409)
            {
                // ResourceExistsError
            }
            catch (RequestFailedException exc) when (exc.Status == 404)
            {
                if (!retry)
                {
                    throw;
                }

                await store.ForgetContainerAsync(cancellationToken).ConfigureAwait(false);
                await BootstrapAsync(retry: false, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exc) when (exc.Status == 412)
            {
                // the blob exists under another holder's lease
            }
        }

        public async Task<(byte[]? Data, string? ETag)> ReadAsync(CancellationToken cancellationToken = default)
        {
            BlobDownloadResult result;
            try
            {
                var options = new BlobDownloadOptions { Conditions = new BlobRequestConditions { LeaseId = _lease?.LeaseId } };
                result = (await blob.DownloadContentAsync(options, cancellationToken).ConfigureAwait(false)).Value;
            }
            catch (RequestFailedException exc) when (exc.Status == 404)
            {
                return (null, null);
            }
            catch (Exception exc) when (IsAzureError(exc))
            {
                throw new SyncException("could not read instance state", exc);
            }

            var data = result.Content.ToArray();
            return (data.Length == 0 ? null : data, ETagText(result.Details.ETag));
        }

        public async Task<string?> WriteAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            try
            {
                var options = new BlobUploadOptions { Conditions = new BlobRequestConditions { LeaseId = _lease?.LeaseId } };
                var info = await blob.UploadAsync(BinaryData.FromBytes(data), options, cancellationToken).ConfigureAwait(false);
                return ETagText(info.Value.ETag);
            }
            catch (Exception exc) when (IsAzureError(exc))
            {
                throw new SyncException("could not save instance state; the lease may have been lost", exc);
            }
        }

        /// <summary><c>renew()</c>: any exception propagates (the caller treats it as a lost lease).</summary>
        public async Task RenewAsync(CancellationToken cancellationToken = default)
        {
            var lease = _lease ?? throw new InvalidOperationException("the lease is not held"); // Python: AttributeError on None
            await lease.RenewAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <summary><c>__exit__</c>: release; a failed release is ignored (an expired lease frees itself).</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0 || _lease is null)
            {
                return;
            }

            try
            {
                await _lease.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception exc) when (IsAzureError(exc))
            {
                // expires after LEASE_SECONDS
            }
        }

        /// <summary>The ETag header's value as Python's SDK returned it (with its quotes).</summary>
        private static string ETagText(ETag etag) => etag.ToString("H");
    }
}
