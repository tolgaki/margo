using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Margo.Cloud.Core;

/// <summary>The steps of a provisioning plan, in the order <see cref="Instances.Plan(string, bool, string?)"/> returns them.</summary>
public static class PlanSteps
{
    public const string RouteProvisioning = "route_provisioning";
    public const string Deploy = "deploy";
    public const string RouteReady = "route_ready";
    public const string RouteSuspended = "route_suspended";
    public const string RouteRetired = "route_retired";
    public const string DeleteApp = "delete_app";
    public const string DeleteIdentity = "delete_identity";
}

/// <summary>Azure names for one instance. Python: the dict from <c>instances.resource_names</c>.</summary>
/// <param name="App">Container App, at most 32 characters.</param>
/// <param name="Identity">User-assigned managed identity.</param>
/// <param name="Container">Blob container for the instance's state, at most 63 characters.</param>
public sealed record InstanceResourceNames(string App, string Identity, string Container);

/// <summary>The hired instance an activity is addressed to: <c>recipient.agenticAppId</c> and
/// <c>recipient.agenticUserId</c> (or <c>aadObjectId</c>).</summary>
public sealed record AgentIdentity(string AgentIdentityId, string AgentUserId);

/// <summary>
/// One container per hired Margo: names, routes, lifecycle and plans, without any Azure SDK.
/// Python: <c>margo_autopilot/instances.py</c>. The router, the provisioner and each instance
/// container use this so they agree on how an instance is named, where it lives and what a
/// lifecycle event means.
/// </summary>
/// <remarks>
/// An instance is one hired Margo: the agent application instance (<c>agenticAppId</c>) and her
/// agent user (<c>agenticUserId</c>). The manager is not part of the key. A new manager keeps the
/// same container, and the orchestrator already gives each manager a separate account and state.
/// </remarks>
public static class Instances
{
    /// <summary>Agent 365 lifecycle notifications (activity name <c>agentLifecycle</c>), by lower-cased
    /// <c>valueType</c>, and the provisioning action each one causes.</summary>
    public static IReadOnlyDictionary<string, string> Lifecycle { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["agenticuseridentitycreated"] = ProvisioningActions.Ensure,
        ["agenticuseridentityupdated"] = ProvisioningActions.Ensure,
        ["agenticuserworkloadonboardingupdated"] = ProvisioningActions.Ensure,
        ["agenticusermanagerupdated"] = ProvisioningActions.Ensure, // same container; the new manager gets a new account
        ["agenticuserundeleted"] = ProvisioningActions.Ensure,
        ["agenticuserenabled"] = ProvisioningActions.Ensure,
        ["agenticuserdisabled"] = ProvisioningActions.Suspend,
        ["agenticuserdeleted"] = ProvisioningActions.Retire,
    };

    /// <summary>Provisioner configuration that <see cref="DeploymentParameters(string, string, string, JsonObject)"/>
    /// requires to be present and truthy, in Python's order (the order of the "missing" message).</summary>
    public static IReadOnlyList<string> RequiredConfig { get; } =
    [
        "environmentId", "environmentDomain", "image", "storageAccountName", "registryServer",
        "registryId", "aiAccountName", "modelDeployment", "storageRoleDefinitionId",
        "pullRoleDefinitionId", "modelRoleDefinitionId", "speechRoleDefinitionId", "publicBaseUrl", "tenantId",
        "blueprintClientId", "blueprintCredentialIdentityId", "blueprintCredentialClientId",
    ];

    /// <summary>Whether <paramref name="key"/> is an instance key: exactly 20 lower-case hex digits.</summary>
    public static bool IsInstanceKey(string? key)
    {
        if (key is null || key.Length != 20)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A short, stable, opaque key for one hired instance (never an email or display name):
    /// the first 20 hex digits of SHA-256 over <c>"{identity}\n{user}"</c> in UTF-8.</summary>
    /// <exception cref="BindingException">An identifier is malformed, or both are the same.</exception>
    /// <exception cref="System.Text.EncoderFallbackException">An identifier holds a lone surrogate
    /// (Python raises UnicodeEncodeError, a ValueError but not a BindingError).</exception>
    public static string InstanceKey(string? agentIdentityId, string? agentUserId)
    {
        OpaqueIdentifier.Check("agent_identity_id", agentIdentityId);
        OpaqueIdentifier.Check("agent_user_id", agentUserId);
        if (string.Equals(agentIdentityId, agentUserId, StringComparison.Ordinal))
        {
            throw new BindingException("agent identity and agent user must differ");
        }

        var digest = SHA256.HashData(PythonText.StrictUtf8.GetBytes(agentIdentityId + "\n" + agentUserId));
        return Convert.ToHexStringLower(digest)[..20];
    }

    /// <summary>Azure names for one instance: Container App (&lt;=32), identity, Blob container (&lt;=63).</summary>
    /// <exception cref="RouteException">Not an instance key.</exception>
    public static InstanceResourceNames ResourceNames(string? key)
    {
        if (!IsInstanceKey(key))
        {
            throw new RouteException("not an instance key");
        }

        return new InstanceResourceNames("margo-" + key, "id-margo-" + key, "margo-i-" + key);
    }

    /// <summary>The instance's internal HTTPS address inside the Container Apps environment.</summary>
    /// <exception cref="RouteException">The domain is not <c>[a-z0-9.-]{3,253}</c>, or the key is not an instance key.</exception>
    public static string InternalUrl(string? key, string? environmentDomain)
    {
        if (environmentDomain is null || environmentDomain.Length is < 3 or > 253
            || !environmentDomain.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
        {
            throw new RouteException("environment domain is required");
        }

        return "https://" + ResourceNames(key).App + ".internal." + environmentDomain;
    }

    /// <summary>The addressed instance from a raw activity's <c>recipient</c>, or <see langword="null"/>.</summary>
    /// <remarks><c>agenticUserId</c> is used when truthy, otherwise <c>aadObjectId</c>; both values must
    /// be JSON strings (an empty string passes here and fails at <see cref="InstanceKey"/>).</remarks>
    public static AgentIdentity? ActivityIdentity(JsonNode? activity)
    {
        if (activity is not JsonObject obj || obj["recipient"] is not JsonObject recipient)
        {
            return null;
        }

        var identity = PythonValues.AsString(recipient["agenticAppId"]);
        var userNode = recipient["agenticUserId"];
        var user = PythonValues.AsString(PythonValues.IsTruthy(userNode) ? userNode : recipient["aadObjectId"]);
        return identity is null || user is null ? null : new AgentIdentity(identity, user);
    }

    /// <summary>A provisioning message for an Agent 365 lifecycle notification (activity
    /// <c>name</c> <c>agentLifecycle</c>, any case), or <see langword="null"/> for anything else.</summary>
    public static ProvisioningMessage? LifecycleMessage(JsonNode? activity)
    {
        if (activity is not JsonObject obj || !PythonText.LowerEquals(PythonValues.AsString(obj["name"]), "agentlifecycle"))
        {
            return null;
        }

        var valueType = PythonValues.AsString(obj["valueType"]);
        var lifecycleEvent = valueType is null ? null : PythonText.LowerAscii(valueType);
        var identity = ActivityIdentity(activity);
        if (lifecycleEvent is null || !Lifecycle.TryGetValue(lifecycleEvent, out var action) || identity is null)
        {
            return null;
        }

        return new ProvisioningMessage(action, identity.AgentIdentityId, identity.AgentUserId, lifecycleEvent);
    }

    /// <summary>Validate a provisioning message from the queue; return it with its instance key.</summary>
    /// <remarks>Only the fields a provisioner reads are kept: <c>event</c> when it is a string, and
    /// <c>redeploy</c> by Python truthiness.</remarks>
    /// <exception cref="RouteException">Not an object, an unknown action, or identifiers that cannot form a key.</exception>
    public static CheckedMessage CheckMessage(JsonNode? message)
    {
        if (message is not JsonObject obj || PythonValues.AsString(obj["action"]) is not { } action || !ProvisioningActions.IsKnown(action))
        {
            throw new RouteException("unknown provisioning action");
        }

        var identity = PythonValues.AsString(obj["agent_identity_id"]);
        var user = PythonValues.AsString(obj["agent_user_id"]);
        var key = KeyForMessage(identity, user);
        return new CheckedMessage(key, new ProvisioningMessage(action, identity!, user!, PythonValues.AsString(obj["event"]),
            PythonValues.IsTruthy(obj["redeploy"])));
    }

    /// <summary>Validate a typed provisioning message (an operator's enqueue) before it is sent.</summary>
    /// <exception cref="RouteException">An unknown action, or identifiers that cannot form a key.</exception>
    public static CheckedMessage CheckMessage(ProvisioningMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!ProvisioningActions.IsKnown(message.Action))
        {
            throw new RouteException("unknown provisioning action");
        }

        return new CheckedMessage(KeyForMessage(message.AgentIdentityId, message.AgentUserId), message);
    }

    /// <summary>Parse queue text as Python's <c>json.loads</c> and validate it, as the provisioner does
    /// before handling a message.</summary>
    /// <exception cref="RouteException">The text is not JSON (Python raises JSONDecodeError, also a
    /// ValueError), or <see cref="CheckMessage(JsonNode?)"/> refuses it.</exception>
    public static CheckedMessage ParseMessage(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        JsonNode? message;
        try
        {
            message = PythonJson.Loads(raw);
        }
        catch (JsonException exc)
        {
            throw new RouteException("provisioning message is not JSON", exc);
        }

        return CheckMessage(message);
    }

    /// <summary>The ordered, idempotent steps for one provisioning message, given the current route.</summary>
    /// <remarks>Storage is never deleted here: a retired instance's state is kept until an operator
    /// deletes it after the retention period.</remarks>
    public static IReadOnlyList<string> Plan(ProvisioningMessage message, RouteEntry? current)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Plan(message.Action, message.Redeploy, current?.State);
    }

    /// <summary>The plan for an action and the current route state (<see langword="null"/> when there
    /// is no route). Any action other than ensure and suspend is planned as a retirement, as in Python.</summary>
    public static IReadOnlyList<string> Plan(string action, bool redeploy, string? currentState)
    {
        if (action == ProvisioningActions.Ensure)
        {
            return currentState == RouteStates.Ready && !redeploy
                ? []
                : [PlanSteps.RouteProvisioning, PlanSteps.Deploy, PlanSteps.RouteReady];
        }

        if (action == ProvisioningActions.Suspend)
        {
            return currentState is null or RouteStates.Suspended or RouteStates.Retired ? [] : [PlanSteps.RouteSuspended];
        }

        return currentState is null or RouteStates.Retired
            ? []
            : [PlanSteps.RouteRetired, PlanSteps.DeleteApp, PlanSteps.DeleteIdentity];
    }

    /// <summary>ARM parameters for <c>autopilot/infra/instance.json</c>: one instance, from provisioner config.</summary>
    public static JsonObject DeploymentParameters(string key, ProvisioningMessage message, JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(message);
        return DeploymentParameters(key, message.AgentIdentityId, message.AgentUserId, config);
    }

    /// <summary>ARM parameters for <c>autopilot/infra/instance.json</c>, as <c>{name: {"value": value}}</c>
    /// in Python's order. Optional settings default as in the template; <c>environmentDomain</c> is
    /// configuration for the route URL, not a template parameter.</summary>
    /// <exception cref="RouteException">Required configuration is missing or falsy, the key is not an
    /// instance key, <c>minReplicas</c> is not 0 or 1, or <c>extraEnv</c> is not iterable.</exception>
    public static JsonObject DeploymentParameters(string key, string agentIdentityId, string agentUserId, JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var missing = RequiredConfig.Where(name => !PythonValues.IsTruthy(config[name])).ToList();
        if (missing.Count > 0)
        {
            throw new RouteException("provisioner configuration is missing: " + string.Join(", ", missing));
        }

        var names = ResourceNames(key);
        const string replicasMessage = "minReplicas must be 0 or 1: one container serves one instance";
        var minReplicas = BigInteger.Zero;
        if (config.TryGetPropertyValue("minReplicas", out var replicasNode) && !PythonValues.TryToInt(replicasNode, out minReplicas))
        {
            throw new RouteException(replicasMessage); // Python: TypeError or ValueError from int()
        }

        var extraEnv = ExtraEnv(config);
        if (minReplicas != BigInteger.Zero && minReplicas != BigInteger.One)
        {
            throw new RouteException(replicasMessage);
        }

        var values = new JsonObject
        {
            ["instanceKey"] = key,
            ["agentIdentityId"] = agentIdentityId,
            ["agentUserId"] = agentUserId,
            ["appName"] = names.App,
            ["identityName"] = names.Identity,
            ["stateContainerName"] = names.Container,
            ["minReplicas"] = (int)minReplicas,
            ["voiceLiveModel"] = Optional(config, "voiceLiveModel"),
            ["voiceName"] = Optional(config, "voiceName"),
            ["voiceEndpointId"] = Optional(config, "voiceEndpointId"),
            ["appInsightsConnectionString"] = Optional(config, "appInsightsConnectionString"),
            ["extraEnv"] = extraEnv,
        };
        foreach (var name in RequiredConfig)
        {
            if (name != "environmentDomain")
            {
                values[name] = config[name]!.DeepClone();
            }
        }

        var parameters = new JsonObject();
        foreach (var (name, value) in values)
        {
            parameters[name] = new JsonObject { ["value"] = value?.DeepClone() };
        }

        return parameters;
    }

    private static string KeyForMessage(string? agentIdentityId, string? agentUserId)
    {
        try
        {
            return InstanceKey(agentIdentityId, agentUserId);
        }
        catch (BindingException exc)
        {
            throw new RouteException(exc.Message, exc);
        }
    }

    /// <summary>Python's <c>config.get(name, "")</c>: a present value, even JSON null, is kept.</summary>
    private static JsonNode? Optional(JsonObject config, string name) =>
        config.TryGetPropertyValue(name, out var value) ? value?.DeepClone() : JsonValue.Create(string.Empty);

    /// <summary>Python's <c>list(config.get("extraEnv", []))</c>: a list is copied, an object gives its
    /// keys, a string its code points; anything else is not iterable.</summary>
    private static JsonArray ExtraEnv(JsonObject config)
    {
        if (!config.TryGetPropertyValue("extraEnv", out var node))
        {
            return [];
        }

        switch (node)
        {
            case JsonArray array:
                return (JsonArray)array.DeepClone();
            case JsonObject obj:
                return new JsonArray(obj.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray());
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var text = value.GetValue<string>();
                var items = new JsonArray();
                for (var i = 0; i < text.Length; i++)
                {
                    var width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                    items.Add(text.Substring(i, width));
                    i += width - 1;
                }

                return items;
            default:
                throw new RouteException("extraEnv must be a list"); // Python: TypeError, not iterable
        }
    }
}
