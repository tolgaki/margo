using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Xunit;
using static Margo.Cloud.State.Tests.Memory.MemoryStoreTests;

namespace Margo.Cloud.State.Tests.Memory;

/// <summary>Library-level ports of tests/test_memory_governance.py that need a clock or file-system detail.</summary>
public sealed class MemoryGovernanceTests
{
    private static JsonObject Policy(Action<JsonObject>? change = null)
    {
        var value = MemoryGovernance.DefaultPolicy();
        change?.Invoke(value);
        return value;
    }

    private static void Configure(MemoryStore store, JsonObject policy)
    {
        var preview = MemoryGovernance.PolicyPreview(store, policy);
        MemoryGovernance.SetPolicy(store, policy, Evidence((string)preview["subject_id"]!, decision: "configure"));
    }

    [Fact]
    public void Stale_policy_approval_cannot_change_a_newer_policy()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var original = Policy();
        var preview = MemoryGovernance.PolicyPreview(store, original);
        Configure(store, Policy(policy => policy["usage_enabled"] = true));
        var error = Assert.Throws<StateError>(() =>
            MemoryGovernance.SetPolicy(store, original, Evidence((string)preview["subject_id"]!, decision: "configure")));
        Assert.Equal("human evidence must name the exact subject and revision", error.Message);
        Assert.True((bool)store.Policy()["data"]!["usage_enabled"]!);
    }

    [Fact]
    public void Retention_erases_due_memories_by_the_clock_in_bounded_pages()
    {
        using var root = TempRoot.Create();
        var now = DateTimeOffset.UtcNow;
        string first, second;
        using (PyRuntime.Override(clock: () => now - TimeSpan.FromDays(30)))
        {
            using var old = new MemoryStore(Account, root.State);
            first = (string)old.Put("old-a", Data(), "active")["id"]!;
            second = (string)old.Put("old-b", Data(), "active")["id"]!;
        }

        using var store = new MemoryStore(Account, root.State);
        Assert.Empty((JsonArray)MemoryGovernance.Maintain(store)["forgotten_ids"]!);
        Configure(store, Policy(policy => policy["retention_days"] = new JsonObject { ["episode"] = 7 }));
        var page = MemoryGovernance.Maintain(store, 1);
        Assert.Equal(1L, (long)page["processed"]!);
        Assert.Single((JsonArray)page["forgotten_ids"]!);
        var final = MemoryGovernance.Maintain(store, 1, page["next_cursor"]!.DeepClone());
        var erased = ((JsonArray)page["forgotten_ids"]!).Concat((JsonArray)final["forgotten_ids"]!).Select(id => (string)id!).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), erased);
        Assert.False((bool)page["outbound_actions"]!);
    }

    [Fact]
    public void Sanitized_export_is_exact_reviewed_private_and_never_overwrites()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var identity = store.RecordId("agent", "recipe");
        store.Put("recipe", Data(("domain", "agent"), ("kind", "lesson"), ("authority", "user_confirmed")), "active", 1, Evidence(identity));
        var recipe = new JsonObject
        {
            ["title"] = "Prepare a review",
            ["goal"] = "Allow preparation time.",
            ["preconditions"] = "The user agreed a preparation policy.",
            ["steps"] = "Read the current context; prepare a private agenda.",
            ["limitations"] = "Never move a meeting without exact approval.",
        };
        var preview = MemoryGovernance.ExportPreview(store, identity, recipe);
        var consent = Evidence((string)preview["subject_id"]!, decision: "export");
        var destination = root.Combine("out", "recipe.md");
        var changed = (JsonObject)recipe.DeepClone();
        changed["steps"] = "Different instructions.";
        Assert.Throws<StateError>(() => MemoryGovernance.ExportRecipe(store, identity, changed, consent, destination));

        var result = MemoryGovernance.ExportRecipe(store, identity, recipe, consent, destination);
        Assert.False((bool)result["published"]!);
        var text = File.ReadAllText(destination);
        Assert.StartsWith("# Prepare a review\n\n## Goal\n\nAllow preparation time.\n\n## Preconditions\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(identity, text, StringComparison.Ordinal);
        Assert.DoesNotContain("source_refs", text, StringComparison.Ordinal);
        Assert.Equal("600", Modes.Of(destination));
        Assert.Equal("700", Modes.Of(root.Combine("out")));
        Assert.Equal(PyIds.Sha256Hex(text), (string)result["sha256"]!);

        var exists = Assert.Throws<PyOSErrorTwoPaths>(() => MemoryGovernance.ExportRecipe(store, identity, recipe, consent, destination));
        Assert.Equal("FileExistsError", exists.PythonType);
        Assert.Matches(new Regex(@"^\[Errno 17\] File exists: '.*/out/\.memory-export-[a-z0-9_]{8}' -> '" + Regex.Escape(destination) + "'$"), exists.Message);
        Assert.Equal(["recipe.md"], Directory.GetFiles(root.Combine("out")).Select(Path.GetFileName).ToArray());

        var marked = (JsonObject)recipe.DeepClone();
        marked["steps"] = "Contact dana@example.com";
        Assert.Throws<StateError>(() => MemoryGovernance.ExportPreview(store, identity, marked));
    }

    [Fact]
    public void Usage_requires_the_policy_and_is_replay_safe()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var row = store.Put("usage", Data(), "active");
        var refs = new JsonArray(new JsonObject { ["id"] = row["id"]!.DeepClone(), ["revision"] = 1 });
        Assert.Throws<StateError>(() => store.RecordUsage(refs.DeepClone(), "drafting"));
        Configure(store, Policy(policy => policy["usage_enabled"] = true));
        store.RecordUsage(refs.DeepClone(), "drafting", eventId: "synthetic-usage");
        Assert.True((bool)store.RecordUsage(refs.DeepClone(), "drafting", eventId: "synthetic-usage")["replayed"]!);
        Assert.Throws<StateError>(() => store.RecordUsage(refs.DeepClone(), "calendar", eventId: "synthetic-usage"));
        Assert.DoesNotContain((string)row["text"]!, store.Usage(row["id"]).ToJsonString(), StringComparison.Ordinal);
        store.Forget(row["id"], 1, Evidence((string)row["id"]!, decision: "forget"));
        Assert.Empty(store.Usage(row["id"]));
    }

    [Fact]
    public void Explicit_v1_migration_preserves_records_and_defaults_capture_off()
    {
        using var root = TempRoot.Create();
        const string Legacy = "synthetic-legacy-memory@example.com";
        var (identity, keyHash) = MemoryStore.MemoryIdentity(Legacy, "user", "legacy");
        using (var connection = StateStore.Connect(Legacy, root.State))
        {
            connection.ExecuteScript(MemoryStore.SchemaV1);
            connection.With(() =>
            {
                connection.Execute("INSERT INTO memory_meta VALUES('schema_version','1')");
                connection.Execute("INSERT INTO margo_meta VALUES('memory_schema_version','1')");
                connection.Execute("INSERT INTO memory_records VALUES(?,?,?,?,?,?,?,?,?,?,?)", identity, Legacy, keyHash, 1, "user", "episode",
                                   "active", StateStore.CanonicalJson(Data()), "synthetic-hash", StateStore.UtcNow(), StateStore.UtcNow());
            });
        }

        Assert.Throws<StateError>(() => new MemoryStore(Legacy, root.State));
        using var migrated = new MemoryStore(Legacy, root.State, migrate: true);
        Assert.Equal((string)Data()["text"]!, (string)migrated.Show(identity)["text"]!);
        Assert.False((bool)migrated.Policy()["data"]!["capture"]!["enabled"]!);
        Assert.Equal("2", (string)migrated.Health()["schema_version"]!);
    }
}
