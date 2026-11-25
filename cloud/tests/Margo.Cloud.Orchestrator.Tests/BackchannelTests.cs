using System.Text.Json.Nodes;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.Orchestrator.Tests;

/// <summary>
/// The backchannel: Margo reads what other people send her, looks into it, and tells her manager (and only her
/// manager) when it matters, with no card. She never answers the sender here, and never reads as the manager.
/// </summary>
public sealed class BackchannelTests
{
    private const string ChatId = "19:margo-dana-one-on-one";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private readonly List<string> _tokenKinds = [];

    private static (Orchestrator Orchestrator, DirectoryTransport Transport, FakeModel Model) Create(TempState state, OrchestratorOptions? options = null,
                                                                                               params object?[] script)
    {
        var transport = new DirectoryTransport
        {
            Answer = (tool, arguments) => tool == "fetch" && PyNode.AsString(arguments["entityUrls"]?[0])!.StartsWith("/me/chats?", StringComparison.Ordinal)
                ? Task.FromResult(Fixtures.Obj("""
                    {"results": [{"statusCode": 200, "data": {"value": [
                      {"id": "19:group@thread.v2", "chatType": "group", "members": [{"userId": "principal-1"}]},
                      {"id": "19:margo-dana-one-on-one", "chatType": "oneOnOne", "members": [{"userId": "margo-user-1"}, {"userId": "principal-1"}]}]}}]}
                    """))
                : tool == "fetch" && PyNode.AsString(arguments["entityUrls"]?[0])!.StartsWith("/shares/", StringComparison.Ordinal)
                    ? Task.FromResult(Fixtures.Obj("""{"results": [{"statusCode": 200, "data": {"id": "item-9", "parentReference": {"driveId": "drive-9"}}}]}"""))
                : tool is "create_entity" or "do_action" ? Task.FromResult(Fixtures.Obj("""{"statusCode": 201}""")) : null,
        };
        var model = new FakeModel(script);
        var engine = new Margo.Cloud.Engine.Engine(model, "fictional-model", Fixtures.Root);
        var stores = new Dictionary<string, InMemoryLeaseStore>(StringComparer.Ordinal);
        var orchestrator = new Orchestrator(engine, transport, state.Path, container =>
        {
            lock (stores)
            {
                return stores.TryGetValue(container, out var store) ? store : stores[container] = new InMemoryLeaseStore();
            }
        }, (options ?? new OrchestratorOptions()) with { IntakeRetryDelays = [], StateCliCommand = StateCli.Command });
        return (orchestrator, transport, model);
    }

    private Task<TurnReply> Handle(Orchestrator orchestrator, InboundEvent inbound) =>
        orchestrator.HandleAsync(inbound, kind =>
        {
            lock (_tokenKinds)
            {
                _tokenKinds.Add(kind);
            }

            return Task.FromResult<string?>(kind + "-token");
        }, Cancel);

    private static InboundEvent Group(string text = "Can we move the launch to Friday? Dana should approve the budget first.",
                                      string conversation = "19:group@thread.v2", string activity = "g-1") => new()
    {
        AgentIdentityId = "agent-identity-1", AgentUserId = "margo-user-1", SenderId = "colleague-1", SenderName = "Rafa",
        ConversationId = conversation, ConversationType = "groupChat", ActivityId = activity, Channel = "teams", Text = text,
    };

    private static InboundEvent ManagerMail() => new()
    {
        AgentIdentityId = "agent-identity-1", AgentUserId = "margo-user-1", SenderId = "principal-1", SenderName = "Dana",
        ConversationId = "mail-thread-1", ActivityId = "mail-1", Channel = "mail", Text = "What did Rafa decide about the launch date?",
    };

    private static JsonObject Tell(string via, string text, string? subject = null) =>
        FakeModel.Response("r1", FakeModel.Call("tell_manager", new JsonObject { ["via"] = via, ["text"] = text, ["subject"] = subject }.ToJsonString()));

    private static IEnumerable<(string Tool, JsonObject Arguments, string Bearer)> Writes(DirectoryTransport transport) =>
        transport.Calls.Where(call => call.Tool is "create_entity" or "do_action");

    [Fact]
    public async Task A_group_message_that_matters_becomes_a_teams_note_to_the_manager_alone()
    {
        using var state = new TempState();
        var (orchestrator, transport, model) = Create(state, null,
            Tell("teams", "Rafa asked in the launch chat to move the launch to Friday <and> wants your budget approval first."),
            FakeModel.Final("Sent one note."));
        var reply = await Handle(orchestrator, Group());
        Assert.Equal(TurnStatuses.Queued, reply.Status);
        Assert.Null(reply.Reply); // the group gets nothing from Margo

        var (tool, arguments, bearer) = Assert.Single(Writes(transport));
        Assert.Equal(("create_entity", "/chats/" + ChatId + "/messages", "margo-token"), (tool, PyNode.AsString(arguments["parentUrl"]), bearer));
        var content = PyNode.AsString(PyJson.Loads(PyNode.AsString(arguments["jsonBody"])!)!["body"]!["content"]);
        Assert.Contains("&lt;and&gt;", content, StringComparison.Ordinal); // escaped, never markup
        // The run was told what it saw as data, and that nobody asked it anything.
        var input = model.Requests[0].ToJsonString();
        Assert.Contains("An observation, not a request from your manager", input, StringComparison.Ordinal);
        Assert.Contains("tell_manager", model.Requests[0]["tools"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_tokenKinds, kind => kind == Subjects.Principal); // never the sender's token as the manager's
    }

    [Fact]
    public async Task Nothing_worth_telling_sends_nothing()
    {
        using var state = new TempState();
        var (orchestrator, transport, _) = Create(state, null, FakeModel.Final("Sent nothing."));
        await Handle(orchestrator, Group("Thanks all!"));
        Assert.Empty(Writes(transport));
    }

    [Fact]
    public async Task The_managers_email_is_answered_by_email_to_them_alone()
    {
        using var state = new TempState();
        var (orchestrator, transport, model) = Create(state, null,
            Tell("email", "Rafa proposed Friday in the launch chat; nothing is decided yet.", "Re: launch date"),
            FakeModel.Final("Emailed Dana."));
        await Handle(orchestrator, ManagerMail());
        var (tool, arguments, bearer) = Assert.Single(Writes(transport));
        Assert.Equal(("do_action", "/me/sendMail", "margo-token"), (tool, PyNode.AsString(arguments["actionUrl"]), bearer));
        var mail = PyJson.Loads(PyNode.AsString(arguments["jsonBody"])!)!;
        Assert.Equal("dana@example.com", PyNode.AsString(mail["message"]!["toRecipients"]![0]!["emailAddress"]!["address"]));
        Assert.Equal("Re: launch date", PyNode.AsString(mail["message"]!["subject"]));
        Assert.Contains("Your manager, Dana, emailed you", model.Requests[0].ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_run_per_conversation_within_the_cooldown_and_a_daily_cap()
    {
        using var state = new TempState();
        var (orchestrator, _, model) = Create(state, new OrchestratorOptions { BackchannelDailyRuns = 2 },
            FakeModel.Final("Sent nothing."), FakeModel.Final("Sent nothing."), FakeModel.Final("never"));
        await Handle(orchestrator, Group(activity: "g-1"));
        await Handle(orchestrator, Group(activity: "g-2")); // same conversation, inside the cooldown
        await Handle(orchestrator, Group(conversation: "19:other@thread.v2", activity: "g-3"));
        await Handle(orchestrator, Group(conversation: "19:third@thread.v2", activity: "g-4")); // over the daily cap
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task Switched_off_it_only_queues()
    {
        using var state = new TempState();
        var (orchestrator, transport, model) = Create(state, new OrchestratorOptions { Backchannel = false });
        Assert.Equal(TurnStatuses.Queued, (await Handle(orchestrator, Group())).Status);
        Assert.Empty(model.Requests);
        Assert.Empty(Writes(transport));
    }

    [Fact]
    public async Task In_a_chat_with_the_manager_email_works_and_a_teams_note_is_just_the_answer()
    {
        using var state = new TempState();
        var (orchestrator, transport, model) = Create(state, null,
            Tell("email", "The Q3 summary you asked for."),
            (Func<JsonObject, JsonNode?>)(_ => FakeModel.Response("r2", FakeModel.Call("tell_manager", """{"via": "teams", "text": "Hi"}""", "c2"))),
            FakeModel.Final("Emailed you the summary."));
        var reply = await Handle(orchestrator, new InboundEvent
        {
            AgentIdentityId = "agent-identity-1", AgentUserId = "margo-user-1", SenderId = "principal-1", SenderName = "Dana",
            ConversationId = "conv-manager", ConversationType = "personal", ActivityId = "m-1", Channel = "teams", Text = "Email me the Q3 summary.",
        });
        Assert.Equal("Emailed you the summary.", reply.Reply);
        Assert.Equal("/me/sendMail", PyNode.AsString(Assert.Single(Writes(transport)).Arguments["actionUrl"]));
        Assert.Contains("your answer is the message", model.Requests[2].ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_she_can_open_is_shared_with_the_manager_alone()
    {
        using var state = new TempState();
        var (orchestrator, transport, _) = Create(state, null,
            FakeModel.Response("r1", FakeModel.Call("share_with_manager", """{"link": "https://contoso.example.com/sites/launch/Shared%20Documents/plan.docx", "role": "read"}""")),
            FakeModel.Final("Shared the plan with Dana."));
        await Handle(orchestrator, Group("The launch plan is in the team site; Dana hasn't seen it."));
        var (tool, arguments, bearer) = Assert.Single(Writes(transport));
        Assert.Equal(("do_action", "/drives/drive-9/items/item-9/invite", "margo-token"), (tool, PyNode.AsString(arguments["actionUrl"]), bearer));
        Assert.Equal("""[{"objectId": "principal-1"}]""", PyJson.Dumps(PyJson.Loads(PyNode.AsString(arguments["jsonBody"])!)!["recipients"]));
    }
}
