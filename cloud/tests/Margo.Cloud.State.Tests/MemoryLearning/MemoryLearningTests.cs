using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Xunit;
using static Margo.Cloud.State.Tests.Memory.MemoryStoreTests;
using Learning = Margo.Cloud.State.MemoryLearning.MemoryLearning;

namespace Margo.Cloud.State.Tests.MemoryLearning;

/// <summary>
/// Library-level ports of tests/test_memory_learning.py: the cases that patch Python's clock (here
/// <see cref="PyRuntime.Override"/>), the pure helpers, and checks of the C#-only pieces (the Python whitespace class of
/// the preference patterns and the skill glob order). The CLI-observable cases are parity scenarios in
/// cloud/contracts/state/memory-learning/.
/// </summary>
public sealed class MemoryLearningTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static IDisposable Clock(DateTimeOffset moment) => PyRuntime.Override(clock: () => moment);

    internal static long Int(JsonNode? value) => (long)PyJson.AsInt(value);

    private static JsonObject Legacy() => new()
    {
        ["title"] = "Repeated preparation gap",
        ["text"] = "Preparation gaps occurred in three observed meetings.",
        ["scope"] = "meeting preparation",
        ["window_start"] = "2026-08-01T00:00:00Z",
        ["window_end"] = "2026-09-01T00:00:00Z",
        ["coverage"] = "complete",
        ["event_ids"] = new JsonArray("evt1", "evt2", "evt3"),
        ["population"] = 10,
        ["source_refs"] = new JsonArray(new JsonObject { ["kind"] = "tool_result", ["ref"] = "synthetic-evidence" }),
    };

    private static JsonObject Resolved(int count = 3)
    {
        var events = new JsonArray();
        for (var index = 0; index < count; index++)
        {
            events.Add(new JsonObject
            {
                ["id"] = "event-" + index,
                ["independence_key"] = "execution-" + index,
                ["occurred_at"] = $"2026-08-{index + 1:00}T00:00:00Z",
                ["matched"] = true,
                ["source_refs"] = new JsonArray(new JsonObject { ["kind"] = "tool_result", ["ref"] = "synthetic-result-" + index }),
            });
        }

        return new JsonObject
        {
            ["title"] = "Repeated synthetic input failures",
            ["text"] = "Synthetic input-contract failures in the supplied events.",
            ["scope"] = "synthetic tool checks",
            ["trend_type"] = "tool_failures",
            ["window_start"] = "2026-08-01T00:00:00Z",
            ["window_end"] = "2026-09-01T00:00:00Z",
            ["population"] = 10,
            ["coverage"] = "complete",
            ["events"] = events,
        };
    }

    private static string Manifest(string host, string observedAt, bool complete, params JsonObject[] tools) => new JsonObject
    {
        ["environment"] = new JsonObject { ["host"] = host, ["account"] = Account },
        ["observed_at"] = observedAt,
        ["complete"] = complete,
        ["tools"] = new JsonArray(tools.Select(tool => (JsonNode?)tool).ToArray()),
    }.ToJsonString();

    private static JsonObject With(JsonObject data, string key, JsonNode? value)
    {
        var copy = PyNode.CopyObject(data);
        copy[key] = value;
        return copy;
    }

    [Fact]
    public void Trends_require_coverage_population_and_distinct_events()
    {
        using var root = TempRoot.Create();
        using var clock = Clock(Noon);
        using var store = new MemoryStore(Account, root.State);
        var row = Learning.Trend(store, "prep-gap", Legacy());
        Assert.Equal("candidate", (string)row["status"]!);
        Assert.Equal("inferred", (string)row["authority"]!);
        Assert.Equal(0.3, (double)row["metadata"]!["rate"]!);
        foreach (var invalid in new[]
                 {
                     With(Legacy(), "coverage", "partial"), With(Legacy(), "population", 2),
                     With(Legacy(), "event_ids", new JsonArray("evt1", "evt1", "evt1")), With(Legacy(), "event_ids", new JsonArray("evt1")),
                 })
        {
            Assert.Throws<StateError>(() => Learning.Trend(store, "invalid", invalid));
        }
    }

    [Fact]
    public void Trends_reject_future_window_ends_for_legacy_and_resolved_events()
    {
        using var root = TempRoot.Create();
        using var store = new MemoryStore(Account, root.State);
        const string now = "2026-09-06T12:00:00+00:00";
        var legacy = With(With(Legacy(), "window_start", "2026-09-01T00:00:00Z"), "window_end", now);
        legacy["trend_type"] = "tool_failures";
        legacy["population"] = 3;
        var resolved = With(With(Resolved(), "window_start", "2026-09-01T00:00:00Z"), "window_end", now);
        var index = 1;
        foreach (var entry in (JsonArray)resolved["events"]!)
        {
            entry!["occurred_at"] = $"2026-09-0{index++}T00:00:00Z";
        }

        using var clock = Clock(Noon);
        foreach (var (key, data) in new[] { ("closed-legacy", legacy), ("closed-resolved", resolved) })
        {
            var closed = Learning.Trend(store, key, data);
            Assert.Equal(PyDateTime.FromIsoformat(now), PyDateTime.FromIsoformat((string)closed["metadata"]!["window_end"]!));
        }

        const string future = "2026-09-06T12:00:01+00:00";
        foreach (var (key, data) in new[] { ("future-legacy", legacy), ("future-resolved", resolved) })
        {
            var error = Assert.Throws<StateError>(() => Learning.Trend(store, key, With(data, "window_end", future)));
            Assert.Contains("end at or before", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Lesson_review_deadline_follows_the_clock()
    {
        using var root = TempRoot.Create();
        File.WriteAllText(root.Combine("tools.json"), Manifest("synthetic-cli", "2026-08-01T00:00:00Z", complete: true, new JsonObject
        {
            ["name"] = "synthetic-fetch",
            ["version"] = "v1",
            ["exposed"] = true,
            ["input_schema"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("query") },
        }));
        using var store = new MemoryStore(Account, root.State);
        var observed = Learning.Capabilities(store, tools: root.Combine("tools.json"));
        var capability = store.Show(observed["memory_ids"]![0]);
        var fixture = new JsonObject
        {
            ["synthetic"] = true,
            ["environment"] = PyNode.Copy(capability["metadata"]!["environment_requirements"]),
            ["capability_id"] = PyNode.Copy(capability["id"]),
            ["input"] = new JsonObject { ["query"] = "synthetic" },
        };
        File.WriteAllText(root.Combine("fixture.json"), fixture.ToJsonString());
        var evidence = new JsonObject
        {
            ["kind"] = "synthetic_observation",
            ["fixture"] = new JsonObject { ["path"] = root.Combine("fixture.json"), ["sha256"] = PyIds.Sha256Hex(File.ReadAllBytes(root.Combine("fixture.json"))) },
        };
        capability = Learning.Validation(store, capability["id"], 1, evidence);
        var proposal = new JsonObject
        {
            ["title"] = "Use the exposed synthetic query input shape",
            ["scope"] = "synthetic query preparation",
            ["trigger"] = "Before preparing the synthetic query",
            ["goal"] = "Avoid an invalid input shape",
            ["preconditions"] = new JsonArray("Use the pinned host and tool schema"),
            ["procedure"] = new JsonArray("Provide a nonempty string query."),
            ["risk"] = "read_only",
            ["capability_refs"] = new JsonArray(new JsonObject { ["id"] = PyNode.Copy(capability["id"]), ["revision"] = 2 }),
            ["evidence"] = new JsonArray(new JsonObject { ["capability_id"] = PyNode.Copy(capability["id"]), ["observation"] = PyNode.Copy(evidence) }),
            ["counterexamples"] = new JsonArray(),
            ["review_after"] = "2099-01-01T00:00:00Z",
        };
        var lesson = Learning.LessonPlan(store, "deadline", proposal);
        var id = (string)lesson["id"]!;
        using (Clock(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)))
        {
            var error = Assert.Throws<StateError>(() => Learning.ActivateLesson(store, id, 1, Evidence(id)));
            Assert.Equal("lesson review deadline passed; revalidate before activation", error.Message);
        }

        var active = Learning.ActivateLesson(store, id, 1, Evidence(id));
        Assert.Equal("active", (string)active["status"]!);
        Assert.Equal("user_confirmed", (string)active["authority"]!);
        Assert.Equal("reviewed_scoped_recipe", (string)active["metadata"]!["applicability"]!);
    }

    [Fact]
    public void Host_observation_cannot_be_in_the_future_of_the_clock()
    {
        using var root = TempRoot.Create();
        File.WriteAllText(root.Combine("tools.json"), Manifest("h", "2026-09-06T12:00:01Z", complete: false));
        using var store = new MemoryStore(Account, root.State);
        using (Clock(Noon))
        {
            var error = Assert.Throws<StateError>(() => Learning.Capabilities(store, tools: root.Combine("tools.json")));
            Assert.Equal("host observation cannot be in the future", error.Message);
        }

        using (Clock(Noon.AddSeconds(1)))
        {
            Assert.Equal(0L, Int(Learning.Capabilities(store, tools: root.Combine("tools.json"))["observed"]!));
        }
    }

    [Fact]
    public void Capability_is_installed_not_validated_and_hash_change_creates_revision()
    {
        using var root = TempRoot.Create();
        Directory.CreateDirectory(root.Combine("skills", "example"));
        var path = root.Combine("skills", "example", "SKILL.md");
        File.WriteAllText(path, "# Example skill\n");
        using var store = new MemoryStore(Account, root.State);
        Assert.Equal(1L, Int(Learning.Capabilities(store, root.Combine("skills"))["observed"]!));
        var first = (JsonObject)store.List("agent")[0]!;
        Assert.Equal("not_validated", (string)first["metadata"]!["validation"]!);
        Learning.Capabilities(store, root.Combine("skills"));
        Assert.Equal(1L, Int(store.List("agent")[0]!["revision"]));
        File.WriteAllText(path, "# Example skill\nNew version");
        Learning.Capabilities(store, root.Combine("skills"));
        var second = store.List("agent")[0]!;
        Assert.Equal(2L, Int(second["revision"]));
        Assert.NotEqual((string)first["metadata"]!["content_hash"]!, (string)second["metadata"]!["content_hash"]!);
    }

    [Theory]
    [InlineData("400", "caller_mistake")]
    [InlineData("tool_not_found", "stale_host_binding")]
    [InlineData("403", "policy_denial")]
    [InlineData("not_implemented", "missing_capability")]
    [InlineData("invalid_json", "bad_data")]
    [InlineData("timeout", "transient_environment_failure")]
    [InlineData("Rate-Limited", "transient_environment_failure")]
    [InlineData("unknown_failure", "unclassified")]
    [InlineData("confirmed_defect", "unclassified")]
    public void Error_classification_never_generalizes_unknown_errors(string code, string category) =>
        Assert.Equal(category, Learning.ClassifyError(code));

    [Fact]
    public void Confirmed_defects_need_the_flag()
    {
        Assert.Equal("actual_defect", Learning.ClassifyError("confirmed_defect", confirmedDefect: true));
        Assert.Equal("unclassified", Learning.ClassifyError(null, confirmedDefect: true));
        Assert.Equal("caller_mistake", Learning.ClassifyError(400));
    }

    [Fact]
    public void Preference_import_does_not_treat_empty_template_examples_as_facts()
    {
        var sections = Learning.ParsePreferenceSections(
            "# Profile template\n## About me\n- **Name:** Dana\n- **Role:** {fill me in}\n- **Hours:** (e.g. 9 to 5)\n\n"
            + "## Scheduling defaults\nKeep 90-minute focus blocks where possible.\n");
        var joined = string.Join("\n", sections.Select(section => section.Text));
        Assert.Contains("Dana", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("{fill me in}", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("(e.g.", joined, StringComparison.Ordinal);
        Assert.Contains("90-minute", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrapped_and_parenthesized_template_fields_are_fully_removed() =>
        Assert.Empty(Learning.ParsePreferenceSections(
            "## Standing rules for triage\n- **Always flag:** {e.g. a direct ask,\n  customer escalations}\n"
            + "- **Delegate to:** (who handles what)\n- **Hours:** (e.g. mornings,\n  or evenings)\n"));

    [Fact]
    public void Preference_preview_of_an_oversized_file_changes_nothing()
    {
        using var root = TempRoot.Create();
        File.WriteAllBytes(root.Combine("oversized.md"), Enumerable.Repeat((byte)'x', Learning.MaxFileBytes + 1).ToArray());
        using var store = new MemoryStore(Account, root.State);
        var before = store.Conn.Execute("SELECT COUNT(*) FROM memory_meta").FetchOne()![0];
        var error = Assert.Throws<StateError>(() => Learning.PreferencesPlan(store, root.Combine("oversized.md")));
        Assert.Contains("1 MiB source budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, store.Conn.Execute("SELECT COUNT(*) FROM memory_meta").FetchOne()![0]);
    }

    [Fact]
    public void Preference_whitespace_class_matches_python_isspace()
    {
        // The example-field patterns spell out Python's \s; it must equal str.isspace() for every code point.
        var pattern = new Regex(Learning.PythonSpace, RegexOptions.CultureInvariant);
        for (var codePoint = 0; codePoint <= 0xFFFF; codePoint++)
        {
            if (codePoint is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            Assert.True(pattern.IsMatch(((char)codePoint).ToString()) == PyText.IsSpace(codePoint), "U+" + codePoint.ToString("X4"));
        }

        Assert.DoesNotContain(Enumerable.Range(0x10000, 0x100000), PyText.IsSpace);
    }

    [Fact]
    public void Skill_glob_sorts_by_path_components_like_pathlib()
    {
        using var root = TempRoot.Create();
        foreach (var name in new[] { "a-b", "a", "a.b", ".hidden", "Z" })
        {
            Directory.CreateDirectory(root.Combine("skills", name));
            File.WriteAllText(root.Combine("skills", name, "SKILL.md"), name);
        }

        Directory.CreateDirectory(root.Combine("skills", "empty"));
        var names = Learning.GlobSkills(root.Combine("skills")).Select(path => Path.GetFileName(Path.GetDirectoryName(path))).ToList();
        Assert.Equal([".hidden", "Z", "a", "a-b", "a.b"], names);
    }

    [Fact]
    public void Refs_deduplicate_by_digest_and_sort_by_canonical_json()
    {
        var refs = Learning.Refs([
            new JsonObject { ["kind"] = "tool_result", ["ref"] = "b" },
            new JsonObject { ["ref"] = "a", ["kind"] = "tool_result" },
            new JsonObject { ["ref"] = "b", ["kind"] = "tool_result" },
        ]);
        Assert.Equal("""[{"kind":"tool_result","ref":"a"},{"kind":"tool_result","ref":"b"}]""", StateStore.CanonicalJson(refs));
    }

    [Fact]
    public void Default_trend_is_a_fresh_copy()
    {
        var first = Learning.DefaultTrend;
        first["window_days"] = 1;
        Assert.Equal(90L, Int(Learning.DefaultTrend["window_days"]));
        Assert.Equal(Learning.DefaultTrend.Select(pair => pair.Key), Learning.Definition(Learning.DefaultTrend).Select(pair => pair.Key));
    }
}
