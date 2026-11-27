using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.MemoryLearning;
using Xunit;
using static Margo.Cloud.State.Tests.Memory.MemoryStoreTests;
using static Margo.Cloud.State.Tests.MemoryLearning.MemoryLearningTests;

namespace Margo.Cloud.State.Tests.MemoryLearning;

/// <summary>
/// Library-level ports of tests/test_memory_consolidation.py (the CLI-observable cases are parity scenarios in
/// cloud/contracts/state/memory-learning/consolidation_*.json) and checks of the C# API shape memory_state.py needs.
/// </summary>
public sealed class MemoryConsolidationTests
{
    private static JsonObject Candidate(MemoryStore store, string key, string? text = null) => store.Put(key, new JsonObject
    {
        ["domain"] = "agent",
        ["kind"] = "episode",
        ["authority"] = "source_observed",
        ["title"] = "Synthetic bounded review",
        ["text"] = text ?? key,
        ["scope"] = "synthetic",
        ["source_refs"] = new JsonArray(new JsonObject { ["kind"] = "tool_result", ["ref"] = "synthetic:" + key }),
    });

    [Fact]
    public void Bounded_pagination_does_not_drop_unsurfaced_candidates()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var expected = Enumerable.Range(0, 9).Select(index => (string)Candidate(store, "candidate-" + index)["id"]!).ToHashSet(StringComparer.Ordinal);
        var surfaced = new HashSet<string>(StringComparer.Ordinal);
        JsonNode? after = null;
        while (true)
        {
            var plan = MemoryConsolidation.ConsolidationPlan(store, 2, after: PyNode.Copy(after), scanLimit: 3);
            Assert.True(Int(plan["scanned"]) <= 3);
            Assert.True(((JsonArray)plan["items"]!).Count <= 2);
            var result = MemoryConsolidation.SurfaceConsolidation(store, plan);
            foreach (var item in (JsonArray)result["surfaced"]!)
            {
                surfaced.UnionWith(((JsonArray)item!["targets"]!).Select(target => (string)target!["id"]!));
            }

            after = plan["next_after"];
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(expected, surfaced);
        Assert.All(store.List(), record => Assert.Equal("candidate", (string)record!["status"]!));
        foreach (var (limit, scan) in new (PyArg, PyArg)[] { (0, default), (51, default), (default, 201), (default, true) })
        {
            Assert.Throws<StateError>(() => MemoryConsolidation.ConsolidationPlan(store, limit, scanLimit: scan));
        }
    }

    [Fact]
    public void Delivery_is_recorded_once_and_replays_as_a_no_op()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        Candidate(store, "private-candidate", "Private synthetic wording should not be in delivery markers.");
        var plan = MemoryConsolidation.ConsolidationPlan(store);
        Assert.False((bool)MemoryConsolidation.SurfaceConsolidation(store, plan)["replayed"]!);
        var replay = MemoryConsolidation.SurfaceConsolidation(store, plan);
        Assert.True((bool)replay["replayed"]!);
        Assert.Empty((JsonArray)replay["surfaced"]!);
        var markers = store.Conn.Execute(
            "SELECT value FROM memory_meta WHERE key LIKE 'learning:surfaced:%' OR key LIKE 'learning:delivery:%'").FetchAll();
        Assert.NotEmpty(markers);
        Assert.DoesNotContain(markers, row => ((string)row[0]!).Contains("Private synthetic wording", StringComparison.Ordinal));
        Assert.Empty((JsonArray)MemoryConsolidation.ConsolidationPlan(store)["items"]!);
    }

    [Fact]
    public void Plan_parameters_keep_python_defaults_and_none()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var parameters = (JsonObject)MemoryConsolidation.ConsolidationPlan(store)["parameters"]!;
        Assert.Equal("""{"after":null,"environment":null,"limit":10,"scan_limit":100}""", StateStore.CanonicalJson(parameters));
        Assert.Equal("consolidation limit must be between 1 and 50",
                     Assert.Throws<StateError>(() => MemoryConsolidation.ConsolidationPlan(store, PyArg.None)).Message);
    }

    [Fact]
    public void Source_budget_is_shared_across_the_dependency_walk()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        var parent = store.Put("parent", Data(("domain", "agent")), "active");
        var child = store.Put("child", Data(("domain", "agent"), ("source_refs", new JsonArray(
            new JsonObject { ["kind"] = "memory_record", ["ref"] = PyNode.Copy(parent["id"]), ["revision"] = "1" },
            new JsonObject { ["kind"] = "tool_result", ["ref"] = "synthetic:x" }))));
        long[] budget = [2];
        var snapshot = MemoryConsolidation.Sources(store, child, budget: budget);
        Assert.Equal(-1, budget[0]);
        Assert.Equal("source_budget_exceeded", (string)snapshot[^1]!);
        Assert.True(MemoryConsolidation.CurrentSources(new JsonArray()));
        Assert.False(MemoryConsolidation.CurrentSources(snapshot));
    }
}
