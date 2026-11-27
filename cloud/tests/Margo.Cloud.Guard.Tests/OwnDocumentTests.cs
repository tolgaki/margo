using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Xunit;
using static Margo.Cloud.Guard.Tests.Fixtures;

namespace Margo.Cloud.Guard.Tests;

/// <summary>
/// The manager's standing permission for documents they ask for: with the grant on, an attended turn may create an Office
/// document in Margo's own space and share an item with the manager alone, without a claim. Nothing else changes.
/// </summary>
public sealed class OwnDocumentTests
{
    private readonly FakeTokens _tokens = new(token: subject => subject + "-token");
    private readonly FakeTransport _transport = new("""{"statusCode": 200}""");

    private WorkIQGuard Guard(bool grant = true, string access = PrincipalAccessModes.Delegation) =>
        new(Binding, _tokens, _transport, principalAccess: access) { AllowsOwnDocuments = grant };

    private static JsonObject Call(string url, JsonObject body, string subject = "self") => new()
    {
        ["on_data_of"] = subject,
        ["actionUrl"] = url,
        ["jsonBody"] = body.ToJsonString(),
    };

    private static JsonObject Create(string app = "Word") => Call("/office-agent-mcp/generate_artifact", new JsonObject
    {
        ["appName"] = app,
        ["taskDescription"] = "# Q3 review\n\nFictional content.",
        ["userRequest"] = "Write up the Q3 review.",
    });

    private static JsonObject Share(string recipient = "principal-1", string role = "write", bool signIn = true,
                                    string url = "/drives/drive-1/items/item-1/invite", Action<JsonObject>? change = null)
    {
        var body = new JsonObject
        {
            ["recipients"] = new JsonArray(new JsonObject { ["objectId"] = recipient }),
            ["roles"] = new JsonArray(role),
            ["requireSignIn"] = signIn,
            ["sendInvitation"] = true,
            ["message"] = "The document you asked for.",
        };
        change?.Invoke(body);
        return Call(url, body);
    }

    private static async Task<string> Status(WorkIQGuard guard, JsonObject arguments, string mode = RunModes.Attended) =>
        (await guard.CallAsync("do_action", arguments, mode, TestContext.Current.CancellationToken)).Status;

    [Fact]
    public async Task Creating_a_document_and_sharing_it_with_the_manager_need_no_card()
    {
        var guard = Guard();
        Assert.Equal(GuardStatuses.Ok, await Status(guard, Create()));
        Assert.Equal(GuardStatuses.Ok, await Status(guard, Create("PowerPoint")));
        Assert.Equal(GuardStatuses.Ok, await Status(guard, Share()));
        Assert.Equal(GuardStatuses.Ok, await Status(guard, Share(role: "read", url: "/me/drive/items/item-1/invite")));
        Assert.Equal(4, _transport.Calls.Count);
        Assert.All(_transport.Calls, call => Assert.Equal("margo-token", call.Bearer)); // as Margo, never as the manager
    }

    [Fact]
    public async Task Anything_else_still_needs_a_claim()
    {
        var guard = Guard();
        JsonObject[] refused =
        [
            Share(recipient: "colleague-1"),
            Share(change: body => ((JsonArray)body["recipients"]!).Add(new JsonObject { ["objectId"] = "colleague-1" })),
            Share(change: body => body["recipients"] = new JsonArray(new JsonObject { ["email"] = "dana@example.com" })),
            Share(change: body => ((JsonObject)((JsonArray)body["recipients"]!)[0]!)["email"] = "rafa@example.com"),
            Share(role: "owner"),
            Share(change: body => body["roles"] = new JsonArray("read", "write")),
            Share(signIn: false),
            Share(change: body => body["expirationDateTime"] = "2027-01-01T00:00:00Z"),
            Share(url: "/drives/drive-1/items/item-1/createLink"),
            Share(url: "/drives/drive-1/items/item-1/invite?x=1"),
            Share(change: body => body["message"] = new string('x', 1001)),
            Create("Visio"),
            Call("/office-agent-mcp/generate_artifact", new JsonObject { ["appName"] = "Word" }),
            Call("/office-agent-mcp/edit_artifact", new JsonObject { ["appName"] = "Word", ["taskDescription"] = "x", ["artifactUrl"] = "https://contoso.example.com/d.docx" }),
            Call("/office-agent-mcp/generate_artifact", new JsonObject { ["appName"] = "Word", ["taskDescription"] = "x", ["extra"] = 1 }),
            Call("/me/sendMail", new JsonObject { ["message"] = new JsonObject() }),
        ];
        foreach (var arguments in refused)
        {
            Assert.Equal(GuardStatuses.Refused, await Status(guard, arguments));
        }

        // As the manager, unattended, or without the grant: refused too.
        var asPrincipal = Create();
        asPrincipal["on_data_of"] = "principal";
        Assert.NotEqual(GuardStatuses.Ok, await Status(guard, asPrincipal));
        Assert.Equal(GuardStatuses.Refused, await Status(guard, Create(), RunModes.Unattended));
        Assert.Equal(GuardStatuses.Refused, await Status(Guard(grant: false), Create()));
        Assert.Equal(GuardStatuses.Refused, await Status(Guard(grant: false), Share()));
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Margo_reads_a_shared_item_she_can_reach_by_its_sharing_link()
    {
        var result = await Guard(grant: false).CallAsync("fetch", Args("""{"on_data_of": "self", "entityUrls": ["/shares/u!aHR0cHM6Ly9jb250b3NvLmV4YW1wbGUuY29tL2QuZG9jeA/driveItem"]}"""),
                                                         RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal(GuardStatuses.Ok, result.Status);
    }
}
