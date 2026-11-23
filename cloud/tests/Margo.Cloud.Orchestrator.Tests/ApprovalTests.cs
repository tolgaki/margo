using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.Orchestrator.Tests;

/// <summary>
/// Approval cards end to end, with the real state CLI: Margo proposes an exact Work IQ call and asks for approval; the
/// reply carries the card; the manager's tap runs exactly that call once, through the manager's mailbox.
/// </summary>
public sealed partial class ApprovalTests
{
    private const string Input = """
        {"kind": "mail.send", "target": {"on_data_of": "principal", "to": ["dana@example.com"]},
         "payload": {"workiq": {"tool": "do_action", "arguments": {"actionUrl": "/me/sendMail",
           "jsonBody": "{\"message\": {\"subject\": \"It's working\", \"body\": {\"contentType\": \"Text\", \"content\": \"Drafted by Margo.\"}, \"toRecipients\": [{\"emailAddress\": {\"address\": \"dana@example.com\"}}]}, \"saveToSentItems\": true}"}}},
         "why": "The manager asked for this email.", "source_refs": [], "target_fingerprint": "dana@example.com", "work_item_id": null}
        """;

    private readonly Dictionary<string, InMemoryLeaseStore> _stores = new(StringComparer.Ordinal);
    private DirectoryTransport _transport = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [GeneratedRegex("act_[0-9a-f]{32}")]
    private static partial Regex ActionId();

    private Orchestrator Create(TempState state, params object?[] script)
    {
        _transport = new DirectoryTransport();
        var engine = new Margo.Cloud.Engine.Engine(new FakeModel(script), "fictional-model", Fixtures.Root);
        return new Orchestrator(engine, _transport, state.Path, container =>
        {
            lock (_stores)
            {
                if (!_stores.TryGetValue(container, out var store))
                {
                    store = new InMemoryLeaseStore();
                    _stores[container] = store;
                }

                return store;
            }
        }, new OrchestratorOptions
        {
            IntakeRetryDelays = [], PrincipalAccess = PrincipalAccessModes.Delegation, StateCliCommand = StateCli.Command,
        });
    }

    private static InboundEvent Event(string activity, string? text = null, CardDecision? decision = null, string sender = "principal-1",
                                      string conversationType = "personal") => new()
    {
        AgentIdentityId = "agent-identity-1",
        AgentUserId = "margo-user-1",
        SenderId = sender,
        SenderName = "Dana",
        ConversationId = "conv-manager",
        ConversationType = conversationType,
        ActivityId = activity,
        Channel = "teams",
        Text = text,
        Decision = decision,
    };

    private static Task<TurnReply> Handle(Orchestrator orchestrator, InboundEvent inbound) =>
        orchestrator.HandleAsync(inbound, kind => Task.FromResult<string?>(kind + "-token"), Cancel);

    /// <summary>A model that proposes <see cref="Input"/>, asks for its approval with the ID the state tool returned,
    /// then answers.</summary>
    private static object?[] ProposeAndAsk() =>
    [
        FakeModel.Response("r1", FakeModel.Call("margo-state",
            PyJson.Dumps(new JsonObject { ["script"] = "work_state.py", ["args"] = new JsonArray("propose", "--input", "-"), ["input"] = PyJson.Loads(Input) }))),
        (Func<JsonObject, JsonNode?>)(request =>
        {
            var id = ActionId().Matches(request.ToJsonString()).Last().Value;
            return FakeModel.Response("r2", FakeModel.Call("request_approval", "{\"action_id\": \"" + id + "\"}", "c2"));
        }),
        FakeModel.Final("I've put the email on a card for you to approve."),
    ];

    private bool Sent() => _transport.Calls.Any(call => call.Tool == "do_action");

    [Fact]
    public async Task An_approved_card_runs_exactly_that_call_once()
    {
        using var state = new TempState();
        var orchestrator = Create(state, ProposeAndAsk());
        var asked = await Handle(orchestrator, Event("a-1", "Email Dana that it's working."));
        Assert.Equal(TurnStatuses.Answered, asked.Status);
        var card = Assert.Single(asked.Cards);
        Assert.Equal(("mail.send", "principal", "do_action", "/me/sendMail"), (card.Kind, card.OnDataOf, card.Tool, PyNode.AsString(card.Arguments["actionUrl"])));
        Assert.False(Sent()); // asking sends nothing

        var decision = new CardDecision(CardDecision.Approve, card.ActionId, card.Revision, card.ActionHash);
        var approved = await Handle(orchestrator, Event("a-2", decision: decision));
        Assert.Equal((TurnStatuses.Decided, "Done: what the card showed has gone out."), (approved.Status, approved.Reply));
        Assert.Equal("sent", approved.Decided!.OutcomeStatus);
        var (tool, arguments, bearer) = Assert.Single(_transport.Calls, call => call.Tool == "do_action");
        Assert.Equal(("/users/principal-1/sendMail", "margo-token"), (PyNode.AsString(arguments["actionUrl"]), bearer));
        Assert.Equal(PyNode.AsString(card.Arguments["jsonBody"]), PyNode.AsString(arguments["jsonBody"]));

        // A second tap on the same card: already done, nothing sent again.
        var again = await Handle(orchestrator, Event("a-3", decision: decision));
        Assert.Equal("This card is out of date (it was already done). Nothing was sent. Ask me again if you still want it.", again.Reply);
        Assert.Single(_transport.Calls, call => call.Tool == "do_action");
    }

    [Fact]
    public async Task A_rejected_card_sends_nothing_and_cannot_be_approved_afterwards()
    {
        using var state = new TempState();
        var orchestrator = Create(state, ProposeAndAsk());
        var card = Assert.Single((await Handle(orchestrator, Event("a-1", "Email Dana."))).Cards);
        var reject = await Handle(orchestrator, Event("a-2", decision: new CardDecision(CardDecision.Reject, card.ActionId, card.Revision, card.ActionHash)));
        Assert.Equal(("Rejected. Nothing was sent.", "rejected"), (reject.Reply, reject.Decided!.OutcomeStatus));
        var late = await Handle(orchestrator, Event("a-3", decision: new CardDecision(CardDecision.Approve, card.ActionId, card.Revision, card.ActionHash)));
        Assert.Equal("This card is out of date (it was rejected). Nothing was sent. Ask me again if you still want it.", late.Reply);
        Assert.False(Sent());
    }

    [Fact]
    public async Task Only_the_manager_in_their_one_to_one_chat_decides()
    {
        using var state = new TempState();
        var orchestrator = Create(state, ProposeAndAsk());
        var card = Assert.Single((await Handle(orchestrator, Event("a-1", "Email Dana."))).Cards);
        var decision = new CardDecision(CardDecision.Approve, card.ActionId, card.Revision, card.ActionHash);

        var colleague = await Handle(orchestrator, Event("a-2", decision: decision, sender: "colleague-1"));
        var group = await Handle(orchestrator, Event("a-3", decision: decision, conversationType: "groupChat"));
        Assert.Equal((TurnStatuses.DecisionRefused, (string?)null), (colleague.Status, colleague.Reply));
        Assert.Equal((TurnStatuses.DecisionRefused, (string?)null), (group.Status, group.Reply));
        // A card for a revision or hash the ledger does not hold runs nothing.
        var forged = await Handle(orchestrator, Event("a-4", decision: decision with { ActionHash = "not-the-hash" }));
        Assert.StartsWith("This card is out of date", forged.Reply, StringComparison.Ordinal);
        Assert.False(Sent());
    }
}
