using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Xunit;
using static Margo.Cloud.Guard.Tests.Fixtures;

namespace Margo.Cloud.Guard.Tests;

/// <summary>
/// Writes the manager approved on a card: under delegation an approved email goes out from the manager's mailbox
/// (<c>/users/{manager}/sendMail</c>), and <see cref="WorkIQGuard.PreviewWrite"/> refuses a proposal before the manager
/// is asked if it would be refused when run.
/// </summary>
public sealed class ApprovalWriteTests
{
    private static readonly JsonObject SendMail = Args("""
        {"tool": "do_action", "arguments": {"actionUrl": "/me/sendMail",
         "jsonBody": "{\"message\": {\"subject\": \"Fictional\", \"toRecipients\": [{\"emailAddress\": {\"address\": \"dana@example.com\"}}]}}"}}
        """);

    private readonly FakeTokens _tokens = new(token: subject => subject + "-token");
    private readonly FakeTransport _transport = new("""{"statusCode": 202}""");

    [Fact]
    public async Task An_approved_email_is_sent_through_the_manager_s_mailbox_under_delegation()
    {
        using var desk = ApprovalDesk.Open();
        var (actionId, attemptId) = desk.Started(SendMail);
        var guard = new WorkIQGuard(Binding, _tokens, _transport, new LedgerClaimJournal(desk.Ledger), principalAccess: PrincipalAccessModes.Delegation);
        var arguments = PyNode.CopyObject((JsonObject)SendMail["arguments"]!);
        arguments["on_data_of"] = "principal";
        arguments["action_id"] = actionId;
        arguments["attempt_id"] = attemptId;

        var result = await guard.CallAsync("do_action", arguments, RunModes.Attended, TestContext.Current.CancellationToken);

        Assert.Equal(GuardStatuses.Ok, result.Status);
        var (tool, sent, bearer) = Assert.Single(_transport.Calls);
        Assert.Equal(("do_action", "/users/principal-1/sendMail", "margo-token"), (tool, PyNode.AsString(sent["actionUrl"]), bearer));
        Assert.Equal("succeeded", PyNode.AsString(desk.Ledger.Show(actionId)["state"]));
    }

    [Theory]
    [InlineData("fetch")]
    [InlineData("call_function")]
    public async Task Send_mail_is_delegated_for_writes_only(string tool)
    {
        var guard = new WorkIQGuard(Binding, _tokens, _transport, principalAccess: PrincipalAccessModes.Delegation);
        var key = tool == "fetch" ? "entityUrls" : "functionUrl";
        var arguments = new JsonObject { ["on_data_of"] = "principal", [key] = tool == "fetch" ? new JsonArray("/me/sendMail") : "/me/sendMail" };
        var result = await guard.CallAsync(tool, arguments, RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal(GuardStatuses.Unavailable, result.Status);
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public void A_proposal_is_checked_before_the_manager_is_asked()
    {
        var delegated = new WorkIQGuard(Binding, _tokens, _transport, principalAccess: PrincipalAccessModes.Delegation);
        var mail = PyNode.CopyObject((JsonObject)SendMail["arguments"]!);
        mail["on_data_of"] = "principal";
        Assert.Equal(["/users/principal-1/sendMail"], delegated.PreviewWrite("do_action", mail));

        var meeting = Args("""{"on_data_of": "principal", "parentUrl": "/me/events", "jsonBody": "{\"subject\": \"Fictional\"}"}""");
        Assert.Equal(["/users/principal-1/events"], delegated.PreviewWrite("create_entity", meeting));

        var chat = Args("""{"on_data_of": "self", "parentUrl": "/chats/chat-1/messages", "jsonBody": "{\"body\": {\"content\": \"Hi\"}}"}""");
        Assert.Single(delegated.PreviewWrite("create_entity", chat));

        // Refused before the card: not a write, a claim, a bad body, a colleague's mailbox, a path delegation cannot reach.
        Assert.Throws<RefusedException>(() => delegated.PreviewWrite("fetch", Args("""{"on_data_of": "principal", "entityUrls": ["/me/messages"]}""")));
        Assert.Throws<RefusedException>(() => delegated.PreviewWrite("do_action", Args("""{"on_data_of": "principal", "actionUrl": "/me/sendMail", "action_id": "a"}""")));
        Assert.Throws<RefusedException>(() => delegated.PreviewWrite("do_action", Args("""{"on_data_of": "principal", "actionUrl": "/me/sendMail", "jsonBody": "not json"}""")));
        Assert.Throws<RefusedException>(() => delegated.PreviewWrite("do_action", Args("""{"on_data_of": "self", "actionUrl": "/users/colleague-1/sendMail"}""")));
        Assert.Throws<UnavailableException>(() => delegated.PreviewWrite("create_entity", Args("""{"on_data_of": "principal", "parentUrl": "/me/chats/chat-1/messages", "jsonBody": "{}"}""")));
        Assert.Empty(_transport.Calls);
    }
}
