using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.Guard;
using Margo.Cloud.Orchestrator;
using Margo.Cloud.State.Core;
using Microsoft.Agents.Core.Models;

namespace Margo.Cloud.Host;

/// <summary>
/// The Adaptive Card that asks the manager to approve one action (<see cref="ApprovalCard"/>), and its last version
/// once decided. The card is written for the person deciding, not for engineers: it says in plain words what will
/// happen, to whom, with what text and when, and never shows tool names, paths, identifiers or JSON. Everything shown is
/// read from the exact Work IQ call in the ledger, not from the model's text, and that call is what runs. Every value the
/// model wrote is shown in a <c>RichTextBlock</c>, which renders no Markdown, so a link or emphasis in a draft cannot look
/// different on the card from what is sent.
/// </summary>
public static class ApprovalCards
{
    /// <summary>The attachment content type of an Adaptive Card.</summary>
    public const string ContentType = "application/vnd.microsoft.card.adaptive";

    /// <summary>The longest text value shown; longer text is shortened with a note saying so.</summary>
    public const int MaxSummaryChars = 4000;

    /// <summary>Shown when the card cannot describe the action in plain words.</summary>
    public const string Undescribed = "Margo can't describe this action in plain words. If you're not sure what it does, reject it and ask her in chat.";

    /// <summary>Under the details, before a decision.</summary>
    public const string Footer = "Nothing happens until you approve. Approving does exactly this, once.";

    /// <summary>The card asking for approval, with Approve and Reject.</summary>
    public static Attachment Ask(ApprovalCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var data = new Func<string, JsonObject>(verb => new JsonObject
        {
            ["margo"] = verb,
            ["action_id"] = card.ActionId,
            ["revision"] = card.Revision,
            ["action_hash"] = card.ActionHash,
            // Display only, so the card's last version still says which email or meeting it was.
            ["about"] = new JsonArray([.. card.Context.Select(fact => (JsonNode)new JsonArray(fact.Label, fact.Value))]),
        });
        var actions = new JsonArray
        {
            new JsonObject { ["type"] = "Action.Submit", ["title"] = "Approve", ["style"] = "positive", ["data"] = data(CardDecision.Approve) },
            new JsonObject { ["type"] = "Action.Submit", ["title"] = "Reject", ["style"] = "destructive", ["data"] = data(CardDecision.Reject) },
        };
        return Attachment(Card(card, null), actions);
    }

    /// <summary>The card's last version after a decision: the same content, the outcome, and no buttons.</summary>
    public static Attachment Decided(ApprovalCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return Attachment(Card(card, card.Outcome), []);
    }

    /// <summary>The card body (exposed for tests): a question, whose account it uses, the details, and why.</summary>
    public static JsonArray Card(ApprovalCard card, string? outcome)
    {
        ArgumentNullException.ThrowIfNull(card);
        var body = PyJson.Loads(PyNode.AsString(card.Arguments["jsonBody"]) ?? "{}") as JsonObject ?? [];
        var kind = card.ApprovalKind;
        var items = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = Title(kind), ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
            new JsonObject { ["type"] = "TextBlock", ["text"] = Account(kind, card.OnDataOf == Subjects.Principal), ["isSubtle"] = true, ["wrap"] = true },
        };

        foreach (var fact in card.Context)
        {
            items.Add(Field(fact.Label, fact.Value));
        }

        var fields = Summary(kind, body);
        foreach (var (label, value) in fields)
        {
            items.Add(Field(label, value));
        }

        if (kind == ApprovalKind.Other || fields.Count + card.Context.Count == 0)
        {
            items.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = Undescribed, ["color"] = "Attention", ["wrap"] = true });
        }

        if (!string.IsNullOrEmpty(card.Why))
        {
            items.Add(Field("Why", card.Why));
        }

        if (outcome is null)
        {
            items.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = Footer, ["isSubtle"] = true, ["size"] = "Small", ["wrap"] = true, ["spacing"] = "Medium" });
        }
        else
        {
            items.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = outcome,
                ["weight"] = "Bolder",
                ["wrap"] = true,
                ["spacing"] = "Medium",
                ["color"] = card.OutcomeStatus switch
                {
                    "sent" => "Good",
                    "rejected" or "out_of_date" => "Default",
                    _ => "Attention",
                },
            });
        }

        return items;
    }

    private static string Title(ApprovalKind kind) => kind switch
    {
        ApprovalKind.Email => "Send this email?",
        ApprovalKind.Reply => "Send this reply?",
        ApprovalKind.Forward => "Forward this email?",
        ApprovalKind.MeetingNew => "Set up this meeting?",
        ApprovalKind.MeetingChange => "Change this meeting?",
        ApprovalKind.MeetingCancel => "Cancel this meeting?",
        ApprovalKind.Chat or ApprovalKind.Channel => "Post this Teams message?",
        ApprovalKind.DocumentChange => "Change this document?",
        ApprovalKind.Share => "Share this file?",
        ApprovalKind.Delete => "Delete this?",
        ApprovalKind.Page => "Publish this Page?",
        ApprovalKind.EngagePost => "Post this on Viva Engage?",
        ApprovalKind.SharePointChange => "Make this SharePoint change?",
        ApprovalKind.MeetingListen => "Let Margo follow this meeting's live transcript?",
        _ => "Let Margo do this?",
    };

    private static string Account(ApprovalKind kind, bool principal) => (kind, principal) switch
    {
        (ApprovalKind.Email or ApprovalKind.Reply or ApprovalKind.Forward, true) => "From your mailbox. It will show as sent by Margo on your behalf.",
        (ApprovalKind.MeetingNew or ApprovalKind.MeetingChange or ApprovalKind.MeetingCancel, true) => "On your calendar. Invitations go out from Margo on your behalf.",
        (_, true) => "Using your account, by Margo on your behalf.",
        (ApprovalKind.EngagePost, false) => "Posted by Margo, under her own name.",
        (ApprovalKind.Chat or ApprovalKind.Channel, false) => "From Margo, in a conversation she's part of.",
        _ => "From Margo's own account.",
    };

    private static List<(string Label, string Value)> Summary(ApprovalKind kind, JsonObject body)
    {
        var message = body["message"] as JsonObject ?? body;
        var fields = new List<(string Label, string Value)>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                fields.Add((label, value.Length > MaxSummaryChars
                    ? value[..MaxSummaryChars] + " ... (shortened here; ask Margo in chat to see all of it before you approve)"
                    : value));
            }
        }

        Add("To", People(message["toRecipients"]) ?? People(body["toRecipients"]));
        Add("Cc", People(message["ccRecipients"]));
        Add("Bcc", People(message["bccRecipients"]));
        Add(kind == ApprovalKind.Share ? "Share with" : "Invited", People(body["attendees"]) ?? People(body["recipients"]));
        if (kind == ApprovalKind.Share && body["roles"] is JsonArray roles)
        {
            Add("They can", roles.Any(role => PyNode.AsString(role) == "write") ? "view and edit it" : "view it");
        }

        Add("Subject", PyNode.AsString(message["subject"]));
        Add(kind == ApprovalKind.MeetingChange ? "New time" : "When", CardContext.When(body["start"], body["end"]));
        Add("Where", PyNode.AsString(body["location"]?["displayName"]));
        if (body["isOnlineMeeting"] is JsonValue online && online.TryGetValue<bool>(out var isOnline) && isOnline)
        {
            Add("Online", "Teams meeting");
        }

        if (kind == ApprovalKind.Chat)
        {
            Add("Posted in", "a Teams chat Margo is in");
        }
        else if (kind == ApprovalKind.Channel)
        {
            Add("Posted in", "a Teams channel Margo is in");
        }

        Add(kind == ApprovalKind.MeetingCancel ? "Note to attendees" : "Note", PyNode.AsString(body["comment"]) ?? PyNode.AsString(body["message"]));
        Add(kind is ApprovalKind.MeetingNew or ApprovalKind.MeetingChange ? "Details" : "Message", Text(message["body"] ?? body["body"]));
        Add("Attachments", Names(message["attachments"]));
        if (kind is ApprovalKind.Page or ApprovalKind.EngagePost)
        {
            Add("Title", PyNode.AsString(body["title"]));
            Add("Text", PyNode.AsString(body["content"]) ?? PyNode.AsString(body["text"]));
        }

        if (kind == ApprovalKind.DocumentChange)
        {
            Add("Document", PyNode.AsString(body["artifactUrl"]));
            Add("Change", PyNode.AsString(body["taskDescription"]));
        }

        if (kind == ApprovalKind.MeetingChange)
        {
            Add("What changes", string.Join(", ", body.Select(pair => Friendly(pair.Key)).Distinct(StringComparer.Ordinal)));
        }

        return fields;
    }

    private static string Friendly(string key) => key switch
    {
        "start" or "end" => "time",
        "attendees" => "who's invited",
        "subject" => "title",
        "location" => "place",
        "body" => "details",
        "isOnlineMeeting" or "onlineMeetingProvider" => "Teams link",
        "recurrence" => "how often it repeats",
        "isReminderOn" or "reminderMinutesBeforeStart" => "reminder",
        "showAs" => "how it shows on the calendar",
        "sensitivity" => "privacy",
        _ => "other settings",
    };

    private static string? People(JsonNode? recipients)
    {
        if (recipients is not JsonArray list || list.Count == 0)
        {
            return null;
        }

        return string.Join("; ", list.Select(item =>
        {
            var address = PyNode.AsString(item?["emailAddress"]?["address"]) ?? PyNode.AsString(item?["email"]);
            var name = PyNode.AsString(item?["emailAddress"]?["name"]);
            if (address is null)
            {
                return item?["objectId"] is not null ? "a person in your organization" : "someone Margo couldn't name";
            }

            return string.IsNullOrEmpty(name) ? address : name + " (" + address + ")";
        }));
    }

    private static string? Names(JsonNode? attachments) =>
        attachments is JsonArray { Count: > 0 } list
            ? string.Join("; ", list.Select(item => PyNode.AsString(item?["name"]) ?? "an unnamed file"))
            : null;

    /// <summary>The text of a message body; HTML is shown as plain text, so what the reader sees is the words sent.</summary>
    private static string? Text(JsonNode? content)
    {
        var text = PyNode.AsString(content?["content"]) ?? PyNode.AsString(content);
        if (text is null)
        {
            return null;
        }

        if (!string.Equals(PyNode.AsString(content?["contentType"]), "html", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        text = Regex.Replace(text, @"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\b[^>]*>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"<(script|style)\b.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, "<[^>]*>", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\n\s*\n\s*\n+", "\n\n", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
    }

    private static Attachment Attachment(JsonArray body, JsonArray actions)
    {
        var card = new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["body"] = body,
            ["actions"] = actions,
        };
        return new Attachment { ContentType = ContentType, Content = JsonSerializer.Deserialize<JsonElement>(card.ToJsonString()) };
    }

    private static JsonObject Field(string label, string value) => new()
    {
        ["type"] = "RichTextBlock",
        ["inlines"] = new JsonArray
        {
            new JsonObject { ["type"] = "TextRun", ["text"] = label + ": ", ["weight"] = "Bolder" },
            new JsonObject { ["type"] = "TextRun", ["text"] = value },
        },
    };
}
