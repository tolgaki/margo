using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Xunit;

namespace Margo.Cloud.Router.Tests;

/// <summary>Port of <c>tests/test_autopilot_instances.py</c> <c>RouterDecisions</c>, plus edge cases
/// checked against the Python <c>router/core.py</c>.</summary>
public sealed class RouterDecisionTests
{
    private const string Identity = "agent-identity-1";
    private const string User = "margo-user-1";
    private const string Domain = "calmforest-1a2b3c.example.com";

    private static JsonObject Activity(Action<JsonObject>? change = null)
    {
        var value = new JsonObject
        {
            ["type"] = "message",
            ["recipient"] = new JsonObject { ["agenticAppId"] = Identity, ["agenticUserId"] = User },
            ["from"] = new JsonObject { ["aadObjectId"] = "principal-1" },
            ["text"] = "Brief me",
        };
        change?.Invoke(value);
        return value;
    }

    private static RouteTable Table(string? state = null, string? url = null)
    {
        var table = new RouteTable();
        if (state is not null)
        {
            var key = Instances.InstanceKey(Identity, User);
            table.Put(key, Identity, User, state, url ?? Instances.InternalUrl(key, Domain));
        }

        return table;
    }

    [Fact]
    public void Activities_go_to_their_own_instance_only()
    {
        var decision = RouterCore.Decide(Activity(), Table(RouteStates.Ready));
        Assert.Equal(RouterDecisionKinds.Forward, decision.Kind);
        Assert.Contains(".internal.calmforest-1a2b3c.example.com", decision.Url, StringComparison.Ordinal);
        Assert.Equal("https://margo-" + decision.Key + ".internal." + Domain, decision.Url);
        var other = RouterCore.Decide(Activity(a => a["recipient"] = new JsonObject { ["agenticAppId"] = Identity, ["agenticUserId"] = "margo-user-2" }),
            Table(RouteStates.Ready));
        Assert.Equal(RouterDecisionKinds.Provision, other.Kind);
        Assert.NotEqual(decision.Key, other.Key);
    }

    [Fact]
    public void Unknown_waiting_stopped_and_malformed()
    {
        Assert.Equal("ensure", RouterCore.Decide(Activity(), Table()).Message!.Action);
        Assert.Equal(RouterDecisionKinds.Provision, RouterCore.Decide(Activity(), Table(RouteStates.Failed)).Kind);
        Assert.Equal(503, RouterCore.Decide(Activity(), Table(RouteStates.Provisioning)).Status);
        foreach (var state in (string[])[RouteStates.Suspended, RouteStates.Retired])
        {
            Assert.Equal(RouterDecisionKinds.Ignore, RouterCore.Decide(Activity(), Table(state)).Kind);
        }

        Assert.Equal(400, RouterCore.Decide(Activity(a => a["recipient"] = new JsonObject()), Table()).Status);
        Assert.Equal(400, RouterCore.Decide(Activity(a => a["recipient"] = new JsonObject { ["agenticAppId"] = "x", ["agenticUserId"] = "x" }), Table()).Status);
        var lifecycle = RouterCore.Decide(Activity(a =>
        {
            a["type"] = "event";
            a["name"] = "agentLifecycle";
            a["valueType"] = "agenticUserDeleted";
        }), Table(RouteStates.Ready));
        Assert.Equal((RouterDecisionKinds.Lifecycle, "retire"), (lifecycle.Kind, lifecycle.Message!.Action));
    }

    [Fact]
    public void Every_decision_carries_python_fields()
    {
        // Values from margo_autopilot.router.core.decide on Python 3.13.
        var key = Instances.InstanceKey(Identity, User);
        Assert.Equal(new RouterDecision("wait", 503, key, RetryAfter: 30), RouterCore.Decide(Activity(), Table(RouteStates.Provisioning)));
        Assert.Equal(new RouterDecision("ignore", 200, key, Reason: "instance is suspended"), RouterCore.Decide(Activity(), Table(RouteStates.Suspended)));
        Assert.Equal(new RouterDecision("reject", 400, Reason: "activity has no agent recipient"), RouterCore.Decide(new JsonArray(1), Table()));
        Assert.Equal(new RouterDecision("reject", 400, Reason: "activity has an invalid agent recipient"),
            RouterCore.Decide(Activity(a => a["recipient"] = new JsonObject { ["agenticAppId"] = "a/b", ["agenticUserId"] = User }), Table()));

        var first = RouterCore.Decide(Activity(), Table());
        Assert.Equal(("provision", 503, 30), (first.Kind, first.Status, first.RetryAfter));
        Assert.Equal("{\"action\": \"ensure\", \"agent_identity_id\": \"agent-identity-1\", \"agent_user_id\": \"margo-user-1\", \"event\": \"first-activity\"}",
            first.Message!.ToJson());
        Assert.Equal("retry-after-failure", RouterCore.Decide(Activity(), Table(RouteStates.Failed)).Message!.Event);

        // A ready route without a url is provisioned again, as a failure.
        var noUrl = RouteTable.Loads("{\"routes\": {\"" + key + "\": {\"state\": \"ready\", \"url\": \"\"}}, \"schema_version\": 1}");
        Assert.Equal("retry-after-failure", RouterCore.Decide(Activity(), noUrl).Message!.Event);
    }

    [Fact]
    public void Aad_object_id_stands_in_for_a_missing_agent_user()
    {
        var decision = RouterCore.Decide(Activity(a => a["recipient"] = new JsonObject { ["agenticAppId"] = Identity, ["aadObjectId"] = User }),
            Table(RouteStates.Ready));
        Assert.Equal(RouterDecisionKinds.Forward, decision.Kind);
    }

    [Fact]
    public void A_lifecycle_name_in_any_case_becomes_a_provisioning_message()
    {
        var decision = RouterCore.Decide(Activity(a =>
        {
            a["name"] = "AgentLifecycle";
            a["valueType"] = "agenticUserDisabled";
        }), Table(RouteStates.Ready));
        Assert.Equal("{\"action\": \"suspend\", \"agent_identity_id\": \"agent-identity-1\", \"agent_user_id\": \"margo-user-1\", \"event\": \"agenticuserdisabled\"}",
            decision.Message!.ToJson());
        Assert.Equal(200, decision.Status);
    }

    [Fact]
    public void A_ready_route_whose_url_is_not_text_fails_as_python_did()
    {
        // Python's decide returned the number and Router.messages then failed with TypeError when it
        // built the address (500); voice_target failed with AttributeError.
        var key = Instances.InstanceKey(Identity, User);
        var table = RouteTable.Loads("{\"routes\": {\"" + key + "\": {\"state\": \"ready\", \"url\": 7}}, \"schema_version\": 1}");
        Assert.Throws<InvalidOperationException>(() => RouterCore.Decide(Activity(), table));
        Assert.Throws<InvalidOperationException>(() => RouterCore.VoiceTargetFor("/i/" + key + "/voice/", table));
    }

    [Fact]
    public void Voice_paths_are_confined_to_one_ready_instance()
    {
        var key = Instances.InstanceKey(Identity, User);
        var target = RouterCore.VoiceTargetFor("/i/" + key + "/voice/ws", Table(RouteStates.Ready));
        Assert.EndsWith("/voice/ws", target.Url, StringComparison.Ordinal);
        Assert.Equal("/voice/", RouterCore.VoiceTargetFor("/i/" + key + "/voice", Table(RouteStates.Ready)).Url![^7..]);
        foreach (var path in (string[])["/i/" + key + "/voice/../admin", "/i/" + key + "/voice//x", "/i/zz/voice/", "/voice/ws", "/i/" + key + "/voice/a?b"])
        {
            Assert.Equal(404, RouterCore.VoiceTargetFor(path, Table(RouteStates.Ready)).Status);
        }

        Assert.Equal(503, RouterCore.VoiceTargetFor("/i/" + key + "/voice/", Table(RouteStates.Provisioning)).Status);
        Assert.Equal(404, RouterCore.VoiceTargetFor("/i/" + key + "/voice/", Table(RouteStates.Retired)).Status);
    }

    [Fact]
    public void Voice_targets_match_python_values()
    {
        // Values from margo_autopilot.router.core.voice_target on Python 3.13.
        var key = Instances.InstanceKey(Identity, User);
        var ready = Table(RouteStates.Ready, "https://margo-x.internal.example.com//");
        Assert.Equal(new VoiceTarget(200, key, "https://margo-x.internal.example.com/voice/app.js"), RouterCore.VoiceTargetFor("/i/" + key + "/voice/app.js", ready));
        Assert.Equal(new VoiceTarget(503, RetryAfter: 30), RouterCore.VoiceTargetFor("/i/" + key + "/voice/", Table(RouteStates.Failed)));
        Assert.Equal(new VoiceTarget(404), RouterCore.VoiceTargetFor("/i/" + key + "/voice/", Table(RouteStates.Suspended)));
        Assert.Equal(new VoiceTarget(404), RouterCore.VoiceTargetFor("/i/" + Instances.InstanceKey(Identity, "margo-user-2") + "/voice/", ready));
        Assert.Equal(new VoiceTarget(404), RouterCore.VoiceTargetFor(null, ready));
        Assert.Equal(new VoiceTarget(404), RouterCore.VoiceTargetFor("/i/" + key + "/voicex", ready));
        Assert.Equal(new VoiceTarget(404), RouterCore.VoiceTargetFor("/i/" + key.ToUpperInvariant() + "/voice/", ready));

        // Python's "$" also matches before a final newline; the newline is not part of the address.
        Assert.Equal(new VoiceTarget(200, key, "https://margo-x.internal.example.com/voice/a"), RouterCore.VoiceTargetFor("/i/" + key + "/voice/a\n", ready));
        Assert.Equal(new VoiceTarget(404), RouterCore.VoiceTargetFor("/i/" + key + "/voice/a\n\n", ready));
    }
}
