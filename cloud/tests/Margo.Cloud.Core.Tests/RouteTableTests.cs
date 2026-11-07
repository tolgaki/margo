using System.Text.Json.Nodes;
using Xunit;
using static Margo.Cloud.Core.Tests.Fixtures;

namespace Margo.Cloud.Core.Tests;

/// <summary>tests/test_autopilot_instances.py NamesAndRoutes.test_route_table_round_trip_and_validation, plus
/// byte-for-byte compatibility with blobs the Python provisioner writes.</summary>
public sealed class RouteTableTests
{
    private const string OtherKey = "7688250577852fdae674";

    // Golden blobs written by margo_autopilot.instances.RouteTable.dumps with the clock at Fixtures.Now.
    private const string EmptyBlob = """
        {
         "routes": {},
         "schema_version": 1
        }
        """;

    private const string FailedBlob = """
        {
         "routes": {
          "7688250577852fdae674": {
           "agent_identity_id": "agent-\u00e9-1",
           "agent_user_id": "user-\ud83d\ude00",
           "error": "Caf\u00e9 \ud83d\ude00 XS",
           "revision": 1,
           "state": "failed",
           "updated_at": "2026-11-07T09:30:00+00:00",
           "url": null
          },
          "8f6b44ff6f8dd73b5b3a": {
           "agent_identity_id": "agent-identity-1",
           "agent_user_id": "margo-user-1",
           "revision": 1,
           "state": "ready",
           "updated_at": "2026-11-07T09:30:00+00:00",
           "url": "https://margo-8f6b44ff6f8dd73b5b3a.internal.calmforest-1a2b3c.example.com"
          }
         },
         "schema_version": 1
        }
        """;

    private const string RetiredBlob = """
        {
         "routes": {
          "7688250577852fdae674": {
           "agent_identity_id": "agent-\u00e9-1",
           "agent_user_id": "user-\ud83d\ude00",
           "retired_at": "2026-11-07T09:30:00+00:00",
           "revision": 2,
           "state": "retired",
           "updated_at": "2026-11-07T09:30:00+00:00",
           "url": null
          },
          "8f6b44ff6f8dd73b5b3a": {
           "agent_identity_id": "agent-identity-1",
           "agent_user_id": "margo-user-1",
           "revision": 1,
           "state": "ready",
           "updated_at": "2026-11-07T09:30:00+00:00",
           "url": "https://margo-8f6b44ff6f8dd73b5b3a.internal.calmforest-1a2b3c.example.com"
          }
         },
         "schema_version": 1
        }
        """;

    private const string ForeignInput = """
        {"schema_version": 1.0, "extra": "dropped", "routes": {"8f6b44ff6f8dd73b5b3a": {"state": "ready", "url": null,
        "revision": "7", "note": "caf\u00e9", "n": [1.0, 1e16, 1e15, 0.0001, 0.00001, -0.0, -0, 12345678901234567890123,
        2.5e-7, 1E2, true, false, null, {}, []], "nested": {"b": 1, "a": {"z": "\u007f", "y": "\t"}}}}}
        """;

    private const string ForeignBlob = """
        {
         "routes": {
          "8f6b44ff6f8dd73b5b3a": {
           "n": [
            1.0,
            1e+16,
            1000000000000000.0,
            0.0001,
            1e-05,
            -0.0,
            0,
            12345678901234567890123,
            2.5e-07,
            100.0,
            true,
            false,
            null,
            {},
            []
           ],
           "nested": {
            "a": {
             "y": "\t",
             "z": "\u007f"
            },
            "b": 1
           },
           "note": "caf\u00e9",
           "revision": "7",
           "state": "ready",
           "url": null
          }
         },
         "schema_version": 1
        }
        """;

    private const string ForeignAfterPut = """
        {
         "routes": {
          "8f6b44ff6f8dd73b5b3a": {
           "agent_identity_id": "agent-identity-1",
           "agent_user_id": "margo-user-1",
           "revision": 8,
           "state": "suspended",
           "updated_at": "2026-11-07T09:30:00+00:00",
           "url": null
          }
         },
         "schema_version": 1
        }
        """;

    private static RouteTable Table() => new(new FixedTime(Now));

    [Fact]
    public void Route_table_round_trip_and_validation()
    {
        var table = Table();
        var key = Instances.InstanceKey(Identity, User);
        table.Put(key, Identity, User, RouteStates.Ready, url: Instances.InternalUrl(key, Domain));
        var again = RouteTable.Loads(table.Dumps());
        Assert.Equal(RouteStates.Ready, again.Resolve(Identity, User).Entry!.State);
        Assert.Equal(1, again.Get(key)!.Revision);
        again.Put(key, Identity, User, RouteStates.Retired);
        Assert.Equal((2L, table.Get(key)!.Url), (again.Get(key)!.Revision!.Value, again.Get(key)!.Url));
        Assert.NotNull(again.Get(key)!.RetiredAt);
        foreach (var text in new[]
        {
            "{",
            """{"schema_version": 9, "routes": {}}""",
            """{"schema_version": 1, "routes": {"x": {"state": "ready"}}}""",
            "{\"schema_version\": 1, \"routes\": {\"" + key + "\": {\"state\": \"dancing\"}}}",
        })
        {
            Assert.Throws<RouteException>(() => RouteTable.Loads(text));
        }

        Assert.Empty(RouteTable.Loads(string.Empty).Routes);
        Assert.Empty(RouteTable.Loads(null).Routes);
    }

    [Fact]
    public void Blobs_are_byte_for_byte_what_python_writes()
    {
        var table = Table();
        Assert.Equal(Lf(EmptyBlob), table.Dumps());
        var identity = "agent-" + U(0xe9) + "-1";
        var user = "user-" + U(0x1f600);
        table.Put(Key, Identity, User, RouteStates.Ready, url: Instances.InternalUrl(Key, Domain));
        var failed = table.Put(OtherKey, identity, user, RouteStates.Failed, error: "Caf" + U(0xe9) + " " + U(0x1f600) + " " + new string('x', 600));
        Assert.Equal(Lf(FailedBlob).Replace("XS", new string('x', 493), StringComparison.Ordinal), table.Dumps());
        Assert.Equal(RouteTable.MaxErrorLength, failed.Error!.EnumerateRunes().Count()); // code points, pair kept whole
        table.Put(OtherKey, identity, user, RouteStates.Retired);
        Assert.Equal(Lf(RetiredBlob), table.Dumps());
    }

    [Fact]
    public void Python_blobs_load_and_dump_unchanged()
    {
        var blob = Lf(RetiredBlob);
        Assert.Equal(blob, RouteTable.Loads(blob).Dumps());
        var foreign = RouteTable.Loads(ForeignInput, new FixedTime(Now));
        Assert.Equal(Lf(ForeignBlob), foreign.Dumps()); // unknown fields kept, numbers as Python prints them
        foreign.Put(Key, Identity, User, RouteStates.Suspended);
        Assert.Equal(Lf(ForeignAfterPut), foreign.Dumps()); // revision "7" + 1, url carried over (null)
    }

    [Fact]
    public void Validation_messages_match_python()
    {
        Assert.Equal("route table is not JSON", Assert.Throws<RouteException>(() => RouteTable.Loads("{")).Message);
        Assert.Equal("route table is not JSON", Assert.Throws<RouteException>(() => RouteTable.Loads("  ")).Message);
        Assert.Equal("unsupported route table", Assert.Throws<RouteException>(() => RouteTable.Loads("[]")).Message);
        Assert.Equal("unsupported route table", Assert.Throws<RouteException>(() => RouteTable.Loads("""{"schema_version": "1", "routes": {}}""")).Message);
        Assert.Equal("route table has no routes", Assert.Throws<RouteException>(() => RouteTable.Loads("""{"schema_version": 1}""")).Message);
        Assert.Equal("route table has no routes", Assert.Throws<RouteException>(() => RouteTable.Loads("""{"schema_version": 1, "routes": []}""")).Message);
        foreach (var entry in new[] { "[]", "{}", """{"state": null}""", """{"state": 1}""", """{"state": ["ready"]}""", """{"state": "Ready"}""" })
        {
            var text = "{\"schema_version\": 1, \"routes\": {\"" + Key + "\": " + entry + "}}";
            Assert.Equal("malformed route entry", Assert.Throws<RouteException>(() => RouteTable.Loads(text)).Message);
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("true")] // Python: True == 1
    public void Schema_version_compares_like_python(string version)
    {
        var table = RouteTable.Loads("{\"schema_version\": " + version + ", \"routes\": {}}");
        Assert.Equal(0, table.Count);
        Assert.Equal(Lf(EmptyBlob), table.Dumps());
    }

    [Fact]
    public void A_repeated_key_keeps_the_last_value_like_python()
    {
        var text = "{\"schema_version\": 1, \"routes\": {\"" + Key + "\": {\"state\": \"dancing\"}, \"" + Key + "\": {\"state\": \"ready\"}}}";
        Assert.Equal(RouteStates.Ready, RouteTable.Loads(text).Get(Key)!.State);
    }

    [Fact]
    public void Put_rebuilds_the_entry_and_counts_revisions()
    {
        var table = RouteTable.Loads(
            "{\"schema_version\": 1, \"routes\": {\"" + Key + "\": {\"state\": \"ready\", \"url\": \"https://old.example.com\", \"revision\": 2.9, \"custom\": 1}, \""
            + OtherKey + "\": {\"state\": \"ready\", \"revision\": true}}}", new FixedTime(Now));
        var entry = table.Put(Key, Identity, User, RouteStates.Suspended);
        Assert.Equal(3, entry.Revision); // int(2.9) + 1
        Assert.Equal("https://old.example.com", entry.Url);
        Assert.False(entry.ToJsonObject().ContainsKey("custom"));
        Assert.Equal("2026-11-07T09:30:00+00:00", entry.UpdatedAt);
        Assert.Null(entry.RetiredAt);
        Assert.Null(entry.Error);
        Assert.Equal(2, table.Put(OtherKey, Identity, "margo-user-2", RouteStates.Ready, url: string.Empty).Revision); // int(True) + 1
        Assert.Equal(string.Empty, table.Get(OtherKey)!.Url); // an empty url is still a url, as in Python
        Assert.Equal([Key, OtherKey], table.Routes.Select(pair => pair.Key)); // replaced in place
    }

    [Fact]
    public void Put_refuses_unknown_states_and_bad_revisions_without_changing_anything()
    {
        var table = Table();
        Assert.Equal("unknown route state", Assert.Throws<RouteException>(() => table.Put(Key, Identity, User, "dancing")).Message);
        Assert.Throws<RouteException>(() => table.Put(Key, Identity, User, null));
        Assert.Equal(0, table.Count);
        foreach (var revision in new[] { "null", "\"seven\"", "[]", "{}", "\"7.0\"" })
        {
            var blob = "{\"schema_version\": 1, \"routes\": {\"" + Key + "\": {\"state\": \"ready\", \"revision\": " + revision + "}}}";
            var loaded = RouteTable.Loads(blob);
            var before = loaded.Dumps();
            Assert.Throws<RouteException>(() => loaded.Put(Key, Identity, User, RouteStates.Ready)); // Python: TypeError or ValueError
            Assert.Equal(before, loaded.Dumps());
        }

        var spaced = RouteTable.Loads("{\"schema_version\": 1, \"routes\": {\"" + Key + "\": {\"state\": \"ready\", \"revision\": \" +1_0 \"}}}");
        Assert.Equal(11, spaced.Put(Key, Identity, User, RouteStates.Ready).Revision); // int(" +1_0 ") == 10
    }

    [Fact]
    public void Put_does_not_validate_the_key_but_loading_does()
    {
        var table = Table();
        table.Put("not-a-key", Identity, User, RouteStates.Ready);
        Assert.Equal("malformed route entry", Assert.Throws<RouteException>(() => RouteTable.Loads(table.Dumps())).Message);
    }

    [Fact]
    public void Resolve_returns_nothing_for_identifiers_that_cannot_form_a_key()
    {
        var table = Table();
        Assert.Equal(new RouteLookup(null, null), table.Resolve("x", "x"));
        Assert.Equal(new RouteLookup(null, null), table.Resolve(null, User));
        var lookup = table.Resolve(Identity, User);
        Assert.Equal(Key, lookup.Key);
        Assert.Null(lookup.Entry);
    }

    [Fact]
    public void Entries_are_snapshots()
    {
        var table = Table();
        var first = table.Put(Key, Identity, User, RouteStates.Provisioning);
        var json = first.ToJsonObject();
        json["state"] = "ready";
        table.Put(Key, Identity, User, RouteStates.Ready);
        Assert.Equal(RouteStates.Provisioning, first.State);
        Assert.Equal(RouteStates.Ready, table.Get(Key)!.State);
        Assert.Equal(Identity, table.Routes.Single().Value.AgentIdentityId);
        Assert.Equal(User, table.Get(Key)!.AgentUserId);
    }

    [Fact]
    public void Non_ascii_errors_are_escaped_and_cut_at_code_points()
    {
        var table = Table();
        var error = new string('e', 499) + U(0x1f600) + "tail";
        var entry = table.Put(Key, Identity, User, RouteStates.Failed, error: error);
        Assert.Equal(new string('e', 499) + U(0x1f600), entry.Error);
        Assert.Contains("\\ud83d\\ude00\"", table.Dumps(), StringComparison.Ordinal);
        Assert.Null(table.Put(Key, Identity, User, RouteStates.Failed, error: string.Empty).Error);
    }

    [Fact]
    public void Lone_surrogate_escapes_are_refused_as_not_json()
    {
        // Documented deviation: Python accepts "\ud800" in a string; System.Text.Json cannot hold it.
        var text = "{\"schema_version\": 1, \"routes\": {\"" + Key + "\": {\"state\": \"ready\", \"note\": \"\\ud800\"}}}";
        Assert.Equal("route table is not JSON", Assert.Throws<RouteException>(() => RouteTable.Loads(text)).Message);
    }

    [Fact]
    public void Raw_lone_surrogates_are_refused_as_not_json_too()
    {
        // A raw lone surrogate (not an escape) used to escape as a plain ArgumentException.
        var text = "{\"schema_version\": 1, \"routes\": {\"" + Key + "\": {\"state\": \"ready\", \"note\": \"" + LoneSurrogate + "\"}}}";
        Assert.Equal("route table is not JSON", Assert.Throws<RouteException>(() => RouteTable.Loads(text)).Message);
        var key = "{\"schema_version\": 1, \"routes\": {\"" + LoneSurrogate + "\": {\"state\": \"ready\"}}}";
        Assert.Equal("route table is not JSON", Assert.Throws<RouteException>(() => RouteTable.Loads(key)).Message);
    }

    [Fact]
    public void Integers_python_cannot_convert_are_refused_as_python_does()
    {
        // Expected results from Python 3.13 (int/str conversion limited to 4300 digits).
        var nines = new string('9', 4300);
        string Blob(string revision) => "{\"schema_version\": 1, \"routes\": {\"" + Key + "\": {\"state\": \"ready\", \"revision\": " + revision + "}}}";

        // json.loads raises ValueError for a longer int literal: the table is not JSON.
        Assert.Equal("route table is not JSON", Assert.Throws<RouteException>(() => RouteTable.Loads(Blob("1" + nines))).Message);

        // int() of a longer revision string raises ValueError in put; nothing changes.
        var longText = RouteTable.Loads(Blob("\"1" + nines + "\""));
        var before = longText.Dumps();
        Assert.Equal("malformed route entry", Assert.Throws<RouteException>(() => longText.Put(Key, Identity, User, RouteStates.Ready)).Message);
        Assert.Equal(before, longText.Dumps());

        // A 4300-digit revision loads and is incremented, but json.dumps cannot write the 4301-digit
        // result (ValueError), so no blob that Python cannot read is ever written.
        foreach (var revision in new[] { nines, "\"" + nines + "\"" })
        {
            var table = RouteTable.Loads(Blob(revision), new FixedTime(Now));
            Assert.Contains(nines, table.Dumps(), StringComparison.Ordinal);
            var entry = table.Put(Key, Identity, User, RouteStates.Suspended);
            Assert.Null(entry.Revision); // does not fit a long
            Assert.Equal(RouteStates.Suspended, table.Get(Key)!.State);
            var error = Assert.Throws<ArgumentException>(() => table.Dumps());
            Assert.StartsWith("Exceeds the limit (4300 digits) for integer string conversion", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void States_and_raw_entries_are_exposed()
    {
        Assert.Equal(["ready", "provisioning", "suspended", "retired", "failed"], RouteStates.All);
        var table = Table();
        table.Put(Key, Identity, User, RouteStates.Ready, url: "https://margo.example.com");
        var json = table.Get(Key)!.ToJsonObject();
        Assert.Equal(["agent_identity_id", "agent_user_id", "state", "url", "revision", "updated_at"], json.Select(pair => pair.Key));
        Assert.IsType<JsonObject>(json);
    }
}
