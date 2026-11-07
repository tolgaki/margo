using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.MemorySearch;
using Xunit;
using Search = Margo.Cloud.State.MemorySearch.MemorySearch;

namespace Margo.Cloud.State.Tests.MemorySearch;

/// <summary>
/// Python's lifecycle fake from tests/test_memory_search.py: an in-memory record set over a private in-memory SQLite
/// connection, with the same job, block and eligibility rules. Real storage is covered by the parity scenarios in
/// cloud/contracts/state/memory-search/.
/// </summary>
internal sealed class FakeMemory : IMemorySource, IDisposable
{
    public FakeMemory(bool readOnly = false)
    {
        Conn = PyConnection.Connect(":memory:");
        ReadOnly = readOnly;
    }

    public string Account => "synthetic-memory-search";

    public PyConnection Conn { get; }

    public bool ReadOnly { get; }

    public OrderedDictionary<string, JsonObject> Records { get; } = new(StringComparer.Ordinal);

    public OrderedDictionary<string, JsonObject> Jobs { get; } = new(StringComparer.Ordinal);

    public OrderedDictionary<string, JsonObject> Blocked { get; } = new(StringComparer.Ordinal);

    public Func<string, JsonArray> LinksOf { get; set; } = _ => [];

    public Func<JsonNode?, JsonNode?, JsonNode?, JsonNode?, JsonArray>? EligibleOverride { get; set; }

    public JsonObject Add(string identity, string text, string kind = "episode", params string[] routines)
    {
        Records[identity] = new JsonObject
        {
            ["id"] = identity, ["account"] = Account, ["revision"] = 1L, ["domain"] = "user", ["kind"] = kind,
            ["status"] = "active", ["title"] = identity, ["text"] = text, ["authority"] = "user_confirmed",
            ["scope"] = "personal", ["source_refs"] = new JsonArray(), ["routines"] = PyNode.StrList(routines),
            ["entities"] = new JsonArray(),
        };
        Jobs[identity] = Job(identity, 1, "upsert");
        Blocked.Remove(identity);
        return Records[identity];
    }

    public JsonObject Revise(string identity, string text)
    {
        var record = Records[identity];
        var revision = (long)PyJson.AsInt(record["revision"]) + 1;
        record["revision"] = revision;
        record["text"] = text;
        Jobs[identity] = Job(identity, revision, "upsert");
        Blocked.Remove(identity);
        return record;
    }

    public static JsonObject Job(string identity, long revision, string operation) =>
        new() { ["memory_id"] = identity, ["revision"] = revision, ["operation"] = operation };

    public JsonArray List(JsonNode? domain = null, JsonNode? status = null) => new(Records.Values
        .Where(row => (!PyJson.Truthy(domain) || PyJson.PyEquals(row["domain"], domain))
                      && (!PyJson.Truthy(status) || PyJson.PyEquals(row["status"], status)))
        .Select(row => PyNode.Copy(row)).ToArray());

    public JsonArray Eligible(JsonNode? domain, JsonNode? routine, JsonNode? usage, JsonNode? environment)
    {
        if (EligibleOverride is { } eligible)
        {
            return eligible(domain, routine, usage, environment);
        }

        return BaseEligible(domain, routine);
    }

    public JsonArray BaseEligible(JsonNode? domain, JsonNode? routine) => new(List(domain, "active")
        .Where(row => ((JsonArray)row!["routines"]!).Count == 0 || PyObj.Contains(row["routines"], routine))
        .Select(row => PyNode.Copy(row)).ToArray());

    public JsonObject Show(JsonNode? memoryId) => PyNode.CopyObject(Records[PyNode.AsString(memoryId)!]);

    public JsonArray Links(JsonNode? memoryId) => LinksOf(PyNode.AsString(memoryId)!);

    public JsonArray ActiveLinks(JsonNode? memoryId) => Links(memoryId);

    public bool DreamIndexCurrent(JsonNode? record) => true;

    public bool WorkSourcesCurrent(JsonNode? record) => throw new PyAttributeError("'FakeMemory' object has no attribute '_work_sources_current'");

    public JsonArray PendingJobs(PyArg limit = default) =>
        new(Jobs.Values.Where(job => !PyNode.Is(job["operation"], "blocked")).Take((int)PyJson.AsInt(limit.Or(100L)))
                .Select(job => PyNode.Copy(job)).ToArray());

    public bool CompleteJob(JsonNode? memoryId, JsonNode? revision)
    {
        var identity = PyNode.AsString(memoryId)!;
        if (Jobs.TryGetValue(identity, out var job) && PyJson.PyEquals(job["revision"], revision))
        {
            Jobs.Remove(identity);
            return true;
        }

        return false;
    }

    public bool BlockJob(JsonNode? memoryId, JsonNode? revision, JsonNode? reason)
    {
        var identity = PyNode.AsString(memoryId)!;
        var record = Records[identity];
        if (!PyNode.Is(reason, "representation_exceeds_limit") || !PyJson.PyEquals(record["revision"], revision)
            || !PyNode.Is(record["status"], "active") || PyRepr.Length(MemoryStore.SearchText(record)) <= MemoryStore.MaxMemoryTextChars)
        {
            return false;
        }

        var value = (long)PyJson.AsInt(revision);
        Jobs[identity] = Job(identity, value, "blocked");
        Blocked[identity] = new JsonObject { ["memory_id"] = identity, ["revision"] = value, ["reason"] = PyNode.Copy(reason) };
        return true;
    }

    public JsonArray BlockedJobs(PyArg limit = default) =>
        new(Blocked.Values.Take((int)PyJson.AsInt(limit.Or(100L))).Select(job => PyNode.Copy(job)).ToArray());

    public void Dispose() => Conn.Close();
}

/// <summary>Library-level ports of tests/test_memory_search.py (the fake-store cases).</summary>
public sealed class MemorySearchTests
{
    internal static JsonObject UnitEncoder(JsonArray texts)
    {
        var vectors = new JsonArray();
        foreach (var text in texts)
        {
            var folded = PyRepr.CaseFold(PyNode.AsString(text)!);
            vectors.Add(folded.Contains("review", StringComparison.Ordinal) || folded.Contains("preparation", StringComparison.Ordinal)
                ? new JsonArray(1L, 0L, 0L)
                : new JsonArray(0L, 1L, 0L));
        }

        return new JsonObject { ["model_fingerprint"] = "unit-test-vectors-not-production", ["dimensions"] = 3L, ["vectors"] = vectors };
    }

    private static JsonNode? FailingEncoder(JsonArray texts) => throw new InvalidOperationException("Unexpected embedding request");

    private static List<string> Ids(JsonObject packet, string key = "results") =>
        ((JsonArray)packet[key]!).Select(row => key == "results" ? (string)row!["memory"]!["id"]! : (string)row!["id"]!).ToList();

    private static long Count(PyConnection conn, string table) => (long)conn.Execute("SELECT count(*) FROM " + table).FetchOne()![0]!;

    private static JsonArray Reasons(JsonObject packet, int index) => (JsonArray)packet["results"]![index]!["matched_by"]!;

    [Fact]
    public void Index_and_semantic_rank()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("meeting", "Leave preparation time before decision reviews");
        memory.Add("lunch", "Food and lunch choices");
        Assert.Equal(2L, PyJson.AsInt(search.Index()["indexed"]));
        var result = search.Search("avoid rushed review", mode: "semantic");
        Assert.Equal("meeting", Ids(result)[0]);
        Assert.Equal(["semantic"], Reasons(result, 0).Select(value => (string)value!));
    }

    [Fact]
    public void Forget_during_embedding_is_never_returned()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("meeting", "Preparation time");
        search.Index();
        search.Encoder = texts =>
        {
            memory.Records["meeting"]["status"] = "forgotten";
            return UnitEncoder(texts);
        };
        Assert.Empty(Ids(search.Search("review")));
        Assert.Equal(1, search.PurgeUnavailable());
        Assert.Equal(0L, PyJson.AsInt(search.Health()["vectors"]));
        Assert.Equal(0, Count(memory.Conn, "semantic_fts"));
    }

    [Fact]
    public void Forgotten_seed_cannot_supply_an_unrelated_relationship_result()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("meeting", "Review preparation");
        memory.Add("other", "Independent food context");
        memory.LinksOf = _ => [new JsonObject { ["source_id"] = "meeting", ["target_id"] = "other", ["relation"] = "related_to" }];
        search.Encoder = texts =>
        {
            memory.Records["meeting"]["status"] = "forgotten";
            memory.Records["meeting"]["revision"] = 2L;
            return UnitEncoder(texts);
        };
        Assert.Empty(Ids(search.Search("review")));
    }

    [Fact]
    public void Changed_revision_never_uses_old_vectors()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("meeting", "Preparation time");
        search.Index();
        memory.Records["meeting"]["revision"] = 2L;
        memory.Records["meeting"]["text"] = "Food and lunch choices";
        var result = search.Search("review", mode: "semantic");
        Assert.Empty(Ids(result));
        Assert.NotEmpty((JsonArray)result["warnings"]!);
    }

    [Fact]
    public void Stale_keyword_index_cannot_retrieve_removed_text()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("record", "alpha special keyword");
        search.Index();
        memory.Records["record"]["revision"] = 2L;
        memory.Records["record"]["text"] = "entirely unrelated content";
        Assert.Empty(Ids(search.Search("alpha", mode: "lexical")));
    }

    [Fact]
    public void Replayed_fts_content_is_rejected_and_rebuilt_with_current_vectors()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("record", "uniqueobsolete");
        search.Index();
        var oldText = (string)memory.Conn.Execute("SELECT content FROM semantic_fts").FetchOne()![0]!;
        memory.Records["record"]["revision"] = 2L;
        memory.Records["record"]["text"] = "beta current";
        memory.Jobs["record"] = FakeMemory.Job("record", 2, "upsert");
        search.Index();
        memory.Conn.With(() => memory.Conn.Execute("UPDATE semantic_fts SET content=? WHERE memory_id='record'", oldText));
        Assert.Empty(Ids(search.Search("uniqueobsolete", mode: "lexical")));
        Assert.Equal(1L, PyJson.AsInt(search.Index(rebuild: true)["indexed"]));
        var indexed = (string)memory.Conn.Execute("SELECT content FROM semantic_fts WHERE memory_id='record'").FetchOne()![0]!;
        Assert.DoesNotContain("uniqueobsolete", indexed, StringComparison.Ordinal);
        Assert.Equal("record", Ids(search.Search("beta", mode: "lexical"))[0]);
    }

    [Fact]
    public void Rebuild_does_not_keep_scheduling_sensitive_records_ahead_of_valid_work()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("sensitive", "Private sensitive content")["sensitivity"] = "sensitive";
        memory.Add("ordinary", "Current preparation");
        var first = search.Index(1L, true);
        Assert.Equal(1L, PyJson.AsInt(first["deleted_jobs"]));
        Assert.Equal(0L, PyJson.AsInt(first["indexed"]));
        Assert.Equal(1L, PyJson.AsInt(first["remaining_rebuild"]));
        var second = search.Index(1L, true);
        Assert.Equal(1L, PyJson.AsInt(second["indexed"]));
        Assert.Equal(0L, PyJson.AsInt(second["remaining_rebuild"]));
    }

    [Fact]
    public void Forgetting_purges_orphan_indexes_without_a_document_row()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("record", "Context");
        search.Index();
        memory.Conn.With(() => memory.Conn.Execute("DELETE FROM semantic_documents"));
        memory.Records["record"]["status"] = "forgotten";
        Assert.Equal(1, search.PurgeUnavailable());
        Assert.Equal(0L, PyJson.AsInt(search.Health()["vectors"]));
        Assert.Equal(0, Count(memory.Conn, "semantic_fts"));
    }

    [Fact]
    public void Context_rechecks_records_after_search_and_eligibility()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("record", "review preparation");
        var count = 0;
        memory.EligibleOverride = (domain, routine, _, _) =>
        {
            count++;
            var result = memory.BaseEligible(domain, routine);
            if (count == 3)
            {
                memory.Records["record"]["status"] = "forgotten";
            }

            return result;
        };
        Assert.Empty((JsonArray)search.Context("review", mode: "lexical")["entries"]!);
    }

    [Fact]
    public void Lexical_fallback_explicit_and_unindexed_memories_visible()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("review", "Acronym XYZSPEC needs review");
        search.Encoder = FailingEncoder;
        var result = search.Search("XYZSPEC \" OR *", mode: "lexical");
        Assert.Equal("review", Ids(result)[0]);
        Assert.Null(result["model_fingerprint"]);
    }

    [Fact]
    public void Model_versions_never_mix()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("meeting", "Preparation time");
        search.Index();
        search.Encoder = texts =>
        {
            var result = UnitEncoder(texts);
            result["model_fingerprint"] = "different-model";
            return result;
        };
        Assert.Empty(Ids(search.Search("review", mode: "semantic")));
        search.Index(rebuild: true);
        Assert.Single(Ids(search.Search("review", mode: "semantic")));
    }

    [Fact]
    public void Forgetting_during_index_cannot_resurrect_vectors()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("meeting", "Preparation time");
        search.Encoder = texts =>
        {
            memory.Records["meeting"]["status"] = "forgotten";
            memory.Records["meeting"]["revision"] = 2L;
            return UnitEncoder(texts);
        };
        var result = search.Index();
        Assert.Equal(0L, PyJson.AsInt(result["indexed"]));
        Assert.Equal(1L, PyJson.AsInt(result["skipped_changed"]));
        Assert.Equal(0L, PyJson.AsInt(search.Health()["vectors"]));
    }

    [Fact]
    public void Exact_provider_identity_does_not_merge_same_labels()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        var identity = new JsonObject { ["provider"] = "directory", ["scope"] = "one", ["external_id"] = "person" };
        var first = memory.Add("first", "Person context", "person");
        first["identity"] = PyNode.Copy(identity);
        first["title"] = "Dana";
        var second = memory.Add("second", "Person context", "person");
        second["identity"] = PyNode.DictWith(identity, ("scope", "two"));
        second["title"] = "Dana";
        var key = MemoryContext.IdentityKey(identity);
        Assert.Equal(["first"], Ids(search.Search("unmatched-query", mode: "lexical", entities: new JsonArray(key))));
        var packet = search.Search(key, mode: "lexical");
        var exact = ((JsonArray)packet["results"]!).Where(row => ((JsonArray)row!["matched_by"]!).Any(value => (string)value! == "exact"))
                                                     .Select(row => (string)row!["memory"]!["id"]!).ToList();
        Assert.Equal(["first"], exact);
    }

    [Fact]
    public void Explicit_entities_are_validated_and_canonicalized()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("local-test-id", "Local context");
        Assert.Equal("local-test-id", Ids(search.Search("unmatched-query", mode: "lexical", entities: new JsonArray("local-test-id")))[0]);
        var person = memory.Add("person", "Person context", "person");
        person["identity"] = new JsonObject { ["provider"] = "directory", ["scope"] = "one", ["external_id"] = "person" };
        var packet = search.Search("unmatched-query", mode: "lexical", entities: new JsonArray("identity: [ \"directory\", \"one\", \"person\" ]"));
        Assert.Equal("person", Ids(packet)[0]);
        foreach (var malformed in new[] { "Dana", "identity:not-json", "person:display name", "" })
        {
            var error = Assert.Throws<StateError>(() => search.Search("unmatched-query", mode: "lexical", entities: new JsonArray(malformed)));
            Assert.Contains("scoped", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Temporally_inapplicable_links_cannot_supply_relationship_hits()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("meeting", "Review preparation");
        memory.Add("unrelated", "Other context");
        memory.LinksOf = _ =>
        [
            new JsonObject
            {
                ["source_id"] = "meeting", ["target_id"] = "unrelated", ["relation"] = "owns",
                ["data"] = new JsonObject { ["valid_to"] = "2020-01-01T00:00:00Z" },
            },
        ];
        Assert.Equal(["meeting"], Ids(search.Search("review", mode: "lexical")));
    }

    [Fact]
    public void Context_budget_includes_serialization_and_reasons()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("constraint", "Protect focus.", "preference");
        memory.Add("meeting", string.Concat(Enumerable.Repeat("\\\n\"", 300)));
        var packet = search.Context("meeting", mode: "lexical", budgetChars: 2200L);
        Assert.Equal(PyRepr.Length(StateStore.CanonicalJson(packet)), PyJson.AsInt(packet["used_chars"]));
        Assert.True(PyJson.AsInt(packet["used_chars"]) <= 2200);
        Assert.Equal("constraint", Ids(packet, "entries")[0]);
        Assert.Contains("mandatory_constraint", ((JsonArray)packet["entries"]![0]!["selection_reasons"]!).Select(value => (string)value!));
        Assert.False((bool)packet["context_is_consent"]!);
    }

    [Fact]
    public void Mandatory_preferences_not_lost_to_similarity()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("constraint", "Never schedule outside agreed working hours", "preference", "calendar");
        memory.Add("meeting", "Preparation time");
        var packet = search.Context("review", routine: "calendar", mode: "lexical");
        Assert.Equal("constraint", Ids(packet, "entries")[0]);
        Assert.True((bool)packet["entries"]![0]!["mandatory"]!);
        Assert.DoesNotContain("constraint", Ids(search.Context("review", routine: "drafting", mode: "lexical"), "entries"));
    }

    [Fact]
    public void Saved_baseline_profile_is_context_even_without_a_keyword_match()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        var profile = memory.Add("profile", "Working hours and protected time.", "profile");
        profile["authority"] = "source_observed";
        profile["metadata"] = new JsonObject { ["imported_configuration"] = true, ["source_heading"] = "About me" };
        var packet = search.Context("unrelated query", mode: "lexical");
        Assert.Equal("profile", Ids(packet, "entries")[0]);
        Assert.True((bool)packet["entries"]![0]!["mandatory"]!);
        Assert.Equal("source_observed", (string)packet["entries"]![0]!["authority"]!);
    }

    [Fact]
    public void Mandatory_overflow_blocks_rather_than_silently_truncating()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("constraint", new string('a', 2000), "preference");
        Assert.Equal("blocked", (string)search.Context("other", mode: "lexical", budgetChars: 1000L)["status"]!);
    }

    [Fact]
    public void Invalid_vector_or_query_fails_explicitly()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("meeting", "Preparation time");
        search.Encoder = _ => new JsonObject
        {
            ["model_fingerprint"] = "bad", ["dimensions"] = 3L, ["vectors"] = new JsonArray(new JsonArray(double.NaN, 0L, 1L)),
        };
        Assert.Throws<StateError>(() => search.Index());
        Assert.Throws<StateError>(() => search.Search(""));
    }

    [Fact]
    public void Read_only_constructor_leaves_missing_index_uninitialized()
    {
        using var memory = new FakeMemory(readOnly: true);
        memory.Add("record", "Indexless lexical context");
        var search = new Search(memory, FailingEncoder);
        Assert.False(search.Initialized);
        Assert.Equal("record", Ids(search.Search("Indexless", mode: "lexical"))[0]);
        Assert.Equal("record", Ids(search.Graph("record"), "nodes")[0]);
        Assert.Equal("not_initialized", (string)search.Health()["status"]!);
        Assert.Equal(0L, PyJson.AsInt(search.Health()["vectors"]));
        Assert.Equal(0L, (long)memory.Conn.Execute("SELECT count(*) FROM sqlite_master WHERE name LIKE 'semantic_%'").FetchOne()![0]!);
    }

    [Fact]
    public void Read_only_constructor_rejects_incomplete_index_schema()
    {
        using var memory = new FakeMemory(readOnly: true);
        memory.Conn.Execute("CREATE TABLE semantic_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL)");
        var error = Assert.Throws<StateError>(() => new Search(memory, UnitEncoder));
        Assert.Contains("incomplete", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_index_constructor_only_inspects_schema()
    {
        using var memory = new FakeMemory();
        _ = new Search(memory, UnitEncoder);
        var before = memory.Conn.TotalChanges;
        var inspected = new Search(memory, UnitEncoder);
        Assert.True(inspected.Initialized);
        Assert.Equal(before, memory.Conn.TotalChanges);
    }

    [Fact]
    public void Representation_limit_boundary_and_quarantine()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        var exact = memory.Add("exact", "x");
        exact["text"] = new string('x', MemoryStore.MaxMemoryTextChars - (MemoryStore.SearchText(exact).Length - 1));
        var oversized = memory.Add("oversized", "x");
        oversized["text"] = new string('x', MemoryStore.MaxMemoryTextChars - (MemoryStore.SearchText(oversized).Length - 1) + 1);
        Assert.Equal(MemoryStore.MaxMemoryTextChars, MemoryStore.SearchText(exact).Length);
        Assert.Equal(MemoryStore.MaxMemoryTextChars + 1, MemoryStore.SearchText(oversized).Length);
        var result = search.Index();
        Assert.Equal(1L, PyJson.AsInt(result["indexed"]));
        Assert.Equal("partial", (string)result["status"]!);
        Assert.Equal(["oversized"], ((JsonArray)result["blocked_ids"]!).Select(value => (string)value!));
        Assert.Equal(0L, PyJson.AsInt(result["remaining_jobs"]));
    }

    [Fact]
    public void Oversized_job_does_not_consume_page_or_starve_short_job()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("oversized", "x")["text"] = new string('x', MemoryStore.MaxMemoryTextChars + 1);
        memory.Add("short", "Current preparation");
        var result = search.Index(1L);
        Assert.Equal(1L, PyJson.AsInt(result["indexed"]));
        Assert.Equal(["oversized"], ((JsonArray)result["newly_blocked_ids"]!).Select(value => (string)value!));
        Assert.Equal(["oversized"], ((JsonArray)result["blocked_ids"]!).Select(value => (string)value!));
        Assert.Empty(memory.PendingJobs());
    }

    [Fact]
    public void Blocked_job_recovers_only_after_new_revision()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("oversized", "x")["text"] = new string('x', MemoryStore.MaxMemoryTextChars + 1);
        Assert.Equal(["oversized"], ((JsonArray)search.Index()["blocked_ids"]!).Select(value => (string)value!));
        Assert.Empty((JsonArray)search.Index()["newly_blocked_ids"]!);
        Assert.Empty((JsonArray)search.Index(rebuild: true)["newly_blocked_ids"]!);
        memory.Revise("oversized", "Current preparation");
        var recovered = search.Index();
        Assert.Equal(1L, PyJson.AsInt(recovered["indexed"]));
        Assert.Empty((JsonArray)recovered["blocked_ids"]!);
        Assert.Equal("ready", (string)recovered["status"]!);
    }

    [Fact]
    public void Transient_encoder_failure_preserves_retryable_queue()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, _ => throw new StateError("runtime unavailable"));
        memory.Add("short", "Current preparation");
        var error = Assert.Throws<StateError>(() => search.Index());
        Assert.Contains("runtime unavailable", error.Message, StringComparison.Ordinal);
        Assert.Equal(["short"], memory.PendingJobs().Select(job => (string)job!["memory_id"]!));
        Assert.Empty(memory.BlockedJobs());
    }

    [Fact]
    public void Rebuild_drains_delete_without_loading_encoder()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, FailingEncoder);
        var record = memory.Add("forgotten", "Old context");
        record["status"] = "forgotten";
        record["revision"] = 2L;
        memory.Jobs["forgotten"] = FakeMemory.Job("forgotten", 2, "delete");
        var result = search.Index(rebuild: true);
        Assert.Equal(1L, PyJson.AsInt(result["deleted_jobs"]));
        Assert.Equal(0L, PyJson.AsInt(result["indexed"]));
        Assert.Equal(0L, PyJson.AsInt(result["remaining_jobs"]));
        Assert.Equal(0L, PyJson.AsInt(result["remaining_rebuild"]));
    }

    // ------------------------------------------------------------------------------ C# seams (CPython-measured values)

    [Fact]
    public void Python_sum_matches_cpython_for_mixed_ints_and_floats()
    {
        // Measured with CPython 3.13: sum(values).
        Assert.Equal(-1.0000000002940896e+16, Sum(1.0, -0.0550075046336487, -1e16, new BigInteger(-2940895)));
        Assert.Equal(1.0000000012749528e+16, Sum(0.7297289715877373, 1e16, new BigInteger(12749529)));
        Assert.Equal(3.5, Sum(BigInteger.One, 1e16, -1e16, 3.5));
        Assert.Equal(9.223372036854776e+18, Sum(BigInteger.Pow(2, 63), 1.0, 2.0));
        Assert.Equal(1.0, Sum(Enumerable.Repeat((object)0.1, 10).ToArray()));
        Assert.Equal(double.PositiveInfinity, Sum(1e308, 1e308));
        Assert.Equal(BigInteger.Pow(2, 64) + 1, Search.PySum([BigInteger.Pow(2, 63), BigInteger.Pow(2, 63), BigInteger.One]));
        Assert.Equal(BigInteger.Zero, Search.PySum([]));
    }

    private static double Sum(params object[] values) => (double)Search.PySum(values);

    [Theory]
    [InlineData("[3,4]", "9a99193fcdcc4c3f")]
    [InlineData("[1.5,-2.25,0.125]", "0eda0d3f15c754bfbe223d3d")]
    [InlineData("[4611686018427387904,4611686018427387904,1.5]", "f304353ff304353fb6c38720")]
    public void Vector_bytes_match_cpython_struct_pack(string vector, string hex)
    {
        var value = (JsonArray)PyJson.Loads(vector)!;
        Assert.Equal(hex, Convert.ToHexStringLower(Search.VectorBytes(value, value.Count)));
    }

    [Fact]
    public void Read_snapshot_never_ends_a_callers_transaction()
    {
        using var memory = new FakeMemory();
        var search = new Search(memory, UnitEncoder);
        memory.Add("record", "Context");
        memory.Conn.Execute("BEGIN");
        search.Explain("record");
        Assert.True(memory.Conn.InTransaction);
        memory.Conn.Rollback();
        search.Explain("record");
        Assert.False(memory.Conn.InTransaction);
    }

    [Fact]
    public void Connected_context_builds_more_than_once_like_python()
    {
        // Python's ConnectedContext keeps gaps/edges on the instance, so a second build() on the same context works and
        // returns the accumulated diagnostics (both results hold the same list object).
        using var memory = new FakeMemory();
        memory.Add("record", "Context");
        var context = new ConnectedContext(memory);
        var first = context.Build(["record"]);
        context.Diagnose("record", "origin-a");
        var second = context.Build(["record"]);
        context.Diagnose("record", "origin-b");
        Assert.Equal(["record"], ((JsonArray)second["nodes"]!).Select(node => (string)node!["id"]!));
        Assert.Equal(["origin-a", "origin-b"], ((JsonArray)second["gaps"]!).Select(gap => (string)gap!["origin_id"]!));
        Assert.Equal(["origin-a"], ((JsonArray)first["gaps"]!).Select(gap => (string)gap!["origin_id"]!));
        Assert.Same(context.Gaps, second["gaps"]);
    }

    [Fact]
    public void Real_store_without_runtime_reports_the_local_embedding_error()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore("search-fixture@example.com", root.State);
        var search = new Search(store);
        var key = store.RecordId("user", "fixture");
        store.Put("fixture", new JsonObject
        {
            ["domain"] = "user", ["kind"] = "episode", ["title"] = "Fixture", ["text"] = "Review preparation",
            ["authority"] = "user_confirmed", ["scope"] = "personal",
            ["source_refs"] = new JsonArray(new JsonObject { ["kind"] = "user_statement", ["ref"] = "conversation:fixture" }),
        }, "active", 1L, Memory.MemoryStoreTests.Evidence(key));
        var error = Assert.Throws<StateError>(() => search.Index());
        Assert.StartsWith("Local semantic embeddings unavailable: missing_runtime:", error.Message, StringComparison.Ordinal);
        Assert.Equal(key, Ids(search.Search("review", mode: "lexical"))[0]);
        Assert.Equal(1L, PyJson.AsInt(search.Health()["pending_jobs"]));
    }
}
