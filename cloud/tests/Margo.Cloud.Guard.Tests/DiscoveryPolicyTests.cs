using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.Guard.Tests;

/// <summary>
/// What search_paths finds can be used: Work IQ's own path families are readable, a Copilot agent can be asked, and
/// Graph paging and delta links are followed as relative paths. Writes still need the manager's approval.
/// </summary>
public sealed class DiscoveryPolicyTests
{
    private readonly FakeTransport _transport = new("""{"statusCode": 200}""");

    private WorkIQGuard Guard() => new(Fixtures.Binding, new FakeTokens(), _transport);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("self", "/engage/communities?$top=5")]
    [InlineData("self", "/pages/recent")]
    [InlineData("self", "/sharepoint/sites/contoso.example.com/lists")]
    [InlineData("principal", "/copilot/communications/realtimeActivityFeed/meetings/meeting-1/getArtifacts")]
    [InlineData("principal", "/office-agent-mcp/status")]
    public async Task Work_IQ_families_are_readable(string subject, string path)
    {
        var result = await Guard().CallAsync("fetch", new JsonObject { ["on_data_of"] = subject, ["entityUrls"] = new JsonArray(path) }, RunModes.Attended, Cancel);
        Assert.Equal(GuardStatuses.Ok, result.Status);
    }

    [Theory]
    [InlineData("create_entity", "parentUrl", "/pages")]
    [InlineData("do_action", "actionUrl", "/pages/create_page")]
    [InlineData("create_entity", "parentUrl", "/engage/communities/c1/threads")]
    [InlineData("create_entity", "parentUrl", "/copilot/communications/realtimeActivityFeed/multiActivitySubscriptions")]
    public async Task Writes_to_them_still_need_an_approval(string tool, string pathKey, string path)
    {
        var result = await Guard().CallAsync(tool, new JsonObject { ["on_data_of"] = "self", [pathKey] = path, ["jsonBody"] = "{}" }, RunModes.Attended, Cancel);
        Assert.Equal(GuardStatuses.Refused, result.Status);
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task A_copilot_agent_can_be_asked_by_its_id()
    {
        var result = await Guard().CallAsync("ask", new JsonObject
        {
            ["on_data_of"] = "self", ["question"] = "What is our travel policy for conferences?", ["agentId"] = "contoso.hr-policy_agent:1",
        }, RunModes.Attended, Cancel);
        Assert.Equal(GuardStatuses.Ok, result.Status);
        Assert.Equal("contoso.hr-policy_agent:1", PyNode.AsString(Assert.Single(_transport.Calls).Arguments["agentId"]));
    }

    [Fact]
    public async Task A_graph_paging_link_is_followed_as_a_relative_path()
    {
        var result = await Guard().CallAsync("fetch", new JsonObject
        {
            ["on_data_of"] = "self",
            ["entityUrls"] = new JsonArray("https://graph.microsoft.com/v1.0/me/messages?$top=25&$skip=25"),
        }, RunModes.Attended, Cancel);
        Assert.Equal(GuardStatuses.Ok, result.Status);
        Assert.Equal("/me/messages?$top=25&$skip=25", PyNode.AsString(Assert.Single(_transport.Calls).Arguments["entityUrls"]![0]));
    }

    [Fact]
    public async Task A_delta_link_is_replayed_through_call_function()
    {
        var result = await Guard().CallAsync("call_function", new JsonObject
        {
            ["on_data_of"] = "self",
            ["functionUrl"] = "https://graph.microsoft.com/v1.0/me/calendarView/delta?$deltatoken=abc123",
        }, RunModes.Attended, Cancel);
        Assert.Equal(GuardStatuses.Ok, result.Status);
        Assert.Equal("/me/calendarView/delta?$deltatoken=abc123", PyNode.AsString(Assert.Single(_transport.Calls).Arguments["functionUrl"]));
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/users/someone@example.com/messages")] // still a colleague's mailbox
    [InlineData("https://evil.example.com/v1.0/me/messages")]                            // not Graph
    [InlineData("https://graph.microsoft.com/v1.0/servicePrincipals")]                  // a blocked resource
    public async Task A_link_still_meets_every_path_rule(string link)
    {
        var result = await Guard().CallAsync("fetch", new JsonObject { ["on_data_of"] = "self", ["entityUrls"] = new JsonArray(link) }, RunModes.Attended, Cancel);
        Assert.Equal(GuardStatuses.Refused, result.Status);
        Assert.Empty(_transport.Calls);
    }
}
