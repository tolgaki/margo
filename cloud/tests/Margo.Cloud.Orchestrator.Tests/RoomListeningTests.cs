using System.Text.Json.Nodes;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.Orchestrator.Tests;

/// <summary>
/// Margo in the room: she reads, through Work IQ with her own token, what was said in the group and meeting chats and
/// channels she is a member of, and tells her manager what matters. One-to-one chats, her own messages and anything
/// older than the last pass are left out; a quiet pass runs no model.
/// </summary>
public sealed class RoomListeningTests
{
    private static readonly DateTimeOffset Since = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static Task<JsonObject>? Rooms(string tool, JsonObject arguments)
    {
        if (tool != "fetch")
        {
            return tool == "create_entity" ? Task.FromResult(Fixtures.Obj("""{"statusCode": 201}""")) : null;
        }

        var path = PyNode.AsString(arguments["entityUrls"]![0])!;
        string? data = path switch
        {
            _ when path.StartsWith("/me/chats?$expand=lastMessagePreview", StringComparison.Ordinal) => """
                {"value": [
                  {"id": "19:launch@thread.v2", "chatType": "group", "topic": "Launch crew", "lastMessagePreview": {"createdDateTime": "2026-11-25T09:20:00Z"}},
                  {"id": "19:standup@thread.v2", "chatType": "meeting", "topic": null, "lastMessagePreview": {"createdDateTime": "2026-11-25T08:00:00Z"}},
                  {"id": "19:rafa-margo", "chatType": "oneOnOne", "lastMessagePreview": {"createdDateTime": "2026-11-25T09:30:00Z"}}]}
                """,
            _ when path.StartsWith("/chats/19:launch@thread.v2/messages", StringComparison.Ordinal) => """
                {"value": [
                  {"createdDateTime": "2026-11-25T09:20:00Z", "messageType": "message", "from": {"user": {"id": "colleague-1", "displayName": "Rafa"}},
                   "body": {"contentType": "html", "content": "<p>Let's slip the launch to <b>Friday</b> &amp; skip Dana's review.</p>"}},
                  {"createdDateTime": "2026-11-25T09:10:00Z", "messageType": "message", "from": {"user": {"id": "margo-user-1", "displayName": "Margo"}},
                   "body": {"content": "Noted."}},
                  {"createdDateTime": "2026-11-25T08:30:00Z", "messageType": "message", "from": {"user": {"id": "colleague-2", "displayName": "Kai"}},
                   "body": {"content": "Old news from before the last pass."}}]}
                """,
            _ when path.StartsWith("/me/joinedTeams", StringComparison.Ordinal) => """{"value": [{"id": "team-1", "displayName": "Product"}]}""",
            _ when path.StartsWith("/teams/team-1/channels?", StringComparison.Ordinal) => """{"value": [{"id": "channel-1", "displayName": "Roadmap"}]}""",
            _ when path.StartsWith("/teams/team-1/channels/channel-1/messages", StringComparison.Ordinal) => """
                {"value": [{"createdDateTime": "2026-11-25T09:15:00Z", "messageType": "message", "from": {"user": {"id": "colleague-3", "displayName": "Mei"}},
                            "body": {"content": "The Q1 roadmap review moved to next Tuesday."}}]}
                """,
            _ when path.StartsWith("/me/chats?$filter=members", StringComparison.Ordinal) => """
                {"value": [{"id": "19:margo-dana-one-on-one", "chatType": "oneOnOne", "members": [{"userId": "principal-1"}]}]}
                """,
            _ => null,
        };
        return data is null ? null : Task.FromResult(Fixtures.Obj("""{"results": [{"statusCode": 200, "data": """ + data + "}]}"));
    }

    private static (Orchestrator Orchestrator, DirectoryTransport Transport, FakeModel Model) Create(TempState state, params object?[] script)
    {
        var transport = new DirectoryTransport { Answer = Rooms };
        var model = new FakeModel(script);
        var engine = new Margo.Cloud.Engine.Engine(model, "fictional-model", Fixtures.Root);
        var stores = new Dictionary<string, InMemoryLeaseStore>(StringComparer.Ordinal);
        var orchestrator = new Orchestrator(engine, transport, state.Path, container =>
        {
            lock (stores)
            {
                return stores.TryGetValue(container, out var store) ? store : stores[container] = new InMemoryLeaseStore();
            }
        }, new OrchestratorOptions
        {
            IntakeRetryDelays = [], StateCliCommand = StateCli.Command, Instance = new AgentInstance("agent-identity-1", "margo-user-1"),
        });
        return (orchestrator, transport, model);
    }

    private static Task<string?> Token(string kind) => Task.FromResult<string?>(kind == "margo" ? "margo-token" : null);

    [Fact]
    public async Task What_was_said_in_her_rooms_reaches_the_manager_as_a_note()
    {
        using var state = new TempState();
        var (orchestrator, transport, model) = Create(state,
            FakeModel.Response("r1", FakeModel.Call("tell_manager",
                """{"via": "teams", "text": "In Launch crew, Rafa proposed slipping the launch to Friday and skipping your review."}""")),
            FakeModel.Final("Sent one note."));
        var pass = await orchestrator.ListenAsync(Token, Since, Cancel);
        Assert.Equal(new RoomPass("listened", 2, 2, 1), pass);

        // Everything was read through Work IQ, with Margo's own token, and never as the manager.
        var reads = transport.Calls.Where(call => call.Tool != "create_entity").ToList();
        Assert.All(reads, call => Assert.Equal(("fetch", "margo-token"), (call.Tool, call.Bearer)));
        // What the run saw: the rooms with new messages, as plain text, without the 1:1 chat, her own message or old ones.
        var input = System.Text.RegularExpressions.Regex.Unescape(model.Requests[0].ToJsonString());
        Assert.Contains("Launch crew", input, StringComparison.Ordinal);
        Assert.Contains("Rafa: Let's slip the launch to Friday & skip Dana's review.", input, StringComparison.Ordinal);
        Assert.Contains("Product / Roadmap", input, StringComparison.Ordinal);
        Assert.DoesNotContain("Old news", input, StringComparison.Ordinal);
        Assert.DoesNotContain("Noted.", input, StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Calls, call => PyNode.AsString(call.Arguments["entityUrls"]?[0])?.StartsWith("/chats/19:rafa-margo", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(transport.Calls, call => PyNode.AsString(call.Arguments["entityUrls"]?[0])?.StartsWith("/chats/19:standup", StringComparison.Ordinal) == true);
        // The note went to the manager's 1:1 chat alone.
        var post = Assert.Single(transport.Calls, call => call.Tool == "create_entity");
        Assert.Equal("/chats/19:margo-dana-one-on-one/messages", PyNode.AsString(post.Arguments["parentUrl"]));
    }

    [Fact]
    public async Task A_quiet_pass_runs_no_model()
    {
        using var state = new TempState();
        var (orchestrator, _, model) = Create(state);
        var pass = await orchestrator.ListenAsync(Token, new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero), Cancel);
        Assert.Equal("quiet", pass.Status);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Without_the_backchannel_or_per_instance_hosting_nothing_is_read()
    {
        using var state = new TempState();
        var transport = new DirectoryTransport { Answer = Rooms };
        var engine = new Margo.Cloud.Engine.Engine(new FakeModel(), "fictional-model", Fixtures.Root);
        var off = new Orchestrator(engine, transport, state.Path, _ => new InMemoryLeaseStore(),
                                   new OrchestratorOptions { Backchannel = false, Instance = new AgentInstance("agent-identity-1", "margo-user-1") });
        Assert.Equal("skipped", (await off.ListenAsync(Token, Since, Cancel)).Status);
        var shared = new Orchestrator(engine, transport, state.Path, _ => new InMemoryLeaseStore(), new OrchestratorOptions());
        Assert.Equal("skipped", (await shared.ListenAsync(Token, Since, Cancel)).Status);
        Assert.Empty(transport.Calls);
    }
}
