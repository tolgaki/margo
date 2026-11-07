using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Proactive;
using Xunit;

namespace Margo.Cloud.State.Tests.Proactive;

/// <summary>
/// Library-level ports of tests/test_proactive_state.py (StateTests) and of the proactive_state calls that
/// tests/test_task_runs.py, tests/test_user_journeys.py and tests/test_memory_dream.py make. CLI-observable cases
/// are also judged against Python by cloud/contracts/state/proactive/*.json.
/// </summary>
public sealed class ProactiveStateTests : IDisposable
{
    private const string Start = "2026-01-01T00:00:00Z";
    private const string End = "2026-01-02T00:00:00Z";
    private readonly TempRoot _root;
    private PyConnection _conn;

    public ProactiveStateTests()
    {
        _root = TempRoot.Create();
        _conn = StateStore.Connect("tenant:user", _root.State);
        ProactiveState.Initialize(_conn);
    }

    public void Dispose()
    {
        _conn.Close();
        _root.Dispose();
    }

    private static JsonObject Json(string text) => (JsonObject)StateStore.ParseJson(text)!;

    private (int Code, string Stdout, string Stderr) Cli(params string[] args) =>
        ScriptRunner.Run(new ProactiveStateScript(), ["--account", "tenant:user", "--state-dir", _root.State, .. args]);

    private JsonObject Add(string stableId = "one", string revision = "v1") =>
        ProactiveState.QueueAdd(_conn, new JsonObject { ["id"] = stableId, ["revision"] = revision, ["title"] = "Example" });

    private string StartAttempt(Action<JsonObject>? overrides = null)
    {
        var data = new JsonObject
        {
            ["family"] = "mail", ["scope"] = new JsonObject { ["folder"] = "inbox" },
            ["window"] = new JsonObject { ["start"] = Start, ["end"] = End }, ["run_id"] = "run-1",
            ["capability"] = "messages.list", ["query_version"] = "1", ["cadence_seconds"] = 3600,
        };
        overrides?.Invoke(data);
        return (string)ProactiveState.CoverageStart(_conn, data)["attempt_id"]!;
    }

    private JsonObject Page(string attempt, bool final = true, int page = 1, JsonArray? observations = null, string? continuation = null)
    {
        var data = new JsonObject { ["page"] = page, ["observations"] = observations ?? [], ["final"] = final };
        if (continuation is not null)
        {
            data["continuation"] = continuation;
        }

        return ProactiveState.CoveragePage(_conn, attempt, data);
    }

    private JsonObject Complete(string attempt) => ProactiveState.CoverageFinish(_conn, attempt, new JsonObject
    {
        ["status"] = "complete", ["checkpoint_at"] = End, ["watermark"] = End, ["delta_token"] = "opaque-secret-token",
    });

    private string Available(string batch, JsonArray? ids = null, Action<JsonObject>? overrides = null)
    {
        var data = new JsonObject
        {
            ["content"] = "A durable brief", ["ids"] = ids ?? ["one"], ["status"] = "available",
            ["receipt"] = new JsonObject { ["kind"] = "local" },
        };
        overrides?.Invoke(data);
        return (string)ProactiveState.PublicationRecord(_conn, batch, data)["publication_id"]!;
    }

    private JsonObject Source(int index = 0) => (JsonObject)ProactiveState.CoverageStatus(_conn)[index]!;

    private long Count(string sql) => (long)_conn.Execute(sql).FetchOne()![0]!;

    private static string Message(Action action) => Assert.ThrowsAny<StateError>(action).Message;

    [Fact]
    public void Python_ints_beyond_64_bits_keep_their_value_in_payloads_digests_and_pages()
    {
        // Review regression: JsonNode.DeepClone re-serialized BigInteger values as {"IsEven":...} objects, so the
        // stored payload, the computed revision/item_key and the page fingerprint all differed from Python.
        // Expected digests measured with CPython 3.13 (hashlib.sha256 of margo_store.canonical_json).
        var added = ProactiveState.QueueAdd(_conn, new JsonObject { ["id"] = "big", ["count"] = PyJson.Int(BigInteger.Pow(10, 30)) });
        Assert.Equal("f0c2837f50cca668cdaf115d7804e595920281f366b32b669b64a979b3449567", (string)added["revision"]!);
        var item = _conn.Execute("SELECT item_key,payload FROM proactive_items WHERE stable_id='big'").FetchOne()!;
        Assert.Equal("322fea145f0376ec6d6e842dd966d2aa05295a4f8114531c6aec7849ae804693", item["item_key"]);
        Assert.Contains("\"count\":1000000000000000000000000000000,", (string)item["payload"]!, StringComparison.Ordinal);

        var attempt = StartAttempt();
        Page(attempt, observations: [new JsonObject { ["id"] = "m", ["revision"] = "1", ["size"] = PyJson.Int(-BigInteger.Pow(2, 64)) }]);
        Assert.Equal("{\"id\":\"m\",\"revision\":\"1\",\"size\":-18446744073709551616}",
                     _conn.Execute("SELECT payload FROM proactive_observations").FetchOne()![0]);
        Assert.Equal("83b9137e55ef78c4229ccefd9fc3562512f00072e6d29d2de331d8e094fe5a3c",
                     _conn.Execute("SELECT digest FROM proactive_pages").FetchOne()![0]);

        var value = new JsonObject { ["a"] = new JsonArray(PyJson.Int(BigInteger.Pow(2, 63)), 1L, 1.5, "x", true, null) };
        var copy = Py.Copy(value);
        Assert.NotSame(value, copy);
        Assert.Equal("{\"a\":[9223372036854775808,1,1.5,\"x\",true,null]}", StateStore.CanonicalJson(copy));
    }

    [Fact]
    public void Reobserved_timestamp_is_not_content_revision()
    {
        var first = StartAttempt();
        Page(first, observations: [new JsonObject { ["id"] = "same", ["revision"] = "v1", ["title"] = "Unchanged", ["observed_at"] = "2026-01-01T10:00:00Z" }]);
        Complete(first);
        var second = StartAttempt();
        Page(second, observations: [new JsonObject { ["id"] = "same", ["revision"] = "v1", ["title"] = "Unchanged", ["observed_at"] = "2026-01-01T11:00:00Z" }]);
        var row = _conn.Execute("SELECT payload,observed_at FROM proactive_observations").FetchOne()!;
        Assert.False(Json((string)row["payload"]!).ContainsKey("observed_at"));
        Assert.Equal(StateStore.ValidateTimestamp("2026-01-01T11:00:00Z"), row["observed_at"]);
    }

    [Fact]
    public void Query_change_does_not_reuse_delta_token_or_lose_checkpoint()
    {
        var first = StartAttempt();
        Page(first);
        Complete(first);
        var second = StartAttempt(data => data["query_version"] = "2");
        var info = Json(Cli("coverage-resume", "--attempt", second).Stdout);
        Assert.True((bool)info["resync_required"]!);
        Assert.Null(info["delta_token"]);
        Assert.Equal(StateStore.ValidateTimestamp(End), (string?)info["checkpoint_at"]);
        Page(second);
        Complete(second);
        var old = Json(Cli("coverage-resume", "--attempt", first).Stdout);
        Assert.True((bool)old["resync_required"]!);
        Assert.Null(old["delta_token"]);
        var current = Json(Cli("coverage-resume", "--attempt", second).Stdout);
        Assert.False((bool)current["resync_required"]!);
        Assert.Equal("opaque-secret-token", (string?)current["delta_token"]);
    }

    [Fact]
    public void Accounts_are_isolated()
    {
        using var other = StateStore.Connect("other-tenant:user", _root.State);
        ProactiveState.Initialize(other);
        Add();
        Assert.Equal(0L, other.Execute("SELECT count(*) FROM proactive_items").FetchOne()![0]);
    }

    [Fact]
    public void No_account_status_is_setup_needed_and_writes_fail()
    {
        using (TempRoot.With(("MARGO_ACCOUNT", null)))
        {
            var unconfigured = _root.Combine("unconfigured");
            var (code, stdout, _) = ScriptRunner.Run(new ProactiveStateScript(), ["--state-dir", unconfigured, "status"]);
            Assert.Equal(0, code);
            Assert.Equal("setup-needed", (string?)Json(stdout)["status"]);
            Assert.False(Directory.Exists(unconfigured));
            Assert.Equal(2, ScriptRunner.Run(new ProactiveStateScript(), ["queue-add", "--json", "{\"id\":\"no-account\"}"]).Code);
        }
    }

    [Fact]
    public void Invalid_json_input_and_legacy_cursor_timestamps_are_refused()
    {
        foreach (var (text, message) in new[]
                 {
                     ("{\"id\":\"one\",\"id\":\"two\"}", "duplicate JSON object key"), ("{\"number\":NaN}", "non-finite JSON number"),
                     ("{", "invalid JSON"), ("[1]", "JSON input must be an object"),
                 })
        {
            var (code, _, stderr) = Cli("queue-add", "--json", text);
            Assert.Equal(2, code);
            Assert.Equal("ERROR: " + message + "\n", stderr);
        }

        Assert.Equal(2, Cli("cursor-set", "sweep", "2026-01-01").Code);
        Assert.Equal(0L, Count("SELECT count(*) FROM proactive_legacy_cursors"));
        Assert.Equal("value must contain finite JSON data",
                     Message(() => ProactiveState.QueueAdd(_conn, new JsonObject { ["id"] = "x", ["value"] = double.NaN })));
    }

    [Fact]
    public void Missing_tables_and_schema_versions_are_never_recreated()
    {
        _conn.Execute("DROP TABLE proactive_imports");
        Assert.Equal("incomplete proactive schema; refusing to recreate missing state", Message(() => ProactiveState.Initialize(_conn)));
        Assert.Null(_conn.Execute("SELECT name FROM sqlite_master WHERE name='proactive_imports'").FetchOne());
        _conn.Execute(ProactiveState.Schema[^2]);
        _conn.With(() => _conn.Execute("DELETE FROM proactive_meta"));
        Assert.Equal("unsupported/missing proactive schema version; explicit migration required", Message(() => ProactiveState.Initialize(_conn)));
        Assert.Equal(0L, Count("SELECT count(*) FROM proactive_meta"));
    }

    [Fact]
    public void Validate_schema_allows_only_an_empty_store_when_asked()
    {
        using var fresh = StateStore.Connect("fresh:user", _root.State);
        Assert.False(ProactiveState.ValidateSchema(fresh, allowEmpty: true));
        Assert.Equal("incomplete proactive schema; refusing to recreate missing state", Message(() => ProactiveState.ValidateSchema(fresh)));
        ProactiveState.Initialize(fresh);
        Assert.True(ProactiveState.ValidateSchema(fresh));
        var schema = fresh.Execute("SELECT sql FROM sqlite_master WHERE name='proactive_sources'").FetchOne()![0];
        Assert.Equal(ProactiveState.Schema[5].Replace("IF NOT EXISTS ", "", StringComparison.Ordinal), schema);
    }

    [Fact]
    public void Legacy_migration_replays_and_keeps_hints_unconfirmed()
    {
        var legacy = _root.Combine("legacy");
        Directory.CreateDirectory(legacy);
        var documents = new Dictionary<string, string>
        {
            ["queue.json"] = "[{\"id\": \"one\", \"title\": \"old\"}]",
            ["inflight.json"] = "[{\"id\": \"one\", \"title\": \"old\", \"batch\": \"old-batch\"}, {\"id\": \"two\", \"title\": \"interrupted\", \"drained_at\": \"" + End + "\"}]",
            ["surfaced.json"] = "{\"one\": {\"tier\": \"sweep\", \"ts\": \"" + End + "\"}}",
            ["cursors.json"] = "{\"sweep\": \"" + End + "\"}",
        };
        foreach (var (name, text) in documents)
        {
            File.WriteAllText(Path.Combine(legacy, name), text);
        }

        var first = ProactiveState.ImportLegacy(_conn, legacy);
        Assert.Equal(2L, (long)first["counts"]!["queued"]!);
        _conn.Close();
        _conn = StateStore.Connect("tenant:user", _root.State);
        ProactiveState.Initialize(_conn);
        Assert.True((bool)ProactiveState.ImportLegacy(_conn, legacy)["replayed"]!);
        Assert.Equal(2L, Count("SELECT count(*) FROM proactive_items"));
        Assert.Equal(0L, Count("SELECT count(*) FROM proactive_sources"));
        Assert.Equal(0L, Count("SELECT count(*) FROM proactive_publications"));
        foreach (var (name, text) in documents)
        {
            Assert.Equal(text, File.ReadAllText(Path.Combine(legacy, name)));
        }

        File.WriteAllText(Path.Combine(legacy, "queue.json"), "[{\"id\":\"three\"}]");
        File.WriteAllText(Path.Combine(legacy, "cursors.json"), "{\"sweep\":\"not-a-time\"}");
        Assert.ThrowsAny<StateError>(() => ProactiveState.ImportLegacy(_conn, legacy));
        Assert.Equal(2L, Count("SELECT count(*) FROM proactive_items"));
        foreach (var malformed in new[] { "{", "{}", "[17]" })
        {
            File.WriteAllText(Path.Combine(legacy, "queue.json"), malformed);
            Assert.ThrowsAny<StateError>(() => ProactiveState.ImportLegacy(_conn, legacy));
        }

        File.WriteAllText(Path.Combine(legacy, "queue.json"), "[]");
        File.WriteAllText(Path.Combine(legacy, "cursors.json"), "{\"x\":\"a\",\"x\":\"b\"}");
        Assert.Equal("duplicate JSON object key", Message(() => ProactiveState.ImportLegacy(_conn, legacy)));
        Assert.Equal("legacy directory does not exist", Message(() => ProactiveState.ImportLegacy(_conn, _root.Combine("missing"))));
    }

    [Fact]
    public void Revision_aware_dedupe_and_cli_seen()
    {
        Assert.True((bool)Add()["added"]!);
        Assert.False((bool)Add()["added"]!);
        Assert.True((bool)Add(revision: "v2")["added"]!);
        Assert.Equal("changed item content requires a new revision", Message(() => ProactiveState.QueueAdd(_conn, new JsonObject
        {
            ["id"] = "one", ["revision"] = "v2", ["title"] = "material change",
        })));
        Assert.Equal(1, Cli("seen", "one", "--revision", "v1").Code);
        var batch = (JsonObject)ProactiveState.QueueDrain(_conn, limit: 1)[0]!;
        var receipt = (string)ProactiveState.PublicationRecord(_conn, (string)batch["batch"]!, new JsonObject
        {
            ["content"] = "exact version", ["item_keys"] = new JsonArray((string)batch["item_key"]!), ["status"] = "available",
            ["receipt"] = new JsonObject { ["kind"] = "local" },
        })["publication_id"]!;
        ProactiveState.QueueAck(_conn, receipt, (string)batch["batch"]!);
        Assert.Equal(0, Cli("seen", "one", "--revision", (string)batch["revision"]!).Code);
        Assert.Equal(1, Cli("seen", "one", "--revision", "v3").Code);
        Assert.Equal(2, Cli("mark", "one").Code);
    }

    [Fact]
    public async Task Overlapping_anchors_cannot_steal_live_batches()
    {
        for (var index = 0; index < 12; index++)
        {
            Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        JsonArray Drain(string owner)
        {
            using var connection = StateStore.Connect("tenant:user", _root.State);
            ProactiveState.Initialize(connection);
            return ProactiveState.QueueDrain(connection, owner, limit: 6);
        }

        var batches = await Task.WhenAll(Task.Run(() => Drain("anchor-one"), TestContext.Current.CancellationToken),
                                          Task.Run(() => Drain("anchor-two"), TestContext.Current.CancellationToken));
        Assert.Equal([6, 6], batches.Select(batch => batch.Count));
        var keys = batches.Select(batch => batch.Select(item => (string)item!["item_key"]!).ToHashSet()).ToArray();
        Assert.Empty(keys[0].Intersect(keys[1]));
        Assert.NotEqual((string)batches[0][0]!["batch"]!, (string)batches[1][0]!["batch"]!);
        Assert.Empty(ProactiveState.QueueDrain(_conn, "third"));
        Assert.Equal("batch owner mismatch", Message(() => ProactiveState.QueueRelease(_conn, (string)batches[0][0]!["batch"]!, "wrong-owner")));
    }

    [Fact]
    public void Interruption_release_expiry_and_stale_receipt()
    {
        Add();
        var first = (JsonObject)ProactiveState.QueueDrain(_conn, "first")[0]!;
        var firstBatch = (string)first["batch"]!;
        var receipt = Available(firstBatch);
        _conn.With(() => _conn.Execute("UPDATE proactive_batches SET expires_at=?", StateStore.ValidateTimestamp(End)));
        var second = (JsonObject)ProactiveState.QueueDrain(_conn, "second")[0]!;
        Assert.NotEqual(firstBatch, (string)second["batch"]!);
        Assert.ThrowsAny<StateError>(() => ProactiveState.QueueAck(_conn, receipt, firstBatch));
        Assert.ThrowsAny<StateError>(() => ProactiveState.QueueRenew(_conn, firstBatch, "first", 900));
        Assert.Equal(1L, (long)ProactiveState.QueueRelease(_conn, (string)second["batch"]!, "second")["released"]!);
        Assert.Equal(0L, (long)ProactiveState.QueueRelease(_conn, (string)second["batch"]!, "second")["released"]!);
        Assert.Single(ProactiveState.QueueDrain(_conn, "third"));
    }

    [Fact]
    public void Receipt_is_required_only_actual_subset_acks_and_review_is_separate()
    {
        Add();
        Add("two");
        var batch = (string)ProactiveState.QueueDrain(_conn)[0]!["batch"]!;
        var prepared = (string)ProactiveState.PublicationRecord(_conn, batch, new JsonObject
        {
            ["id"] = "output-1", ["content"] = "Only one item", ["ids"] = new JsonArray("one"), ["status"] = "prepared",
        })["publication_id"]!;
        Assert.ThrowsAny<StateError>(() => ProactiveState.QueueAck(_conn, prepared, batch));
        Assert.ThrowsAny<StateError>(() => ProactiveState.PublicationReview(_conn, prepared));
        ProactiveState.PublicationPublish(_conn, prepared, new JsonObject { ["status"] = "available", ["receipt"] = new JsonObject { ["kind"] = "local" } });
        Assert.ThrowsAny<StateError>(() => ProactiveState.QueueAck(_conn, prepared, batch, ids: ["two"]));
        Assert.Equal(1L, (long)ProactiveState.QueueAck(_conn, prepared, batch)["acked"]!);
        Assert.Equal(0L, (long)ProactiveState.QueueAck(_conn, prepared, batch)["acked"]!);
        var status = ProactiveState.StatusReport(_conn);
        Assert.Equal(1L, (long)status["in_flight"]!);
        Assert.Equal(1L, (long)status["publications"]!["available"]!);
        Assert.False(((JsonObject)status["publications"]!).ContainsKey("reviewed"));
        ProactiveState.PublicationReview(_conn, prepared);
        Assert.Equal(1L, (long)ProactiveState.StatusReport(_conn)["publications"]!["reviewed"]!);
        Assert.Equal("Only one item", (string?)Json(Cli("publication-show", prepared).Stdout)["content"]);
        var listing = (JsonArray)StateStore.ParseJson(Cli("publication-list").Stdout)!;
        Assert.Equal(prepared, (string?)listing[0]!["publication_id"]);
        Assert.Equal("reviewed", (string?)listing[0]!["status"]);
    }

    [Fact]
    public void Publication_replay_is_immutable_and_host_receipts_are_validated()
    {
        Add();
        var batch = (string)ProactiveState.QueueDrain(_conn)[0]!["batch"]!;
        JsonObject Data() => new()
        {
            ["id"] = "durable-output", ["content"] = "sent", ["ids"] = new JsonArray("one"), ["status"] = "published",
            ["receipt"] = new JsonObject { ["kind"] = "host", ["receipt_id"] = "host-id", ["location"] = "host://output/1", ["published_at"] = End },
        };
        ProactiveState.PublicationRecord(_conn, batch, Data());
        Assert.True((bool)ProactiveState.PublicationRecord(_conn, batch, Data())["replayed"]!);
        foreach (var (field, value) in new (string, JsonNode)[] { ("content", "different"), ("ids", new JsonArray("not-included")) })
        {
            var data = Data();
            data[field] = value;
            Assert.ThrowsAny<StateError>(() => ProactiveState.PublicationRecord(_conn, batch, data));
        }

        Assert.ThrowsAny<StateError>(() => Available(batch, overrides: data =>
        {
            data["status"] = "published";
            data["receipt"] = new JsonObject { ["kind"] = "host", ["receipt_id"] = "missing-location" };
        }));
        Assert.Equal("included items must not contain duplicates", Message(() => Available(batch, ids: ["one", "one"])));
    }

    [Fact]
    public void Partial_failure_blocked_checkpoint_and_recovery()
    {
        var successful = StartAttempt();
        Page(successful);
        Complete(successful);
        var initial = Source();
        var checkpoint = (string?)initial["checkpoint_at"];
        var failed = StartAttempt();
        ProactiveState.CoverageFinish(_conn, failed, Json("{\"status\":\"failed\",\"error_class\":\"expired_token\",\"retry_after_seconds\":60}"));
        var state = Source();
        Assert.Equal(checkpoint, (string?)state["checkpoint_at"]);
        Assert.Equal((string?)initial["last_successful_coverage_at"], (string?)state["last_successful_coverage_at"]);
        Assert.Equal(1L, (long)state["consecutive_failures"]!);
        Assert.False((bool)state["has_delta_token"]!);
        Assert.False((bool)ProactiveState.StatusReport(_conn)["all_clear"]!);
        Assert.DoesNotContain("opaque-secret-token", ProactiveState.StatusReport(_conn).ToJsonString(), StringComparison.Ordinal);
        Assert.False((bool)state["escalated"]!);
        _conn.With(() => _conn.Execute("UPDATE proactive_sources SET failure_started_at=?", StateStore.ValidateTimestamp(End)));
        var blocked = StartAttempt();
        ProactiveState.CoverageFinish(_conn, blocked, Json("{\"status\":\"blocked\",\"error_class\":\"access_denied\"}"));
        Assert.Equal(2L, (long)Source()["consecutive_failures"]!);
        var recovery = StartAttempt();
        Page(recovery);
        Complete(recovery);
        var recovered = Source();
        Assert.Equal(0L, (long)recovered["consecutive_failures"]!);
        Assert.NotNull(recovered["recovered_at"]);
        Assert.Equal(1L, (long)recovered["episode"]!);
        Assert.Equal(["degraded", "escalated", "recovered"],
                     _conn.Execute("SELECT transition FROM proactive_health_events").Select(row => (string)row[0]!).Order(StringComparer.Ordinal));
        Assert.True((bool)ProactiveState.StatusReport(_conn)["all_clear"]!);
    }

    [Fact]
    public void Partial_pages_are_atomic_and_cannot_claim_completion()
    {
        var attempt = StartAttempt();
        Page(attempt, final: false, observations: [new JsonObject { ["id"] = "a", ["revision"] = "1" }], continuation: "opaque-next");
        Assert.ThrowsAny<StateError>(() => Complete(attempt));
        Assert.Null(Source()["checkpoint_at"]);
        Assert.ThrowsAny<StateError>(() => Page(attempt, page: 3));
        Assert.True((bool)Page(attempt, final: false, observations: [new JsonObject { ["id"] = "a", ["revision"] = "1" }], continuation: "opaque-next")["replayed"]!);
        Assert.Equal("changed source observation requires a new revision", Message(() => Page(attempt, page: 2, observations:
        [
            new JsonObject { ["id"] = "b", ["revision"] = "1" }, new JsonObject { ["id"] = "a", ["revision"] = "1", ["changed"] = true },
        ])));
        Assert.Equal(1L, Count("SELECT count(*) FROM proactive_observations"));
        ProactiveState.CoverageFinish(_conn, attempt, Json("{\"status\":\"partial\"}"));
        var resumed = StartAttempt(data =>
        {
            data["resume_attempt"] = attempt;
            data["run_id"] = "next-run";
        });
        Page(resumed, page: 2, observations: [new JsonObject { ["id"] = "a", ["revision"] = "2" }]);
        Complete(resumed);
        Assert.Equal(2L, (long)Source()["pagination"]!["pages"]!);
        Assert.Equal(2L, Count("SELECT count(*) FROM proactive_observations"));
    }

    [Fact]
    public void Empty_search_is_not_absence_and_sources_are_independent()
    {
        var mail = StartAttempt();
        var calendar = StartAttempt(data =>
        {
            data["family"] = "calendar";
            data["scope"] = "calendar-id";
        });
        var search = StartAttempt(data =>
        {
            data["family"] = "teams";
            data["scope"] = "channel-id";
            data["kind"] = "search";
        });
        Page(mail);
        Complete(mail);
        Page(search);
        Assert.ThrowsAny<StateError>(() => Complete(search));
        ProactiveState.CoverageFinish(_conn, search, Json("{\"status\":\"unknown\"}"));
        ProactiveState.CoverageFinish(_conn, calendar, Json("{\"status\":\"failed\",\"error_class\":\"RAW TOKEN secret@example.com\"}"));
        var sources = ProactiveState.CoverageStatus(_conn).ToDictionary(source => (string)source!["family"]!, source => (JsonObject)source!);
        Assert.NotNull(sources["mail"]["checkpoint_at"]);
        Assert.Null(sources["calendar"]["checkpoint_at"]);
        Assert.Null(sources["teams"]["checkpoint_at"]);
        Assert.DoesNotContain("RAW TOKEN", ProactiveState.StatusReport(_conn).ToJsonString(), StringComparison.Ordinal);
        Assert.False((bool)ProactiveState.StatusReport(_conn)["all_clear"]!);
        Assert.Equal("unknown", (string?)sources["calendar"]["error_class"]);
    }

    [Fact]
    public void Invalid_timestamp_completion_and_out_of_order_attempts()
    {
        var old = StartAttempt();
        var newer = StartAttempt();
        Page(newer);
        Complete(newer);
        Page(old);
        Assert.False((bool)Complete(old)["source_updated"]!);
        foreach (var data in new[]
                 {
                     "{\"status\":\"complete\",\"checkpoint_at\":\"2026-01-02\"}", "{\"status\":\"failed\",\"checkpoint_at\":\"" + End + "\"}",
                     "{\"status\":\"complete\",\"checkpoint_at\":\"2999-01-01T00:00:00Z\"}",
                 })
        {
            var attempt = StartAttempt();
            Page(attempt);
            Assert.ThrowsAny<StateError>(() => ProactiveState.CoverageFinish(_conn, attempt, Json(data)));
        }

        Assert.Equal(StateStore.ValidateTimestamp(End), (string?)Source()["checkpoint_at"]);
    }

    [Fact]
    public void Failed_write_rolls_back_without_unlocked_fallback()
    {
        using var second = StateStore.Connect("tenant:user", _root.State);
        second.Execute("PRAGMA busy_timeout=10");
        _conn.With(() =>
        {
            _conn.Execute("BEGIN IMMEDIATE");
            _conn.Execute("INSERT INTO proactive_legacy_cursors VALUES ('locked',?)", End);
            var error = Assert.Throws<PySqliteError>(() => ProactiveState.QueueAdd(second, new JsonObject { ["id"] = "blocked-write" }));
            Assert.Equal(PySqliteErrorKind.OperationalError, error.Kind);
        });
        Assert.Equal(0L, second.Execute("SELECT count(*) FROM proactive_items").FetchOne()![0]);
    }

    [Fact]
    public void Cli_delivery_and_coverage_end_to_end()
    {
        JsonNode Invoke(params string[] args)
        {
            var (code, stdout, stderr) = Cli(args);
            Assert.True(code == 0, stderr);
            return StateStore.ParseJson(stdout)!;
        }

        Invoke("queue-add", "--json", "{\"id\":\"one\",\"revision\":\"v1\"}");
        var batch = (string)Invoke("queue-drain", "--owner", "cli-anchor", "--format", "json")[0]!["batch"]!;
        var publication = Invoke("publication-record", "--batch", batch, "--json",
                                 "{\"content\":\"CLI brief\",\"ids\":[\"one\"],\"status\":\"available\",\"receipt\":{\"kind\":\"local\"}}");
        Assert.Equal(1L, (long)Invoke("queue-ack", "--batch", batch, "--receipt", (string)publication["publication_id"]!)["acked"]!);
        var attempt = (string)Invoke("coverage-start", "--json",
                                     "{\"family\":\"mail\",\"scope\":\"inbox\",\"run_id\":\"cli\",\"capability\":\"messages.list\",\"query_version\":\"1\",\"cadence_seconds\":3600}")["attempt_id"]!;
        Invoke("coverage-page", "--attempt", attempt, "--json", "{\"page\":1,\"observations\":[],\"final\":true}");
        Invoke("coverage-finish", "--attempt", attempt, "--json", "{\"status\":\"complete\",\"checkpoint_at\":\"" + End + "\"}");
        Assert.True((bool)Invoke("status")["all_clear"]!);
        Assert.Equal(StateStore.ValidateTimestamp(End), (string?)Invoke("coverage-resume", "--attempt", attempt)["checkpoint_at"]);
    }

    [Fact]
    public void Rapid_retries_do_not_count_as_missed_cadences()
    {
        for (var i = 0; i < 3; i++)
        {
            ProactiveState.CoverageFinish(_conn, StartAttempt(), Json("{\"status\":\"failed\",\"error_class\":\"timeout\"}"));
        }

        var source = Source();
        Assert.Equal(3L, (long)source["consecutive_failures"]!);
        Assert.Equal(1L, (long)source["missed_cadences"]!);
        Assert.False((bool)source["escalated"]!);
        Assert.Equal(1L, Count("SELECT count(*) FROM proactive_health_events"));
    }

    [Fact]
    public void Scope_window_keys_and_malformed_payload()
    {
        StartAttempt();
        StartAttempt(data => data["scope"] = new JsonObject { ["folder"] = "archive" });
        StartAttempt(data => data["window"] = new JsonObject { ["start"] = End, ["end"] = "2026-01-03T00:00:00Z" });
        Assert.Equal(2L, Count("SELECT count(*) FROM proactive_sources"));
        Add();
        _conn.With(() => _conn.Execute("UPDATE proactive_items SET payload='{broken'"));
        var (code, _, stderr) = Cli("queue-list", "--format", "json");
        Assert.Equal(2, code);
        Assert.DoesNotContain("Traceback", stderr, StringComparison.Ordinal);
        Assert.Contains("no reset", stderr, StringComparison.Ordinal);
        Assert.Equal("{broken", _conn.Execute("SELECT payload FROM proactive_items").FetchOne()![0]);
    }

    [Fact]
    public void Polling_windows_share_checkpoint_and_failure_episode()
    {
        var first = StartAttempt();
        Page(first);
        Complete(first);
        const string secondEnd = "2026-01-03T00:00:00Z";
        var second = StartAttempt(data => data["window"] = new JsonObject { ["start"] = End, ["end"] = secondEnd });
        Assert.Equal(ProactiveState.Attempt(_conn, first)["source_key"], ProactiveState.Attempt(_conn, second)["source_key"]);
        Assert.Equal(StateStore.ValidateTimestamp(End), (string?)Json(Cli("coverage-resume", "--attempt", second).Stdout)["checkpoint_at"]);
        Page(second);
        ProactiveState.CoverageFinish(_conn, second, Json("{\"status\":\"complete\",\"checkpoint_at\":\"" + secondEnd + "\"}"));
        Assert.True((bool)ProactiveState.StatusReport(_conn)["all_clear"]!);
        foreach (var (start, end) in new[] { (secondEnd, "2026-01-04T00:00:00Z"), ("2026-01-04T00:00:00Z", "2026-01-05T00:00:00Z") })
        {
            var attempt = StartAttempt(data => data["window"] = new JsonObject { ["start"] = start, ["end"] = end });
            ProactiveState.CoverageFinish(_conn, attempt, Json("{\"status\":\"failed\",\"error_class\":\"timeout\"}"));
        }

        var source = Source();
        Assert.Equal(2L, (long)source["consecutive_failures"]!);
        Assert.Equal(StateStore.ValidateTimestamp(secondEnd), (string?)source["checkpoint_at"]);
    }

    [Fact]
    public void Fixed_provider_windows_and_retirement()
    {
        JsonObject Window(string start, string end) => new() { ["start"] = start, ["end"] = end };
        var first = StartAttempt(data =>
        {
            data["family"] = "calendar";
            data["collection_window"] = Window(Start, End);
        });
        Page(first);
        Complete(first);
        var second = StartAttempt(data =>
        {
            data["family"] = "calendar";
            data["window"] = Window(End, "2026-01-03T00:00:00Z");
            data["collection_window"] = Window(End, "2026-01-03T00:00:00Z");
        });
        Page(second);
        ProactiveState.CoverageFinish(_conn, second, Json("{\"status\":\"complete\",\"checkpoint_at\":\"2026-01-03T00:00:00Z\"}"));
        var firstKey = (string)ProactiveState.Attempt(_conn, first)["source_key"]!;
        Assert.NotEqual(firstKey, ProactiveState.Attempt(_conn, second)["source_key"]);
        ProactiveState.CoverageRetire(_conn, firstKey, "Old calendar horizon");
        _conn.With(() => _conn.Execute("UPDATE proactive_sources SET last_successful_coverage_at=? WHERE source_key=?",
                                       StateStore.ValidateTimestamp(Start), firstKey));
        Assert.True((bool)ProactiveState.StatusReport(_conn)["all_clear"]!);
        Assert.ThrowsAny<StateError>(() => StartAttempt(data =>
        {
            data["family"] = "calendar";
            data["collection_window"] = Window(Start, End);
        }));
        StartAttempt(data =>
        {
            data["family"] = "calendar";
            data["collection_window"] = Window(Start, End);
            data["reactivate"] = true;
        });
        Assert.Equal("a provider-bound collection_window must match its requested window", Message(() => StartAttempt(data =>
        {
            data["family"] = "calendar";
            data["window"] = Window(End, "2026-01-03T00:00:00Z");
            data["collection_window"] = Window(Start, End);
        })));
    }

    [Fact]
    public void Continuation_cannot_move_to_a_new_polling_interval()
    {
        var first = StartAttempt();
        Page(first, final: false, continuation: "opaque-next");
        ProactiveState.CoverageFinish(_conn, first, Json("{\"status\":\"partial\"}"));
        Assert.Equal("resume requires compatible partial pages and a still-usable continuation", Message(() => StartAttempt(data =>
        {
            data["window"] = new JsonObject { ["start"] = End, ["end"] = "2026-01-03T00:00:00Z" };
            data["resume_attempt"] = first;
        })));
        var resumed = StartAttempt(data => data["resume_attempt"] = first);
        Assert.Equal(1L, ProactiveState.Attempt(_conn, resumed)["pages"]);
    }

    [Fact]
    public void Legacy_window_identity_is_retired_without_losing_evidence()
    {
        var first = StartAttempt(data => data["collection_window"] = new JsonObject { ["start"] = Start, ["end"] = End });
        Page(first);
        Complete(first);
        _conn.With(() => _conn.Execute("DELETE FROM proactive_meta WHERE key='source_identity_version'"));
        ProactiveState.Initialize(_conn);
        var source = Source();
        Assert.True((bool)source["retired"]!);
        Assert.Equal(StateStore.ValidateTimestamp(End), (string?)source["checkpoint_at"]);
        Assert.False((bool)ProactiveState.StatusReport(_conn)["all_clear"]!);
    }

    [Fact]
    public void Standalone_output_lifecycle_needs_no_dummy_items_or_ack()
    {
        Assert.Empty(ProactiveState.QueueDrain(_conn, "empty-anchor"));
        JsonObject Data() => new() { ["id"] = "standalone-1", ["content"] = "A brief with no queued inputs", ["status"] = "prepared" };
        var created = ProactiveState.PublicationRecord(_conn, null, Data());
        Assert.True((bool)created["standalone"]!);
        Assert.True((bool)ProactiveState.PublicationRecord(_conn, null, Data())["replayed"]!);
        Assert.Equal(0L, Count("SELECT count(*) FROM proactive_items"));
        Assert.Equal(1L, (long)ProactiveState.StatusReport(_conn)["unpublished_outputs"]!);
        ProactiveState.PublicationPublish(_conn, (string)created["publication_id"]!,
                                          new JsonObject { ["status"] = "available", ["receipt"] = new JsonObject { ["kind"] = "local" } });
        var listing = ProactiveState.PublicationList(_conn);
        Assert.Equal(0L, (long)listing[0]!["included_items"]!);
        Assert.Equal(0L, (long)listing[0]!["ack_pending"]!);
        Assert.Equal(0L, (long)ProactiveState.StatusReport(_conn)["unpublished_outputs"]!);
        Assert.True((bool)Json(Cli("publication-show", "standalone-1").Stdout)["standalone"]!);
        Assert.ThrowsAny<StateError>(() => ProactiveState.QueueAck(_conn, "standalone-1", batchId: (string)listing[0]!["batch_id"]!));
        var changed = Data();
        changed["content"] = "Changed";
        Assert.ThrowsAny<StateError>(() => ProactiveState.PublicationRecord(_conn, null, changed));
        var claiming = Data();
        claiming["id"] = "other";
        claiming["ids"] = new JsonArray("one");
        Assert.Equal("standalone publications cannot claim queued items", Message(() => ProactiveState.PublicationRecord(_conn, null, claiming)));
        var (code, _, stderr) = Cli("publication-record", "--json",
                                    "{\"content\":\"Another standalone output\",\"status\":\"available\",\"receipt\":{\"kind\":\"local\"}}");
        Assert.True(code == 0, stderr);
    }

    [Fact]
    public void Authentication_failure_waits_for_explicit_reauthentication()
    {
        var successful = StartAttempt();
        Page(successful);
        Complete(successful);
        var blocked = StartAttempt();
        ProactiveState.CoverageFinish(_conn, blocked, Json("{\"status\":\"blocked\",\"error_class\":\"authentication_required\"}"));
        var source = Source();
        Assert.False((bool)source["retry_eligible"]!);
        Assert.True((bool)source["retry_requires_reauthentication"]!);
        Assert.Equal(StateStore.ValidateTimestamp(End), (string?)source["checkpoint_at"]);
        Assert.ThrowsAny<StateError>(() => StartAttempt());
        Assert.Equal(2L, Count("SELECT count(*) FROM proactive_attempts"));
        var recovered = StartAttempt(data => data["reauthenticated"] = true);
        Page(recovered);
        Complete(recovered);
        source = Source();
        Assert.False((bool)source["retry_requires_reauthentication"]!);
        Assert.True((bool)source["retry_eligible"]!);
    }

    [Fact]
    public void Complete_can_clear_obsolete_delta_token()
    {
        var attempt = StartAttempt();
        Page(attempt);
        Complete(attempt);
        Assert.True((bool)Source()["has_delta_token"]!);
        var next = StartAttempt();
        Page(next);
        ProactiveState.CoverageFinish(_conn, next, Json("{\"status\":\"complete\",\"checkpoint_at\":\"" + End + "\",\"delta_token\":null}"));
        var source = Source();
        Assert.False((bool)source["has_delta_token"]!);
        Assert.Equal(StateStore.ValidateTimestamp(End), (string?)source["checkpoint_at"]);
    }

    [Fact]
    public void Missing_host_binding_does_not_assert_authentication_outage()
    {
        var failed = StartAttempt();
        ProactiveState.CoverageFinish(_conn, failed, Json("{\"status\":\"failed\",\"error_class\":\"binding_unavailable\"}"));
        var source = Source();
        Assert.False((bool)source["retry_requires_reauthentication"]!);
        Assert.True((bool)source["retry_eligible"]!);
        Assert.Null(source["checkpoint_at"]);
        var working = StartAttempt(data => data["capability"] = "verified-session.read");
        Page(working, final: false);
        ProactiveState.CoverageFinish(_conn, working, Json("{\"status\":\"partial\"}"));
        Assert.False((bool)ProactiveState.StatusReport(_conn)["all_clear"]!);
        Assert.Null(Source()["checkpoint_at"]);
    }

    // ---------------------------------------------------------------- importers (task_runs.py, memory_dream.py)

    [Fact]
    public void Task_runs_composition_uses_savepoints_inside_the_callers_transaction()
    {
        // task_runs.py: initialize and coverage_start inside the task store's own transaction; a rollback of the
        // outer unit of work also undoes the nested proactive writes.
        var data = new JsonObject
        {
            ["family"] = "mail", ["scope"] = new JsonObject { ["folder"] = "inbox" },
            ["window"] = new JsonObject { ["start"] = Start, ["end"] = End }, ["run_id"] = "attempt-1",
            ["capability"] = "messages.list", ["query_version"] = "1", ["kind"] = "enumeration",
        };
        Assert.Throws<InvalidOperationException>(() => StateStore.Transaction(_conn, () =>
        {
            ProactiveState.Initialize(_conn);
            ProactiveState.CoverageStart(_conn, data);
            throw new InvalidOperationException("task step failed");
        }));
        Assert.Equal(0L, Count("SELECT count(*) FROM proactive_attempts"));
        Assert.False(_conn.InTransaction);
        var collection = StateStore.Transaction(_conn, () => ProactiveState.CoverageStart(_conn, data));
        var attempt = (string)collection["attempt_id"]!;
        Assert.Equal("running", ProactiveState.Attempt(_conn, attempt)["status"]);

        // task_runs.recover: an interrupted read becomes a failed timeout attempt, inside the recovery transaction.
        StateStore.Transaction(_conn, () =>
        {
            Assert.Equal("running", _conn.Execute("SELECT status FROM proactive_attempts WHERE id=?", attempt).FetchOne()![0]);
            ProactiveState.CoverageFinish(_conn, attempt, Json("{\"status\":\"failed\",\"error_class\":\"timeout\"}"));
        });
        Assert.Equal("failed", ProactiveState.Attempt(_conn, attempt)["status"]);

        // A StateError inside the nested call rolls back only its savepoint; the caller decides about the rest.
        StateStore.Transaction(_conn, () =>
        {
            _conn.Execute("INSERT INTO proactive_legacy_cursors VALUES ('outer', ?)", End);
            Assert.Throws<StateError>(() => ProactiveState.CoveragePage(_conn, "missing", Json("{\"page\":1,\"observations\":[],\"final\":true}")));
            Assert.True(_conn.InTransaction);
        });
        Assert.Equal(1L, Count("SELECT count(*) FROM proactive_legacy_cursors"));
    }

    [Fact]
    public void Memory_dream_records_a_standalone_pointer_and_reads_its_availability()
    {
        var created = ProactiveState.PublicationRecord(_conn, null, new JsonObject
        {
            ["id"] = "dream:snap-1", ["content"] = "snap-1", ["status"] = "prepared",
        });
        Assert.True((bool)created["standalone"]!);
        Assert.Equal("prepared", (string?)ProactiveState.PublicationShow(_conn, "dream:snap-1")["status"]);
        ProactiveState.PublicationPublish(_conn, "dream:snap-1", new JsonObject
        {
            ["status"] = "available", ["receipt"] = new JsonObject { ["kind"] = "local" },
        });
        ProactiveState.PublicationReview(_conn, "dream:snap-1");
        var publication = ProactiveState.PublicationShow(_conn, "dream:snap-1");
        Assert.Equal("reviewed", (string?)publication["status"]);
        Assert.Empty((JsonArray)publication["items"]!);
        Assert.Equal("{\"kind\":\"local\",\"publication_id\":\"dream:snap-1\"}", StateStore.CanonicalJson(publication["receipt"]));
        Assert.Equal("unknown publication", Message(() => ProactiveState.PublicationShow(_conn, "dream:missing")));
    }

    [Fact]
    public void Freshness_missed_cadences_and_escalation_follow_the_clock()
    {
        var clock = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        using var scope = PyRuntime.Override(clock: () => clock);
        var attempt = StartAttempt();
        Page(attempt);
        Complete(attempt);
        Assert.True((bool)Source()["fresh"]!);
        clock = clock.AddMinutes(10);
        ProactiveState.CoverageFinish(_conn, StartAttempt(), Json("{\"status\":\"failed\",\"error_class\":\"timeout\",\"retry_after_seconds\":60}"));
        var source = Source();
        Assert.Equal("2026-03-01T00:11:00.000000+00:00", (string?)source["next_retry_at"]);
        Assert.False((bool)source["retry_eligible"]!);
        Assert.Equal(1L, (long)source["missed_cadences"]!);
        clock = clock.AddMinutes(110);
        source = Source();
        Assert.True((bool)source["retry_eligible"]!);
        Assert.Equal(2L, (long)source["missed_cadences"]!);
        Assert.True((bool)source["escalated"]!);
        Assert.False((bool)source["fresh"]!);
        ProactiveState.CoverageFinish(_conn, StartAttempt(), Json("{\"status\":\"failed\",\"error_class\":\"network\"}"));
        Assert.Equal(["degraded", "escalated"],
                     _conn.Execute("SELECT transition FROM proactive_health_events ORDER BY rowid").Select(row => (string)row[0]!));
    }
}
