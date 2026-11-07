using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.WorkIQ;
using Xunit;
using static Margo.Cloud.Guard.Tests.Fixtures;

namespace Margo.Cloud.Guard.Tests;

/// <summary>
/// What the C# guard adds around the Python behaviour: cancellation, the receipt recorder hook, concurrent budgets,
/// result and receipt shapes, constants other ports use, and opening the instance ledger.
/// </summary>
public sealed class GuardRuntimeTests
{
    private static JsonObject Send(string actionId, string attemptId) => new()
    {
        ["on_data_of"] = "principal",
        ["actionUrl"] = "/me/messages/draft-1/send",
        ["action_id"] = actionId,
        ["attempt_id"] = attemptId,
    };

    [Fact]
    public async Task Cancellation_after_dispatch_settles_the_claim_as_unknown_then_rethrows()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        using var cancel = new CancellationTokenSource();
        var transport = new FakeTransport("""{"statusCode": 202}""") { During = cancel.Cancel };
        var guard = new WorkIQGuard(Binding, new FakeTokens(), transport, new LedgerClaimJournal(desk.Ledger));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.CallAsync("do_action", Send(actionId, attemptId), RunModes.Attended, cancel.Token));
        Assert.Single(transport.Calls);
        Assert.Equal("outcome_unknown", desk.State(actionId));
        Assert.Contains(LedgerClaimJournal.DispatchedEvent, desk.Events(actionId));
        var receipt = Assert.Single(guard.Receipts);
        Assert.Equal(("unknown", "outcome_unknown", "cancelled; effect unknown, not retried"), (receipt.Status, receipt.Journal, receipt.Reason));

        var again = await guard.CallAsync("do_action", Send(actionId, attemptId), RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal("refused", again.Status);
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task A_cancelled_call_does_nothing_and_leaves_no_receipt()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var transport = new FakeTransport();
        var tokens = new FakeTokens();
        var guard = new WorkIQGuard(Binding, tokens, transport, new LedgerClaimJournal(desk.Ledger));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.CallAsync("do_action", Send(actionId, attemptId), RunModes.Attended, new CancellationToken(true)));
        Assert.Empty(guard.Receipts);
        Assert.Empty(tokens.Requested);
        Assert.Empty(transport.Calls);
        Assert.Equal("executing", desk.State(actionId));
        Assert.DoesNotContain(LedgerClaimJournal.DispatchedEvent, desk.Events(actionId));
    }

    [Fact]
    public async Task An_operation_cancelled_by_the_transport_itself_is_an_unknown_transport_failure()
    {
        // Only the caller's token makes cancellation an exception; any other OperationCanceledException is Python's
        // "any other exception": the request may have landed.
        var transport = new FakeTransport(error: () => new TaskCanceledException("the transport gave up"));
        var guard = new WorkIQGuard(Binding, new FakeTokens(), transport);
        var result = await guard.CallAsync("fetch", Json("""{"on_data_of": "principal", "entityUrls": ["/me/messages"]}"""), RunModes.Attended,
                                           TestContext.Current.CancellationToken);
        Assert.Equal(("unknown", "transport failure; effect unknown, not retried"), (result.Status, result.Reason));
    }

    [Fact]
    public async Task The_recorder_receives_each_receipt_as_the_call_finishes()
    {
        var recorded = new List<GuardReceipt>();
        var guard = new WorkIQGuard(Binding, new FakeTokens(), new FakeTransport(), recorder: recorded.Add);
        var ct = TestContext.Current.CancellationToken;
        await guard.CallAsync("fetch", Json("""{"on_data_of": "principal", "entityUrls": ["/me/messages?$top=1"]}"""), RunModes.Attended, ct);
        await guard.CallAsync("send_mail", Json("""{"on_data_of": "principal"}"""), RunModes.Attended, ct);
        Assert.Equal(guard.Receipts, recorded);
        Assert.Equal(["ok", "refused"], recorded.Select(receipt => receipt.Status));
        var json = recorded[0].ToJsonObject();
        Assert.Equal(["at", "mode", "tool", "on_data_of", "read_as", "paths", "status_codes", "status"], json.Select(pair => pair.Key));
        Assert.Equal("""["/me/messages"]""", json["paths"]!.ToJsonString());
        Assert.Equal("[200]", json["status_codes"]!.ToJsonString());
        Assert.Equal(["at", "mode", "tool", "on_data_of", "read_as", "paths", "status_codes", "status", "reason"],
                     recorded[1].ToJsonObject().Select(pair => pair.Key));
    }

    [Fact]
    public async Task A_recorder_failure_propagates_after_the_claim_is_settled()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started();
        var guard = new WorkIQGuard(Binding, new FakeTokens(), new FakeTransport("""{"statusCode": 202}"""), new LedgerClaimJournal(desk.Ledger),
                                    recorder: _ => throw new InvalidOperationException("log sink down"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.CallAsync("do_action", Send(actionId, attemptId), RunModes.Attended,
                                                                                  TestContext.Current.CancellationToken));
        Assert.Equal("succeeded", desk.State(actionId));
        Assert.Single(guard.Receipts);
    }

    [Fact]
    public async Task Results_carry_python_keys_and_a_copy_of_the_payload()
    {
        var guard = new WorkIQGuard(Binding, new FakeTokens(), new FakeTransport("""{"statusCode": 200, "data": {"n": 1, "f": 1.0}}"""));
        var ok = await guard.CallAsync("fetch", Json("""{"on_data_of": "self", "entityUrls": ["/me"]}"""), RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal("""{"status": "ok", "read_as": "margo", "tool": "fetch", "data": {"statusCode": 200, "data": {"n": 1, "f": 1.0}}}""",
                     PyJson.Dumps(ok.ToJsonObject()));
        Assert.Equal(PyJson.Dumps(ok.ToJsonObject()), PyJson.Dumps(ok.ToJsonObject()));
        var refused = await guard.CallAsync("fetch", Json("""{"entityUrls": ["/me"]}"""), RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal("""{"status": "refused", "read_as": null, "tool": "fetch", "reason": "on_data_of must be 'principal' or 'self'"}""",
                     PyJson.Dumps(refused.ToJsonObject()));
    }

    [Fact]
    public async Task Arguments_are_not_modified_and_a_payload_is_never_shared()
    {
        var arguments = Args("""{"on_data_of": "principal", "entityUrls": ["/me/messages"], "agentId": ""}""");
        var before = PyJson.Dumps(arguments);
        var transport = new FakeTransport();
        var guard = new WorkIQGuard(Binding, new FakeTokens(), transport, principalAccess: PrincipalAccessModes.Delegation);
        var result = await guard.CallAsync("fetch", arguments, RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal("ok", result.Status);
        Assert.Equal(before, PyJson.Dumps(arguments));
        Assert.Equal("""{"entityUrls": ["/users/principal-1/messages"], "agentId": ""}""", PyJson.Dumps(transport.Calls[0].Arguments));
    }

    [Fact]
    public async Task Json_body_nesting_limit_matches_cpython()
    {
        // Measured with guard.py on CPython 3.13: a body nested 9998 deep is valid JSON (the call then needs a claim);
        // at 9999 json.loads raises RecursionError, which escapes _check_json_body and is a malformed tool call.
        var guard = new WorkIQGuard(Binding, new FakeTokens(), new FakeTransport());
        foreach (var (depth, reason) in new[] { (9998, "no approval journal is configured; writes are unavailable"), (9999, "malformed tool call") })
        {
            var arguments = new JsonObject
            {
                ["on_data_of"] = "principal",
                ["entityUrl"] = "/me/events/e-1",
                ["jsonBody"] = new string('[', depth) + new string(']', depth),
            };
            var result = await guard.CallAsync("update_entity", arguments, RunModes.Attended, TestContext.Current.CancellationToken);
            Assert.Equal(("refused", reason), (result.Status, result.Reason));
        }
    }

    [Fact]
    public async Task Concurrent_calls_never_exceed_the_budget()
    {
        var budget = new CallBudget(5);
        var transport = new FakeTransport();
        var guard = new WorkIQGuard(Binding, new FakeTokens(), transport, budget: budget);
        var calls = Enumerable.Range(0, 40).Select(_ => Task.Run(() => guard.CallAsync(
            "fetch", Json("""{"on_data_of": "self", "entityUrls": ["/me"]}"""), RunModes.Unattended, TestContext.Current.CancellationToken)));
        var results = await Task.WhenAll(calls);
        Assert.Equal(5, results.Count(result => result.Status == "ok"));
        Assert.Equal(35, results.Count(result => result.Status == "budget_exhausted"));
        Assert.Equal(5, budget.Used);
        Assert.Equal(40, guard.Receipts.Count);
    }

    /// <summary>
    /// The guard accepts concurrent calls, but its journal is one SQLite connection whose transactions nest by
    /// connection state: a <c>MarkDispatched</c> that overlaps another becomes a savepoint inside it, and is lost if
    /// that transaction rolls back while the write is still sent. So the guard never lets two calls use the journal at
    /// the same time.
    /// </summary>
    [Fact]
    public async Task Concurrent_claimed_writes_never_use_the_journal_at_the_same_time()
    {
        using var desk = ApprovalDesk.Open();
        var claims = Enumerable.Range(0, 3).Select(_ => desk.Started()).ToList();
        using var journal = new OverlapDetectingJournal(new LedgerClaimJournal(desk.Ledger));
        var transport = new ConcurrentTransport();
        var guard = new WorkIQGuard(Binding, new FakeTokens(), transport, journal);
        var calls = claims.Select(claim => Task.Run(() => guard.CallAsync(
            "do_action", Send(claim.ActionId, claim.AttemptId), RunModes.Attended, TestContext.Current.CancellationToken)));
        var results = await Task.WhenAll(calls);
        Assert.Equal(1, journal.MaxActive);
        Assert.All(results, result => Assert.Equal("ok", result.Status));
        Assert.Equal(3, transport.Count);
        foreach (var (actionId, _) in claims)
        {
            Assert.Equal("succeeded", desk.State(actionId));
            Assert.Contains(LedgerClaimJournal.DispatchedEvent, desk.Events(actionId));
        }
    }

    [Fact]
    public void Budget_limits_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CallBudget(-1));
        var none = new CallBudget(0);
        Assert.False(none.Charge());
        Assert.Equal(0, none.Used);
    }

    [Fact]
    public void Constants_match_the_python_module()
    {
        Assert.Equal("https://workiq.svc.cloud.microsoft/mcp", WorkIQScopes.McpUrl);
        Assert.Equal(WorkIQEndpoints.McpUrl, WorkIQScopes.McpUrl);
        Assert.Equal("api://workiq.svc.cloud.microsoft", WorkIQScopes.Resource);
        Assert.Equal("api://workiq.svc.cloud.microsoft/.default", WorkIQScopes.DefaultScope);
        Assert.Equal(["principal", "self"], Subjects.Intents.Order(StringComparer.Ordinal));
        Assert.Equal(["attended", "third_party", "unattended"], RunModes.All.Order(StringComparer.Ordinal));
        Assert.Equal(["delegation", "obo"], PrincipalAccessModes.All.Order(StringComparer.Ordinal));
        Assert.Equal(["create_entity", "delete_entity", "do_action", "update_entity"], GuardPolicy.WriteTools.Order(StringComparer.Ordinal));
        Assert.Equal(12, GuardPolicy.ToolArgs.Count);
        Assert.Empty(GuardPolicy.ToolArgs["list_agents"]);
    }

    [Fact]
    public void Open_ledger_uses_the_instance_account()
    {
        using var state = TempState.Create();
        using var ledger = Binding.OpenLedger(state.Root);
        Assert.Equal("autopilot:agent-identity-1:principal-1", ledger.Account);
        Assert.Throws<ArgumentNullException>(() => InstanceLedger.OpenLedger(null!, state.Root));
    }
}

/// <summary>Wraps a journal and records the most calls that were inside it at once; each call waits briefly for a second one to arrive.</summary>
internal sealed class OverlapDetectingJournal(IClaimJournal inner) : IClaimJournal, IDisposable
{
    private readonly ManualResetEventSlim _second = new();
    private int _active;
    private int _maxActive;

    public int MaxActive => Volatile.Read(ref _maxActive);

    public ExecutionClaim Load(JsonNode? actionId, JsonNode? attemptId, string callKey = "workiq") =>
        Enter(() => inner.Load(actionId, attemptId, callKey));

    public void MarkDispatched(ExecutionClaim claim) => Enter(() =>
    {
        inner.MarkDispatched(claim);
        return 0;
    });

    public JsonObject Settle(ExecutionClaim claim, string state, int? statusCode = null) =>
        Enter(() => inner.Settle(claim, state, statusCode));

    public void Dispose() => _second.Dispose();

    private T Enter<T>(Func<T> body)
    {
        var active = Interlocked.Increment(ref _active);
        int seen;
        while ((seen = Volatile.Read(ref _maxActive)) < active && Interlocked.CompareExchange(ref _maxActive, active, seen) != seen)
        {
        }

        if (active > 1)
        {
            _second.Set();
        }
        else
        {
            _second.Wait(TimeSpan.FromMilliseconds(200));
        }

        try
        {
            return body();
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

/// <summary>A thread-safe transport that answers every call with <c>{"statusCode": 202}</c>.</summary>
internal sealed class ConcurrentTransport : IWorkIQTransport
{
    private int _count;

    public string Endpoint => WorkIQEndpoints.McpUrl;

    public int Count => Volatile.Read(ref _count);

    public Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return Task.FromResult(new JsonObject { ["statusCode"] = 202 });
    }
}
