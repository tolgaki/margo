using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;
using Xunit;

namespace Margo.Cloud.State.Tests.Work;

/// <summary>
/// Library-level ports of <c>tests/test_work_state.py</c> (real transactional SQLite in a private temporary root)
/// for what the CLI parity scenarios cannot show: concurrency, the clock, nested transactions, borrowed
/// connections and the API other ports call (Guard: show/history/event/finish; Orchestrator: find_source).
/// </summary>
public sealed class LedgerTests
{
    private const string Account = "fictional-account@example.com";

    private static Ledger Open(TempRoot root, string account = Account) => new(account, root.State);

    private static JsonObject Source(Ledger ledger, string revision = "v1", JsonNode? evidence = null) =>
        ledger.Source("mail", "inbox", "message-1", revision, evidence ?? new JsonObject { ["quote"] = "Please review the fictional specification." },
                      "https://example.com/mail/1");

    private static JsonObject Evidence(string subject, long revision = 1, string decision = "confirm", string? actionHash = null)
    {
        var value = new JsonObject
        {
            ["kind"] = "human_confirmation",
            ["actor"] = "alex@example.com",
            ["statement"] = "I reviewed this exact revision and approve the stated change.",
            ["evidence_ref"] = "conversation:fictional/session/turn/42",
            ["subject_id"] = subject,
            ["revision"] = revision,
            ["decision"] = decision,
            ["decided_at"] = StateStore.UtcNow(),
        };
        if (actionHash is not null)
        {
            value["action_hash"] = actionHash;
        }

        return value;
    }

    private static JsonObject Item(Ledger ledger, JsonObject reference, string key = "claim-1") =>
        ledger.Ingest(new JsonObject
        {
            ["title"] = "Review fictional specification",
            ["owner"] = null,
            ["due"] = null,
            ["direction"] = "owe",
            ["source_refs"] = new JsonArray(PyDynamic.Clone(reference)),
        }, "claim-key-" + key);

    private static JsonObject ActionData(JsonObject reference, string itemId) => new()
    {
        ["kind"] = "mail.reply",
        ["target"] = new JsonObject { ["message_id"] = "message-1", ["recipients"] = new JsonArray("blair@example.com") },
        ["payload"] = new JsonObject { ["body"] = "The fictional review is complete." },
        ["why"] = "Explicit follow-up request",
        ["source_refs"] = new JsonArray(PyDynamic.Clone(reference)),
        ["target_fingerprint"] = "target-v1",
        ["work_item_id"] = itemId,
    };

    private static string Stamp(int minutes = 0) => (PyDateTime.Now() + PyTimeDelta.FromMinutes(minutes)).Isoformat();

    private static JsonObject Approved(Ledger ledger, JsonObject reference)
    {
        var item = Item(ledger, reference);
        var action = ledger.Propose(ActionData(reference, (string)item["id"]!));
        var id = (string)action["id"]!;
        var hash = (string)action["action_hash"]!;
        return ledger.Approve(id, 1, hash, Evidence(id, 1, "approve", hash), Stamp(60));
    }

    private static JsonObject Fresh(JsonObject reference, string? checkedAt = null) => new()
    {
        ["checked_at"] = checkedAt ?? Stamp(),
        ["target_fingerprint"] = "target-v1",
        ["source_refs"] = new JsonArray(PyDynamic.Clone(reference)),
    };

    private static JsonObject Receipt(string state = "succeeded")
    {
        var value = new JsonObject
        {
            ["kind"] = "tool_result",
            ["reference"] = "tool:fictional-result-1",
            ["outcome"] = state,
            ["recorded_at"] = Stamp(),
        };
        if (state == "failed")
        {
            value["definitive_no_effect"] = true;
        }

        return value;
    }

    [Fact]
    public void Default_account_fails_and_accounts_are_isolated()
    {
        using var root = TempRoot.Create();
        Assert.ThrowsAny<StateError>(() => new Ledger(null, root.State));
        using var ledger = Open(root);
        var reference = Source(ledger);
        var item = Item(ledger, reference);
        using var other = Open(root, "other-fictional-account@example.com");
        Assert.ThrowsAny<StateError>(() => other.Show((string)item["id"]!));
        var source = other.Source("mail", "inbox", "message-1", "v1", new JsonObject { ["quote"] = "Different account" }, "https://example.com/1");
        Assert.NotEqual((string?)reference["source_id"], (string?)source["source_id"]);
    }

    [Fact]
    public void Find_source_is_an_exact_account_scoped_lookup()
    {
        using var root = TempRoot.Create();
        using var ledger = Open(root);
        var reference = Source(ledger);
        var found = ledger.FindSource("mail", "inbox", "message-1")!;
        Assert.Equal((string?)reference["source_id"], (string?)found["source_id"]);
        Assert.Equal("v1", (string?)found["revision"]);
        Assert.Null(ledger.FindSource("mail", "inbox", "message-2"));
        Assert.Null(ledger.FindSource("mail", "sent", "message-1"));
        Assert.ThrowsAny<StateError>(() => ledger.FindSource("mail", "inbox", ""));
        using var other = Open(root, "other-fictional-account@example.com");
        Assert.Null(other.FindSource("mail", "inbox", "message-1"));
    }

    [Fact]
    public void Exact_replay_survives_restart_without_semantic_merge()
    {
        using var root = TempRoot.Create();
        string originalId;
        using (var ledger = Open(root))
        {
            var reference = Source(ledger);
            originalId = (string)Item(ledger, reference)["id"]!;
            Assert.Equal(originalId, (string?)Item(ledger, reference)["id"]);
            Assert.NotEqual(originalId, (string?)Item(ledger, reference, "other-extraction-key")["id"]);
        }

        using var reopened = Open(root);
        var again = Source(reopened);
        Assert.Equal(originalId, (string?)Item(reopened, again)["id"]);
        var changed = (JsonObject)PyDynamic.Clone(reopened.Show(originalId)["data"])!;
        changed["title"] = "A different claim";
        Assert.ThrowsAny<StateError>(() => reopened.Ingest(changed, "claim-key-claim-1"));
        Assert.Equal(2, ((JsonArray)reopened.List("all")["items"]!).Count);
    }

    [Fact]
    public async Task Concurrent_begin_starts_exactly_one_attempt()
    {
        using var root = TempRoot.Create();
        JsonObject reference;
        JsonObject action;
        using (var ledger = Open(root))
        {
            reference = Source(ledger);
            action = Approved(ledger, reference);
        }

        var id = (string)action["id"]!;
        using var barrier = new Barrier(2);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using var ledger = Open(root);
            barrier.SignalAndWait();
            try
            {
                return (object)ledger.Begin(id, 1, Fresh(reference));
            }
            catch (StateError)
            {
                return "blocked";
            }
        })));
        Assert.Single(results, result => result is "blocked");
        Assert.Single(results, result => result is JsonObject);
        using var check = Open(root);
        Assert.Single((JsonArray)check.Show(id)["executions"]!);
    }

    [Fact]
    public void Approval_expiry_and_preflight_freshness_follow_the_clock()
    {
        using var root = TempRoot.Create();
        var start = DateTimeOffset.UtcNow;
        JsonObject reference;
        JsonObject action;
        string checkedAt;
        using (PyRuntime.Override(clock: () => start))
        {
            using var ledger = Open(root);
            reference = Source(ledger);
            action = Approved(ledger, reference);
            checkedAt = Stamp();
        }

        var id = (string)action["id"]!;
        using (PyRuntime.Override(clock: () => start.AddMinutes(6)))
        {
            using var ledger = Open(root);
            var error = Assert.ThrowsAny<StateError>(() => ledger.Begin(id, 1, Fresh(reference, checkedAt)));
            Assert.Equal("preflight must be a fresh foreground read within five minutes", error.Message);
            Assert.Equal("approved", (string?)ledger.Show(id)["state"]);
        }

        using (PyRuntime.Override(clock: () => start.AddHours(2)))
        {
            using var ledger = Open(root);
            var error = Assert.ThrowsAny<StateError>(() => ledger.Begin(id, 1, Fresh(reference)));
            Assert.Equal("approval has expired or was invalidated", error.Message);
            Assert.Empty((JsonArray)ledger.Show(id)["executions"]!);
        }
    }

    [Fact]
    public void Uncertain_writes_are_reconciled_never_retried_and_guard_style_claims_settle_once()
    {
        using var root = TempRoot.Create();
        using var ledger = Open(root);
        var reference = Source(ledger);
        var action = Approved(ledger, reference);
        var id = (string)action["id"]!;
        var attempt = ledger.Begin(id, 1, Fresh(reference));
        var attemptId = (string)attempt["attempt_id"]!;
        Assert.Equal(action["payload"]!.ToJsonString(), attempt["payload"]!.ToJsonString());

        // LedgerClaimJournal.mark_dispatched: an event recorded inside the ledger's transaction.
        ledger.Transaction(() =>
        {
            Assert.Equal("executing", (string?)ledger.Show(id)["state"]);
            ledger.Event(id, "workiq_dispatched", new JsonObject { ["attempt_id"] = attemptId, ["dispatched_at"] = StateStore.UtcNow() });
        });
        Assert.Contains(((JsonArray)ledger.History(id)["events"]!).Select(e => (string?)e!["event"]), name => name == "workiq_dispatched");

        ledger.Finish(attemptId, "outcome_unknown");
        Assert.ThrowsAny<StateError>(() => ledger.Begin(id, 1, Fresh(reference)));
        Assert.ThrowsAny<StateError>(() => ledger.EditAction(id, 1, ActionData(reference, (string)action["work_item_id"]!)));
        Assert.ThrowsAny<StateError>(() => ledger.Finish(attemptId, "succeeded", reconcile: true));
        var settled = ledger.Finish(attemptId, "succeeded", Receipt(), reconcile: true);
        Assert.Equal("succeeded", (string?)settled["state"]);
        Assert.ThrowsAny<StateError>(() => ledger.Finish(attemptId, "succeeded", Receipt(), reconcile: true));
    }

    [Fact]
    public void Nested_transactions_use_savepoints_and_borrowed_ledgers_never_repair()
    {
        using var root = TempRoot.Create();
        using var ledger = Open(root);
        var reference = Source(ledger);
        var sourceId = (string)reference["source_id"]!;
        var before = ((JsonArray)ledger.History(sourceId)["events"]!).Count;

        Assert.Throws<InvalidOperationException>(() => ledger.Transaction(() =>
        {
            ledger.Event(sourceId, "inner", new JsonObject());
            throw new InvalidOperationException("fictional failure");
        }));
        Assert.Equal(before, ((JsonArray)ledger.History(sourceId)["events"]!).Count);

        ledger.Transaction(() =>
        {
            var borrowed = Ledger.FromConnection(ledger.Conn, Account);
            Assert.Throws<InvalidOperationException>(() => borrowed.Transaction(() =>
            {
                borrowed.Event(sourceId, "rolled-back", new JsonObject());
                throw new InvalidOperationException("inner failure");
            }));
            borrowed.Event(sourceId, "kept", new JsonObject());
            borrowed.Close(); // a borrowed connection stays open
            Assert.False(ledger.Conn.IsClosed);
        });
        var events = ((JsonArray)ledger.History(sourceId)["events"]!).Select(e => (string?)e!["event"]).ToList();
        Assert.Contains("kept", events);
        Assert.DoesNotContain("rolled-back", events);

        using var connection = StateStore.Connect(Account, root.State);
        connection.RowFactoryIsRow = false;
        Assert.Equal("borrowed ledger connection requires sqlite3.Row results",
                     Assert.ThrowsAny<StateError>(() => Ledger.FromConnection(connection, Account)).Message);
    }

    [Fact]
    public void Missing_work_history_is_not_recreated_on_open()
    {
        using var root = TempRoot.Create();
        string itemId;
        using (var ledger = Open(root))
        {
            itemId = (string)Item(ledger, Source(ledger))["id"]!;
            ledger.Conn.Execute("DROP TABLE work_events");
            ledger.Conn.Execute("DROP TABLE work_item_revisions");
        }

        Assert.Equal("incomplete work schema; refusing to recreate missing history",
                     Assert.ThrowsAny<StateError>(() => Open(root)).Message);
        using var connection = StateStore.Connect(Account, root.State);
        var tables = connection.Execute("SELECT name FROM sqlite_master WHERE type='table'").Select(row => (string)row[0]!).ToList();
        Assert.DoesNotContain("work_events", tables);
        Assert.Equal(1L, connection.Execute("SELECT revision FROM work_items WHERE id=?", itemId).FetchOne()![0]);
    }

    [Fact]
    public void Complete_legacy_schema_is_adopted_once()
    {
        using var root = TempRoot.Create();
        string itemId;
        using (var ledger = Open(root))
        {
            itemId = (string)Item(ledger, Source(ledger))["id"]!;
            ledger.Conn.Execute("DELETE FROM margo_meta WHERE key='work_schema_version'");
            ledger.Conn.Execute("DROP TABLE work_meta");
            ledger.Conn.Commit();
        }

        using (var ledger = Open(root))
        {
            Assert.Equal(1L, (long)ledger.Show(itemId)["revision"]!);
            Assert.Equal("1", ledger.Conn.Execute("SELECT value FROM margo_meta WHERE key='work_schema_version'").FetchOne()![0]);
            ledger.Conn.Execute("DROP TABLE work_meta");
        }

        Assert.ThrowsAny<StateError>(() => Open(root));
    }

    [Fact]
    public void Export_is_private_atomic_and_refuses_hand_edits_and_symlinks()
    {
        using var root = TempRoot.Create();
        using var ledger = Open(root);
        var reference = Source(ledger);
        var item = Item(ledger, reference);
        var id = (string)item["id"]!;
        ledger.UpdateItem(id, 1, "confirmed", evidence: Evidence(id));
        var view = root.Combine("view.md");
        var first = ledger.ExportCommitments(view);
        Assert.Equal(PyIds.Sha256Hex(File.ReadAllBytes(view)), (string?)first["digest"]);
        Assert.Equal("600", Modes.Of(view));
        Assert.Contains("- [confirmed] Review fictional specification — owner: unknown; due: unknown; id: `" + id + "`",
                        File.ReadAllText(view), StringComparison.Ordinal);
        Assert.Equal((string?)first["digest"], (string?)ledger.ExportCommitments(view)["digest"]);
        File.AppendAllText(view, "- Hand edit\n");
        Assert.Contains("hand edits", Assert.ThrowsAny<StateError>(() => ledger.ExportCommitments(view)).Message, StringComparison.Ordinal);
        Assert.EndsWith("- Hand edit\n", File.ReadAllText(view), StringComparison.Ordinal);
        var link = root.Combine("link.md");
        File.CreateSymbolicLink(link, view);
        Assert.Equal("refusing a symlink export", Assert.ThrowsAny<StateError>(() => ledger.ExportCommitments(link)).Message);
        Assert.Empty(Directory.GetFiles(root.Path, ".*.staging"));
    }

    [Fact]
    public void Office_queue_flow_records_candidates_idempotently()
    {
        // The orchestrator's OfficeQueue.record/seen_before through the public API.
        using var root = TempRoot.Create();
        using var ledger = Open(root);
        var key = PyIds.Sha256Hex("sender-1\nconversation-1");
        Assert.Null(ledger.FindSource("office-conversation", "sender-conversation", key));
        for (var i = 0; i < 2; i++)
        {
            ledger.Source("office-conversation", "sender-conversation", key, "v1",
                          new JsonObject { ["from_id"] = "sender-1", ["conversation_id"] = "conversation-1" },
                          "urn:margo:office-conversation:" + key, observedAt: "2026-05-06T07:08:09Z");
            var reference = ledger.Source("office-request", "teams", "message-1", "v1",
                                          new JsonObject { ["from_name"] = "Fictional Sender", ["excerpt"] = "Hello" },
                                          "urn:margo:office-request:teams:message-1", observedAt: "2026-05-06T07:08:09Z");
            ledger.Ingest(new JsonObject
            {
                ["title"] = "Request to the office from Fictional Sender",
                ["owner"] = null,
                ["due"] = null,
                ["direction"] = "owe",
                ["source_refs"] = new JsonArray(reference),
                ["notes"] = "Unreviewed request.",
            }, "office:teams:message-1");
        }

        Assert.NotNull(ledger.FindSource("office-conversation", "sender-conversation", key));
        var pending = (JsonArray)ledger.List("decisions")["items"]!;
        var only = Assert.Single(pending);
        Assert.Equal("office-request", (string?)only!["sources"]![0]!["family"]);
        Assert.Equal("candidate", (string?)only["state"]);
    }

    [Fact]
    public void Meeting_occurrence_delayed_recap_and_carry_forward()
    {
        // tests/test_user_journeys.py::test_meeting_occurrence_delayed_recap_and_carry_forward (the patched
        // work_productivity.utc_now becomes a PyRuntime clock override).
        using var root = TempRoot.Create();
        using var ledger = Open(root);
        var products = new Productivity(ledger);
        static string At(long seconds) => (PyDateTime.Now() + PyTimeDelta.FromSeconds(seconds)).Isoformat();
        var occurrence = ledger.Source("calendar", "occurrences", "occurrence-2026-09-06", "c1",
                                       new JsonObject { ["response"] = "accepted", ["attendance"] = "unknown" },
                                       "https://example.com/calendar/occurrence-2026-09-06");
        var preRead = ledger.Source("file", "meeting-prep", "fictional-pre-read", "f1",
                                    new JsonObject { ["summary"] = "Two open questions; no approved decisions." },
                                    "https://example.com/files/fictional-pre-read");
        var data = new JsonObject
        {
            ["series_id"] = "series-synthetic-review",
            ["occurrence_id"] = "occurrence-2026-09-06",
            ["scheduled_start"] = At(-1800),
            ["scheduled_end"] = At(-900),
            ["source_refs"] = new JsonArray(occurrence, preRead),
            ["attendance"] = "unknown",
            ["work_item_ids"] = new JsonArray(),
            ["topics"] = new JsonArray(new JsonObject
            {
                ["id"] = "topic-accessibility",
                ["title"] = "Accessibility review",
                ["owner"] = null,
                ["created_at"] = At(-3600),
                ["why"] = "The pre-read leaves this open.",
                ["state"] = "open",
            }),
        };
        const string identity = "occurrence-2026-09-06";
        var meeting = products.Put("meeting", identity, data, "scheduled");
        foreach (var state in new[] { "prepped", "occurred" })
        {
            meeting = products.Put("meeting", identity, PyDynamic.Clone(meeting["data"]), state, PyDynamic.Clone(meeting["revision"]));
        }

        var pending = PyDynamic.CloneObject(meeting["data"]);
        pending["recap_retry"] = new JsonObject { ["attempts"] = 0, ["max_attempts"] = 3, ["status"] = "pending", ["next_check_at"] = At(-1) };
        meeting = products.Put("meeting", identity, pending, "recap_pending", PyDynamic.Clone(meeting["revision"]));
        meeting = products.RecapRetry((string)meeting["id"]!, PyDynamic.Clone(meeting["revision"]), "pending", "fixture-only:no-recap-yet", At(60));
        Assert.Equal("unknown", (string?)meeting["data"]!["attendance"]);
        Assert.Equal("pending", (string?)meeting["data"]!["recap_retry"]!["status"]);
        var early = Assert.ThrowsAny<StateError>(() => products.RecapRetry((string)meeting["id"]!, PyDynamic.Clone(meeting["revision"]),
                                                                            "available", "fixture-only:too-early"));
        Assert.Equal("recap check is not yet eligible", early.Message);
        var later = DateTimeOffset.UtcNow.AddSeconds(120);
        using (PyRuntime.Override(clock: () => later))
        {
            meeting = products.RecapRetry((string)meeting["id"]!, PyDynamic.Clone(meeting["revision"]), "available", "fixture-only:delayed-recap-arrived");
        }

        Assert.Equal("available", (string?)meeting["data"]!["recap_retry"]!["status"]);
        var recap = ledger.Source("meeting-recap", "occurrences", "occurrence-2026-09-06", "r1",
                                  new JsonObject { ["summary"] = "Dana will revise the fictional checklist; no due date stated." },
                                  "https://example.com/recaps/occurrence-2026-09-06");
        var candidate = ledger.Ingest(new JsonObject
        {
            ["title"] = "Revise the fictional checklist",
            ["owner"] = null,
            ["due"] = null,
            ["direction"] = "owe",
            ["source_refs"] = new JsonArray(recap),
        }, "recap-action-candidate");
        var proposed = PyDynamic.CloneObject(meeting["data"]);
        proposed["work_item_ids"] = new JsonArray(PyDynamic.Clone(candidate["id"]));
        meeting = products.Put("meeting", identity, proposed, "debrief_proposed", PyDynamic.Clone(meeting["revision"]));
        var revision = (long)meeting["revision"]!;
        meeting = products.Put("meeting", identity, PyDynamic.Clone(meeting["data"]), "reviewed", revision,
                               Evidence((string)meeting["id"]!, revision, "confirm"));
        var next = products.Put("meeting", "occurrence-2026-09-13", new JsonObject
        {
            ["series_id"] = "series-synthetic-review",
            ["occurrence_id"] = "occurrence-2026-09-13",
            ["scheduled_start"] = At(604800),
            ["scheduled_end"] = At(606600),
            ["source_refs"] = new JsonArray(ledger.Source("calendar", "occurrences", "occurrence-2026-09-13", "c1",
                                                          new JsonObject { ["response"] = "accepted", ["attendance"] = "unknown" },
                                                          "https://example.com/calendar/occurrence-2026-09-13")),
            ["attendance"] = "unknown",
            ["work_item_ids"] = new JsonArray(),
            ["topics"] = new JsonArray(),
        }, "scheduled");
        revision = (long)meeting["revision"]!;
        var carried = products.CarryForward((string)meeting["id"]!, revision, PyDynamic.Clone(next["id"]), PyDynamic.Clone(next["revision"]),
                                            Evidence((string)meeting["id"]!, revision, "carry_forward"));
        Assert.Equal("carried_forward", (string?)carried["source"]!["state"]);
        Assert.Equal("agenda_accumulating", (string?)carried["target"]!["state"]);
        Assert.Equal((string?)candidate["id"], (string?)Assert.Single((JsonArray)carried["target"]!["data"]!["work_item_ids"]!));
        Assert.Equal("topic-accessibility", (string?)Assert.Single((JsonArray)carried["target"]!["data"]!["topics"]!)!["id"]);
        Assert.Equal("candidate", (string?)ledger.Show((string)candidate["id"]!)["state"]);
        Assert.Null(ledger.Show((string)candidate["id"]!)["data"]!["due"]);
    }

    [Fact]
    public void Schema_constant_is_byte_identical_to_the_python_schema()
    {
        var python = File.ReadAllText(Path.Combine(RepositoryRoot(), "skills", "chief-of-staff", "scripts", "work_ledger.py"));
        var start = python.IndexOf("SCHEMA = \"\"\"", StringComparison.Ordinal) + "SCHEMA = \"\"\"".Length;
        var end = python.IndexOf("\"\"\"", start, StringComparison.Ordinal);
        Assert.Equal(WorkLedger.Schema, python[start..end]);
    }

    [Fact]
    public void Margo_state_discovers_the_work_scripts()
    {
        Assert.Contains("work_state.py", StateCli.StateCliHost.Scripts.Keys);
        Assert.Contains("work_ledger_probe.py", StateCli.StateCliHost.Scripts.Keys);
    }

    [Fact]
    public void Cli_roundtrip_from_source_to_finished_execution()
    {
        // tests/test_work_state.py::test_real_shared_store_cli_roundtrip, in process.
        using var root = TempRoot.Create();
        var script = new WorkStateScript();
        JsonObject Invoke(string stdin, params string[] args)
        {
            var (code, stdout, stderr) = ScriptRunner.Run(script, ["--account", "fictional-cli-account@example.com", "--state-dir", root.State, .. args], stdin);
            Assert.True(code == 0, stderr);
            return (JsonObject)PyJson.Loads(stdout)!;
        }

        var reference = Invoke("""{"family": "mail", "scope": "inbox", "external_id": "fictional-cli-message", "revision": "v1", "evidence": {"quote": "Please review the example."}, "web_link": "https://example.com/fictional-cli-message"}""",
                               "source", "--input", "-");
        var candidate = Invoke(new JsonObject
        {
            ["claim_key"] = "review-example",
            ["data"] = new JsonObject { ["title"] = "Review the example", ["owner"] = null, ["due"] = null, ["direction"] = "owe", ["source_refs"] = new JsonArray(PyDynamic.Clone(reference)) },
        }.ToJsonString(), "ingest", "--input", "-");
        var proposal = new JsonObject
        {
            ["kind"] = "mail.reply",
            ["target"] = new JsonObject { ["message_id"] = "fictional-cli-message" },
            ["payload"] = new JsonObject { ["body"] = "Reviewed example." },
            ["why"] = "Fictional fixture",
            ["source_refs"] = new JsonArray(PyDynamic.Clone(reference)),
            ["target_fingerprint"] = "fixture-target-v1",
            ["work_item_id"] = PyDynamic.Clone(candidate["id"]),
        };
        var action = Invoke(proposal.ToJsonString(), "propose", "--input", "-");
        var id = (string)action["id"]!;
        var hash = (string)action["action_hash"]!;
        var approved = Invoke(new JsonObject { ["action_hash"] = hash, ["expires_at"] = Stamp(60), ["evidence"] = Evidence(id, 1, "approve", hash) }.ToJsonString(),
                              "approve", id, "--revision", "1", "--input", "-");
        Assert.Equal("approved", (string?)approved["state"]);
        var attempt = Invoke(new JsonObject { ["checked_at"] = Stamp(), ["target_fingerprint"] = "fixture-target-v1", ["source_refs"] = new JsonArray(PyDynamic.Clone(reference)) }.ToJsonString(),
                             "begin", id, "--revision", "1", "--input", "-");
        var finished = Invoke(new JsonObject { ["state"] = "succeeded", ["receipt"] = Receipt() }.ToJsonString(),
                              "finish", (string)attempt["attempt_id"]!, "--input", "-");
        Assert.Equal("succeeded", (string?)finished["state"]);
        var listed = Invoke("", "list", "--view", "all", "--json");
        Assert.Single((JsonArray)listed["actions"]!);
        Assert.Equal("candidate", (string?)listed["items"]![0]!["state"]);
        var (_, help, _) = ScriptRunner.Run(script, ["-h"]);
        Assert.DoesNotContain("--yes", help, StringComparison.Ordinal);
        Assert.DoesNotContain("approve-all", help, StringComparison.Ordinal);
    }

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "skills", "chief-of-staff", "scripts", "work_ledger.py")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
    }
}
