using System.Text.Json;
using System.Text.Json.Nodes;
using Margo.Cloud.Guard;
using Margo.Cloud.Orchestrator;
using Margo.Cloud.State.Core;
using Microsoft.Agents.Core.Models;

namespace Margo.Cloud.Host;

/// <summary>
/// The Adaptive Card that asks the manager to approve one action (<see cref="ApprovalCard"/>), and its last version
/// once decided. Everything shown comes from the exact Work IQ call in the ledger: the readable summary is parsed from
/// that call and the exact request is shown in full beneath it. Every value the model wrote is shown in a
/// <c>RichTextBlock</c>, which renders no Markdown, so a link or emphasis in a draft cannot look different on the card
/// from what is sent.
/// </summary>
public static class ApprovalCards
{
    /// <summary>The attachment content type of an Adaptive Card.</summary>
    public const string ContentType = "application/vnd.microsoft.card.adaptive";

    /// <summary>The longest text value shown in the summary; the exact request below is never shortened.</summary>
    public const int MaxSummaryChars = 4000;

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

    /// <summary>The card body (exposed for tests): a title, who it comes from, the summary, why, and the exact request.</summary>
    public static JsonArray Card(ApprovalCard card, string? outcome)
    {
        ArgumentNullException.ThrowIfNull(card);
        var body = PyJson.Loads(PyNode.AsString(card.Arguments["jsonBody"]) ?? "{}") as JsonObject ?? [];
        var path = PathOf(card.Arguments) ?? "";
        var lower = path.ToLowerInvariant();
        var items = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = Title(card.Tool, lower), ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = card.OnDataOf == Subjects.Principal
                    ? "From your mailbox and calendar, sent by Margo on your behalf."
                    : "From Margo's own account.",
                ["isSubtle"] = true,
                ["wrap"] = true,
            },
        };

        foreach (var (label, value) in Summary(card.Tool, lower, body))
        {
            items.Add(Field(label, value));
        }

        if (!string.IsNullOrEmpty(card.Why))
        {
            items.Add(Field("Why", card.Why));
        }

        items.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = "Exact request", ["weight"] = "Bolder", ["spacing"] = "Medium" });
        items.Add(Verbatim(card.Tool + " " + path + (body.Count > 0 ? "\n" + Pretty(body) : "")));
        items.Add(new JsonObject
        {
            ["type"] = "TextBlock",
            ["text"] = "Action " + card.ActionId + ", version " + card.Revision + ". Approving runs exactly this, once.",
            ["isSubtle"] = true,
            ["size"] = "Small",
            ["wrap"] = true,
        });
        if (outcome is not null)
        {
            items.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = outcome,
                ["weight"] = "Bolder",
                ["wrap"] = true,
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

    private static string? PathOf(JsonObject arguments) =>
        PyNode.AsString(arguments["actionUrl"]) ?? PyNode.AsString(arguments["parentUrl"]) ?? PyNode.AsString(arguments["entityUrl"]);

    private static string Title(string tool, string path) => tool switch
    {
        "do_action" when path.EndsWith("/sendmail", StringComparison.Ordinal) => "Send this email?",
        "do_action" when path.EndsWith("/reply", StringComparison.Ordinal) || path.EndsWith("/replyall", StringComparison.Ordinal) => "Send this reply?",
        "do_action" when path.EndsWith("/forward", StringComparison.Ordinal) => "Forward this email?",
        "do_action" when path.EndsWith("/cancel", StringComparison.Ordinal) => "Cancel this meeting?",
        "create_entity" when path.EndsWith("/events", StringComparison.Ordinal) => "Set up this meeting?",
        "update_entity" when path.Contains("/events/", StringComparison.Ordinal) => "Change this meeting?",
        "delete_entity" => "Delete this?",
        "create_entity" when path.EndsWith("/messages", StringComparison.Ordinal) && (path.StartsWith("/chats/", StringComparison.Ordinal)
                                                                                   || path.StartsWith("/teams/", StringComparison.Ordinal)) => "Post this Teams message?",
        _ => "Approve this action?",
    };

    private static List<(string Label, string Value)> Summary(string tool, string path, JsonObject body)
    {
        var message = body["message"] as JsonObject ?? body;
        var fields = new List<(string Label, string Value)>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                fields.Add((label, value.Length > MaxSummaryChars ? value[..MaxSummaryChars] + " [continues in the exact request]" : value));
            }
        }

        Add("To", Addresses(message["toRecipients"]));
        Add("Cc", Addresses(message["ccRecipients"]));
        Add("Bcc", Addresses(message["bccRecipients"]));
        Add("Attendees", Addresses(body["attendees"]));
        Add("Subject", PyNode.AsString(message["subject"]));
        Add("Starts", When(body["start"]));
        Add("Ends", When(body["end"]));
        Add("Where", PyNode.AsString(body["location"]?["displayName"]));
        if (body["isOnlineMeeting"] is JsonValue online && online.TryGetValue<bool>(out var isOnline) && isOnline)
        {
            Add("Online", "Teams meeting");
        }

        if (tool == "create_entity" && (path.StartsWith("/chats/", StringComparison.Ordinal) || path.StartsWith("/teams/", StringComparison.Ordinal)))
        {
            Add("Posted in", path.StartsWith("/chats/", StringComparison.Ordinal) ? "a chat Margo is in" : "a channel Margo is in");
        }

        Add("Message", PyNode.AsString(body["comment"]));
        Add("Text", PyNode.AsString(message["body"]?["content"]));
        if (tool == "update_entity")
        {
            Add("Changes", string.Join(", ", body.Select(pair => pair.Key)));
        }

        return fields;
    }

    private static string? Addresses(JsonNode? recipients)
    {
        if (recipients is not JsonArray list || list.Count == 0)
        {
            return null;
        }

        return string.Join("; ", list.Select(item =>
        {
            var address = PyNode.AsString(item?["emailAddress"]?["address"]) ?? "?";
            var name = PyNode.AsString(item?["emailAddress"]?["name"]);
            return string.IsNullOrEmpty(name) ? address : name + " <" + address + ">";
        }));
    }

    private static string? When(JsonNode? time) =>
        PyNode.AsString(time?["dateTime"]) is { } dateTime ? dateTime + " (" + (PyNode.AsString(time?["timeZone"]) ?? "no time zone") + ")" : null;

    private static string Pretty(JsonObject body) => body.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private static JsonObject Field(string label, string value) => new()
    {
        ["type"] = "RichTextBlock",
        ["inlines"] = new JsonArray
        {
            new JsonObject { ["type"] = "TextRun", ["text"] = label + ": ", ["weight"] = "Bolder" },
            new JsonObject { ["type"] = "TextRun", ["text"] = value },
        },
    };

    private static JsonObject Verbatim(string text) => new()
    {
        ["type"] = "RichTextBlock",
        ["inlines"] = new JsonArray(new JsonObject { ["type"] = "TextRun", ["text"] = text, ["fontType"] = "Monospace", ["size"] = "Small" }),
    };
}
