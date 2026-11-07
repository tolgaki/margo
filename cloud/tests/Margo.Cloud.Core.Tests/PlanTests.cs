using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using static Margo.Cloud.Core.Tests.Fixtures;

namespace Margo.Cloud.Core.Tests;

/// <summary>tests/test_autopilot_instances.py Plans, including the instance.json/instance.bicep consistency check.</summary>
public sealed class PlanTests
{
    private static RouteEntry Route(string state)
    {
        var table = new RouteTable();
        return table.Put(Key, Identity, User, state);
    }

    private static ProvisioningMessage Message(string action, bool redeploy = false) => new(action, Identity, User, Redeploy: redeploy);

    [Fact]
    public void Plans_are_idempotent_and_never_delete_storage()
    {
        var ensure = Message("ensure");
        Assert.Equal(["route_provisioning", "deploy", "route_ready"], Instances.Plan(ensure, null));
        Assert.Empty(Instances.Plan(ensure, Route("ready")));
        Assert.Equal("deploy", Instances.Plan(Message("ensure", redeploy: true), Route("ready"))[1]);
        Assert.Equal("deploy", Instances.Plan(ensure, Route("failed"))[1]);
        Assert.Equal(["route_suspended"], Instances.Plan(Message("suspend"), Route("ready")));
        Assert.Empty(Instances.Plan(Message("suspend"), null));
        var retire = Instances.Plan(Message("retire"), Route("ready"));
        Assert.Equal(["route_retired", "delete_app", "delete_identity"], retire);
        Assert.DoesNotContain(retire, step => step.Contains("storage", StringComparison.Ordinal) || step.Contains("container", StringComparison.Ordinal));
        Assert.Empty(Instances.Plan(Message("retire"), Route("retired")));
    }

    [Theory]
    [InlineData("ensure", false, null, new[] { "route_provisioning", "deploy", "route_ready" })]
    [InlineData("ensure", false, "provisioning", new[] { "route_provisioning", "deploy", "route_ready" })]
    [InlineData("ensure", false, "suspended", new[] { "route_provisioning", "deploy", "route_ready" })]
    [InlineData("ensure", false, "retired", new[] { "route_provisioning", "deploy", "route_ready" })]
    [InlineData("ensure", false, "ready", new string[0])]
    [InlineData("suspend", false, "suspended", new string[0])]
    [InlineData("suspend", false, "retired", new string[0])]
    [InlineData("suspend", false, "failed", new[] { "route_suspended" })]
    [InlineData("suspend", false, "provisioning", new[] { "route_suspended" })]
    [InlineData("retire", false, null, new string[0])]
    [InlineData("retire", false, "suspended", new[] { "route_retired", "delete_app", "delete_identity" })]
    [InlineData("retire", false, "failed", new[] { "route_retired", "delete_app", "delete_identity" })]
    [InlineData("explode", false, "ready", new[] { "route_retired", "delete_app", "delete_identity" })] // as in Python
    public void Every_action_and_state(string action, bool redeploy, string? state, string[] expected)
    {
        Assert.Equal(expected, Instances.Plan(action, redeploy, state));
    }

    [Fact]
    public void Deployment_parameters_need_full_config_and_one_replica()
    {
        var key = Instances.InstanceKey(Identity, User);
        var message = Message("ensure");
        var parameters = Instances.DeploymentParameters(key, message, Config());
        Assert.Equal("margo-" + key, (string?)parameters["appName"]!["value"]);
        Assert.Equal(User, (string?)parameters["agentUserId"]!["value"]);
        Assert.False(parameters.ContainsKey("environmentDomain"));
        Assert.Throws<RouteException>(() => Instances.DeploymentParameters(key, message, new JsonObject()));
        var three = Config();
        three["minReplicas"] = 3;
        Assert.Throws<RouteException>(() => Instances.DeploymentParameters(key, message, three));
    }

    [Fact]
    public void Parameters_match_the_compiled_template_and_its_source()
    {
        var root = RepoRoot.Find();
        var template = (JsonObject)PythonJson.Loads(File.ReadAllText(Path.Combine(root, "autopilot", "infra", "instance.json")))!;
        var source = File.ReadAllText(Path.Combine(root, "autopilot", "infra", "instance.bicep"));
        var declared = Regex.Matches(source, @"^param (\w+)", RegexOptions.Multiline).Select(match => match.Groups[1].Value).ToHashSet();
        var templateParameters = (JsonObject)template["parameters"]!;
        Assert.True(declared.SetEquals(templateParameters.Select(pair => pair.Key)), "rebuild instance.json from instance.bicep");
        var key = Instances.InstanceKey(Identity, User);
        var sent = Instances.DeploymentParameters(key, Identity, User, Config()).Select(pair => pair.Key).ToHashSet();
        var required = templateParameters.Where(pair => !((JsonObject)pair.Value!).ContainsKey("defaultValue")).Select(pair => pair.Key);
        Assert.Empty(required.Except(sent).Order(StringComparer.Ordinal));
        Assert.Empty(sent.Except(templateParameters.Select(pair => pair.Key)).Order(StringComparer.Ordinal));
        var text = PythonJson.Dumps(template); // json.dumps(template)
        Assert.Contains("\"maxReplicas\": 1", text, StringComparison.Ordinal);
        Assert.Contains("\"external\": false", text, StringComparison.Ordinal);
        Assert.DoesNotContain("storageAccounts/delete", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Parameters_are_in_python_order_with_template_defaults()
    {
        var parameters = Instances.DeploymentParameters(Key, Identity, User, Config());
        Assert.Equal(
            [
                "instanceKey", "agentIdentityId", "agentUserId", "appName", "identityName", "stateContainerName", "minReplicas",
                "voiceLiveModel", "voiceName", "voiceEndpointId", "appInsightsConnectionString", "extraEnv", "environmentId", "image",
                "storageAccountName", "registryServer", "registryId", "aiAccountName", "modelDeployment", "storageRoleDefinitionId",
                "pullRoleDefinitionId", "modelRoleDefinitionId", "speechRoleDefinitionId", "publicBaseUrl", "tenantId", "blueprintClientId",
                "blueprintCredentialIdentityId", "blueprintCredentialClientId",
            ],
            parameters.Select(pair => pair.Key));
        Assert.Equal("""{"value": ""}""", PythonJson.Dumps(parameters["voiceName"]));
        Assert.Equal("""{"value": []}""", PythonJson.Dumps(parameters["extraEnv"]));
        Assert.Equal("""{"value": 0}""", PythonJson.Dumps(parameters["minReplicas"]));
        Assert.Equal("""{"value": "fictional-image"}""", PythonJson.Dumps(parameters["image"]));
        Assert.Equal("id-margo-" + Key, (string?)parameters["identityName"]!["value"]);
        Assert.Equal("margo-i-" + Key, (string?)parameters["stateContainerName"]!["value"]);
    }

    [Fact]
    public void Missing_configuration_is_named_in_order()
    {
        var config = new JsonObject { ["environmentId"] = "x", ["image"] = "" };
        Assert.Equal(
            "provisioner configuration is missing: environmentDomain, image, storageAccountName, registryServer, registryId, aiAccountName, "
            + "modelDeployment, storageRoleDefinitionId, pullRoleDefinitionId, modelRoleDefinitionId, speechRoleDefinitionId, publicBaseUrl, "
            + "tenantId, blueprintClientId, blueprintCredentialIdentityId, blueprintCredentialClientId",
            Assert.Throws<RouteException>(() => Instances.DeploymentParameters(Key, Identity, User, config)).Message);
        Assert.Equal("not an instance key", Assert.Throws<RouteException>(() => Instances.DeploymentParameters("zz", Identity, User, Config())).Message);
    }

    public static TheoryData<string, int> ReplicaValues() => new()
    {
        // Python int(): "1" -> 1, True -> 1, 1.0 -> 1, " 0 " -> 0, 0.9 -> 0.
        { "\"1\"", 1 },
        { "true", 1 },
        { "1.0", 1 },
        { "\" 0 \"", 0 },
        { "0.9", 0 },
    };

    [Theory]
    [MemberData(nameof(ReplicaValues))]
    public void Min_replicas_converts_like_python_int(string json, int expected)
    {
        var config = Config();
        config["minReplicas"] = PythonJson.Loads(json);
        Assert.Equal(expected, (int)Instances.DeploymentParameters(Key, Identity, User, config)["minReplicas"]!["value"]!);
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("1e400")]
    public void Min_replicas_outside_zero_or_one_is_refused(string json)
    {
        var config = Config();
        config["minReplicas"] = PythonJson.Loads(json);
        Assert.Equal("minReplicas must be 0 or 1: one container serves one instance",
            Assert.Throws<RouteException>(() => Instances.DeploymentParameters(Key, Identity, User, config)).Message);
    }

    [Theory]
    [InlineData("\"ab\"", """["a", "b"]""")]
    [InlineData("{\"k\": 1}", """["k"]""")]
    [InlineData("[{\"name\": \"A\", \"value\": \"b\"}]", """[{"name": "A", "value": "b"}]""")]
    [InlineData("\"\"", "[]")]
    public void Extra_env_is_listed_like_python_list(string json, string expected)
    {
        var config = Config();
        config["extraEnv"] = PythonJson.Loads(json);
        Assert.Equal(expected, PythonJson.Dumps(Instances.DeploymentParameters(Key, Identity, User, config)["extraEnv"]!["value"]));
    }

    [Fact]
    public void Extra_env_must_be_iterable_and_present_nulls_are_kept()
    {
        var config = Config();
        config["extraEnv"] = 5;
        Assert.Throws<RouteException>(() => Instances.DeploymentParameters(Key, Identity, User, config));
        config = Config();
        config["voiceName"] = null; // Python config.get("voiceName", "") keeps a present None
        Assert.Equal("""{"value": null}""", PythonJson.Dumps(Instances.DeploymentParameters(Key, Identity, User, config)["voiceName"]));
    }

    [Fact]
    public void Parameters_do_not_share_nodes_with_the_configuration()
    {
        var config = Config();
        config["extraEnv"] = new JsonArray(new JsonObject { ["name"] = "MARGO_FLAG", ["value"] = "on" });
        var parameters = Instances.DeploymentParameters(Key, Identity, User, config);
        ((JsonArray)config["extraEnv"]!).Clear();
        Assert.Single((JsonArray)parameters["extraEnv"]!["value"]!);
    }
}
