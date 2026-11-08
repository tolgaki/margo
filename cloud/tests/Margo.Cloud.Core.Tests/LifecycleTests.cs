using System.Text.Json.Nodes;
using Xunit;
using static Margo.Cloud.Core.Tests.Fixtures;

namespace Margo.Cloud.Core.Tests;

/// <summary>The former Python instance tests' NamesAndRoutes.test_lifecycle_events_map_to_provisioning_actions,
/// activity_identity, and the provisioning queue message format.</summary>
public sealed class LifecycleTests
{
    private static JsonObject Lifecycle(string? name, JsonNode? valueType, Action<JsonObject>? change = null) => Activity(activity =>
    {
        activity["type"] = "event";
        activity["name"] = name;
        activity["valueType"] = valueType;
        change?.Invoke(activity);
    });

    [Fact]
    public void Lifecycle_events_map_to_provisioning_actions()
    {
        var cases = new Dictionary<string, string>
        {
            ["agenticUserIdentityCreated"] = "ensure",
            ["agenticUserManagerUpdated"] = "ensure",
            ["agenticUserDisabled"] = "suspend",
            ["agenticUserDeleted"] = "retire",
        };
        foreach (var (lifecycleEvent, action) in cases)
        {
            var message = Instances.LifecycleMessage(Lifecycle("agentLifecycle", lifecycleEvent))!;
            Assert.Equal((action, User), (message.Action, message.AgentUserId));
        }

        Assert.Null(Instances.LifecycleMessage(Activity()));
        Assert.Null(Instances.LifecycleMessage(Activity(a => { a["name"] = "agentLifecycle"; a["valueType"] = "somethingNew"; })));
    }

    [Fact]
    public void Every_lifecycle_event_has_an_action()
    {
        Assert.Equal(8, Instances.Lifecycle.Count);
        Assert.Equal(["agenticuserdisabled"], Instances.Lifecycle.Where(pair => pair.Value == "suspend").Select(pair => pair.Key));
        Assert.Equal(["agenticuserdeleted"], Instances.Lifecycle.Where(pair => pair.Value == "retire").Select(pair => pair.Key));
        foreach (var lifecycleEvent in Instances.Lifecycle.Keys)
        {
            var message = Instances.LifecycleMessage(Lifecycle("agentLifecycle", lifecycleEvent))!;
            Assert.Equal(new ProvisioningMessage(Instances.Lifecycle[lifecycleEvent], Identity, User, lifecycleEvent), message);
        }
    }

    [Fact]
    public void Names_and_events_are_lower_cased_as_python_does()
    {
        // Expected results from Python 3.13: str.lower() maps KELVIN SIGN to "k"; U+0130 becomes "i" plus a dot.
        var upper = Instances.LifecycleMessage(Lifecycle("AGENTLIFECYCLE", "AGENTICUSERENABLED"))!;
        Assert.Equal(("ensure", "agenticuserenabled"), (upper.Action, upper.Event));
        var kelvin = Instances.LifecycleMessage(Lifecycle("agentLifecycle", "agenticUserWor" + U(0x212a) + "loadOnboardingUpdated"))!;
        Assert.Equal("agenticuserworkloadonboardingupdated", kelvin.Event);
        Assert.Null(Instances.LifecycleMessage(Lifecycle("agentL" + U(0x130) + "fecycle", "agenticUserDeleted")));
        Assert.Null(Instances.LifecycleMessage(Lifecycle("agentLifecycle", "agenticUserDeleted" + U(0x131))));
    }

    [Fact]
    public void Non_string_or_empty_fields_are_not_lifecycle_events()
    {
        Assert.Null(Instances.LifecycleMessage(Lifecycle(null, "agenticUserDeleted", a => a["name"] = 5)));
        Assert.Null(Instances.LifecycleMessage(Lifecycle("agentLifecycle", 5)));
        Assert.Null(Instances.LifecycleMessage(Lifecycle("agentLifecycle", string.Empty)));
        Assert.Null(Instances.LifecycleMessage(Lifecycle("agentLifecycle", null)));
        Assert.Null(Instances.LifecycleMessage(Lifecycle("agentLifecycle", "agenticUserDeleted", a => a.Remove("recipient"))));
        Assert.Null(Instances.LifecycleMessage(new JsonArray()));
        Assert.Null(Instances.LifecycleMessage(null));
    }

    [Fact]
    public void Activity_identity_reads_the_recipient_like_python()
    {
        static AgentIdentity? Identify(JsonNode recipient) => Instances.ActivityIdentity(new JsonObject { ["recipient"] = recipient });

        Assert.Equal(new AgentIdentity(Identity, User), Instances.ActivityIdentity(Activity()));
        Assert.Equal(new AgentIdentity("a", "u"), Identify(new JsonObject { ["agenticAppId"] = "a", ["agenticUserId"] = "", ["aadObjectId"] = "u" }));
        Assert.Equal(new AgentIdentity("a", string.Empty), Identify(new JsonObject { ["agenticAppId"] = "a", ["agenticUserId"] = 0, ["aadObjectId"] = "" }));
        Assert.Null(Identify(new JsonObject { ["agenticAppId"] = "a", ["agenticUserId"] = 7 }));
        Assert.Equal(new AgentIdentity(string.Empty, "u"), Identify(new JsonObject { ["agenticAppId"] = "", ["aadObjectId"] = "u" }));
        Assert.Null(Identify(new JsonArray("x")));
        Assert.Null(Identify(new JsonObject { ["agenticUserId"] = "u" }));
        Assert.Null(Instances.ActivityIdentity(new JsonArray()));
        Assert.Null(Instances.ActivityIdentity(null));
    }

    [Fact]
    public void Queue_messages_are_the_text_python_writes()
    {
        // Golden text from json.dumps(message, sort_keys=True) in the router and the provisioner's enqueue.
        var activity = Lifecycle("agentLifecycle", "agenticUserDeleted", a =>
            a["recipient"] = new JsonObject { ["agenticAppId"] = "agent-" + U(0xe9) + "-1", ["agenticUserId"] = "user-" + U(0x1f600) });
        Assert.Equal(
            """{"action": "retire", "agent_identity_id": "agent-\u00e9-1", "agent_user_id": "user-\ud83d\ude00", "event": "agenticuserdeleted"}""",
            Instances.LifecycleMessage(activity)!.ToJson());
        Assert.Equal(
            """{"action": "ensure", "agent_identity_id": "agent-identity-1", "agent_user_id": "margo-user-1", "event": "operator", "redeploy": true}""",
            new ProvisioningMessage("ensure", Identity, User, "operator", Redeploy: true).ToJson());
        Assert.Equal("""{"action": "suspend", "agent_identity_id": "a", "agent_user_id": "b"}""", new ProvisioningMessage("suspend", "a", "b").ToJson());
    }

    [Fact]
    public void Queue_messages_round_trip_through_check()
    {
        var message = new ProvisioningMessage("ensure", Identity, User, "first-activity");
        var parsed = Instances.ParseMessage(message.ToJson());
        Assert.Equal(new CheckedMessage(Key, message), parsed);
        Assert.Equal(new CheckedMessage(Key, message), Instances.CheckMessage(message));
    }

    [Fact]
    public void Check_message_refuses_like_python()
    {
        Assert.Equal("agent identity and agent user must differ",
            Assert.Throws<RouteException>(() => Instances.ParseMessage("""{"action": "ensure", "agent_identity_id": "a", "agent_user_id": "a"}""")).Message);
        Assert.Equal("agent_identity_id must be an explicit opaque identifier",
            Assert.Throws<RouteException>(() => Instances.ParseMessage("""{"action": "ensure", "agent_identity_id": 5, "agent_user_id": "a"}""")).Message);
        foreach (var text in new[] { """{"action": "explode"}""", "[]", """{"action": ["ensure"]}""", """{"action": "Ensure", "agent_identity_id": "a", "agent_user_id": "b"}""", "null" })
        {
            Assert.Equal("unknown provisioning action", Assert.Throws<RouteException>(() => Instances.ParseMessage(text)).Message);
        }

        Assert.Equal("provisioning message is not JSON", Assert.Throws<RouteException>(() => Instances.ParseMessage("not json")).Message);
        Assert.Equal("unknown provisioning action", Assert.Throws<RouteException>(() => Instances.CheckMessage(new ProvisioningMessage("explode", "a", "b"))).Message);
        Assert.IsType<BindingException>(Assert.Throws<RouteException>(() => Instances.CheckMessage(new ProvisioningMessage("retire", "a", "a"))).InnerException);
    }

    [Fact]
    public void Unreadable_queue_text_is_not_json()
    {
        // Python's provisioner drops these (UnicodeEncodeError or ValueError); a raw lone surrogate
        // used to escape ParseMessage as a plain ArgumentException.
        var lone = "{\"action\": \"ensure\", \"agent_identity_id\": \"a" + LoneSurrogate + "\", \"agent_user_id\": \"b\"}";
        Assert.Equal("provisioning message is not JSON", Assert.Throws<RouteException>(() => Instances.ParseMessage(lone)).Message);
        var huge = "{\"action\": \"ensure\", \"agent_identity_id\": \"a\", \"agent_user_id\": \"b\", \"n\": 1" + new string('0', 4300) + "}";
        Assert.Equal("provisioning message is not JSON", Assert.Throws<RouteException>(() => Instances.ParseMessage(huge)).Message);
    }

    [Fact]
    public void Shared_tables_cannot_be_changed_by_callers()
    {
        // The lifecycle map decides which platform events retire an instance; Python's order is kept.
        Assert.True(((ICollection<KeyValuePair<string, string>>)Instances.Lifecycle).IsReadOnly);
        Assert.False(Instances.Lifecycle is IDictionary<string, string> { IsReadOnly: false });
        Assert.Equal(
            [
                "agenticuseridentitycreated", "agenticuseridentityupdated", "agenticuserworkloadonboardingupdated",
                "agenticusermanagerupdated", "agenticuserundeleted", "agenticuserenabled", "agenticuserdisabled", "agenticuserdeleted",
            ],
            Instances.Lifecycle.Keys);
        Assert.True(((ICollection<string>)Instances.RequiredConfig).IsReadOnly);
        Assert.True(((ICollection<string>)RouteStates.All).IsReadOnly);
        Assert.True(((ICollection<string>)ProvisioningActions.All).IsReadOnly);
    }

    [Fact]
    public void Check_message_keeps_what_the_provisioner_reads()
    {
        var parsed = Instances.ParseMessage("""{"action": "ensure", "agent_identity_id": "a", "agent_user_id": "b", "event": 5, "redeploy": "yes", "key": "forged"}""");
        Assert.Equal(Instances.InstanceKey("a", "b"), parsed.Key); // the key is always recomputed
        Assert.Null(parsed.Message.Event);
        Assert.True(parsed.Message.Redeploy); // Python truthiness
        foreach (var falsy in new[] { "false", "0", "0.0", "\"\"", "[]", "{}", "null" })
        {
            var message = Instances.ParseMessage("{\"action\": \"ensure\", \"agent_identity_id\": \"a\", \"agent_user_id\": \"b\", \"redeploy\": " + falsy + "}");
            Assert.False(message.Message.Redeploy);
        }
    }
}
