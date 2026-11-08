using System.Net;
using System.Text;
using Azure;
using Margo.Cloud.Orchestrator;

namespace Margo.Cloud.Host.Tests;

/// <summary>
/// <c>AzureStoreTests</c> of <c>tests/test_autopilot_runtime.py</c>, against the real Azure SDK over an in-memory Blob
/// service, plus the rest of <c>azure_store.py</c>'s contract (lease waits, reads, writes, renewals, release, daily
/// copies) and a state bundle shared by two sandboxes through it.
/// </summary>
public sealed class AzureBlobLeaseStoreTests : IDisposable
{
    private readonly FakeBlobService _blobs = new();
    private readonly ManualTime _time = new();
    private readonly List<TimeSpan> _pauses = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _blobs.Dispose();

    private Func<string, ILeaseStore> Factory() => AzureBlobLeaseStore.Factory(_blobs.Client(), _time, (pause, _) =>
    {
        _pauses.Add(pause);
        _time.Advance(pause);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task Stores_are_reused_and_construction_makes_no_network_call()
    {
        var storeFor = Factory();
        Assert.Same(storeFor("margo-a"), storeFor("margo-a"));
        Assert.NotSame(storeFor("margo-a"), storeFor("margo-b"));
        Assert.Empty(_blobs.Calls);
        var store = storeFor("margo-a");
        for (var i = 0; i < 2; i++)
        {
            await using var lease = await store.AcquireAsync("state.zip", Token);
        }

        Assert.Equal(1, _blobs.Calls.Count(call => call == "create"));
        Assert.Equal(["create", "upload", "acquire", "release", "upload", "acquire", "release"], _blobs.Calls);
    }

    [Fact]
    public async Task A_blob_leased_by_another_sandbox_is_waited_for_not_an_error()
    {
        var store = Factory()("margo-a");
        await using (await store.AcquireAsync("state.zip", Token))
        {
        }

        // While another sandbox holds the lease, the create-if-missing answers 412 rather than 409.
        _blobs.Fail("upload", HttpStatusCode.PreconditionFailed, "LeaseIdMissing");
        await using (var lease = await store.AcquireAsync("state.zip", Token))
        {
            Assert.NotNull(_blobs.LeaseOf("margo-a", "state.zip"));
        }

        Assert.Equal(["upload", "acquire", "release"], _blobs.Calls.TakeLast(3));
        Assert.Null(_blobs.LeaseOf("margo-a", "state.zip"));
    }

    [Fact]
    public async Task Other_storage_failures_are_typed_sync_errors()
    {
        var store = Factory()("margo-a");
        _blobs.Fail("upload", HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch");
        var error = await Assert.ThrowsAsync<SyncException>(() => store.AcquireAsync("state.zip", Token));
        Assert.Equal("instance state is busy or unavailable", error.Message);
        Assert.IsType<RequestFailedException>(error.InnerException);
    }

    [Fact]
    public async Task A_removed_container_is_created_again()
    {
        var store = Factory()("margo-a");
        await using (await store.AcquireAsync("state.zip", Token))
        {
        }

        _blobs.RemoveContainer("margo-a");
        await using (await store.AcquireAsync("state.zip", Token))
        {
        }

        Assert.Equal(2, _blobs.Calls.Count(call => call == "create"));
    }

    [Fact]
    public async Task A_container_missing_twice_is_a_sync_error()
    {
        var store = Factory()("margo-a");
        _blobs.Fail("upload", HttpStatusCode.NotFound, "ContainerNotFound");
        _blobs.Fail("upload", HttpStatusCode.NotFound, "ContainerNotFound");
        var error = await Assert.ThrowsAsync<SyncException>(() => store.AcquireAsync("state.zip", Token));
        Assert.Equal("instance state is busy or unavailable", error.Message);
        Assert.Equal(["create", "upload", "create", "upload"], _blobs.Calls);
    }

    [Fact]
    public async Task The_lease_lasts_sixty_seconds_and_an_existing_blob_is_kept()
    {
        var store = Factory()("margo-a");
        await using (var lease = await store.AcquireAsync("state.zip", Token))
        {
            await lease.WriteAsync([1, 2, 3], Token);
        }

        await using (await store.AcquireAsync("state.zip", Token))
        {
        }

        // The bootstrap never overwrites: If-None-Match: * and a 409 is fine.
        Assert.Equal([1, 2, 3], _blobs.Content("margo-a", "state.zip"));
        var acquire = _blobs.Requests.Where(request => request.Headers.TryGetValues("x-ms-lease-action", out var action) && action.Single() == "acquire");
        Assert.All(acquire, request => Assert.Equal("60", request.Headers.GetValues("x-ms-lease-duration").Single()));
        var bootstrap = _blobs.Requests.Where(request => request.Method == HttpMethod.Put && !request.RequestUri!.Query.Contains("comp=", StringComparison.Ordinal)
                                                         && request.RequestUri.AbsolutePath.EndsWith("/state.zip", StringComparison.Ordinal)
                                                         && !request.Headers.Contains("x-ms-lease-id"));
        Assert.All(bootstrap, request => Assert.Equal("*", request.Headers.GetValues("If-None-Match").Single()));
    }

    [Fact]
    public async Task A_busy_lease_is_retried_every_second_for_up_to_two_minutes()
    {
        var store = Factory()("margo-a");
        _blobs.LeaseElsewhere("margo-a", "state.zip");
        for (var i = 0; i < 3; i++)
        {
            _blobs.Fail("acquire", HttpStatusCode.Conflict, "LeaseAlreadyPresent");
        }

        _blobs.ReleaseElsewhere("margo-a", "state.zip");
        await using (await store.AcquireAsync("state.zip", Token))
        {
        }

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], _pauses);

        _pauses.Clear();
        _blobs.LeaseElsewhere("margo-a", "state.zip");
        var error = await Assert.ThrowsAsync<SyncException>(() => store.AcquireAsync("state.zip", Token));
        Assert.Equal("instance state is busy or unavailable", error.Message);
        Assert.Equal(120, _pauses.Count); // attempts at 0..120 s; the one at 120 s is past the deadline
    }

    [Fact]
    public async Task Reads_and_writes_go_under_the_lease_and_report_etags_with_their_quotes()
    {
        var store = Factory()("margo-a");
        await using var lease = await store.AcquireAsync("state.zip", Token);
        var leaseId = _blobs.LeaseOf("margo-a", "state.zip");

        // The bootstrap's empty blob is "no bundle".
        var (empty, emptyTag) = await lease.ReadAsync(Token);
        Assert.Null(empty);
        Assert.StartsWith("\"0x8DE", emptyTag, StringComparison.Ordinal);

        var etag = await lease.WriteAsync(Encoding.UTF8.GetBytes("bundle"), Token);
        Assert.Matches("^\"0x8DE[0-9A-F]{6}\"$", etag);
        var (data, readTag) = await lease.ReadAsync(Token);
        Assert.Equal("bundle", Encoding.UTF8.GetString(data!));
        Assert.Equal(etag, readTag);
        var leased = _blobs.Requests.Where(request => request.Method == HttpMethod.Get
                                                      || (request.Method == HttpMethod.Put && request.Headers.Contains("x-ms-lease-id") && !request.RequestUri!.Query.Contains("comp=", StringComparison.Ordinal)));
        Assert.All(leased, request => Assert.Equal(leaseId, request.Headers.GetValues("x-ms-lease-id").Single()));
        Assert.Equal(3, leased.Count());
    }

    [Fact]
    public async Task A_missing_blob_reads_as_nothing_and_failures_are_sync_errors()
    {
        var store = Factory()("margo-a");
        await using var lease = await store.AcquireAsync("state.zip", Token);
        _blobs.Fail("download", HttpStatusCode.NotFound, "BlobNotFound");
        Assert.Equal((null, null), await lease.ReadAsync(Token));

        _blobs.Fail("download", HttpStatusCode.InternalServerError, "InternalError");
        Assert.Equal("could not read instance state", (await Assert.ThrowsAsync<SyncException>(() => lease.ReadAsync(Token))).Message);

        _blobs.Fail("upload", HttpStatusCode.PreconditionFailed, "LeaseLost");
        Assert.Equal("could not save instance state; the lease may have been lost",
            (await Assert.ThrowsAsync<SyncException>(() => lease.WriteAsync([1], Token))).Message);
    }

    /// <summary>
    /// Python's <c>except AzureError</c> also caught connection failures (<c>ServiceRequestError</c>). With the SDK's
    /// retries on, as in production, .NET reports those as an <see cref="AggregateException"/> of the attempts; they are
    /// the same typed failures (and a failed release is still ignored).
    /// </summary>
    [Fact]
    public async Task Connection_failures_after_the_sdk_s_retries_are_typed_sync_errors()
    {
        var store = AzureBlobLeaseStore.Factory(_blobs.RetryingClient())("margo-a");
        _blobs.Unreachable.Add("acquire");
        var busy = await Assert.ThrowsAsync<SyncException>(() => store.AcquireAsync("state.zip", Token));
        Assert.Equal("instance state is busy or unavailable", busy.Message);
        Assert.Equal(3, _blobs.Calls.Count(call => call == "acquire")); // the SDK retried
        _blobs.Unreachable.Clear();

        var lease = await store.AcquireAsync("state.zip", Token);
        _blobs.Unreachable.Add("download");
        Assert.Equal("could not read instance state", (await Assert.ThrowsAsync<SyncException>(() => lease.ReadAsync(Token))).Message);
        _blobs.Unreachable.Add("upload");
        Assert.Equal("could not save instance state; the lease may have been lost",
            (await Assert.ThrowsAsync<SyncException>(() => lease.WriteAsync([1], Token))).Message);
        _blobs.Unreachable.Add("release");
        await lease.DisposeAsync(); // an expired lease frees itself
        Assert.Equal(3, _blobs.Calls.Count(call => call == "release"));
    }

    /// <summary>Python: <c>ServiceResponseTimeoutError</c> is an <c>AzureError</c> too.</summary>
    [Fact]
    public async Task Network_timeouts_are_typed_sync_errors_but_the_caller_s_cancellation_is_not()
    {
        var store = AzureBlobLeaseStore.Factory(_blobs.RetryingClient(TimeSpan.FromMilliseconds(50)))("margo-a");
        _blobs.Hanging.Add("acquire");
        Assert.Equal("instance state is busy or unavailable",
            (await Assert.ThrowsAsync<SyncException>(() => store.AcquireAsync("state.zip", Token))).Message);
        _blobs.Hanging.Clear();

        var lease = await store.AcquireAsync("state.zip", Token);
        _blobs.Hanging.Add("download");
        Assert.Equal("could not read instance state", (await Assert.ThrowsAsync<SyncException>(() => lease.ReadAsync(Token))).Message);
        _blobs.Hanging.Clear();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.ReadAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AcquireAsync("other.zip", cancelled.Token));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_renewal_propagates_and_a_failed_release_is_ignored()
    {
        var store = Factory()("margo-a");
        var lease = await store.AcquireAsync("state.zip", Token);
        await lease.RenewAsync(Token);
        _blobs.Fail("renew", HttpStatusCode.Conflict, "LeaseIdMismatchWithLeaseOperation");
        await Assert.ThrowsAsync<RequestFailedException>(() => lease.RenewAsync(Token));
        _blobs.Fail("release", HttpStatusCode.Conflict, "LeaseIdMismatchWithLeaseOperation");
        await lease.DisposeAsync();
        await lease.DisposeAsync(); // released once
        Assert.Equal(1, _blobs.Calls.Count(call => call == "release"));
    }

    [Fact]
    public async Task Daily_copies_are_unleased_overwrites_and_create_the_container()
    {
        var store = Factory()("margo-a");
        await store.PutAsync("daily/2026-11-08.zip", [1], Token);
        await store.PutAsync("daily/2026-11-08.zip", [2], Token);
        Assert.Equal([2], _blobs.Content("margo-a", "daily/2026-11-08.zip"));
        Assert.Equal(["create", "upload", "upload"], _blobs.Calls);
        Assert.DoesNotContain(_blobs.Requests, request => request.Headers.Contains("If-None-Match") || request.Headers.Contains("x-ms-lease-id"));

        _blobs.Fail("upload", HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch");
        await Assert.ThrowsAsync<RequestFailedException>(() => store.PutAsync("daily/2026-11-09.zip", [3], Token));
    }

    [Fact]
    public async Task Two_sandboxes_share_one_state_through_the_store()
    {
        using var state = new TempState();
        var storeFor = Factory();
        var container = StateSync.ContainerName("account-fictional");
        var first = new StateSync(state.Combine("a", "margo.sqlite3"), storeFor(container), "account-fictional");
        var second = new StateSync(state.Combine("b", "margo.sqlite3"), storeFor(container), "account-fictional");
        Directory.CreateDirectory(state.Combine("a"));
        Directory.CreateDirectory(state.Combine("b"));

        await first.RunAsync(token =>
        {
            TempState.WriteDatabase(state.Combine("a", "margo.sqlite3"), "account-fictional", "first");
            return Task.FromResult(0);
        }, cancellationToken: Token);
        var outcome = await second.RunAsync(token => Task.FromResult(TempState.ReadNote(state.Combine("b", "margo.sqlite3"))), cancellationToken: Token);
        Assert.Equal("first", outcome.Result);
        Assert.NotNull(outcome.Restored);
        Assert.Contains("daily/", string.Join(",", _blobs.Requests.Select(request => request.RequestUri!.AbsolutePath)), StringComparison.Ordinal);
        Assert.Null(_blobs.LeaseOf(container, StateSync.Bundle));
    }
}
