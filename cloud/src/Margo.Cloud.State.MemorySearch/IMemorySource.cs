using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;

namespace Margo.Cloud.State.MemorySearch;

/// <summary>
/// The memory-store surface <c>memory_search.py</c> and <c>memory_context.py</c> call. Python duck-types the
/// <c>memory</c> argument (its tests pass a fake store); this interface is that seam. <see cref="MemoryStoreSource"/>
/// adapts the real <see cref="MemoryStore"/>. Values are Python values as <see cref="JsonNode"/>, as in State.Memory.
/// </summary>
public interface IMemorySource
{
    /// <summary><c>memory.account</c>.</summary>
    string Account { get; }

    /// <summary><c>memory.conn</c>.</summary>
    PyConnection Conn { get; }

    /// <summary><c>getattr(memory, "read_only", False)</c>.</summary>
    bool ReadOnly { get; }

    /// <summary><c>memory.list(domain=, status=)</c>.</summary>
    JsonArray List(JsonNode? domain = null, JsonNode? status = null);

    /// <summary><c>memory.eligible(domain=, routine=, usage=, environment=)</c> (<paramref name="usage"/> is passed explicitly).</summary>
    JsonArray Eligible(JsonNode? domain, JsonNode? routine, JsonNode? usage, JsonNode? environment);

    /// <summary><c>memory.show(memory_id)</c>.</summary>
    JsonObject Show(JsonNode? memoryId);

    /// <summary><c>memory.links(memory_id)</c>.</summary>
    JsonArray Links(JsonNode? memoryId);

    /// <summary>
    /// <c>getattr(memory, "active_links", memory.links)(memory_id)</c>: a source without temporal/source-safe links
    /// returns <see cref="Links"/>.
    /// </summary>
    JsonArray ActiveLinks(JsonNode? memoryId);

    /// <summary><c>memory.dream_index_current(record)</c>.</summary>
    bool DreamIndexCurrent(JsonNode? record);

    /// <summary><c>memory._work_sources_current(record)</c>.</summary>
    bool WorkSourcesCurrent(JsonNode? record);

    /// <summary><c>memory.pending_jobs(limit=)</c>.</summary>
    JsonArray PendingJobs(PyArg limit = default);

    /// <summary><c>memory.complete_job(memory_id, revision)</c>.</summary>
    bool CompleteJob(JsonNode? memoryId, JsonNode? revision);

    /// <summary><c>memory.block_job(memory_id, revision, reason)</c>.</summary>
    bool BlockJob(JsonNode? memoryId, JsonNode? revision, JsonNode? reason);

    /// <summary><c>memory.blocked_jobs(limit=)</c>.</summary>
    JsonArray BlockedJobs(PyArg limit = default);
}

/// <summary>The real store behind <see cref="IMemorySource"/>.</summary>
public sealed class MemoryStoreSource : IMemorySource
{
    public MemoryStoreSource(MemoryStore store)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>The wrapped store.</summary>
    public MemoryStore Store { get; }

    public string Account => Store.Account;

    public PyConnection Conn => Store.Conn;

    public bool ReadOnly => Store.ReadOnly;

    public JsonArray List(JsonNode? domain = null, JsonNode? status = null) => Store.List(domain, status);

    public JsonArray Eligible(JsonNode? domain, JsonNode? routine, JsonNode? usage, JsonNode? environment) =>
        Store.Eligible(domain, routine, PyArg.Of(usage), environment);

    public JsonObject Show(JsonNode? memoryId) => Store.Show(memoryId);

    public JsonArray Links(JsonNode? memoryId) => Store.Links(memoryId);

    public JsonArray ActiveLinks(JsonNode? memoryId) => Store.ActiveLinks(memoryId);

    public bool DreamIndexCurrent(JsonNode? record) => Store.DreamIndexCurrent(record);

    public bool WorkSourcesCurrent(JsonNode? record) => Store.WorkSourcesCurrent(record);

    public JsonArray PendingJobs(PyArg limit = default) => Store.PendingJobs(limit);

    public bool CompleteJob(JsonNode? memoryId, JsonNode? revision) => Store.CompleteJob(memoryId, revision);

    public bool BlockJob(JsonNode? memoryId, JsonNode? revision, JsonNode? reason) => Store.BlockJob(memoryId, revision, reason);

    public JsonArray BlockedJobs(PyArg limit = default) => Store.BlockedJobs(limit);
}
