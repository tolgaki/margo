using System.Net;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.WorkIQ;
using Margo.Cloud.WorkIQ.Tests;
using Xunit;

namespace Margo.Cloud.Guard.Tests;

/// <summary>The guard over the real MCP SDK client and a scripted Work IQ: what the model is told, end to end.</summary>
public sealed class GuardOverMcpSdkTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static (WorkIQGuard Guard, ScriptedMcpServer Server, WorkIQMcpTransport Transport) Create()
    {
        var server = new ScriptedMcpServer();
        var transport = new WorkIQMcpTransport(options: McpTransportOptions.Default with { HandlerFactory = server.Handler });
        return (new WorkIQGuard(Fixtures.Binding, new FakeTokens(), transport), server, transport);
    }

    private static JsonObject Fetch(params string[] paths) => new() { ["on_data_of"] = "self", ["entityUrls"] = new JsonArray([.. paths.Select(path => (JsonNode)path)]) };

    [Fact]
    public async Task A_read_is_ok_and_its_data_reaches_the_model()
    {
        var (guard, server, transport) = Create();
        await using var _ = transport;
        server.OnCall = (_, _, _) => Task.FromResult(ScriptedMcpServer.Result(JsonNode.Parse("""
            {"results": [{"statusCode": 200, "data": {"value": [{"subject": "Q3 plan"}]}}]}
            """)!.AsObject()));
        var result = await guard.CallAsync("fetch", Fetch("/me/messages?$top=1"), RunModes.Attended, Cancel);
        Assert.Equal(GuardStatuses.Ok, result.Status);
        Assert.Equal("Q3 plan", PyNode.AsString(result.Data!["results"]![0]!["data"]!["value"]![0]!["subject"]));
        Assert.Equal(Fixtures.MargoToken, server.Requests.First().Bearer);
    }

    [Fact]
    public async Task Mixed_results_are_partial()
    {
        var (guard, server, transport) = Create();
        await using var _ = transport;
        server.OnCall = (_, _, _) => Task.FromResult(ScriptedMcpServer.Result(JsonNode.Parse("""
            {"results": [{"statusCode": 200, "data": {}}, {"statusCode": 403, "error": {"code": "accessDenied"}}]}
            """)!.AsObject()));
        var result = await guard.CallAsync("fetch", Fetch("/me/messages", "/me/events"), RunModes.Attended, Cancel);
        Assert.Equal(GuardStatuses.Partial, result.Status);
    }

    [Fact]
    public async Task A_throttled_read_is_an_error_with_its_status()
    {
        var (guard, server, transport) = Create();
        await using var _ = transport;
        server.OnCall = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var result = await guard.CallAsync("fetch", Fetch("/me/messages"), RunModes.Attended, Cancel);
        Assert.Equal(GuardStatuses.Error, result.Status);
        Assert.Equal(429, (int)PyJson.AsInt(Assert.Single(guard.Receipts[^1].StatusCodes)));
    }

    [Fact]
    public async Task The_servers_tool_list_reaches_the_guard()
    {
        var (guard, server, transport) = Create();
        await using var _ = transport;
        server.Tools.Add(new JsonObject { ["name"] = "fetch", ["description"] = "Fetch.", ["inputSchema"] = new JsonObject { ["type"] = "object" } });
        var tools = await guard.ServerToolsAsync(Cancel);
        Assert.Equal("fetch", Assert.Single(tools).Name);
    }
}
