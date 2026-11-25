using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.State.Core;
using Margo.Cloud.WorkIQ;
using Xunit;

namespace Margo.Cloud.Guard.Tests;

/// <summary>Work IQ's own tool list reaches the engine through the guard, and server error text reaches the model only.</summary>
public sealed class ServerToolsTests
{

    private sealed class ListingTransport(IReadOnlyList<WorkIQTool>? tools = null, Exception? error = null) : IWorkIQTransport
    {
        public string Endpoint => WorkIQEndpoints.McpUrl;

        public List<string> Bearers { get; } = [];

        public Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default) =>
            error is null ? Task.FromResult(new JsonObject { ["statusCode"] = 200 }) : Task.FromException<JsonObject>(error);

        public Task<IReadOnlyList<WorkIQTool>> ListToolsAsync(string bearer, CancellationToken cancellationToken = default)
        {
            Bearers.Add(bearer);
            return Task.FromResult(tools ?? []);
        }
    }

    [Fact]
    public async Task The_tool_list_is_asked_as_margo_and_costs_no_budget()
    {
        var tools = new[] { new WorkIQTool("fetch", "Fetch.", new JsonObject { ["type"] = "object" }, true) };
        var transport = new ListingTransport(tools);
        var budget = new CallBudget(1);
        var guard = new WorkIQGuard(Fixtures.Binding, new FakeTokens(token: subject => subject + "-token"), transport, budget: budget);
        Assert.Same(tools, await guard.ServerToolsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["margo-token"], transport.Bearers);
        Assert.True(budget.Charge()); // still unspent
    }

    [Fact]
    public async Task Without_margos_token_the_list_is_empty()
    {
        var guard = new WorkIQGuard(Fixtures.Binding, new FakeTokens([Subjects.Margo]), new ListingTransport([new WorkIQTool("fetch", "", [], null)]));
        Assert.Empty(await guard.ServerToolsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_servers_error_text_goes_to_the_model_not_the_receipt()
    {
        var receipts = new List<GuardReceipt>();
        var transport = new ListingTransport(error: new TransportException("Work IQ refused the request (JSON-RPC -32602)", (int?)null)
        {
            ServerMessage = "entityUrls must be an array, got /users/dana@example.com",
        });
        var guard = new WorkIQGuard(Fixtures.Binding, new FakeTokens(token: subject => subject + "-token"), transport, recorder: receipts.Add);
        var read = await guard.CallAsync("fetch", new JsonObject { ["on_data_of"] = "self", ["entityUrls"] = new JsonArray("/me/messages") },
                                         RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal((GuardStatuses.Error, "Work IQ error"), (read.Status, read.Reason));
        Assert.Equal("entityUrls must be an array, got /users/dana@example.com", PyNode.AsString(read.Data!["error"]));
        Assert.DoesNotContain("dana", PyJson.Dumps(Assert.Single(receipts).ToJsonObject()), StringComparison.Ordinal);
    }
}
