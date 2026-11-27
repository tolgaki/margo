using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Xunit;
using static Margo.Cloud.Guard.Tests.Fixtures;

namespace Margo.Cloud.Guard.Tests;

/// <summary>
/// Review (security): the principal's token must be the bound principal's. The guard reads the subject (<c>oid</c>) of a
/// readable (JWS) principal token and refuses to send it when it names someone else, so a token exchanged for the wrong
/// person can never be presented, and labelled, as the principal's data. No Python equivalent.
/// </summary>
public sealed class TokenSubjectTests
{
    private const string Read = """{"on_data_of": "principal", "entityUrls": ["/me/messages"]}""";

    /// <summary>A fictional, unsigned compact JWS with the given payload (the guard never verifies signatures).</summary>
    private static string Jws(string payload) =>
        Segment("""{"alg":"RS256","typ":"JWT"}""") + "." + Segment(payload) + "." + Segment("fictional-signature");

    private static string Segment(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (WorkIQGuard Guard, FakeTransport Transport) Guard(string principalToken, string margoToken = MargoToken,
                                                                      string access = PrincipalAccessModes.Obo)
    {
        var transport = new FakeTransport();
        var tokens = new FakeTokens(token: subject => subject == Subjects.Principal ? principalToken : margoToken);
        return (new WorkIQGuard(Binding, tokens, transport, principalAccess: access), transport);
    }

    [Fact]
    public async Task A_principal_token_for_another_person_is_blocked_and_never_sent()
    {
        var (guard, transport) = Guard(Jws("""{"oid": "someone-else", "tid": "tenant-1"}"""));
        var result = await guard.CallAsync("fetch", Json(Read), RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal("blocked", result.Status);
        Assert.Equal("token unavailable for principal: the token is not the bound principal's", result.Reason);
        Assert.Empty(transport.Calls);
        Assert.DoesNotContain("someone-else", Dumps(guard.Receipts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_readable_principal_token_without_a_subject_is_blocked()
    {
        var (guard, transport) = Guard(Jws("""{"tid": "tenant-1"}"""));
        var result = await guard.CallAsync("fetch", Json(Read), RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal("blocked", result.Status);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task The_bound_principals_token_is_sent()
    {
        var token = Jws("""{"oid": "PRINCIPAL-1", "tid": "tenant-1"}"""); // object IDs compare without case
        var (guard, transport) = Guard(token);
        var result = await guard.CallAsync("fetch", Json(Read), RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal("ok", result.Status);
        Assert.Equal(token, Assert.Single(transport.Calls).Bearer);
    }

    [Fact]
    public async Task An_opaque_principal_token_cannot_be_read_and_is_sent_as_before()
    {
        var (guard, transport) = Guard(PrincipalToken);
        var result = await guard.CallAsync("fetch", Json(Read), RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal("ok", result.Status);
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task Margos_own_token_and_delegated_principal_reads_are_not_principal_tokens()
    {
        var margo = Jws("""{"oid": "margo-user-1"}""");
        var (guard, transport) = Guard(Jws("""{"oid": "someone-else"}"""), margo, PrincipalAccessModes.Delegation);
        var ct = TestContext.Current.CancellationToken;
        var self = await guard.CallAsync("fetch", Json("""{"on_data_of": "self", "entityUrls": ["/me/messages"]}"""), RunModes.Attended, ct);
        var delegated = await guard.CallAsync("fetch", Json(Read), RunModes.Attended, ct);
        Assert.Equal(("ok", "ok"), (self.Status, delegated.Status));
        Assert.Equal([margo, margo], transport.Calls.Select(call => call.Bearer));
    }

    [Fact]
    public async Task A_claimed_write_with_a_wrong_subject_token_is_never_dispatched()
    {
        var journal = new RecordingJournal();
        var transport = new FakeTransport();
        var tokens = new FakeTokens(token: subject => subject == Subjects.Principal ? Jws("""{"oid": "someone-else"}""") : MargoToken);
        var guard = new WorkIQGuard(Binding, tokens, transport, journal);
        var result = await guard.CallAsync("do_action", Json("""
            {"on_data_of": "principal", "actionUrl": "/me/messages/draft-1/send", "action_id": "a-1", "attempt_id": "t-1"}
            """), RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal("blocked", result.Status);
        Assert.Empty(transport.Calls);
        Assert.Equal(0, journal.Dispatched);
        Assert.Equal(["failed"], journal.Settled);
    }

    /// <summary>A journal holding one live claim for <c>/me/messages/draft-1/send</c>, approved by the principal.</summary>
    private sealed class RecordingJournal : IClaimJournal
    {
        public int Dispatched { get; private set; }

        public List<string> Settled { get; } = [];

        public ExecutionClaim Load(JsonNode? actionId, JsonNode? attemptId, string callKey = "workiq") =>
            new(Binding.Account, "a-1", "t-1", 1, "hash-1", "principal", callKey,
                Args("""{"tool": "do_action", "arguments": {"actionUrl": "/me/messages/draft-1/send"}}"""), "principal-1");

        public void MarkDispatched(ExecutionClaim claim) => Dispatched++;

        public JsonObject Settle(ExecutionClaim claim, string state, int? statusCode = null)
        {
            Settled.Add(state);
            return new JsonObject();
        }
    }
}
