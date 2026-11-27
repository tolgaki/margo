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

    private readonly Dictionary<string, TestStore> _stores = new(StringComparer.Ordinal);
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
                    store = new TestStore(new InMemoryLeaseStore());
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
                                      string? conversationType = "personal") => new()
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
    private static object?[] ProposeAndAsk(string input = Input) =>
    [
        FakeModel.Response("r1", FakeModel.Call("margo-state",
            PyJson.Dumps(new JsonObject { ["script"] = "work_state.py", ["args"] = new JsonArray("propose", "--input", "-"), ["input"] = PyJson.Loads(input) }))),
        (Func<JsonObject, JsonNode?>)(request =>
        {
            var id = ActionId().Matches(request.ToJsonString()).Last().Value;
            return FakeModel.Response("r2", FakeModel.Call("request_approval", "{\"action_id\": \"" + id + "\"}", "c2"));
        }),
        FakeModel.Final("I've put the email on a card for you to approve."),
    ];

    private bool Sent() => _transport.Calls.Any(call => call.Tool == "do_action");

    private const string ReplyInput = """
        {"kind": "mail.reply", "target": {"on_data_of": "principal", "message": "msg-1"},
         "payload": {"workiq": {"tool": "do_action", "arguments": {"actionUrl": "/me/messages/msg-1/reply",
           "jsonBody": "{\"comment\": \"Thursday works.\"}"}}},
         "why": "Rafa asked when the review is.", "source_refs": [], "target_fingerprint": "msg-1", "work_item_id": null}
        """;

    [Fact]
    public async Task A_card_for_a_reply_says_which_email_it_answers()
    {
        // The call names the email only by its ID: the card reads it from the mailbox and says which one it is.
        using var state = new TempState();
        var orchestrator = Create(state, ProposeAndAsk(ReplyInput));
        _transport.Answer = (tool, arguments) => tool == "fetch" && PyNode.AsString(arguments["entityUrls"]?[0])!.Contains("/messages/msg-1?$select=", StringComparison.Ordinal)
            ? Task.FromResult(Fixtures.Obj("""{"results": [{"statusCode": 200, "data": {"subject": "Q3 numbers", "from": {"emailAddress": {"name": "Rafa", "address": "rafa@example.com"}}, "receivedDateTime": "2026-11-24T09:15:00Z"}}]}"""))
            : null;
        var card = Assert.Single((await Handle(orchestrator, Event("a-1", "Tell Rafa Thursday works."))).Cards);
        Assert.Equal(ApprovalKind.Reply, card.ApprovalKind);
        Assert.Equal([new CardFact("Replying to", "\u201cQ3 numbers\u201d from Rafa (rafa@example.com), November 24")], card.Context);
        Assert.False(Sent());

        // The tap carries the context back, so the card's last version still says which email it was.
        var decision = new CardDecision(CardDecision.Approve, card.ActionId, card.Revision, card.ActionHash) { Context = card.Context };
        var approved = await Handle(orchestrator, Event("a-2", decision: decision));
        Assert.Equal("Done: the email has gone out.", approved.Reply);
        Assert.Equal(card.Context, approved.Decided!.Context);
    }

    [Fact]
    public async Task A_card_says_so_when_it_cannot_look_up_the_email()
    {
        using var state = new TempState();
        var orchestrator = Create(state, ProposeAndAsk(ReplyInput));
        _transport.Answer = (tool, arguments) => tool == "fetch" && PyNode.AsString(arguments["entityUrls"]?[0])!.Contains("/messages/msg-1", StringComparison.Ordinal)
            ? Task.FromResult(Fixtures.Obj("""{"results": [{"statusCode": 404, "error": {"code": "ErrorItemNotFound"}}]}"""))
            : null;
        var card = Assert.Single((await Handle(orchestrator, Event("a-1", "Tell Rafa Thursday works."))).Cards);
        Assert.Equal([new CardFact("Which one", CardContext.NotFound)], card.Context);
    }

    [Theory]
    [InlineData("refused", "Nothing went out: this isn't something I'm allowed to do for you.")]
    [InlineData("blocked", "Nothing went out: your organization's settings don't allow it.")]
    [InlineData("error", "Nothing went out: Microsoft 365 turned it down.")]
    public void Failure_reasons_are_plain(string status, string expected)
    {
        Assert.Equal(expected, CardDecisions.NotSent.Replace("{0}", CardDecisions.PlainReason(status), StringComparison.Ordinal));
    }

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
        Assert.Equal((TurnStatuses.Decided, "Done: the email has gone out."), (approved.Status, approved.Reply));
        Assert.Equal("sent", approved.Decided!.OutcomeStatus);
        var (tool, arguments, bearer) = Assert.Single(_transport.Calls, call => call.Tool == "do_action");
        Assert.Equal(("/users/principal-1/sendMail", "margo-token"), (PyNode.AsString(arguments["actionUrl"]), bearer));
        Assert.Equal(PyNode.AsString(card.Arguments["jsonBody"]), PyNode.AsString(arguments["jsonBody"]));

        // A second tap on the same card: already done, nothing sent again.
        var again = await Handle(orchestrator, Event("a-3", decision: decision));
        Assert.Equal("This card is out of date (it was already done). Nothing was sent. Ask me again if you still want it.", again.Reply);
        Assert.Single(_transport.Calls, call => call.Tool == "do_action");
    }

    private const string CancelInput = """
        {"kind": "calendar.cancel", "target": {"on_data_of": "principal", "event": "evt-1"},
         "payload": {"workiq": {"tool": "do_action", "arguments": {"actionUrl": "/me/events/evt-1/cancel",
           "jsonBody": "{\"comment\": \"Something came up.\"}"}}},
         "why": "You asked me to cancel it.", "source_refs": [], "target_fingerprint": "evt-1", "work_item_id": null}
        """;

    /// <summary>The meeting a cancellation acts on, as Microsoft 365 answers for it now.</summary>
    private void Meeting(string? subject, string start = "2026-12-03T14:00:00")
    {
        _transport.Answer = (tool, arguments) =>
        {
            if (tool != "fetch" || !PyNode.AsString(arguments["entityUrls"]?[0])!.Contains("/events/evt-1", StringComparison.Ordinal))
            {
                return null;
            }

            if (subject is null)
            {
                return Task.FromResult(Fixtures.Obj("{\"results\": [{\"statusCode\": 503}]}"));
            }

            var item = new JsonObject
            {
                ["subject"] = subject,
                ["start"] = new JsonObject { ["dateTime"] = start, ["timeZone"] = "UTC" },
                ["end"] = new JsonObject { ["dateTime"] = "2026-12-03T14:30:00", ["timeZone"] = "UTC" },
            };
            return Task.FromResult(new JsonObject { ["results"] = new JsonArray(new JsonObject { ["statusCode"] = 200, ["data"] = item }) });
        };
    }

    private async Task<(Orchestrator Orchestrator, CardDecision Decision)> AskToCancel(TempState state)
    {
        var orchestrator = Create(state, ProposeAndAsk(CancelInput));
        Meeting("Budget review");
        var card = Assert.Single((await Handle(orchestrator, Event("a-1", "Cancel the budget review."))).Cards);
        Assert.Contains(new CardFact("Meeting", "Budget review"), card.Context);
        return (orchestrator, new CardDecision(CardDecision.Approve, card.ActionId, card.Revision, card.ActionHash) { Context = card.Context });
    }

    [Fact]
    public async Task What_an_approval_acts_on_is_read_again_before_it_runs()
    {
        using var state = new TempState();
        var (orchestrator, decision) = await AskToCancel(state);
        var readsBefore = _transport.Calls.Count(call => call.Tool == "fetch");
        var approved = await Handle(orchestrator, Event("a-2", decision: decision));
        Assert.Equal("Done: the meeting is canceled and the attendees are being told.", approved.Reply);
        Assert.True(_transport.Calls.Count(call => call.Tool == "fetch") > readsBefore); // the meeting was read at the tap
    }

    [Fact]
    public async Task An_approval_is_not_used_when_what_it_acts_on_has_changed()
    {
        // The card said "Budget review, 2:00 PM". By the tap the meeting has moved: that is not what was approved.
        using var state = new TempState();
        var (orchestrator, decision) = await AskToCancel(state);
        Meeting("Budget review", start: "2026-12-04T09:00:00");
        var stale = await Handle(orchestrator, Event("a-2", decision: decision));
        Assert.Equal("This card is out of date (what it acts on has changed since I made it). Nothing was sent. Ask me again if you still want it.", stale.Reply);
        Assert.Equal("out_of_date", stale.Decided!.OutcomeStatus);
        Assert.False(Sent());

        // The action is dismissed, not left waiting: the same card cannot run later even if the meeting moves back.
        Meeting("Budget review");
        await Handle(orchestrator, Event("a-3", decision: decision));
        Assert.False(Sent());
    }

    [Fact]
    public async Task An_approval_waits_when_what_it_acts_on_cannot_be_read()
    {
        using var state = new TempState();
        var (orchestrator, decision) = await AskToCancel(state);
        Meeting(null);
        var waiting = await Handle(orchestrator, Event("a-2", decision: decision));
        Assert.StartsWith("Nothing went out: I couldn't check that what this acts on is still as the card says.", waiting.Reply, StringComparison.Ordinal);
        Assert.Null(waiting.Decided); // the card keeps its buttons
        Assert.False(Sent());

        Meeting("Budget review");
        Assert.Equal("Done: the meeting is canceled and the attendees are being told.", (await Handle(orchestrator, Event("a-3", decision: decision))).Reply);
    }

    [Fact]
    public async Task A_sent_action_is_not_sent_again_when_the_turn_s_state_cannot_be_saved()
    {
        // The claim is saved to the shared state before the call leaves. The save at the end of the turn then fails, so
        // this machine's copy is discarded: what is installed next must still say the action was sent.
        using var state = new TempState();
        var orchestrator = Create(state, ProposeAndAsk());
        var card = Assert.Single((await Handle(orchestrator, Event("a-1", "Email Dana that it's working."))).Cards);
        var decision = new CardDecision(CardDecision.Approve, card.ActionId, card.Revision, card.ActionHash);
        var store = Assert.Single(_stores.Values);
        store.WritesLeft = 1; // the checkpoint before dispatch; the turn's own save fails

        var approved = await Handle(orchestrator, Event("a-2", decision: decision));
        Assert.Equal(new TurnReply(Orchestrator.StateNotSaved, "state_unsafe"), approved);
        Assert.Single(_transport.Calls, call => call.Tool == "do_action");

        store.WritesLeft = null;
        var again = await Handle(orchestrator, Event("a-3", decision: decision)); // a second tap on the card, which kept its buttons
        Assert.Equal("This card is out of date (it was already approved and run). Nothing was sent. Ask me again if you still want it.", again.Reply);
        Assert.Single(_transport.Calls, call => call.Tool == "do_action");
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_claim_cannot_be_saved_first()
    {
        using var state = new TempState();
        var orchestrator = Create(state, ProposeAndAsk());
        var card = Assert.Single((await Handle(orchestrator, Event("a-1", "Email Dana that it's working."))).Cards);
        var decision = new CardDecision(CardDecision.Approve, card.ActionId, card.Revision, card.ActionHash);
        var store = Assert.Single(_stores.Values);
        store.FailWrites = true;

        await Handle(orchestrator, Event("a-2", decision: decision));
        Assert.False(Sent());

        // Nothing went out and nothing was saved, so the card still stands: approved again, it runs once.
        store.FailWrites = false;
        var approved = await Handle(orchestrator, Event("a-3", decision: decision));
        Assert.Equal("Done: the email has gone out.", approved.Reply);
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
    public async Task Only_the_manager_decides_in_their_chat_or_a_room()
    {
        using var state = new TempState();
        var orchestrator = Create(state, ProposeAndAsk());
        var card = Assert.Single((await Handle(orchestrator, Event("a-1", "Email Dana."))).Cards);
        var decision = new CardDecision(CardDecision.Approve, card.ActionId, card.Revision, card.ActionHash);

        var colleague = await Handle(orchestrator, Event("a-2", decision: decision, sender: "colleague-1"));
        var colleagueInRoom = await Handle(orchestrator, Event("a-3", decision: decision, sender: "colleague-1", conversationType: "groupChat"));
        var unnamed = await Handle(orchestrator, Event("a-5", decision: decision, conversationType: null));
        Assert.Equal((TurnStatuses.DecisionRefused, (string?)null), (colleague.Status, colleague.Reply));
        Assert.Equal((TurnStatuses.DecisionRefused, (string?)null), (colleagueInRoom.Status, colleagueInRoom.Reply));
        Assert.Equal((TurnStatuses.DecisionRefused, (string?)null), (unnamed.Status, unnamed.Reply));
        // A card for a revision or hash the ledger does not hold runs nothing.
        var forged = await Handle(orchestrator, Event("a-4", decision: decision with { ActionHash = "not-the-hash" }));
        Assert.StartsWith("This card is out of date", forged.Reply, StringComparison.Ordinal);
        Assert.False(Sent());
        // The manager's own tap in the room the card was posted in counts.
        var room = await Handle(orchestrator, Event("a-6", decision: decision, conversationType: "groupChat"));
        Assert.NotEqual(TurnStatuses.DecisionRefused, room.Status);
        Assert.True(Sent());
    }
}
