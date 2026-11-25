using System.Text.Json.Nodes;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;
using Margo.Cloud.WorkIQ;
using Xunit;

namespace Margo.Cloud.Orchestrator.Tests;

/// <summary><c>create_document</c> in a manager turn: created in Margo's OneDrive, shared with the manager only, never retried.</summary>
public sealed class DocumentTests
{
    private const string Link = "https://contoso.example.com/personal/margo/Documents/Q3%20review.docx";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static (Orchestrator Orchestrator, DirectoryTransport Transport, FakeModel Model) Create(TempState state)
    {
        var transport = new DirectoryTransport();
        var model = new FakeModel(
            FakeModel.Response("r1", FakeModel.Call("create_document",
                """{"app": "Word", "outline": "# Q3 review\n\nFictional content.", "request": "Write up the Q3 review."}""")),
            FakeModel.Final("Here is the Q3 review."));
        var engine = new Margo.Cloud.Engine.Engine(model, "fictional-model", Fixtures.Root);
        var stores = new Dictionary<string, InMemoryLeaseStore>(StringComparer.Ordinal);
        var orchestrator = new Orchestrator(engine, transport, state.Path, container =>
        {
            lock (stores)
            {
                return stores.TryGetValue(container, out var store) ? store : stores[container] = new InMemoryLeaseStore();
            }
        }, new OrchestratorOptions { IntakeRetryDelays = [], PrincipalAccess = PrincipalAccessModes.Delegation, StateCliCommand = StateCli.Command });
        return (orchestrator, transport, model);
    }

    private static InboundEvent Ask() => new()
    {
        AgentIdentityId = "agent-identity-1", AgentUserId = "margo-user-1", SenderId = "principal-1", SenderName = "Dana",
        ConversationId = "conv-manager", ConversationType = "personal", ActivityId = "a-1", Channel = "teams",
        Text = "Write up the Q3 review as a Word document.",
    };

    private static Task<JsonObject>? Answer(string tool, JsonObject arguments) =>
        PyNode.AsString(arguments["actionUrl"]) switch
        {
            "/office-agent-mcp/generate_artifact" => Task.FromResult(Fixtures.Obj(
                "{\"statusCode\": 200, \"resource_links\": [{\"uri\": \"" + Link + "\", \"name\": \"Q3 review.docx\"}]}")),
            "/drives/drive-1/items/item-1/invite" => Task.FromResult(Fixtures.Obj("""{"statusCode": 200}""")),
            _ when tool == "fetch" && PyNode.AsString(arguments["entityUrls"]?[0])!.StartsWith("/shares/", StringComparison.Ordinal) =>
                Task.FromResult(Fixtures.Obj("""{"results": [{"statusCode": 200, "data": {"id": "item-1", "parentReference": {"driveId": "drive-1"}}}]}""")),
            _ => null,
        };

    [Fact]
    public async Task A_document_is_created_as_margo_and_shared_with_the_manager_only()
    {
        using var state = new TempState();
        var (orchestrator, transport, model) = Create(state);
        transport.Answer = Answer;
        var reply = await orchestrator.HandleAsync(Ask(), kind => Task.FromResult<string?>(kind + "-token"), Cancel);
        Assert.Equal("Here is the Q3 review.", reply.Reply);

        var calls = transport.Calls.Where(call => PyNode.AsString(call.Arguments["entityUrls"]?[0])?.StartsWith("/me/manager", StringComparison.Ordinal) != true).ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.Equal("margo-token", call.Bearer));
        var generate = PyJson.Loads(PyNode.AsString(calls[0].Arguments["jsonBody"])!)!;
        Assert.Equal(("Word", "Write up the Q3 review."), ((string?)generate["appName"], (string?)generate["userRequest"]));
        Assert.Equal("/shares/" + TurnDocuments.ShareId(Link) + "/driveItem?$select=id,parentReference", PyNode.AsString(calls[1].Arguments["entityUrls"]![0]));
        var invite = PyJson.Loads(PyNode.AsString(calls[2].Arguments["jsonBody"])!)!;
        Assert.Equal("""[{"objectId": "principal-1"}]""", PyJson.Dumps(invite["recipients"]));
        Assert.Equal(("write", true), ((string?)invite["roles"]![0], (bool)invite["requireSignIn"]!));

        // The model got the link and who it was shared with.
        var output = model.Requests[1].ToJsonString();
        Assert.Contains("Q3%20review.docx", output, StringComparison.Ordinal);
        Assert.Contains("your manager only", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unclear_creation_is_unknown_and_nothing_else_runs()
    {
        using var state = new TempState();
        var (orchestrator, transport, model) = Create(state);
        transport.Answer = (tool, arguments) => PyNode.AsString(arguments["actionUrl"]) == "/office-agent-mcp/generate_artifact"
            ? Task.FromException<JsonObject>(new TransportTimeoutException("no answer (fake)"))
            : null;
        await orchestrator.HandleAsync(Ask(), kind => Task.FromResult<string?>(kind + "-token"), Cancel);
        Assert.Single(transport.Calls, call => call.Tool == "do_action");
        Assert.DoesNotContain(transport.Calls, call => PyNode.AsString(call.Arguments["entityUrls"]?[0])?.StartsWith("/shares/", StringComparison.Ordinal) == true);
        Assert.Contains("do not try again", model.Requests[1].ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_sharing_id_is_unpadded_base64url()
    {
        Assert.Equal("u!aHR0cHM6Ly9vbmVkcml2ZS5saXZlLmNvbS9yZWRpcj9yZXNpZD0xMjMhNDU", TurnDocuments.ShareId("https://onedrive.live.com/redir?resid=123!45"));
    }
}
