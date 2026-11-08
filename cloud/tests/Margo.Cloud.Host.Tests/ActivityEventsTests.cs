using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Voice;
using Microsoft.Agents.A365.Notifications.Models;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Authentication.Model;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Host.Tests;

/// <summary>The SDK activity to orchestrator event translation (<c>_identity</c>, the two handlers, <c>_email_reference</c>).</summary>
public sealed class ActivityEventsTests
{
    private static IActivity Read(JsonObject json) => ProtocolJsonSerializer.ToObject<IActivity>(json.ToJsonString());

    [Fact]
    public void Identity_comes_from_the_recipient_and_sender_accounts()
    {
        var message = ActivityEvents.Message(Read(Activities.Message("hello", conversationType: "personal")), out var email)!;
        Assert.False(email);
        Assert.Equal(Activities.AgentIdentity, message.AgentIdentityId);
        Assert.Equal(Activities.AgentUser, message.AgentUserId);
        Assert.Equal(Activities.Manager, message.SenderId);
        Assert.Equal("Dana", message.SenderName);
        Assert.Equal("chat-principal-1", message.ConversationId);
        Assert.Equal("activity-1", message.ActivityId);
        Assert.Equal("personal", message.ConversationType);
        Assert.Equal("teams", message.Channel);
        Assert.Equal("hello", message.Text);

        // agent_user_id falls back to the recipient's object ID.
        var json = Activities.Message("hello");
        json["recipient"]!.AsObject().Remove("agenticUserId");
        json["recipient"]!["aadObjectId"] = "margo-object-1";
        Assert.Equal("margo-object-1", ActivityEvents.Message(Read(json), out _)!.AgentUserId);

        // Without an agent identity, an agent user or a conversation: no event.
        foreach (var change in new Action<JsonObject>[]
                 {
                     activity => activity["recipient"]!.AsObject().Remove("agenticAppId"),
                     activity => activity["recipient"]!.AsObject().Remove("agenticUserId"),
                     activity => activity["conversation"]!["id"] = "",
                 })
        {
            var broken = Activities.Message("hello");
            change(broken);
            Assert.Null(ActivityEvents.Message(Read(broken), out _));
        }
    }

    [Fact]
    public void Missing_ids_are_derived_from_the_content()
    {
        var json = Activities.Message("first");
        json.Remove("id");
        var first = ActivityEvents.Message(Read(json), out _)!;
        Assert.Equal(ActivityClassifier.StableIds(null, "chat-principal-1", Activities.Manager, "first").ExternalId, first.ActivityId);
        Assert.StartsWith("sha256-", first.ActivityId, StringComparison.Ordinal);
    }

    [Fact]
    public void Email_shapes_are_recognised_from_the_sdk_activity()
    {
        bool IsEmail(string channel, JsonArray? entities = null)
        {
            var json = Activities.Message("x", channel: channel);
            if (entities is not null)
            {
                json["entities"] = entities;
            }

            return ActivityEvents.IsEmail(Read(json));
        }

        Assert.True(IsEmail("email"));
        Assert.True(IsEmail("agents:email"));
        Assert.True(IsEmail("Agents:Email"));
        Assert.True(IsEmail("msteams", [new JsonObject { ["type"] = "productInfo", ["id"] = "email" }]));
        Assert.False(IsEmail("msteams"));
        Assert.False(IsEmail("agents:teams"));
        Assert.False(IsEmail("msteams", [new JsonObject { ["type"] = "mention" }]));
    }

    [Fact]
    public void Mail_keeps_its_outlook_ids_and_is_never_a_teams_turn()
    {
        var mail = ActivityEvents.Message(Read(Activities.Mail("Please review")), out var email)!;
        Assert.True(email);
        Assert.Equal("mail", mail.Channel);
        Assert.Equal("outlook-message-1", mail.ActivityId);
        Assert.Equal("outlook-conversation-1", mail.ConversationId);

        // Without the Outlook reference the transport's IDs are used.
        var bare = Activities.Mail("Please review", mailId: null);
        bare.Remove("entities");
        var plain = ActivityEvents.Message(Read(bare), out _)!;
        Assert.Equal("transport-1", plain.ActivityId);
        Assert.Equal("mail-conversation-transport", plain.ConversationId);
    }

    /// <summary>
    /// <c>_email_reference</c> caught every exception (<c>except Exception: return None</c>): a reference the SDK cannot
    /// read falls back to the transport's IDs instead of failing the turn (whose error handler would then answer the
    /// mail with the exception text).
    /// </summary>
    [Fact]
    public void An_unreadable_outlook_reference_falls_back_to_the_transport_ids()
    {
        JsonObject Broken(JsonArray entities)
        {
            var json = Activities.Mail("Please review");
            json["entities"] = entities;
            return json;
        }

        var shapes = new[]
        {
            new JsonArray(new JsonObject { ["type"] = "emailNotification", ["id"] = new JsonObject { ["nested"] = 1 } }),
            new JsonArray(new JsonObject { ["type"] = "emailNotification", ["id"] = 5, ["conversationId"] = "outlook-conversation-1" }),
        };
        foreach (var entities in shapes)
        {
            var activity = Read(Broken(entities));
            Assert.Null(ActivityEvents.EmailReference(activity));
            var mail = ActivityEvents.Message(activity, out var email)!;
            Assert.True(email);
            Assert.Equal("transport-1", mail.ActivityId);
            Assert.Equal("mail-conversation-transport", mail.ConversationId);
        }
    }

    [Fact]
    public void A_mail_notification_uses_the_sender_object_id_or_its_channel_id()
    {
        var activity = Read(Activities.Mail("Numbers attached", type: "event"));
        var notification = new AgentNotificationActivity(activity);
        Assert.Equal(NotificationTypeEnum.EmailNotification, notification.NotificationType);
        var inbound = ActivityEvents.MailNotification(activity, notification)!;
        Assert.Equal("colleague-1", inbound.SenderId);
        Assert.Equal("mail", inbound.Channel);
        Assert.Null(inbound.ConversationType);
        Assert.Equal("outlook-message-1", inbound.ActivityId);
        Assert.Equal("outlook-conversation-1", inbound.ConversationId);

        var json = Activities.Mail("Numbers attached", type: "event");
        json["from"]!.AsObject().Remove("aadObjectId");
        var fallback = Read(json);
        Assert.Equal("29:colleague-1", ActivityEvents.MailNotification(fallback, new AgentNotificationActivity(fallback))!.SenderId);

        json["recipient"]!.AsObject().Remove("agenticAppId");
        var anonymous = Read(json);
        Assert.Null(ActivityEvents.MailNotification(anonymous, new AgentNotificationActivity(anonymous)));
    }

    [Fact]
    public async Task Voice_tokens_go_through_the_sdk_connection()
    {
        var connection = new FakeConnection();
        var connections = new Microsoft.Agents.Authentication.ConfigurationConnections(
            new Dictionary<string, IAccessTokenProvider> { ["SERVICE_CONNECTION"] = connection },
            new List<ConnectionMapItem> { new() { ServiceUrl = "*", Connection = "SERVICE_CONNECTION" } });
        var tokens = new SdkVoiceTokens(connections);
        var token = TestContext.Current.CancellationToken;
        Assert.Equal("margo-workiq-token", await tokens.GetAgenticUserTokenAsync(Tokens.TenantId, Activities.AgentIdentity, Activities.AgentUser,
                                                                                 [VoiceService.WorkIQScope], token));
        Assert.Equal("principal-voice-token", await tokens.AcquireTokenOnBehalfOfAsync([VoiceService.WorkIQScope], "person-sign-in", token));
        Assert.Equal(["agentic-user " + Tokens.TenantId + " " + Activities.AgentIdentity + " " + Activities.AgentUser + " " + HostSettings.WorkIQScope,
                      "obo " + HostSettings.WorkIQScope], connection.Requests);
    }

    [Fact]
    public void The_adapter_logger_drops_events_that_carry_bodies()
    {
        using var logs = new CapturingLoggerProvider();
        var logger = new BodyFreeLogger<ActivityEventsTests>(logs.CreateLogger("adapter"));
        logger.Log(LogLevel.Debug, new EventId(4), "Activity Accepted: {\"text\": \"secret\"}", null, (state, _) => state);
        logger.Log(LogLevel.Error, new EventId(10), "Missing or invalid body, Activity ='{...}'", null, (state, _) => state);
        logger.Log(LogLevel.Error, new EventId(7), "Unexpected exception in CloudAdapter.ProcessAsync", null, (state, _) => state);
        Assert.Equal(["Unexpected exception in CloudAdapter.ProcessAsync"], logs.Entries.Select(entry => entry.Message));
    }
}
