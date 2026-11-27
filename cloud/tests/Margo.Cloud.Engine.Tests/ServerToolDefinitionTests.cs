using System.Text.Json.Nodes;
using Margo.Cloud.Guard;
using Margo.Cloud.WorkIQ;
using Xunit;

namespace Margo.Cloud.Engine.Tests;

/// <summary>The model's Work IQ tools come from the server's own descriptions, limited to what the guard allows.</summary>
public sealed class ServerToolDefinitionTests
{
    private static WorkIQTool Tool(string name, string description, string schema) =>
        new(name, description, JsonNode.Parse(schema)!.AsObject(), true);

    private static JsonObject Named(JsonArray tools, string name) =>
        tools.OfType<JsonObject>().Single(tool => (string?)tool["name"] == name);

    [Fact]
    public void A_server_tool_keeps_its_description_and_schemas_for_the_arguments_the_guard_accepts()
    {
        var fetch = Tool("fetch", "Fetch Microsoft 365 resources by relative path. Use $select and $top.", """
            {"type": "object",
             "properties": {"entityUrls": {"type": "array", "items": {"type": "string"}, "description": "Relative Graph paths."},
                            "agentId": {"type": "string"}, "debug": {"type": "boolean"}},
             "required": ["entityUrls", "agentId"]}
            """);
        var tools = EngineTools.FunctionTools(RunModes.Attended, [fetch]);
        var defined = Named(tools, "workiq-fetch");
        Assert.Equal("Fetch Microsoft 365 resources by relative path. Use $select and $top.", (string?)defined["description"]);
        var properties = defined["parameters"]!["properties"]!.AsObject();
        Assert.Equal(["on_data_of", "entityUrls"], properties.Select(pair => pair.Key));
        Assert.Equal("Relative Graph paths.", (string?)properties["entityUrls"]!["description"]);
        Assert.Equal(EngineTools.OnDataOfDescription, (string?)properties["on_data_of"]!["description"]);
        Assert.Equal("""["on_data_of","entityUrls"]""", defined["parameters"]!["required"]!.ToJsonString());
    }

    [Fact]
    public void Only_tools_the_server_offers_and_the_guard_allows_are_given()
    {
        var tools = EngineTools.FunctionTools(RunModes.Attended, [
            Tool("ask", "Ask Copilot.", """{"type": "object", "properties": {"question": {"type": "string"}}}"""),
            Tool("do_action", "Run an action.", """{"type": "object", "properties": {"actionUrl": {"type": "string"}}}"""),
            Tool("delete_everything", "Not a Work IQ tool Margo knows.", """{"type": "object"}"""),
        ]);
        var workIQ = tools.OfType<JsonObject>().Select(tool => (string?)tool["name"]).Where(name => name!.StartsWith("workiq-", StringComparison.Ordinal));
        Assert.Equal(["workiq-ask"], workIQ); // writes never reach the model; unknown tools are not passed on
    }

    [Fact]
    public void An_empty_server_description_falls_back_to_margos()
    {
        var tools = EngineTools.FunctionTools(RunModes.Attended, [Tool("retrieve", " ", """{"type": "object"}""")]);
        Assert.Equal(EngineTools.Descriptions["retrieve"], (string?)Named(tools, "workiq-retrieve")["description"]);
    }

    [Fact]
    public void Without_a_server_list_the_built_in_definitions_are_used()
    {
        Assert.Equal(EngineTools.FunctionTools(RunModes.Attended).ToJsonString(), EngineTools.FunctionTools(RunModes.Attended, []).ToJsonString());
        Assert.Equal(8, EngineTools.FunctionTools(RunModes.Attended).OfType<JsonObject>().Count(tool => ((string?)tool["name"])!.StartsWith("workiq-", StringComparison.Ordinal)));
    }

    [Fact]
    public void Ask_offers_a_copilot_agent_id_and_no_other_tool_does()
    {
        var schema = """{"type": "object", "properties": {"question": {"type": "string"}, "entityUrls": {"type": "array"}, "agentId": {"type": "string"}}}""";
        var tools = EngineTools.FunctionTools(RunModes.Attended, [Tool("ask", "Ask Copilot.", schema), Tool("fetch", "Fetch.", schema)]);
        Assert.True(Named(tools, "workiq-ask")["parameters"]!["properties"]!.AsObject().ContainsKey("agentId"));
        Assert.False(Named(tools, "workiq-fetch")["parameters"]!["properties"]!.AsObject().ContainsKey("agentId"));
    }
}
