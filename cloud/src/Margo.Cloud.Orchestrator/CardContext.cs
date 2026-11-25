using System.Globalization;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Engine;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Orchestrator;

/// <summary>What an approval card asks for, in the manager's terms.</summary>
public enum ApprovalKind
{
    Email,
    Reply,
    Forward,
    MeetingNew,
    MeetingChange,
    MeetingCancel,
    Chat,
    Channel,
    DocumentChange,
    Share,
    Delete,
    Page,
    EngagePost,
    SharePointChange,
    MeetingListen,
    Other,
}

/// <summary>One line of plain context on an approval card: a label and its value.</summary>
public sealed record CardFact(string Label, string Value);

/// <summary>
/// The plain-language context of an approval card: what kind of action it is, and, for an action that names an existing
/// item only by its ID (a reply, a meeting change or cancellation, a chat post, a file share, a deletion), a description
/// of that item read from Microsoft 365, so the manager sees which email, meeting, chat or file it is.
/// </summary>
public static class CardContext
{
    /// <summary>At most this many facts are carried with a card.</summary>
    public const int MaxFacts = 6;

    /// <summary>The longest fact value carried with a card.</summary>
    public const int MaxFactChars = 500;

    /// <summary>Shown when the item could not be read.</summary>
    public const string NotFound = "Margo couldn't look up which one this is. Ask her in chat before you approve.";

    // The invariant culture writes English day and month names, and works in globalization-invariant mode.
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    /// <summary>The kind of a Work IQ write call.</summary>
    public static ApprovalKind KindOf(string tool, JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return KindOf(tool, PathOf(arguments));
    }

    /// <summary>The kind of a Work IQ write call, from its tool and path.</summary>
    public static ApprovalKind KindOf(string tool, string? path)
    {
        var lower = (path ?? "").Split('?')[0].ToLowerInvariant();
        if (tool is "create_entity" or "update_entity" or "do_action" or "delete_entity")
        {
            if (lower.StartsWith("/pages", StringComparison.Ordinal))
            {
                return ApprovalKind.Page;
            }

            if (lower.StartsWith("/engage/", StringComparison.Ordinal))
            {
                return ApprovalKind.EngagePost;
            }

            if (lower.StartsWith("/sharepoint/", StringComparison.Ordinal))
            {
                return ApprovalKind.SharePointChange;
            }

            if (lower.Contains("/realtimeactivityfeed/", StringComparison.Ordinal))
            {
                return ApprovalKind.MeetingListen;
            }
        }

        return tool switch
        {
            "do_action" when lower.EndsWith("/sendmail", StringComparison.Ordinal) => ApprovalKind.Email,
            "do_action" when lower.EndsWith("/reply", StringComparison.Ordinal) || lower.EndsWith("/replyall", StringComparison.Ordinal) => ApprovalKind.Reply,
            "do_action" when lower.EndsWith("/forward", StringComparison.Ordinal) => ApprovalKind.Forward,
            "do_action" when lower.EndsWith("/cancel", StringComparison.Ordinal) && lower.Contains("/events/", StringComparison.Ordinal) => ApprovalKind.MeetingCancel,
            "create_entity" when lower.EndsWith("/events", StringComparison.Ordinal) => ApprovalKind.MeetingNew,
            "update_entity" when lower.Contains("/events/", StringComparison.Ordinal) => ApprovalKind.MeetingChange,
            "do_action" when lower.EndsWith("/office-agent-mcp/edit_artifact", StringComparison.Ordinal) => ApprovalKind.DocumentChange,
            "do_action" when lower.EndsWith("/invite", StringComparison.Ordinal) => ApprovalKind.Share,
            "delete_entity" => ApprovalKind.Delete,
            "create_entity" when lower.EndsWith("/messages", StringComparison.Ordinal) && lower.StartsWith("/chats/", StringComparison.Ordinal) => ApprovalKind.Chat,
            "create_entity" when lower.EndsWith("/messages", StringComparison.Ordinal) && lower.StartsWith("/teams/", StringComparison.Ordinal) => ApprovalKind.Channel,
            _ => ApprovalKind.Other,
        };
    }

    /// <summary>The path a write call acts on.</summary>
    public static string? PathOf(JsonObject arguments) =>
        PyNode.AsString(arguments["actionUrl"]) ?? PyNode.AsString(arguments["parentUrl"]) ?? PyNode.AsString(arguments["entityUrl"]);

    /// <summary>"Thursday, November 27, 2026, 2:00 PM to 2:30 PM (Pacific Standard Time)", or null without a start.</summary>
    public static string? When(JsonNode? start, JsonNode? end)
    {
        if (Time(start) is not { } from)
        {
            return null;
        }

        var zone = PyNode.AsString((start as JsonObject)?["timeZone"]);
        var text = from.ToString("dddd, MMMM d, yyyy, h:mm tt", English);
        if (Time(end) is { } to)
        {
            text += " to " + (to.Date == from.Date ? to.ToString("h:mm tt", English) : to.ToString("dddd, MMMM d, yyyy, h:mm tt", English));
        }

        return string.IsNullOrEmpty(zone) ? text : text + " (" + zone + ")";
    }

    /// <summary>
    /// Reads the existing item a write call acts on and describes it, or returns no facts when the call names none. A
    /// failed read gives <see cref="NotFound"/>; the card is still sent, with that warning.
    /// </summary>
    public static async Task<IReadOnlyList<CardFact>> DescribeAsync(WorkIQGuard guard, string tool, JsonObject arguments, string onDataOf,
                                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(arguments);
        var kind = KindOf(tool, arguments);
        var path = (PathOf(arguments) ?? "").Split('?')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        (string Url, Func<JsonObject, IEnumerable<CardFact>> Describe)? lookup = kind switch
        {
            ApprovalKind.Reply or ApprovalKind.Forward when segments.Length >= 2 =>
                (Parent(path) + "?$select=subject,from,receivedDateTime", item => Email(kind == ApprovalKind.Reply ? "Replying to" : "Forwarding", item)),
            ApprovalKind.MeetingCancel => (Parent(path) + "?$select=subject,start,end", item => Meeting(item)),
            ApprovalKind.MeetingChange => (path + "?$select=subject,start,end", item => Meeting(item)),
            ApprovalKind.Delete when path.Contains("/events/", StringComparison.OrdinalIgnoreCase) => (path + "?$select=subject,start,end", item => Meeting(item)),
            ApprovalKind.Delete when path.Contains("/messages/", StringComparison.OrdinalIgnoreCase) => (path + "?$select=subject,from,receivedDateTime", item => Email("Email", item)),
            ApprovalKind.Chat => (Parent(path) + "?$select=topic,chatType", item => Chat(item)),
            ApprovalKind.Channel => (Parent(path) + "?$select=displayName", item => Named("Channel", item, "displayName")),
            ApprovalKind.Share => (Parent(path) + "?$select=name,webUrl", item => Named("File", item, "name")),
            _ => null,
        };
        if (lookup is not { } found)
        {
            return [];
        }

        var result = await guard.CallAsync("fetch", new JsonObject
        {
            ["on_data_of"] = onDataOf,
            ["entityUrls"] = new JsonArray(found.Url),
        }, RunModes.Attended, cancellationToken).ConfigureAwait(false);
        if (result.Status == GuardStatuses.Ok && FirstData(result.Data) is JsonObject item)
        {
            var facts = found.Describe(item).Where(fact => !string.IsNullOrWhiteSpace(fact.Value)).ToList();
            if (facts.Count > 0)
            {
                return Bounded(facts);
            }
        }

        return [new CardFact("Which one", NotFound)];
    }

    /// <summary>Facts cut to <see cref="MaxFacts"/> items of at most <see cref="MaxFactChars"/> characters.</summary>
    public static IReadOnlyList<CardFact> Bounded(IEnumerable<CardFact> facts) =>
        [.. facts.Take(MaxFacts).Select(fact => fact with
        {
            Label = fact.Label.Length > 40 ? fact.Label[..40] : fact.Label,
            Value = fact.Value.Length > MaxFactChars ? fact.Value[..MaxFactChars] + "..." : fact.Value,
        })];

    private static string Parent(string path) => path[..Math.Max(0, path.TrimEnd('/').LastIndexOf('/'))];

    private static IEnumerable<CardFact> Email(string label, JsonObject item)
    {
        var subject = PyNode.AsString(item["subject"]);
        var from = item["from"]?["emailAddress"];
        var address = PyNode.AsString(from?["address"]);
        var name = PyNode.AsString(from?["name"]);
        var sender = address is null ? name : string.IsNullOrEmpty(name) ? address : name + " (" + address + ")";
        var text = "“" + (string.IsNullOrEmpty(subject) ? "(no subject)" : subject) + "”";
        if (sender is not null)
        {
            text += " from " + sender;
        }

        if (Time(item["receivedDateTime"]) is { } received)
        {
            text += ", " + received.ToString("MMMM d", English);
        }

        yield return new CardFact(label, text);
    }

    private static IEnumerable<CardFact> Meeting(JsonObject item)
    {
        var subject = PyNode.AsString(item["subject"]);
        yield return new CardFact("Meeting", string.IsNullOrEmpty(subject) ? "(no title)" : subject);
        if (When(item["start"], item["end"]) is { } when)
        {
            yield return new CardFact("Currently", when);
        }
    }

    private static IEnumerable<CardFact> Chat(JsonObject item)
    {
        var topic = PyNode.AsString(item["topic"]);
        yield return new CardFact("Chat", !string.IsNullOrEmpty(topic) ? topic : PyNode.AsString(item["chatType"]) switch
        {
            "oneOnOne" => "a one-to-one chat with Margo",
            "meeting" => "a meeting chat",
            _ => "a group chat Margo is in",
        });
    }

    private static IEnumerable<CardFact> Named(string label, JsonObject item, string key)
    {
        yield return new CardFact(label, PyNode.AsString(item[key]) ?? "");
    }

    private static DateTime? Time(JsonNode? time)
    {
        var value = time is JsonObject zoned ? PyNode.AsString(zoned["dateTime"]) : PyNode.AsString(time);
        return value is not null && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static JsonNode? FirstData(JsonNode? data) =>
        (data as JsonObject)?["results"] is JsonArray { Count: > 0 } results ? (results[0] as JsonObject)?["data"] : data;
}
