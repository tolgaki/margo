using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Engine;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Orchestrator;

/// <summary>
/// Margo's channel to her manager for one run (<see cref="IManagerChannel"/>): a Teams message in their 1:1 chat, an
/// email from her own mailbox to them alone, or a file shared with them alone. Everything goes through Work IQ on a
/// guard with the manager's standing permission for what only they receive (<see cref="WorkIQGuard.AllowsManagerChannel"/>);
/// the guard checks that the manager is the only recipient. Nothing is retried: an unclear answer is reported as unknown.
/// </summary>
internal sealed class TurnManagerChannel(
    Func<InstanceBinding, WorkIQGuard> newGuard, InstanceBinding binding, string? managerMail, string mode,
    ConcurrentDictionary<string, string> chatIds) : IManagerChannel
{
    /// <summary>At most this many Teams messages to the manager in one run.</summary>
    public const int MaxMessages = 3;

    /// <summary>At most this many emails to the manager in one run.</summary>
    public const int MaxEmails = 3;

    /// <summary>At most this many files shared with the manager in one run.</summary>
    public const int MaxShares = 5;

    /// <summary>The longest note.</summary>
    public const int MaxNoteChars = WorkIQGuard.MaxManagerMailChars;

    /// <summary>The subject of an email that was given none.</summary>
    public const string DefaultSubject = "From Margo";

    private int _messages;
    private int _emails;
    private int _shares;

    /// <summary>Notes and shares attempted in this run, for the log line (a count only).</summary>
    public int Sent => _messages + _emails + _shares;

    public async Task<JsonObject> TellAsync(string via, string text, string? subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(via);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxNoteChars)
        {
            return Answer(GuardStatuses.Refused, "the note is longer than " + MaxNoteChars + " characters; shorten it or share a document");
        }

        return via == "email" ? await EmailAsync(text, subject, cancellationToken).ConfigureAwait(false)
            : await TeamsAsync(text, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonObject> ShareAsync(string link, string role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (_shares >= MaxShares)
        {
            return Answer(GuardStatuses.Refused, "at most " + MaxShares + " files shared in one run");
        }

        _shares++;
        var guard = newGuard(binding);
        string? itemPath = ItemPath(link);
        if (itemPath is null)
        {
            var item = await guard.CallAsync("fetch", new JsonObject
            {
                ["on_data_of"] = Subjects.Self,
                ["entityUrls"] = new JsonArray("/shares/" + TurnDocuments.ShareId(link) + "/driveItem?$select=id,parentReference"),
            }, mode, cancellationToken).ConfigureAwait(false);
            var found = TurnDocuments.FirstData(item.Data);
            if (item.Status != GuardStatuses.Ok || PyNode.AsString(TurnDocuments.At(found, "id")) is not { Length: > 0 } itemId
                || PyNode.AsString(TurnDocuments.At(TurnDocuments.At(found, "parentReference"), "driveId")) is not { Length: > 0 } driveId)
            {
                return Answer(item.Status == GuardStatuses.Ok ? GuardStatuses.Error : item.Status,
                              "the file could not be found from that link, or you can't open it (" + (item.Reason ?? item.Status) + ")");
            }

            itemPath = "/drives/" + driveId + "/items/" + itemId;
        }

        var shared = await guard.CallAsync("do_action", new JsonObject
        {
            ["on_data_of"] = Subjects.Self,
            ["actionUrl"] = itemPath + "/invite",
            ["jsonBody"] = new JsonObject
            {
                ["recipients"] = new JsonArray(new JsonObject { ["objectId"] = binding.PrincipalId }),
                ["roles"] = new JsonArray(role),
                ["requireSignIn"] = true,
                ["sendInvitation"] = true,
                ["message"] = "Margo shared this with you.",
            }.ToJsonString(),
        }, mode, cancellationToken).ConfigureAwait(false);
        return Outcome(shared, "shared with your manager only");
    }

    private async Task<JsonObject> EmailAsync(string text, string? subject, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(managerMail))
        {
            return Answer(GuardStatuses.Unavailable, "your manager's email address is not in the directory; message them in Teams instead");
        }

        if (_emails >= MaxEmails)
        {
            return Answer(GuardStatuses.Refused, "at most " + MaxEmails + " emails to your manager in one run");
        }

        _emails++;
        var message = new JsonObject
        {
            ["subject"] = string.IsNullOrWhiteSpace(subject) ? DefaultSubject : subject.Length > 255 ? subject[..255] : subject,
            ["body"] = new JsonObject { ["contentType"] = "Text", ["content"] = text },
            ["toRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = managerMail } }),
        };
        var sent = await newGuard(binding).CallAsync("do_action", new JsonObject
        {
            ["on_data_of"] = Subjects.Self,
            ["actionUrl"] = "/me/sendMail",
            ["jsonBody"] = new JsonObject { ["message"] = message, ["saveToSentItems"] = true }.ToJsonString(),
        }, mode, cancellationToken).ConfigureAwait(false);
        return Outcome(sent, "emailed to your manager only");
    }

    private async Task<JsonObject> TeamsAsync(string text, CancellationToken cancellationToken)
    {
        if (mode == RunModes.Attended)
        {
            return Answer(GuardStatuses.Refused, "you are in a chat with your manager: your answer is the message");
        }

        if (_messages >= MaxMessages)
        {
            return Answer(GuardStatuses.Refused, "at most " + MaxMessages + " Teams messages to your manager in one run");
        }

        var chatId = await ChatIdAsync(cancellationToken).ConfigureAwait(false);
        if (chatId is null)
        {
            return Answer(GuardStatuses.Unavailable,
                          "your 1:1 chat with your manager was not found (they may not have chatted with you yet); email them instead");
        }

        _messages++;
        var bound = new InstanceBinding(binding.AgentIdentityId, binding.AgentUserId, binding.PrincipalId, chatId);
        var posted = await newGuard(bound).CallAsync("create_entity", new JsonObject
        {
            ["on_data_of"] = Subjects.Self,
            ["parentUrl"] = "/chats/" + chatId + "/messages",
            ["jsonBody"] = new JsonObject
            {
                ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = Html(text) },
            }.ToJsonString(),
        }, mode, cancellationToken).ConfigureAwait(false);
        return Outcome(posted, "posted in your 1:1 chat with your manager");
    }

    /// <summary>
    /// The Graph ID of the 1:1 chat between Margo and the manager: from <see cref="InstanceBinding.PrincipalChatId"/>, or
    /// looked up once (Margo's chats with the manager as a member) and remembered for this instance.
    /// </summary>
    private async Task<string?> ChatIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(binding.PrincipalChatId))
        {
            return binding.PrincipalChatId;
        }

        if (chatIds.TryGetValue(binding.Account, out var known))
        {
            return known;
        }

        var found = await newGuard(binding).CallAsync("fetch", new JsonObject
        {
            ["on_data_of"] = Subjects.Self,
            ["entityUrls"] = new JsonArray("/me/chats?$filter=members/any(m: m/microsoft.graph.aadUserConversationMember/userId eq '"
                                           + binding.PrincipalId + "')&$expand=members&$select=id,chatType&$top=50"),
        }, mode, cancellationToken).ConfigureAwait(false);
        if (found.Status != GuardStatuses.Ok || TurnDocuments.At(TurnDocuments.FirstData(found.Data), "value") is not JsonArray chats)
        {
            return null;
        }

        foreach (var chat in chats.OfType<JsonObject>())
        {
            if (PyNode.AsString(chat["chatType"]) == "oneOnOne" && PyNode.AsString(chat["id"]) is { Length: > 0 } id
                && chat["members"] is JsonArray members
                && members.OfType<JsonObject>().Any(member => PyNode.AsString(member["userId"]) == binding.PrincipalId))
            {
                chatIds[binding.Account] = id;
                return id;
            }
        }

        return null;
    }

    /// <summary>A drive item path given directly (<c>/drives/{d}/items/{i}</c>), or null for a link to resolve.</summary>
    private static string? ItemPath(string link)
    {
        var segments = link.Split('?')[0].Split('/');
        return segments is ["", "drives", { Length: > 0 }, "items", { Length: > 0 }] ? link.Split('?')[0] : null;
    }

    /// <summary>The note as Teams message HTML: escaped, with line breaks kept.</summary>
    private static string Html(string text) => WebUtility.HtmlEncode(text).Replace("\n", "<br>", StringComparison.Ordinal);

    private static JsonObject Outcome(GuardResult result, string done) => result.Status switch
    {
        GuardStatuses.Ok => new JsonObject { ["status"] = GuardStatuses.Ok, ["result"] = done },
        GuardStatuses.Unknown => Answer(GuardStatuses.Unknown, "no clear answer, so it may or may not have gone out; do not send it again"),
        _ => Answer(result.Status, result.Reason ?? "not sent"),
    };

    private static JsonObject Answer(string status, string reason) => new() { ["status"] = status, ["reason"] = reason };
}
