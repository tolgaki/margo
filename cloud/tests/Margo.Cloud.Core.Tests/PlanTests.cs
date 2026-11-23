using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using static Margo.Cloud.Core.Tests.Fixtures;

namespace Margo.Cloud.Core.Tests;

/// <summary>The former Python instance tests' Plans, including the instance.json/instance.bicep consistency check.</summary>
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

    [Theory]
    [InlineData("first-activity", "suspended", false)]
    [InlineData("first-activity", "retired", false)]
    [InlineData("retry-after-failure", "suspended", false)]
    [InlineData("retry-after-failure", "retired", false)]
    [InlineData("first-activity", null, true)]
    [InlineData("first-activity", "provisioning", true)]
    [InlineData("retry-after-failure", "failed", true)]
    [InlineData("agenticuserenabled", "suspended", true)] // a lifecycle notification starts her again
    [InlineData("agenticuserundeleted", "retired", true)]
    [InlineData("operator", "retired", true)]
    [InlineData(null, "suspended", true)]
    public void An_ensure_for_an_activity_never_starts_a_stopped_instance(string? ensureEvent, string? state, bool deploys)
    {
        // Not in Python: a router replica with an empty or stale route table queues these for a disabled agent.
        var message = new ProvisioningMessage("ensure", Identity, User, ensureEvent);
        var current = state is null ? null : Route(state);
        Assert.Equal(!deploys, Instances.IsStoppedForActivity(message, current));
        Assert.Equal(deploys ? ["route_provisioning", "deploy", "route_ready"] : [], Instances.Plan(message, current));
        Assert.Equal(ensureEvent is "first-activity" or "retry-after-failure", ProvisioningEvents.IsActivity(ensureEvent));
        // Only an ensure is ever ignored: a suspend or retire with the same event is planned as usual.
        Assert.False(Instances.IsStoppedForActivity(new ProvisioningMessage("retire", Identity, User, ensureEvent), current));
    }

    [Fact]
    public void A_retirement_whose_removal_is_pending_is_planned_again_and_clears_the_record_last()
    {
        // Not in Python, which recorded a failed retirement as "failed" (the router then provisioned her again).
        var table = new RouteTable();
        var pending = table.Put(Key, Identity, User, "retired", error: "TimeoutException");
        Assert.True(Instances.IsRemovalPending(pending));
        Assert.Equal(["delete_app", "delete_identity", "route_retired"], Instances.Plan(Message("retire"), pending));
        Assert.Equal(["delete_app", "delete_identity", "route_retired"], Instances.Plan("retire", false, "retired", removalPending: true));
        // A message received before may have stopped between recording "retired" and removing the app.
        Assert.Equal(["delete_app", "delete_identity", "route_retired"], Instances.Plan(Message("retire"), Route("retired"), retried: true));
        Assert.Empty(Instances.Plan(Message("retire"), Route("retired")));
        Assert.False(Instances.IsRemovalPending(Route("retired")));
        Assert.False(Instances.IsRemovalPending(null));
        // Only a retired route can have a pending removal; any other state is retired from the route write.
        Assert.False(Instances.IsRemovalPending(table.Put(Key, Identity, User, "failed", error: "TimeoutException")));
        Assert.Equal(["route_retired", "delete_app", "delete_identity"], Instances.Plan(Message("retire"), Route("ready"), retried: true));
        Assert.Equal(["route_retired", "delete_app", "delete_identity"], Instances.Plan("retire", false, "failed", removalPending: true));
        Assert.Empty(Instances.Plan(Message("retire"), null, retried: true));
        // The other actions are unchanged by it: a lifecycle ensure hires her again, a suspend has nothing to do.
        Assert.Equal("deploy", Instances.Plan(Message("ensure"), pending, retried: true)[1]);
        Assert.Empty(Instances.Plan(Message("suspend"), pending, retried: true));
        Assert.Empty(Instances.Plan(Message("ensure"), Route("ready"), retried: true));
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
        var template = (JsonObject)PythonJson.Loads(File.ReadAllText(Path.Combine(root, "cloud", "infra", "instance.json")))!;
        var source = File.ReadAllText(Path.Combine(root, "cloud", "infra", "instance.bicep"));
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

    /// <summary>
    /// The provisioner deploys <c>instance.json</c>, not its source, so a change to <c>instance.bicep</c> that was not
    /// compiled never reaches an instance. Without the Bicep CLI this cannot compare everything; it compares what
    /// decides access and behaviour: the resources and their API versions, each role's scope and definition, the
    /// environment, the probes, the container, ingress, scale and identities.
    /// </summary>
    [Fact]
    public void The_compiled_template_has_the_resources_roles_environment_and_probes_of_its_source()
    {
        const string rebuild = "rebuild instance.json from instance.bicep";
        var root = RepoRoot.Find();
        var template = (JsonObject)PythonJson.Loads(File.ReadAllText(Path.Combine(root, "cloud", "infra", "instance.json")))!;
        var source = Lf(File.ReadAllText(Path.Combine(root, "cloud", "infra", "instance.bicep")));
        var resources = ((JsonArray)template["resources"]!).Select(node => (JsonObject)node!).ToList();

        // Parameter types, and every deployed resource (not `existing`) with its API version, in source order.
        var types = new Dictionary<string, string> { ["string"] = "string", ["int"] = "int", ["array"] = "array", ["bool"] = "bool", ["object"] = "object" };
        foreach (Match parameter in Regex.Matches(source, @"^param (\w+) (\w+)", RegexOptions.Multiline))
        {
            Assert.True(types[parameter.Groups[2].Value] == (string?)template["parameters"]![parameter.Groups[1].Value]?["type"], rebuild + ": parameter " + parameter.Groups[1].Value);
        }

        var symbols = Regex.Matches(source, @"^ *resource (\w+) '([^'@]+)@?([^']*)'( existing)? = \{", RegexOptions.Multiline)
            .Select(match => (Symbol: match.Groups[1].Value, Type: match.Groups[2].Value, Version: match.Groups[3].Value, Existing: match.Groups[4].Success))
            .ToList();
        var deployed = symbols.Where(symbol => !symbol.Existing).ToList();
        Assert.Equal(7, deployed.Count);
        Assert.True(
            deployed.Select(symbol => symbol.Type + "@" + symbol.Version).SequenceEqual(resources.Select(resource => (string?)resource["type"] + "@" + (string?)resource["apiVersion"])),
            rebuild + ": resources");
        Assert.Equal(
            Regex.Matches(source, @"^output (\w+)", RegexOptions.Multiline).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal),
            ((JsonObject)template["outputs"]!).Select(pair => pair.Key).Order(StringComparer.Ordinal));

        // Role assignments: each one's scope (the resource type of the symbol the source names) and role definition.
        var roles = deployed.Where(symbol => symbol.Type == "Microsoft.Authorization/roleAssignments").ToList();
        var compiledRoles = resources.Where(resource => (string?)resource["type"] == "Microsoft.Authorization/roleAssignments").ToList();
        Assert.Equal(4, roles.Count);
        for (var index = 0; index < roles.Count; index++)
        {
            var block = Block(source, roles[index].Symbol);
            var scope = Regex.Match(block, @"^  scope: (\w+)$", RegexOptions.Multiline).Groups[1].Value;
            var scopeType = scope == "stateContainer" ? "Microsoft.Storage/storageAccounts/blobServices/containers" : symbols.Single(symbol => symbol.Symbol == scope).Type;
            var definition = Regex.Match(block, @"roleDefinitionId: subscriptionResourceId\('Microsoft\.Authorization/roleDefinitions', (\w+)\)").Groups[1].Value;
            Assert.NotEqual(string.Empty, definition);
            var compiled = compiledRoles[index];
            Assert.True(((string?)compiled["scope"])!.StartsWith("[resourceId('" + scopeType + "', ", StringComparison.Ordinal), rebuild + ": scope of " + roles[index].Symbol);
            var compiledDefinition = (string?)compiled["properties"]!["roleDefinitionId"];
            Assert.True(
                compiledDefinition == "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', variables('" + definition + "'))]"
                || compiledDefinition == "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', parameters('" + definition + "'))]",
                rebuild + ": role of " + roles[index].Symbol);
            Assert.Equal("ServicePrincipal", (string?)compiled["properties"]!["principalType"]);
            Assert.Contains("parameters('identityName')", (string?)compiled["properties"]!["principalId"], StringComparison.Ordinal);
        }

        // Only her own state container is a storage scope; never the storage account or another container.
        Assert.Equal(
            "[resourceId('Microsoft.Storage/storageAccounts/blobServices/containers', parameters('storageAccountName'), 'default', parameters('stateContainerName'))]",
            (string?)compiledRoles[0]["scope"]);
        Assert.DoesNotContain(compiledRoles, role => ((string?)role["scope"])!.StartsWith("[resourceId('Microsoft.Storage/storageAccounts', ", StringComparison.Ordinal));

        // The container: environment names in order, probes, command, size, and what surrounds it.
        var app = resources.Single(resource => (string?)resource["type"] == "Microsoft.App/containerApps");
        var properties = (JsonObject)app["properties"]!;
        var container = (JsonObject)Assert.Single((JsonArray)properties["template"]!["containers"]!)!;
        var environment = (string?)container["env"];
        Assert.True(
            Regex.Matches(source, @"\{ name: '([A-Z0-9_]+)', value: ").Select(match => match.Groups[1].Value)
                .SequenceEqual(Regex.Matches(environment!, @"createObject\('name', '([A-Z0-9_]+)', 'value', ").Select(match => match.Groups[1].Value)),
            rebuild + ": environment");
        Assert.EndsWith(", parameters('extraEnv'))]", environment, StringComparison.Ordinal);
        var probes = Regex.Matches(source, @"\{ type: '(\w+)', httpGet: \{ path: '([^']+)', port: (\d+) \}, periodSeconds: (\d+) \}")
            .Select(match => string.Join(" ", match.Groups.Values.Skip(1).Select(group => group.Value))).ToList();
        Assert.Equal(["Liveness /liveness 8088 30", "Readiness /readiness 8088 30"], probes);
        Assert.True(
            probes.SequenceEqual(((JsonArray)container["probes"]!).Select(probe =>
                (string?)probe!["type"] + " " + (string?)probe["httpGet"]!["path"] + " " + (int)probe["httpGet"]!["port"]! + " " + (int)probe["periodSeconds"]!)),
            rebuild + ": probes");
        var command = Regex.Match(source, @"command: \[ 'dotnet', '([^']+)' \]").Groups[1].Value;
        Assert.Equal(["dotnet", command], ((JsonArray)container["command"]!).Select(part => (string?)part));
        var size = Regex.Match(source, @"resources: \{ cpu: json\('([\d.]+)'\), memory: '(\w+)' \}");
        Assert.Equal(("[json('" + size.Groups[1].Value + "')]", size.Groups[2].Value), ((string?)container["resources"]!["cpu"], (string?)container["resources"]!["memory"]));
        var ingress = Regex.Match(source, @"ingress: \{ external: (\w+), targetPort: (\d+), transport: '(\w+)', allowInsecure: (\w+) \}");
        Assert.True(ingress.Success);
        Assert.Equal(
            "{\"external\": " + ingress.Groups[1].Value + ", \"targetPort\": " + ingress.Groups[2].Value + ", \"transport\": \"" + ingress.Groups[3].Value
            + "\", \"allowInsecure\": " + ingress.Groups[4].Value + "}",
            PythonJson.Dumps(properties["configuration"]!["ingress"]));
        Assert.Equal(
            "{\"minReplicas\": \"[parameters('minReplicas')]\", \"maxReplicas\": " + Regex.Match(source, @"scale: \{ minReplicas: minReplicas, maxReplicas: (\d+) \}").Groups[1].Value + "}",
            PythonJson.Dumps(properties["template"]!["scale"]));
        Assert.Equal(Regex.Match(source, @"terminationGracePeriodSeconds: (\d+)").Groups[1].Value, ((int)properties["template"]!["terminationGracePeriodSeconds"]!).ToString(System.Globalization.CultureInfo.InvariantCulture));
        // The image is pulled as the instance's own identity; the app also carries the role-less blueprint credential.
        Assert.Contains("registries: [ { server: registryServer, identity: identity.id } ]", source, StringComparison.Ordinal);
        Assert.Equal(
            "[{\"server\": \"[parameters('registryServer')]\", \"identity\": \"[resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', parameters('identityName'))]\"}]",
            PythonJson.Dumps(properties["configuration"]!["registries"]));
        Assert.Equal(
            [
                "[format('{0}', resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', parameters('identityName')))]",
                "[format('{0}', parameters('blueprintCredentialIdentityId'))]",
            ],
            ((JsonObject)app["identity"]!["userAssignedIdentities"]!).Select(pair => pair.Key));
        Assert.Equal(2, Regex.Count(Block(source, "app"), @"^      '\$\{[\w.]+\}': \{\}$", RegexOptions.Multiline));
    }

    /// <summary>The source text of one top-level resource, from its declaration to its closing brace.</summary>
    private static string Block(string source, string symbol)
    {
        var start = source.IndexOf("\nresource " + symbol + " '", StringComparison.Ordinal);
        Assert.True(start >= 0, "resource " + symbol);
        return source[start..(source.IndexOf("\n}\n", start, StringComparison.Ordinal) + 2)];
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
