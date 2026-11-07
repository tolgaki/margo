using System.Net.Sockets;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;
using Margo.Cloud.WorkIQ;
using Xunit;
using static Margo.Cloud.Guard.Tests.Fixtures;

namespace Margo.Cloud.Guard.Tests;

/// <summary>
/// Port of <c>tests/test_autopilot_guard.py</c> <c>GuardedWriteTests</c>. Claims come from the real approval journal
/// (a <see cref="Ledger"/> opened with <see cref="InstanceLedger.OpenLedger"/>); only the database
/// location is synthetic.
/// </summary>
public sealed class GuardedWriteTests
{
    private FakeTransport _transport = new();

    private WorkIQGuard Guard(ApprovalDesk desk, FakeTransport transport, ITokenProvider? tokens = null)
    {
        _transport = transport;
        return new WorkIQGuard(Binding, tokens ?? new FakeTokens(), transport, new LedgerClaimJournal(desk.Ledger));
    }

    private static Task<GuardResult> Send(WorkIQGuard guard, string actionId, string attemptId, string mode = RunModes.Attended,
                                          string? actionUrl = null, string? onDataOf = null)
    {
        var arguments = new JsonObject
        {
            ["on_data_of"] = onDataOf ?? "principal",
            ["actionUrl"] = actionUrl ?? "/me/messages/draft-1/send",
            ["action_id"] = actionId,
            ["attempt_id"] = attemptId,
        };
        return guard.CallAsync("do_action", arguments, mode, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Exact_claim_executes_once_and_records_receipt()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var guard = Guard(desk, new FakeTransport("""{"statusCode": 202}"""));
        Assert.Equal("ok", (await Send(guard, actionId, attemptId)).Status);
        Assert.Equal(PrincipalToken, _transport.Calls[0].Bearer);
        Assert.Equal("""{"actionUrl":"/me/messages/draft-1/send"}""", _transport.Calls[0].Arguments.ToJsonString());
        var action = desk.Ledger.Show(actionId);
        Assert.Equal("succeeded", (string?)action["state"]);
        var receipt = action["executions"]![0]!["receipt"]!;
        Assert.Equal(202, (long)receipt["status_code"]!);
        Assert.Equal("workiq:" + attemptId, (string?)receipt["reference"]);
        Assert.Equal("refused", (await Send(guard, actionId, attemptId)).Status);
        Assert.Single(_transport.Calls);

        var first = guard.Receipts[0];
        Assert.Equal((actionId, attemptId, "succeeded", "ok"), (first.ActionId, first.AttemptId, first.Journal, first.Status));
        Assert.Equal(["/me/messages/draft-1/send"], first.Paths);
        Assert.Equal("[202]", new JsonArray(first.StatusCodes.Select(Copy).ToArray()).ToJsonString());
        Assert.Contains(LedgerClaimJournal.DispatchedEvent, desk.Events(actionId));
    }

    [Fact]
    public async Task Mismatched_call_or_subject_is_refused_and_claim_stays_live()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var guard = Guard(desk, new FakeTransport("""{"statusCode": 202}"""));
        var changed = await Send(guard, actionId, attemptId, actionUrl: "/me/messages/draft-2/send");
        Assert.Equal("refused", changed.Status);
        Assert.Equal("call differs from the approved Work IQ call; revise and re-approve", changed.Reason);
        var subject = await Send(guard, actionId, attemptId, onDataOf: "self");
        Assert.Equal("refused", subject.Status);
        Assert.Equal("approved action was for on_data_of=principal", subject.Reason);
        Assert.Empty(_transport.Calls);
        Assert.Equal("executing", desk.State(actionId));
    }

    [Fact]
    public async Task Unattended_run_cannot_use_a_live_claim()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var guard = Guard(desk, new FakeTransport("""{"statusCode": 202}"""));
        foreach (var mode in new[] { RunModes.Unattended, RunModes.ThirdParty })
        {
            Assert.Equal("refused", (await Send(guard, actionId, attemptId, mode)).Status);
        }

        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Timeout_is_unknown_and_never_retried()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var guard = Guard(desk, new FakeTransport(error: () => new TransportTimeoutException("no response")));
        var result = await Send(guard, actionId, attemptId);
        Assert.Equal("unknown", result.Status);
        Assert.Equal("no response; effect unknown, not retried", result.Reason);
        Assert.Equal("outcome_unknown", desk.State(actionId));
        Assert.Equal("refused", (await Send(guard, actionId, attemptId)).Status);
        Assert.Single(_transport.Calls);
        Assert.Equal("outcome_unknown", guard.Receipts[0].Journal);
    }

    [Fact]
    public async Task Rejected_request_is_a_no_effect_failure_but_server_error_is_unknown()
    {
        using var desk = ApprovalDesk.Open();
        var (rejectedId, rejectedAttempt) = desk.Started();
        var guard = Guard(desk, new FakeTransport(error: () => new TransportException("forbidden", statusCode: 403)));
        Assert.Equal("error", (await Send(guard, rejectedId, rejectedAttempt)).Status);
        Assert.Equal("failed", desk.State(rejectedId));
        var failed = desk.Ledger.Show(rejectedId)["executions"]![0]!["receipt"]!;
        Assert.Equal(403, (long)failed["status_code"]!);
        Assert.True((bool)failed["definitive_no_effect"]!);
        Assert.Equal("[403]", new JsonArray(guard.Receipts[0].StatusCodes.Select(Copy).ToArray()).ToJsonString());

        var (serverId, serverAttempt) = desk.Started();
        guard = Guard(desk, new FakeTransport(error: () => new TransportException("unavailable", statusCode: 503)));
        var server = await Send(guard, serverId, serverAttempt);
        Assert.Equal("unknown", server.Status);
        Assert.Equal(GuardPolicy.UnknownWrite, server.Reason);
        Assert.Equal("outcome_unknown", desk.State(serverId));
    }

    [Fact]
    public async Task Success_without_a_status_code_stays_unknown()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var guard = Guard(desk, new FakeTransport("""{"data": {}}"""));
        var result = await Send(guard, actionId, attemptId);
        Assert.Equal("unknown", result.Status);
        Assert.Contains("not retried", result.Reason, StringComparison.Ordinal);
        Assert.Equal("outcome_unknown", desk.State(actionId));
    }

    [Fact]
    public async Task Action_without_exact_workiq_call_cannot_execute()
    {
        using var desk = ApprovalDesk.Open();
        var item = desk.Ledger.Ingest(new JsonObject
        {
            ["title"] = "Reply",
            ["owner"] = null,
            ["due"] = null,
            ["direction"] = "owe",
            ["source_refs"] = new JsonArray(PyNode.Copy(desk.Reference)),
        }, "legacy-claim");
        var action = desk.Ledger.Propose(new JsonObject
        {
            ["kind"] = "mail.reply",
            ["target"] = new JsonObject { ["message_id"] = "message-1" },
            ["payload"] = new JsonObject { ["body"] = "Fictional reply." },
            ["why"] = "Legacy shape",
            ["source_refs"] = new JsonArray(PyNode.Copy(desk.Reference)),
            ["target_fingerprint"] = "target-v1",
            ["work_item_id"] = PyNode.Copy(item["id"]),
        });
        var (actionId, attemptId) = desk.Begin(action, "principal-1", "host-interaction:fictional/teams-card/2", "Send it.");
        var guard = Guard(desk, new FakeTransport("""{"statusCode": 202}"""));
        var result = await Send(guard, actionId, attemptId);
        Assert.Equal("refused", result.Status);
        Assert.Equal("no live execution claim: approved action does not carry an exact workiq call", result.Reason);
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Missing_token_settles_claim_as_no_effect()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var guard = Guard(desk, new FakeTransport("""{"statusCode": 202}"""), new FakeTokens([Subjects.Principal]));
        Assert.Equal("blocked", (await Send(guard, actionId, attemptId)).Status);
        Assert.Equal("failed", desk.State(actionId));
        Assert.Empty(_transport.Calls);
        Assert.DoesNotContain(LedgerClaimJournal.DispatchedEvent, desk.Events(actionId));
        Assert.Equal("failed", guard.Receipts[0].Journal);
    }

    [Fact]
    public async Task Unexpected_transport_failure_is_unknown_and_never_resent()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var first = Guard(desk, new FakeTransport(error: () => new IOException("reset by peer", new SocketException((int)SocketError.ConnectionReset))));
        var result = await Send(first, actionId, attemptId);
        Assert.Equal("unknown", result.Status);
        Assert.Equal("transport failure; effect unknown, not retried", result.Reason);
        Assert.Equal("outcome_unknown", desk.State(actionId));
        var second = Guard(desk, new FakeTransport("""{"statusCode": 202}"""));
        Assert.Equal("refused", (await Send(second, actionId, attemptId)).Status);
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Dispatch_is_durable_across_guards_after_a_crash()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var journal = new LedgerClaimJournal(desk.Ledger);
        journal.MarkDispatched(journal.Load(actionId, attemptId)); // the process died after this point
        Assert.Equal("executing", desk.State(actionId));
        var guard = Guard(desk, new FakeTransport("""{"statusCode": 202}"""));
        var result = await Send(guard, actionId, attemptId);
        Assert.Equal("refused", result.Status);
        Assert.Contains("reconcile", result.Reason, StringComparison.Ordinal);
        Assert.Empty(_transport.Calls);
        var partial = new ExecutionClaim(desk.Ledger.Account, actionId, attemptId, null, PyNode.Copy(desk.Ledger.Show(actionId)["action_hash"]),
                                         null, LedgerClaimJournal.WorkIQCallKey, new JsonObject(), null);
        Assert.Throws<ClaimException>(() => journal.MarkDispatched(partial));
    }

    [Fact]
    public async Task Token_provider_crash_is_blocked_and_settles_without_sending()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var guard = Guard(desk, new FakeTransport("""{"statusCode": 202}"""), new BrokenTokens());
        var result = await Send(guard, actionId, attemptId);
        Assert.Equal("blocked", result.Status);
        Assert.DoesNotContain("vault unreachable", result.Reason, StringComparison.Ordinal);
        Assert.Equal("token unavailable for principal: token provider error", result.Reason);
        Assert.Equal("failed", desk.State(actionId));
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Approval_must_come_from_the_manager()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started(actor: "colleague-1");
        var guard = Guard(desk, new FakeTransport("""{"statusCode": 202}"""));
        var result = await Send(guard, actionId, attemptId);
        Assert.Equal("refused", result.Status);
        Assert.Contains("manager", result.Reason, StringComparison.Ordinal);
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Manager_can_authorise_mail_from_margos_own_mailbox()
    {
        using var desk = ApprovalDesk.Open();
        var body = PyJson.Dumps(Json("""{"message": {"subject": "Agenda", "toRecipients": [{"emailAddress": {"address": "rafa@example.com"}}]}}"""));
        var own = new JsonObject
        {
            ["tool"] = "do_action",
            ["arguments"] = new JsonObject { ["actionUrl"] = "/me/sendMail", ["jsonBody"] = body },
        };
        var (actionId, attemptId) = desk.Started(workiq: own, onDataOf: "self");
        var guard = Guard(desk, new FakeTransport("""{"statusCode": 202}"""));
        var arguments = new JsonObject
        {
            ["actionUrl"] = "/me/sendMail",
            ["jsonBody"] = body,
            ["on_data_of"] = "self",
            ["action_id"] = actionId,
            ["attempt_id"] = attemptId,
        };
        Assert.Equal("ok", (await guard.CallAsync("do_action", arguments, RunModes.Attended, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(MargoToken, _transport.Calls[0].Bearer);
        Assert.Equal("succeeded", desk.State(actionId));
        arguments["on_data_of"] = "principal";
        Assert.Equal("refused", (await guard.CallAsync("do_action", arguments, RunModes.Attended, TestContext.Current.CancellationToken)).Status);
    }

    private static JsonNode? Copy(JsonNode node) => PyNode.Copy(node);
}
