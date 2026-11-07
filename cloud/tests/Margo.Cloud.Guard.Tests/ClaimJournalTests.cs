using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;
using Xunit;
using static Margo.Cloud.Guard.Tests.Fixtures;

namespace Margo.Cloud.Guard.Tests;

/// <summary>
/// <c>claims.py</c> <c>LedgerClaimJournal</c> against a real temporary ledger. Expected messages and claim fields were
/// checked against the Python journal on the same scenarios. The reply-gate scenarios of
/// <c>tests/test_autopilot_guard.py</c> (<c>test_reply_gate_*</c>) are ported here at the journal level; the gate's own
/// decisions (<c>replies.py</c>) belong to the Orchestrator port.
/// </summary>
public sealed class ClaimJournalTests
{
    private static void Refused(string message, Action action) => Assert.Equal(message, Assert.Throws<ClaimException>(action).Message);

    [Fact]
    public void Load_refuses_anything_but_a_live_attempt_with_an_exact_call()
    {
        using var desk = ApprovalDesk.Open();
        var journal = new LedgerClaimJournal(desk.Ledger);
        Refused("action_id and attempt_id are required for a guarded write", () => journal.Load(null, "attempt-1"));
        Refused("action_id and attempt_id are required for a guarded write", () => journal.Load("action-1", 1));
        var unknown = Assert.Throws<ClaimException>(() => journal.Load("action_nope", "attempt-1"));
        Assert.Equal("unknown action", unknown.Message);
        Assert.IsType<StateError>(unknown.InnerException, exactMatch: false);
        Assert.Equal("ClaimError", unknown.PythonType);

        var item = desk.Ledger.Ingest(new JsonObject
        {
            ["title"] = "t",
            ["owner"] = null,
            ["due"] = null,
            ["direction"] = "owe",
            ["source_refs"] = new JsonArray(PyNode.Copy(desk.Reference)),
        }, "item-claim");
        Refused("action has no live execution claim", () => journal.Load((string)item["id"]!, "attempt-1"));
        var proposed = desk.Ledger.Propose(new JsonObject
        {
            ["kind"] = "mail.send",
            ["target"] = new JsonObject { ["message_id"] = "draft-1", ["on_data_of"] = "principal" },
            ["payload"] = new JsonObject { ["workiq"] = PyNode.CopyObject(ApprovalDesk.Send) },
            ["why"] = "w",
            ["source_refs"] = new JsonArray(PyNode.Copy(desk.Reference)),
            ["target_fingerprint"] = "target-v1",
            ["work_item_id"] = PyNode.Copy(item["id"]),
        });
        Refused("action has no live execution claim", () => journal.Load((string)proposed["id"]!, "attempt-1"));

        var (actionId, attemptId) = desk.Started();
        Refused("execution attempt is not live for the current revision", () => journal.Load(actionId, "attempt_nope"));
        Refused("approved action does not carry an exact reply call", () => journal.Load(actionId, attemptId, LedgerClaimJournal.ReplyCallKey));

        var claim = journal.Load(actionId, attemptId);
        Assert.Equal(Binding.Account, claim.Account);
        Assert.Equal((actionId, attemptId, "workiq"), (claim.ActionId, claim.AttemptId, claim.CallKey));
        Assert.Equal("1", PyJson.Dumps(claim.Revision));
        Assert.Equal(desk.Ledger.Show(actionId)["action_hash"]!.ToJsonString(), claim.ActionHash!.ToJsonString());
        Assert.Equal("\"principal\"", PyJson.Dumps(claim.OnDataOf));
        Assert.Equal("\"principal-1\"", PyJson.Dumps(claim.ApprovedBy));
        // Stored as canonical JSON, so it reads back with sorted keys (in Python too).
        Assert.Equal(PyJson.Dumps(ApprovalDesk.Send, sortKeys: true), PyJson.Dumps(claim.Call));
        Assert.Null(claim.Call.Parent);
    }

    [Fact]
    public void Dispatch_is_recorded_once_and_blocks_every_later_load()
    {
        using var desk = ApprovalDesk.Open();
        var journal = new LedgerClaimJournal(desk.Ledger);
        var (actionId, attemptId) = desk.Started();
        var claim = journal.Load(actionId, attemptId);
        journal.MarkDispatched(claim);
        var dispatched = ((JsonArray)desk.Ledger.History(actionId)["events"]!).Single(e => (string?)e!["event"] == LedgerClaimJournal.DispatchedEvent)!;
        Assert.Equal(attemptId, (string?)dispatched["data"]!["attempt_id"]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}\+00:00$", (string)dispatched["data"]!["dispatched_at"]!);
        Refused("attempt was already dispatched; reconcile it, never resend", () => journal.Load(actionId, attemptId));
        Refused("attempt was already dispatched; reconcile it, never resend", () => journal.MarkDispatched(claim));
        Assert.Equal("executing", desk.State(actionId));
    }

    [Fact]
    public void Settle_records_the_outcome_once_with_a_truthful_receipt()
    {
        using var desk = ApprovalDesk.Open();
        var journal = new LedgerClaimJournal(desk.Ledger);

        var (failedId, failedAttempt) = desk.Started();
        var failedClaim = journal.Load(failedId, failedAttempt);
        var failed = journal.Settle(failedClaim, "failed", 403);
        Assert.Equal("failed", (string?)failed["state"]);
        var receipt = failed["executions"]![0]!["receipt"]!.AsObject();
        Assert.Equal(["definitive_no_effect", "kind", "outcome", "recorded_at", "reference", "status_code"], receipt.Select(p => p.Key));
        Assert.Equal(("tool_result", "workiq:" + failedAttempt, "failed", 403L, true),
                     ((string?)receipt["kind"], (string?)receipt["reference"], (string?)receipt["outcome"], (long)receipt["status_code"]!, (bool)receipt["definitive_no_effect"]!));
        Assert.Equal("execution is already settled; no replay", Assert.Throws<StateError>(() => journal.Settle(failedClaim, "succeeded", 200)).Message);
        Refused("execution claim is no longer live", () => journal.MarkDispatched(failedClaim));

        var (unknownId, unknownAttempt) = desk.Started();
        var unknown = journal.Settle(journal.Load(unknownId, unknownAttempt), "outcome_unknown", 503);
        Assert.Equal("outcome_unknown", (string?)unknown["state"]);
        Assert.Null(unknown["executions"]![0]!["receipt"]);

        var (okId, okAttempt) = desk.Started();
        var ok = journal.Settle(journal.Load(okId, okAttempt), "succeeded");
        Assert.Equal(["kind", "outcome", "recorded_at", "reference"], ok["executions"]![0]!["receipt"]!.AsObject().Select(p => p.Key));

        var (otherId, otherAttempt) = desk.Started();
        var other = journal.Load(otherId, otherAttempt);
        Assert.Equal("invalid execution outcome", Assert.Throws<StateError>(() => journal.Settle(other, "bogus")).Message);
        Assert.Equal("partial", (string?)journal.Settle(other, "partial")["state"]);
    }

    [Fact]
    public void Approver_is_the_actor_on_the_approval_of_the_current_revision()
    {
        using var desk = ApprovalDesk.Open();
        var journal = new LedgerClaimJournal(desk.Ledger);
        var item = desk.Ledger.Ingest(new JsonObject
        {
            ["title"] = "t",
            ["owner"] = null,
            ["due"] = null,
            ["direction"] = "owe",
            ["source_refs"] = new JsonArray(PyNode.Copy(desk.Reference)),
        }, "approver-claim");
        JsonObject Data(string why) => new()
        {
            ["kind"] = "mail.send",
            ["target"] = new JsonObject { ["message_id"] = "draft-1" },
            ["payload"] = new JsonObject { ["workiq"] = PyNode.CopyObject(ApprovalDesk.Send) },
            ["why"] = why,
            ["source_refs"] = new JsonArray(PyNode.Copy(desk.Reference)),
            ["target_fingerprint"] = "target-v1",
            ["work_item_id"] = PyNode.Copy(item["id"]),
        };
        var action = desk.Ledger.Propose(Data("w"));
        var id = (string)action["id"]!;
        var hash = (string)action["action_hash"]!;
        desk.Ledger.Approve(id, 1, hash, Evidence(id, 1, hash, "colleague-1"), Stamp(60));
        var edited = desk.Ledger.EditAction(id, 1, Data("changed"));
        Assert.Equal(2L, (long)edited["revision"]!);
        var editedHash = (string)edited["action_hash"]!;
        desk.Ledger.Approve(id, 2, editedHash, Evidence(id, 2, editedHash, "principal-1"), Stamp(60));
        var attempt = desk.Ledger.Begin(id, 2, new JsonObject
        {
            ["checked_at"] = Stamp(),
            ["target_fingerprint"] = "target-v1",
            ["source_refs"] = new JsonArray(PyNode.Copy(desk.Reference)),
        });
        var claim = journal.Load(id, (string)attempt["attempt_id"]!);
        Assert.Equal("\"principal-1\"", PyJson.Dumps(claim.ApprovedBy));
        Assert.Equal("2", PyJson.Dumps(claim.Revision));
        Assert.Null(claim.OnDataOf); // the target names no subject
    }

    [Fact]
    public void Reply_gate_approve_mode_claims_only_the_approved_text()
    {
        // test_reply_gate_approve_mode_sends_only_the_approved_text, at the journal level: the gate loads the "reply"
        // call, compares it with the proposed reply, dispatches, and settles after the runtime sends.
        using var desk = ApprovalDesk.Open();
        var journal = new LedgerClaimJournal(desk.Ledger);
        var reply = Args("""{"to": "colleague-1", "text": "Dana can meet on Thursday."}""");
        var (actionId, attemptId) = desk.Started(payload: new JsonObject { ["reply"] = PyNode.CopyObject(reply) });
        Refused("approved action does not carry an exact workiq call", () => journal.Load(actionId, attemptId));
        var claim = journal.Load(actionId, attemptId, LedgerClaimJournal.ReplyCallKey);
        Assert.Equal(Binding.Account, claim.Account);
        Assert.Equal("\"principal-1\"", PyJson.Dumps(claim.ApprovedBy));
        Assert.True(PyJson.PyEquals(reply, claim.Call));
        Assert.False(PyJson.PyEquals(Args("""{"to": "colleague-1", "text": "Dana can meet on Friday."}"""), claim.Call));
        Assert.False(PyJson.PyEquals(Args("""{"to": "colleague-2", "text": "Dana can meet on Thursday."}"""), claim.Call));
        journal.MarkDispatched(claim);
        journal.Settle(claim, "succeeded", 200);
        Assert.Equal("succeeded", desk.State(actionId));
        Refused("action has no live execution claim", () => journal.Load(actionId, attemptId, LedgerClaimJournal.ReplyCallKey));
    }

    [Fact]
    public void Reply_gate_sees_approval_by_anyone_else_and_refuses_workiq_claims()
    {
        // test_reply_gate_rejects_approval_by_anyone_else_and_workiq_claims, at the journal level.
        using var desk = ApprovalDesk.Open();
        var journal = new LedgerClaimJournal(desk.Ledger);
        var reply = Args("""{"to": "colleague-1", "text": "Approved by the wrong person."}""");
        var (actionId, attemptId) = desk.Started(payload: new JsonObject { ["reply"] = reply }, actor: "colleague-1");
        Assert.Equal("\"colleague-1\"", PyJson.Dumps(journal.Load(actionId, attemptId, LedgerClaimJournal.ReplyCallKey).ApprovedBy));
        var (workiqId, workiqAttempt) = desk.Started();
        Refused("approved action does not carry an exact reply call", () => journal.Load(workiqId, workiqAttempt, LedgerClaimJournal.ReplyCallKey));
    }

    [Fact]
    public async Task A_claim_from_another_instance_is_refused()
    {
        // The account key separates instances: another agent identity for the same manager opens its own ledger.
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var other = new InstanceBinding("agent-identity-2", "margo-user-2", "principal-1", "chat-principal-2");
        Assert.NotEqual(Binding.Account, other.Account);
        var transport = new FakeTransport("""{"statusCode": 202}""");
        var guard = new WorkIQGuard(other, new FakeTokens(), transport, new LedgerClaimJournal(desk.Ledger));
        var arguments = new JsonObject
        {
            ["on_data_of"] = "principal",
            ["actionUrl"] = "/me/messages/draft-1/send",
            ["action_id"] = actionId,
            ["attempt_id"] = attemptId,
        };
        var result = await guard.CallAsync("do_action", arguments, RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal(("refused", "execution claim belongs to another instance"), (result.Status, result.Reason));
        Assert.Empty(transport.Calls);
        Assert.Equal("executing", desk.State(actionId));

        using var otherLedger = other.OpenLedger(desk.Root);
        Assert.Equal(other.Account, otherLedger.Account);
        Assert.Equal("unknown action", Assert.Throws<ClaimException>(() => new LedgerClaimJournal(otherLedger).Load(actionId, attemptId)).Message);
    }

    private static JsonObject Evidence(string id, int revision, string hash, string actor) => new()
    {
        ["kind"] = "human_confirmation",
        ["actor"] = actor,
        ["statement"] = "Send this exact message.",
        ["evidence_ref"] = "host-interaction:fictional/teams-card/3",
        ["subject_id"] = id,
        ["revision"] = revision,
        ["decision"] = "approve",
        ["action_hash"] = hash,
        ["decided_at"] = Stamp(),
    };
}
