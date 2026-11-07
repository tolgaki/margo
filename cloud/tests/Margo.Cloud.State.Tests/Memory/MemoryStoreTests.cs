using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Xunit;

namespace Margo.Cloud.State.Tests.Memory;

/// <summary>
/// Library-level ports of tests/test_memory_store.py, tests/test_memory_governance.py and the store-level cases of
/// tests/test_memory_review_regressions.py that need concurrency, a clock or patched file probes (the rest are parity
/// scenarios in cloud/contracts/state/memory/).
/// </summary>
public sealed class MemoryStoreTests
{
    internal const string Account = "synthetic-memory-account@example.com";

    internal static JsonObject Data(params (string Key, JsonNode? Value)[] overrides)
    {
        var value = new JsonObject
        {
            ["domain"] = "user",
            ["kind"] = "episode",
            ["title"] = "Synthetic review preparation",
            ["text"] = "A fictional review benefited from preparation time.",
            ["authority"] = "source_observed",
            ["scope"] = "personal",
            ["source_refs"] = new JsonArray(new JsonObject { ["kind"] = "tool_result", ["ref"] = "tool:synthetic-result" }),
        };
        foreach (var (key, item) in overrides)
        {
            value[key] = item;
        }

        return value;
    }

    internal static JsonObject Evidence(string subject, long revision = 1, string decision = "confirm", string? decidedAt = null) => new()
    {
        ["kind"] = "human_confirmation",
        ["actor"] = "alex@example.com",
        ["statement"] = "I reviewed this exact synthetic memory change.",
        ["evidence_ref"] = "conversation:synthetic/session/turn/1",
        ["subject_id"] = subject,
        ["revision"] = revision,
        ["decision"] = decision,
        ["decided_at"] = decidedAt ?? PyDateTime.Now().Isoformat(),
    };

    private static JsonArray FileRef(string path, string hash) =>
        new(new JsonObject { ["kind"] = "file", ["ref"] = path, ["revision"] = hash });

    [Fact]
    public void Idempotent_input_and_rejection_are_stable()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var first = store.Put("rejected-example", Data(), "rejected");
        var replay = store.Put("rejected-example", Data(), "rejected", 99);
        Assert.True(PyJson.PyEquals(first, replay));
        Assert.Equal(1L, (long)first["revision"]!);
        Assert.Equal(1L, (long)store.Health()["by_status"]!["rejected"]!);
    }

    [Fact]
    public void Omitted_status_is_candidate_but_an_explicit_none_is_refused()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        Assert.Equal("candidate", (string)store.Put("default-status", Data())["status"]!);
        var error = Assert.Throws<StateError>(() => store.Put("none-status", Data(), PyArg.None));
        Assert.Equal("unsupported memory status", error.Message);
        Assert.Throws<StateError>(() => store.History(store.RecordId("user", "default-status"), PyArg.None));
        Assert.Single(store.History(store.RecordId("user", "default-status")));
        Assert.Throws<StateError>(() => store.Eligible(usage: PyArg.None));
        Assert.Throws<StateError>(() => store.Eligible(includeStale: PyArg.None));
        Assert.Empty(store.Eligible(includeStale: true));
    }

    [Fact]
    public void Concurrent_stale_revision_is_rejected()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        using var other = new MemoryStore(Account, root.State);
        var first = store.Put("concurrent", Data());
        store.Put("concurrent", Data(("text", "First concurrent edit.")), "candidate", first["revision"]);
        var error = Assert.Throws<StateError>(() => other.Put("concurrent", Data(("text", "Conflicting concurrent edit.")), "candidate", 1));
        Assert.Equal("revision conflict: current revision is 2", error.Message);
    }

    [Fact]
    public void A_writer_holding_its_transaction_keeps_other_writers_out_but_not_readers()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var row = store.Put("readable", Data(), "active");
        store.Transaction(() =>
        {
            store.Conn.Execute("INSERT INTO memory_meta VALUES('synthetic-write','pending')");
            using var reader = new MemoryStore(Account, root.State, readOnly: true);
            Assert.Equal((string)row["id"]!, (string)reader.Show(row["id"])["id"]!);
            var readonlyError = Assert.Throws<PySqliteError>(() => reader.Conn.Execute("INSERT INTO memory_meta VALUES('forbidden-write','no')"));
            Assert.Contains("readonly", readonlyError.Message, StringComparison.Ordinal);
        });
        Assert.Equal(1L, store.Conn.Execute("SELECT count(*) FROM memory_meta WHERE key='synthetic-write'").FetchOne()![0]);
    }

    [Fact]
    public void Nested_transactions_roll_back_only_the_failed_part()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        store.Transaction(() =>
        {
            store.Put("kept", Data());
            Assert.Throws<StateError>(() => store.Transaction(() =>
            {
                store.Put("dropped", Data());
                throw new StateError("synthetic failure");
            }));
            Assert.Throws<StateError>(() => store.Put("kept", Data(("text", "conflict")), "candidate", 9));
        });
        Assert.Equal(["Synthetic review preparation"], store.List().Select(record => (string)record!["title"]!).ToArray());
        Assert.Throws<StateError>(() => store.Show(store.RecordId("user", "dropped")));
    }

    [Fact]
    public void File_sources_require_hash_and_become_ineligible_after_change()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var path = root.Combine("profile.txt");
        File.WriteAllText(path, "Saved fact");
        Assert.Throws<StateError>(() => store.Put("no-hash", Data(("source_refs", new JsonArray(new JsonObject { ["kind"] = "file", ["ref"] = path }))), "active"));
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        store.Put("file-fact", Data(("source_refs", FileRef(path, digest))), "active");
        Assert.Single(store.Eligible());
        File.WriteAllText(path, "Changed fact");
        Assert.Empty(store.Eligible());
    }

    [Fact]
    public void Source_hash_is_shared_only_within_each_eligibility_pass()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var source = root.Combine("shared.txt");
        var content = Enumerable.Repeat((byte)'a', 65536).ToArray();
        File.WriteAllBytes(source, content);
        var refs = FileRef(source, Convert.ToHexStringLower(SHA256.HashData(content)));
        for (var number = 0; number < 20; number++)
        {
            store.Put("source-" + number, Data(("source_refs", refs.DeepClone())), "active");
        }

        var opened = 0;
        MemoryNative.Hooks.Value = new MemoryNative.FileHooks { Opened = path => opened += path == source ? 1 : 0 };
        try
        {
            Assert.Equal(20, store.Eligible().Count);
            Assert.Equal(1, opened);
            Assert.Equal(20, store.Eligible().Count);
            Assert.Equal(2, opened);
        }
        finally
        {
            MemoryNative.Hooks.Value = null;
        }

        var oversized = root.Combine("oversized.txt");
        var large = Enumerable.Repeat((byte)'b', MemoryStore.MaxSourceFileBytes + 1).ToArray();
        File.WriteAllBytes(oversized, large);
        store.Put("large-source", Data(("source_refs", FileRef(oversized, Convert.ToHexStringLower(SHA256.HashData(large))))), "active");
        Assert.Equal(20, store.Eligible().Count);
    }

    [Fact]
    public void Source_change_within_one_pass_is_rehashed()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var source = root.Combine("changing.txt");
        File.WriteAllText(source, "before");
        var mtime = File.GetLastWriteTimeUtc(source);
        var (first, second) = store.FreshnessPass(() =>
        {
            var before = store.FileDigest(source);
            File.WriteAllText(source, "after!");
            File.SetLastWriteTimeUtc(source, mtime);
            return (before, store.FileDigest(source));
        });
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("before"u8)), first);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("after!"u8)), second);
    }

    [Fact]
    public void Source_digest_accepts_distinct_stat_and_fstat_ctimes()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var source = root.Combine("stable.txt");
        File.WriteAllText(source, "Synthetic unchanged evidence\n");
        MemoryNative.Hooks.Value = new MemoryNative.FileHooks { FStat = (_, info) => info with { CtimeNs = info.CtimeNs + 1_000_000 } };
        try
        {
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("Synthetic unchanged evidence\n"u8)), store.FileDigest(source));
        }
        finally
        {
            MemoryNative.Hooks.Value = null;
        }
    }

    public static TheoryData<string> IdentityFields => ["dev", "ino", "size", "mtime"];

    public static TheoryData<string> SignatureFields => ["dev", "ino", "size", "mtime", "ctime"];

    private static PyFileStat Bump(PyFileStat info, string field) => field switch
    {
        "dev" => info with { Device = info.Device + 1 },
        "ino" => info with { Inode = info.Inode + 1 },
        "size" => info with { Size = info.Size + 1 },
        "mtime" => info with { MtimeNs = info.MtimeNs + 1 },
        _ => info with { CtimeNs = info.CtimeNs + 1 },
    };

    [Theory]
    [MemberData(nameof(IdentityFields))]
    public void Source_digest_rejects_a_different_file_opened_after_stat(string field)
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var source = root.Combine("replaced-before-open.txt");
        File.WriteAllText(source, "Synthetic evidence");
        MemoryNative.Hooks.Value = new MemoryNative.FileHooks { FStat = (_, info) => Bump(info, field) };
        try
        {
            Assert.Null(store.FileDigest(source));
        }
        finally
        {
            MemoryNative.Hooks.Value = null;
        }
    }

    [Theory]
    [MemberData(nameof(SignatureFields))]
    public void Source_digest_rejects_descriptor_changes_during_read(string field)
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var source = root.Combine("changed-during-read.txt");
        File.WriteAllText(source, "Synthetic evidence");
        var calls = 0;
        MemoryNative.Hooks.Value = new MemoryNative.FileHooks
        {
            FStat = (_, info) =>
            {
                calls++;
                var opened = info with { CtimeNs = info.CtimeNs + 1_000_000 };
                return calls == 1 ? opened : Bump(opened, field);
            },
        };
        try
        {
            Assert.Null(store.FileDigest(source));
            Assert.Equal(2, calls);
        }
        finally
        {
            MemoryNative.Hooks.Value = null;
        }
    }

    [Theory]
    [MemberData(nameof(SignatureFields))]
    public void Source_digest_rejects_path_changes_during_read(string field)
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var source = root.Combine("replaced-during-read.txt");
        File.WriteAllText(source, "Synthetic evidence");
        var calls = 0;
        MemoryNative.Hooks.Value = new MemoryNative.FileHooks
        {
            Stat = (path, info) => path == source && ++calls == 2 ? Bump(info, field) : info,
        };
        try
        {
            Assert.Null(store.FileDigest(source));
            Assert.Equal(2, calls);
        }
        finally
        {
            MemoryNative.Hooks.Value = null;
        }
    }

    [Fact]
    public void Several_derived_descendants_are_requeued_in_a_fixed_order()
    {
        // Python iterates a set here (hash order, random per desktop process); the port uses sorted order.
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        store.Conn.ExecuteScript("CREATE TABLE semantic_vectors (memory_id TEXT); CREATE TABLE semantic_documents (memory_id TEXT);");
        var parent = store.Put("parent", Data(("text", "Cedar ships in March.")), "active");
        var children = new List<string>();
        foreach (var name in new[] { "child-a", "child-b", "child-c" })
        {
            var child = store.Put(name, Data(("source_refs", new JsonArray(new JsonObject
            {
                ["kind"] = "memory_record", ["ref"] = parent["id"]!.DeepClone(), ["revision"] = "1",
            }))), "active");
            store.CompleteJob(child["id"], 1);
            store.Conn.Execute("INSERT INTO semantic_vectors VALUES(?)", child["id"]);
            store.Conn.Execute("INSERT INTO semantic_documents VALUES(?)", child["id"]);
            children.Add((string)child["id"]!);
        }

        store.Put("parent", Data(("text", "Cedar ships in April.")), "active", 1);
        var requeued = store.Conn.Execute("SELECT memory_id,op FROM memory_jobs WHERE memory_id<>? ORDER BY rowid", parent["id"])
                            .Select(row => (string)row[0]! + ":" + row[1]).ToList();
        Assert.Equal(children.Order(StringComparer.Ordinal).Select(id => id + ":delete"), requeued);
        Assert.Equal(0L, store.Conn.Execute("SELECT count(*) FROM semantic_vectors").FetchOne()![0]);
        Assert.Equal(0L, store.Conn.Execute("SELECT count(*) FROM semantic_documents").FetchOne()![0]);
        Assert.Equal(children.Order(StringComparer.Ordinal).Prepend((string)parent["id"]!).Order(StringComparer.Ordinal),
                     store.DerivedDescendants((string)parent["id"]!));
    }

    [Fact]
    public void Human_decisions_may_be_at_most_one_minute_in_the_future()
    {
        using var root = TempRoot.Create();
        var now = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        using var clock = PyRuntime.Override(clock: () => now);
        var subject = "subject";
        Assert.NotNull(HumanEvidence.Human(Evidence(subject, decidedAt: "2026-03-04T05:07:07+00:00"), subject, 1, "confirm"));
        var error = Assert.Throws<StateError>(() =>
            HumanEvidence.Human(Evidence(subject, decidedAt: "2026-03-04T05:07:07.000001+00:00"), subject, 1, "confirm"));
        Assert.Equal("human decision cannot be in the future", error.Message);
        Assert.NotNull(HumanEvidence.Human(Evidence(subject, decidedAt: "2026-03-04T06:07:07+01:00"), subject, 1, "confirm"));
        Assert.Throws<PyOverflowError>(() => HumanEvidence.Human(Evidence(subject, decidedAt: "0001-01-01T00:00:00+01:00"), subject, 1, "confirm"));
    }

    [Fact]
    public void Accounts_are_isolated_and_schema_loss_is_not_recreated()
    {
        using var root = TempRoot.Create();
        string id;
        using (var store = new MemoryStore(Account, root.State))
        {
            id = (string)store.Put("same-key", Data())["id"]!;
            using var other = new MemoryStore("different-synthetic-account@example.com", root.State);
            Assert.NotEqual(id, (string)other.Put("same-key", Data())["id"]!);
            Assert.Throws<StateError>(() => other.Show(id));
        }

        var (_, database) = StateStore.StatePath(Account, root.State);
        using (var connection = PyConnection.Connect(database))
        {
            connection.Execute("DROP TABLE memory_jobs");
        }

        var error = Assert.Throws<StateError>(() => new MemoryStore(Account, root.State));
        Assert.Equal("incomplete memory schema; refusing to recreate missing history", error.Message);
    }

    [Fact]
    public void Schema_text_matches_the_python_definition()
    {
        Assert.StartsWith("\nCREATE TABLE IF NOT EXISTS memory_meta (\n key TEXT PRIMARY KEY, value TEXT NOT NULL\n);", MemoryStore.SchemaV1,
                          StringComparison.Ordinal);
        Assert.Contains("relation TEXT NOT NULL, created_at TEXT NOT NULL, data TEXT NOT NULL DEFAULT '{}',", MemoryStore.Schema, StringComparison.Ordinal);
        Assert.EndsWith(" refs TEXT NOT NULL, outcome TEXT NOT NULL, created_at TEXT NOT NULL\n);\n", MemoryStore.Schema, StringComparison.Ordinal);
        Assert.Equal(8, MemoryStore.Schema.Split(';').Count(part => part.Contains("CREATE TABLE", StringComparison.Ordinal)));
    }

    [Fact]
    public void Python_dynamic_helpers_follow_cpython()
    {
        Assert.True(PyJson.PyEquals(JsonNode.Parse("""{"a":"b","relationship_change":1}"""),
                                    Py.DictWith(JsonNode.Parse("""[["a","b"]]"""), ("relationship_change", 1))));
        Assert.True(PyJson.PyEquals(JsonNode.Parse("""{"x":"y","c":"d"}"""), Py.DictWith(JsonNode.Parse("""[{"x":1,"y":2},"cd"]"""))));
        Assert.Equal("dictionary update sequence element #0 has length 1; 2 is required",
                     Assert.Throws<PyValueError>(() => Py.DictWith(JsonValue.Create("ab"))).Message);
        Assert.Equal("cannot convert dictionary update sequence element #0 to a sequence",
                     Assert.Throws<PyTypeError>(() => Py.DictWith(JsonNode.Parse("[5]"))).Message);
        Assert.Equal("'int' object is not iterable", Assert.Throws<PyTypeError>(() => Py.DictWith(JsonValue.Create(5))).Message);
        Assert.Equal("unhashable type: 'list'", Assert.Throws<PyTypeError>(() => Py.In(new JsonArray(), MemoryStore.Domains)).Message);
        Assert.Equal("{'a': [1, 2.5, None, True], 'b': 'x'}", Py.Repr(JsonNode.Parse("""{"a":[1,2.5,null,true],"b":"x"}""")));
        Assert.True(Py.IsDigit("\u0661\u00b2"));
        Assert.False(Py.IsDigit(""));
        Assert.False(Py.IsDigit("1_0"));
        Assert.True(Py.IsWord(0x2167)); // ROMAN NUMERAL EIGHT: Python's \w, not .NET's
        Assert.False(Py.IsWord(0x0301)); // COMBINING ACUTE ACCENT: .NET's \w, not Python's
        Assert.True(Py.IsSpace(0x1C));
        Assert.False(Py.IsSpace(0x200B));
    }

    [Theory]
    [InlineData("Contact dana@example.com", true)]
    [InlineData("café@exämple.com", true)]
    [InlineData("a@b.c", false)]
    [InlineData("a@b.example\u00e9", false)]
    [InlineData("me@host.example.c0m", true)]
    [InlineData("see https://", true)]
    [InlineData("/home/ x", false)]
    [InlineData("/Users/someone", true)]
    [InlineData("C:\\\\x", true)]
    [InlineData("id 123e4567\u002De89b\u002D12d3\u002Da456\u002D426614174000 here", true)]
    [InlineData("x123e4567\u002De89b\u002D12d3\u002Da456\u002D426614174000", false)]
    [InlineData("mem_0123456789abcdef", true)]
    [InlineData("\u00e9mem_0123456789abcdef", false)]
    [InlineData("\u212aey", false)]
    [InlineData("api\u212aey: x", false)]
    [InlineData("api \u212aey: x", true)]
    [InlineData("pa\u017f\u017fword: x", true)]
    [InlineData("P\u0130ASSWORD", false)]
    [InlineData("ap\u0131.key=1", true)]
    [InlineData("api\nkey:x", false)]
    [InlineData("my_password: x", false)]
    [InlineData("bearer\u00a0:", true)]
    public void Export_markers_use_python_regular_expression_semantics(string text, bool expected)
    {
        Assert.Equal(expected, PyPatterns.HasPrivateMarker(StateStore.CanonicalJson(JsonValue.Create(text))));
    }

    [Fact]
    public void Search_text_counts_code_points_for_the_representation_limit()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var probe = Data(("text", "x"), ("entities", new JsonArray("user")));
        var prefix = PyRepr.Length(MemoryStore.SearchText(probe)) - 1;
        var text = new StringBuilder().Insert(0, "\U0001F600", MemoryStore.MaxMemoryTextChars - prefix).ToString();
        store.Put("astral-limit", Data(("text", text), ("entities", new JsonArray("user"))));
        var error = Assert.Throws<StateError>(() => store.Put("over", Data(("text", text + "x"), ("entities", new JsonArray("user")))));
        Assert.StartsWith("memory search representation exceeds 16384 characters", error.Message, StringComparison.Ordinal);
    }
}
