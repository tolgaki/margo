using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Orchestrator;
using Margo.Cloud.State.Core;
using Microsoft.Agents.A365.Notifications.Models;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;

namespace Margo.Cloud.Host;

/// <summary>
/// Turning an authenticated Agents SDK activity into the orchestrator's event (Python: <c>_identity</c>, the bodies of
/// <c>on_message</c> and <c>on_mail_notification</c>, <c>_email_reference</c>). Identity comes from the activity's
/// accounts, never from its text.
/// </summary>
public static class ActivityEvents
{
    /// <summary><c>_identity(activity)</c>: the agent identity, the agent user and the sender.</summary>
    public static InboundEvent Identity(IActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var recipient = activity.Recipient;
        var sender = activity.From;
        return new InboundEvent
        {
            AgentIdentityId = recipient?.AgenticAppId,
            AgentUserId = Or(recipient?.AgenticUserId, recipient?.AadObjectId),
            SenderId = sender?.AadObjectId,
            SenderName = sender?.Name,
        };
    }

    /// <summary><c>activity.is_email(activity)</c> over the SDK activity (see Core's PORTING.md).</summary>
    public static bool IsEmail(IActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var entities = new List<ActivityEntity>();
        foreach (var entity in activity.Entities ?? [])
        {
            entities.Add(ActivityEntity.FromJson(JsonNode.Parse(ProtocolJsonSerializer.ToJson(entity))));
        }

        // The entity loop covers what get_product_info_entity() would find, so ProductInfoId stays empty.
        return ActivityClassifier.IsEmail(new EmailFacts
        {
            Channel = activity.ChannelId?.Channel,
            SubChannel = activity.ChannelId?.SubChannel,
            ChannelIdText = activity.ChannelId?.ToString(),
            Entities = entities,
        });
    }

    /// <summary>
    /// <c>_email_reference(activity)</c>: the Outlook message behind a mail activity, if it carries one. Like Python
    /// (<c>except Exception: return None</c>), a reference the SDK cannot read is none.
    /// </summary>
    public static EmailReference? EmailReference(IActivity activity)
    {
        try
        {
            return new AgentNotificationActivity(activity).EmailNotification;
        }
        catch (Exception exc) when (exc is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The event of a Teams message or mail on the message route (<c>on_message</c>), or null when the activity has no
    /// agent identity or conversation ("message without agent identity or conversation; ignored").
    /// </summary>
    public static InboundEvent? Message(IActivity activity, out bool email)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var inbound = Identity(activity);
        email = IsEmail(activity);
        var conversation = activity.Conversation;
        if (string.IsNullOrEmpty(inbound.AgentIdentityId) || string.IsNullOrEmpty(inbound.AgentUserId) || string.IsNullOrEmpty(conversation?.Id))
        {
            return null;
        }

        // Mail reaches this route first; its Outlook message ID survives redelivery.
        var reference = email ? EmailReference(activity) : null;
        var ids = ActivityClassifier.StableIds(Or(reference?.Id, activity.Id), Or(reference?.ConversationId, conversation.Id),
                                               inbound.SenderId, activity.Text ?? string.Empty);
        // conversation_type decides whether this is the manager's one-to-one chat; a group chat or channel never runs
        // a turn that reads as the manager.
        var decision = email ? null : Decision(activity.Value);
        return inbound with
        {
            ConversationId = ids.ConversationId,
            ActivityId = ids.ExternalId,
            ConversationType = conversation.ConversationType,
            Channel = email ? "mail" : "teams",
            // A tap on an approval card is a decision, never text for the model.
            Text = decision is null ? activity.Text ?? string.Empty : null,
            Decision = decision,
        };
    }

    /// <summary>
    /// The decision an approval card's button carries (<see cref="ApprovalCards"/>), or null when the activity's value is
    /// not one: <c>{"margo": "approve"|"reject", "action_id", "revision", "action_hash", "about"}</c> (<c>about</c>: the
    /// card's context, shown again on its last version and nothing else). Who tapped it, and whether
    /// it matches the ledger, is checked by the orchestrator.
    /// </summary>
    public static CardDecision? Decision(object? value)
    {
        if (value is null)
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = value as JsonNode ?? JsonNode.Parse(ProtocolJsonSerializer.ToJson(value));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (node is not JsonObject data || data["margo"] is not JsonValue verbValue || !verbValue.TryGetValue<string>(out var verb)
            || verb is not (CardDecision.Approve or CardDecision.Reject)
            || data["action_id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var actionId) || actionId.Length is 0 or > 100
            || data["action_hash"] is not JsonValue hashValue || !hashValue.TryGetValue<string>(out var actionHash) || actionHash.Length is 0 or > 200
            || data["revision"] is not JsonValue revisionValue || !TryLong(revisionValue, out var revision))
        {
            return null;
        }

        var context = data["about"] is JsonArray about
            ? CardContext.Bounded(about.OfType<JsonArray>()
                                       .Where(pair => pair.Count == 2 && pair[0] is JsonValue && pair[1] is JsonValue)
                                       .Select(pair => (Label: PyNode.AsString(pair[0]), Value: PyNode.AsString(pair[1])))
                                       .Where(pair => pair.Label is { Length: > 0 } && pair.Value is { Length: > 0 })
                                       .Select(pair => new CardFact(pair.Label!, pair.Value!)))
            : [];
        return new CardDecision(verb, actionId, revision, actionHash) { Context = context };
    }

    private static bool TryLong(JsonValue value, out long result)
    {
        if (value.TryGetValue(out result))
        {
            return true;
        }

        return value.TryGetValue<string>(out var text) && long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// The event of an Agent 365 mail notification (<c>on_mail_notification</c>), or null when it has no agent identity.
    /// </summary>
    public static InboundEvent? MailNotification(IActivity activity, AgentNotificationActivity notification) =>
        MailNotification(activity, notification?.EmailNotification);

    /// <summary>
    /// The same from the Outlook reference alone: null when the activity has none, or one the SDK cannot read (Python's
    /// wrapper then left <c>email</c> None and the handler used the transport's IDs).
    /// </summary>
    public static InboundEvent? MailNotification(IActivity activity, EmailReference? mail)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var inbound = Identity(activity);
        var sender = activity.From;
        var text = activity.Text ?? string.Empty;
        var senderId = Or(sender?.AadObjectId, sender?.Id);
        var ids = ActivityClassifier.StableIds(Or(mail?.Id, activity.Id), Or(mail?.ConversationId, activity.Conversation?.Id), senderId, text);
        inbound = inbound with
        {
            SenderId = senderId,
            SenderName = sender?.Name,
            ConversationId = ids.ConversationId,
            ActivityId = ids.ExternalId,
            Channel = "mail",
            ConversationType = null,
            Text = text,
        };
        return string.IsNullOrEmpty(inbound.AgentIdentityId) || string.IsNullOrEmpty(inbound.AgentUserId) ? null : inbound;
    }

    /// <summary>Python's <c>a or b</c> for optional text.</summary>
    private static string? Or(string? first, string? second) => string.IsNullOrEmpty(first) ? second : first;
}
