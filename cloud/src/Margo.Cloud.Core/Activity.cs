using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Margo.Cloud.Core;

/// <summary>One entity of an activity, as the email classification reads it.</summary>
/// <param name="Type">The entity type, such as <c>productInfo</c> or <c>mention</c>.</param>
/// <param name="Id">The entity's <c>id</c>, when it has one (a <c>productInfo</c> entity names the product).</param>
public sealed record ActivityEntity(string? Type, string? Id)
{
    /// <summary>
    /// An entity from activity JSON, read as Python's <c>activity._value</c> reads a dict: the exact
    /// key when its value is truthy, otherwise the last key that lower-cases to the name, otherwise
    /// empty. A non-string value is kept as its JSON text, which, like Python's <c>str()</c> of it,
    /// never matches a product or type name.
    /// </summary>
    public static ActivityEntity FromJson(JsonNode? entity)
    {
        if (entity is not JsonObject obj)
        {
            return new ActivityEntity(null, null);
        }

        return new ActivityEntity(Field(obj, "type"), Field(obj, "id"));
    }

    private static string? Field(JsonObject obj, string name)
    {
        var exact = obj[name];
        if (PythonValues.IsTruthy(exact))
        {
            return Text(exact);
        }

        JsonNode? lowered = null;
        foreach (var (key, value) in obj)
        {
            if (PythonText.LowerEquals(key, name))
            {
                lowered = value;
            }
        }

        return PythonValues.IsTruthy(lowered) ? Text(lowered) : null;
    }

    private static string Text(JsonNode? value) => PythonValues.AsString(value) ?? value!.ToJsonString();
}

/// <summary>
/// What <see cref="ActivityClassifier.IsEmail(EmailFacts)"/> needs from an activity. The Host reads
/// these from the Agents SDK <c>Activity</c>; <see cref="FromActivityJson"/> reads them from raw
/// activity JSON.
/// </summary>
/// <remarks>
/// Python reads <c>activity.channel_id</c>, which the SDK gives either as an object with
/// <c>channel</c> and <c>sub_channel</c> or as text such as <c>agents:email</c>. Set
/// <see cref="Channel"/> and <see cref="SubChannel"/> from the structured form, and
/// <see cref="ChannelIdText"/> to the channel id's text (<c>ChannelId.ToString()</c>), which is used
/// only when <see cref="Channel"/> is empty. <see cref="ProductInfoId"/> is the id of the SDK's
/// product-info entity (<c>get_product_info_entity()</c>), if any.
/// </remarks>
public sealed record EmailFacts
{
    public string? Channel { get; init; }

    public string? SubChannel { get; init; }

    public string? ChannelIdText { get; init; }

    public string? ProductInfoId { get; init; }

    public IReadOnlyList<ActivityEntity> Entities { get; init; } = [];

    /// <summary>The facts in a raw Bot Framework activity: <c>channelId</c> (text) and <c>entities</c>.</summary>
    public static EmailFacts FromActivityJson(JsonNode? activity)
    {
        if (activity is not JsonObject obj)
        {
            return new EmailFacts();
        }

        var entities = obj["entities"] is JsonArray array ? array.Select(ActivityEntity.FromJson).ToList() : [];
        return new EmailFacts { ChannelIdText = PythonValues.AsString(obj["channelId"]), Entities = entities };
    }
}

/// <summary>The identifiers recorded for one inbound message or mail notification.</summary>
/// <param name="ExternalId">One request per external ID in the office queue.</param>
/// <param name="ConversationId">The conversation the request belongs to.</param>
public sealed record MessageIds(string ExternalId, string ConversationId);

/// <summary>Activity classification that needs no SDK. Python: <c>activity.py</c>.</summary>
public static class ActivityClassifier
{
    /// <summary>
    /// Email reaches the message route too: channel <c>email</c> or <c>agents:email</c>, or product
    /// <c>email</c>. Mirrors the classification in Microsoft's Agent 365 samples. Treating email as
    /// a chat turn would let mail trigger on-behalf-of reads and replies, which this version never does.
    /// </summary>
    public static bool IsEmail(EmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var channel = facts.Channel ?? string.Empty;
        var subChannel = facts.SubChannel ?? string.Empty;
        if (channel.Length == 0)
        {
            var text = facts.ChannelIdText ?? string.Empty;
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            (channel, subChannel) = colon < 0 ? (text, string.Empty) : (text[..colon], text[(colon + 1)..]);
        }

        if (PythonText.LowerEquals(channel, "email") || (PythonText.LowerEquals(channel, "agents") && PythonText.LowerEquals(subChannel, "email")))
        {
            return true;
        }

        if (PythonText.LowerEquals(facts.ProductInfoId, "email"))
        {
            return true;
        }

        return facts.Entities.Any(entity => PythonText.LowerEquals(entity.Type, "productinfo") && PythonText.LowerEquals(entity.Id, "email"));
    }

    /// <summary>Email classification for raw activity JSON; see <see cref="EmailFacts.FromActivityJson"/>.</summary>
    public static bool IsEmail(JsonNode? activity) => IsEmail(EmailFacts.FromActivityJson(activity));

    /// <summary>
    /// The external and conversation IDs for an inbound message or mail notification.
    /// </summary>
    /// <remarks>
    /// The office queue records one request per external ID. A missing ID must not become a shared
    /// placeholder, which would make every later message collide with the first. It is derived from
    /// the content instead: <c>sha256-</c> and the SHA-256 of
    /// <c>json.dumps([sender or None, conversation or None, text or ""], ensure_ascii=False)</c> in
    /// UTF-8. A redelivery is recorded once, and different messages never share an ID (identical
    /// repeats from the same sender in the same conversation are one request). A missing
    /// conversation becomes <c>message-</c> and the external ID.
    /// </remarks>
    /// <exception cref="System.Text.EncoderFallbackException">A value holds a lone surrogate, which
    /// Python cannot encode either (UnicodeEncodeError).</exception>
    public static MessageIds StableIds(string? messageId, string? conversationId, string? senderId, string? text)
    {
        if (string.IsNullOrEmpty(messageId))
        {
            var material = PythonJson.Dumps(
                new JsonArray(NullIfEmpty(senderId), NullIfEmpty(conversationId), JsonValue.Create(text ?? string.Empty)),
                ensureAscii: false);
            messageId = "sha256-" + Convert.ToHexStringLower(SHA256.HashData(PythonText.StrictUtf8.GetBytes(material)));
        }

        if (string.IsNullOrEmpty(conversationId))
        {
            conversationId = "message-" + messageId;
        }

        return new MessageIds(messageId, conversationId);
    }

    private static JsonValue? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : JsonValue.Create(value);
}
