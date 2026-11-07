using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.MemoryCommands;
using Margo.Cloud.State.Tasks;
using Xunit;

namespace Margo.Cloud.State.Tests.MemoryCommands;

/// <summary>
/// Library-level ports for what the parity scenarios (<c>cloud/contracts/state/memory-commands/</c>) cannot show:
/// <see cref="PyZoneInfo"/> against CPython-measured offsets (the scenarios also sweep every system zone), the Dream
/// window under a fixed clock, <c>tests/test_memory_dream.py::test_concurrent_corrections_have_one_revision_owner</c>
/// (two connections racing), the per-flow <c>MAX_SCAN</c> override, the chunked Dream input reader and an in-process
/// <c>memory_state.py</c> round trip.
/// </summary>
public sealed class MemoryCommandsTests
{
    private const string Account = "dream-fixture@example.com";
    private const string Host = "fixture-host";
    private const string Workspace = "fixture-workspace";

    private static readonly int[] Numbers = [2, 3];

    private static bool HasZones => File.Exists("/usr/share/zoneinfo/America/New_York");

    // python3 -c 'datetime.fromisoformat(T).replace(tzinfo=ZoneInfo(K), fold=f).utcoffset()' (CPython 3.13.16, tzdata of the image).
    [Theory]
    [InlineData("America/New_York", "2021-03-14T02:30:00", -18000, -14400)]
    [InlineData("America/New_York", "2021-11-07T01:30:00", -14400, -18000)]
    [InlineData("Asia/Kolkata", "1850-06-01T00:00:00", 21208, 21208)]
    [InlineData("Europe/Dublin", "2024-10-27T01:30:00", 3600, 0)]
    [InlineData("Africa/Cairo", "2040-10-25T23:30:00", 10800, 7200)]
    [InlineData("Pacific/Apia", "2011-12-30T12:00:00", -36000, 50400)]
    [InlineData("Australia/Lord_Howe", "2024-04-07T01:45:00", 39600, 37800)]
    [InlineData("Antarctica/Troll", "2040-03-25T02:00:00", 0, 7200)]
    [InlineData("Europe/London", "1800-01-01T00:00:00", -75, -75)]
    public void Zone_offsets_follow_pep_495_folds_like_cpython(string key, string wall, long fold0, long fold1)
    {
        Assert.SkipUnless(HasZones, "no system time zone database");
        var zone = PyZoneInfo.Load(key);
        var local = PyDateTime.FromIsoformat(wall);
        Assert.Equal(fold0, (long)zone.UtcOffset(local, 0).TotalSeconds());
        Assert.Equal(fold1, (long)zone.UtcOffset(local, 1).TotalSeconds());
    }

    [Theory]
    [InlineData("America/New_York", "2021-11-07T06:30:00+00:00", "2021-11-07T01:30:00")]
    [InlineData("Pacific/Apia", "2011-12-30T10:00:00+00:00", "2011-12-31T00:00:00")]
    [InlineData("Africa/Cairo", "2040-10-25T21:00:00+00:00", "2040-10-25T23:00:00")]
    public void From_utc_gives_cpython_wall_times(string key, string utc, string wall)
    {
        Assert.SkipUnless(HasZones, "no system time zone database");
        Assert.Equal(wall, PyZoneInfo.Load(key).FromUtc(PyDateTime.FromIsoformat(utc)).Isoformat());
    }

    [Theory]
    [InlineData("", "ValueError", "ZoneInfo keys must be normalized relative paths, got: ")]
    [InlineData("/etc/localtime", "ValueError", "ZoneInfo keys may not be absolute paths, got: /etc/localtime")]
    [InlineData("a/../b", "ValueError", "ZoneInfo keys must be normalized relative paths, got: a/../b")]
    [InlineData("..", "ValueError", "ZoneInfo keys must refer to subdirectories of TZPATH, got: ..")]
    [InlineData("Mars/Base", "ZoneInfoNotFoundError", "'No time zone found with key Mars/Base'")]
    public void Zone_keys_are_validated_like_tzpath(string key, string type, string message)
    {
        var error = Assert.IsAssignableFrom<PyException>(Record.Exception(() => PyZoneInfo.Load(key)));
        Assert.Equal(type, error.PythonType);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Zone_keys_that_are_not_strings_raise_type_errors()
    {
        Assert.Equal("unhashable type: 'list'", Assert.Throws<PyTypeError>(() => PyZoneInfo.Load(new JsonArray("UTC"))).Message);
        Assert.Equal("expected str, bytes or os.PathLike object, not int",
                     Assert.Throws<PyTypeError>(() => PyZoneInfo.Load(JsonValue.Create(5))).Message);
        Assert.Equal("expected str, bytes or os.PathLike object, not NoneType", Assert.Throws<PyTypeError>(() => PyZoneInfo.Load((JsonNode?)null)).Message);
    }

    [Fact]
    public void An_empty_tzpath_has_no_zones_but_utc_needs_none()
    {
        using var root = TempRoot.Create(new Dictionary<string, string> { ["PYTHONTZPATH"] = "" });
        Assert.Empty(PyZoneInfo.TzPath());
        Assert.IsType<ZoneInfoNotFoundError>(Record.Exception(() => PyZoneInfo.Load("America/New_York")));
        var (start, end) = MemoryDream.Window(Request("2020-02-03", "UTC", "00:00"));
        Assert.Equal("2020-02-03T00:00:00+00:00", start.Isoformat());
        Assert.Equal("2020-02-04T00:00:00+00:00", end.Isoformat());
    }

    [Theory]
    [InlineData("a//b", "a/b")]
    [InlineData("a/./b/", "a/b")]
    [InlineData("a/../b", "b")]
    [InlineData("../a", "../a")]
    [InlineData("a/..", ".")]
    [InlineData("", ".")]
    [InlineData("//a", "//a")]
    [InlineData("///a/", "/a")]
    public void Normpath_matches_posixpath(string path, string expected) => Assert.Equal(expected, PyZoneInfo.NormPath(path));

    [Fact]
    public void Dream_window_refuses_folds_gaps_and_unfinished_days_under_a_fixed_clock()
    {
        Assert.SkipUnless(HasZones, "no system time zone database");
        using var clock = PyRuntime.Override(clock: () => new DateTimeOffset(2021, 11, 8, 6, 0, 0, TimeSpan.Zero));
        var (start, end) = MemoryDream.Window(Request("2021-11-06", "America/New_York", "02:00"));
        Assert.Equal("2021-11-06T06:00:00+00:00", start.Isoformat());
        Assert.Equal("2021-11-07T07:00:00+00:00", end.Isoformat());
        Assert.Equal("invalid Dream daily window: ambiguous/nonexistent cutoff; choose another local time",
                     Assert.Throws<StateError>(() => MemoryDream.Window(Request("2021-11-06", "America/New_York", "01:30"))).Message);
        Assert.Equal("invalid Dream daily window: ambiguous/nonexistent cutoff; choose another local time",
                     Assert.Throws<StateError>(() => MemoryDream.Window(Request("2021-03-13", "America/New_York", "02:30"))).Message);
        // The window ending 2021-11-08T07:00Z has not completed at 06:00Z.
        Assert.Equal("invalid Dream daily window: the daily cutoff has not completed",
                     Assert.Throws<StateError>(() => MemoryDream.Window(Request("2021-11-07", "America/New_York", "02:00"))).Message);
        Assert.IsType<PyOverflowError>(Record.Exception(() => MemoryDream.Window(Request("0001-01-01", "Asia/Tokyo", "00:00"))));
    }

    [Fact]
    public async Task Max_scan_override_is_scoped_to_the_flow()
    {
        Assert.Equal(500, MemoryDream.MaxScan);
        MemoryDream.OverrideMaxScan(1);
        try
        {
            Assert.Equal(1, MemoryDream.MaxScan);
            Assert.Equal(500, await Task.Run(() => { MemoryDream.OverrideMaxScan(null); return MemoryDream.MaxScan; }));
            Assert.Equal(1, MemoryDream.MaxScan);
        }
        finally
        {
            MemoryDream.OverrideMaxScan(null);
        }
    }

    [Fact]
    public void Concurrent_corrections_have_one_revision_owner()
    {
        using var root = TempRoot.Create();
        using (new TaskStore(Account, root.State))
        using (var memory = new MemoryStore(Account, root.State))
        {
            Configure(memory);
            Assert.Equal(1L, (long)MemoryDream.Checkpoint(memory, Event("host-rev-1", null, "Consider the Cedar option. Timing is open."))["revision"]!);
        }

        using var barrier = new Barrier(2);
        string Revise(int number)
        {
            using var memory = new MemoryStore(Account, root.State);
            barrier.SignalAndWait();
            try
            {
                return MemoryDream.Checkpoint(memory, Event("revision-" + number, 1, "Changed " + number))["revision"]!.ToString();
            }
            catch (StateError)
            {
                return "conflict";
            }
        }

        var environment = PyRuntime.EnvironmentSnapshot();
        var results = Numbers.AsParallel().WithDegreeOfParallelism(2)
                                     .Select(number => { using (PyRuntime.Override(environment)) { return Revise(number); } })
                                     .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Assert.Equal(["2", "conflict"], results);
        using var check = new MemoryStore(Account, root.State);
        Assert.Equal(2L, (long)check.Show(check.RecordId("user", Key()))["revision"]!);
    }

    [Fact]
    public void Dream_inputs_are_read_in_text_wrapper_chunks()
    {
        // read(100001) of a file: the first chunk is 100001 bytes; an undecodable byte after it is never decoded.
        var data = Encoding.UTF8.GetBytes("{\"x\": \"" + new string('a', 120000) + "\"}");
        data[^3] = 0xff;
        var reads = new List<int>();
        var offset = 0;
        var text = PyTextFile.ReadChunks(size =>
        {
            reads.Add(size);
            var count = Math.Min(size, data.Length - offset);
            var chunk = data.AsSpan(offset, count).ToArray();
            offset += count;
            return chunk;
        }, MemoryDream.MaxInput + 1);
        Assert.Equal(MemoryDream.MaxInput + 1, PyRepr.Length(text));
        Assert.Equal([100001], reads);
    }

    [Fact]
    public void Memory_state_runs_in_process()
    {
        using var root = TempRoot.Create();
        var script = new MemoryStateScript();
        var (code, output, error) = Run(script, root, ["record-id", "user", "future"]);
        Assert.Equal(0, code);
        Assert.Equal("{\"id\":\"" + MemoryStore.MemoryIdentity(Account, "user", "future").Id + "\"}\n", output);
        Assert.Equal("", error);
        (code, _, error) = Run(script, root, ["list"]);
        Assert.Equal(2, code);
        Assert.Contains("\"code\":\"not_initialized\"", error, StringComparison.Ordinal);
        (code, output, _) = Run(script, root, ["init"]);
        Assert.Equal(0, code);
        Assert.Contains("\"schema_version\":\"2\"", output, StringComparison.Ordinal);
        (code, _, error) = Run(script, root, ["put", "k", "--input", "-"], "{\"dat\": {}}");
        Assert.Equal(2, code);
        Assert.Contains("MemoryStore.put() got an unexpected keyword argument 'dat'. Did you mean 'data'?", error, StringComparison.Ordinal);
    }

    private static (int Code, string Output, string Error) Run(MemoryStateScript script, TempRoot root, string[] args, string stdin = "")
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = script.Run(["--account", Account, "--state-dir", root.State, .. args], new StringReader(stdin), output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static JsonObject Request(string day, string zone, string cutoff) => new()
    {
        ["host"] = Host,
        ["workspace"] = Workspace,
        ["day"] = day,
        ["timezone"] = zone,
        ["cutoff"] = cutoff,
        ["lookback_days"] = 1,
        ["after"] = null,
    };

    private static string Key() => "dream-event:" + MemoryGovernance.Digest(StateStore.CanonicalJson(
        new JsonArray(Account, Host, Workspace, "fixture-session", "event-1")));

    private static JsonObject Event(string revision, long? expected, string text) => new()
    {
        ["capability"] = new JsonObject
        {
            ["account"] = Account,
            ["host"] = Host,
            ["workspace"] = Workspace,
            ["adapter"] = "margo-session-checkpoint-v1",
            ["content_access"] = "current-session",
            ["session_list"] = false,
            ["authorized"] = true,
        },
        ["session"] = "fixture-session",
        ["event"] = "event-1",
        ["revision"] = revision,
        ["expected_revision"] = expected,
        ["timestamp"] = "2020-02-03T12:00:00+00:00",
        ["speaker"] = "user",
        ["locator"] = "conversation:fixture-session/event-1",
        ["text"] = text,
        ["origin"] = "margo-session",
        ["sensitivity"] = "private",
        ["allowed_uses"] = new JsonArray("reasoning"),
        ["entities"] = new JsonArray("project:cedar"),
        ["work_refs"] = new JsonArray(),
    };

    private static void Configure(MemoryStore memory)
    {
        var policy = new JsonObject
        {
            ["capture"] = new JsonObject
            {
                ["enabled"] = true,
                ["domains"] = new JsonArray("user"),
                ["kinds"] = new JsonArray("episode", "decision"),
                ["scopes"] = new JsonArray(MemoryDream.ScopeFor(Account, Host, Workspace)),
                ["source_kinds"] = new JsonArray("session_checkpoint", "memory_record"),
            },
            ["allow_sensitive"] = false,
            ["usage_enabled"] = false,
            ["usage_retention_days"] = 30,
            ["retention_days"] = new JsonObject(),
            ["review_days"] = new JsonObject(),
        };
        var preview = MemoryGovernance.PolicyPreview(memory, policy);
        MemoryGovernance.SetPolicy(memory, policy, new JsonObject
        {
            ["kind"] = "human_confirmation",
            ["actor"] = "alex@example.com",
            ["statement"] = "I reviewed this exact synthetic memory change.",
            ["evidence_ref"] = "conversation:synthetic/session/turn/1",
            ["subject_id"] = PyNode.Copy(preview["subject_id"]),
            ["revision"] = 1,
            ["decision"] = "configure",
            ["decided_at"] = "2020-01-01T00:00:00+00:00",
        });
    }
}
