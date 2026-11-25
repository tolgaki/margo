using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.Core;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Orchestrator;

/// <summary>What one listening pass found and did (counts only).</summary>
/// <param name="Status"><c>listened</c>, <c>quiet</c> (nothing new), <c>skipped</c> (off, over the daily limit, or no
/// manager) or <c>failed</c>.</param>
/// <param name="Rooms">Rooms with new messages.</param>
/// <param name="Messages">New messages read.</param>
/// <param name="Notes">Notes to the manager attempted.</param>
public sealed record RoomPass(string Status, int Rooms = 0, int Messages = 0, int Notes = 0);

/// <summary>One message someone wrote in a room Margo is in, as plain text.</summary>
internal sealed record RoomMessage(DateTimeOffset At, string Sender, string Text);

/// <summary>A room with new messages: a group chat, a meeting chat or a channel.</summary>
internal sealed record Room(string Kind, string Name, string Id, List<RoomMessage> Messages);

/// <summary>
/// Reads the rooms Margo is a member of (group and meeting chats, channels of teams she is in) through Work IQ with her
/// own token, for what was said since the last pass. Bounded: <see cref="MaxRooms"/> rooms, a page of each, at most
/// <see cref="MaxChars"/> characters in all. One-to-one chats are not rooms: messages to Margo there arrive on their own.
/// </summary>
internal sealed partial class RoomReader(WorkIQGuard guard, string agentUserId)
{
    /// <summary>At most this many rooms are read in one pass.</summary>
    public const int MaxRooms = 8;

    /// <summary>At most this many characters of one message.</summary>
    public const int MaxMessageChars = 600;

    /// <summary>At most this many characters in all.</summary>
    public const int MaxChars = 15_000;

    /// <summary>At most this many teams and channels are looked at.</summary>
    public const int MaxTeams = 3;

    public const int MaxChannels = 6;

    public async Task<List<Room>> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var rooms = new List<Room>();
        var chats = await FetchAsync("/me/chats?$expand=lastMessagePreview&$orderby=lastMessagePreview/createdDateTime desc&$top=20",
                                     cancellationToken).ConfigureAwait(false);
        foreach (var chat in (chats?["value"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (rooms.Count >= MaxRooms)
            {
                break;
            }

            var type = PyNode.AsString(chat["chatType"]);
            var last = Time(chat["lastMessagePreview"]?["createdDateTime"]);
            if (type is not ("group" or "meeting") || PyNode.AsString(chat["id"]) is not { Length: > 0 } id || last is null || last <= since)
            {
                continue;
            }

            var page = await FetchAsync("/chats/" + id + "/messages?$top=10&$orderby=createdDateTime desc", cancellationToken).ConfigureAwait(false);
            var messages = Messages(page, since);
            if (messages.Count > 0)
            {
                var name = PyNode.AsString(chat["topic"]) is { Length: > 0 } topic ? topic : type == "meeting" ? "a meeting chat" : "a group chat";
                rooms.Add(new Room(type == "meeting" ? "meeting chat" : "group chat", name, id, messages));
            }
        }

        var teams = await FetchAsync("/me/joinedTeams?$select=id,displayName", cancellationToken).ConfigureAwait(false);
        var channels = 0;
        foreach (var team in (teams?["value"] as JsonArray ?? []).OfType<JsonObject>().Take(MaxTeams))
        {
            if (PyNode.AsString(team["id"]) is not { Length: > 0 } teamId)
            {
                continue;
            }

            var list = await FetchAsync("/teams/" + teamId + "/channels?$select=id,displayName", cancellationToken).ConfigureAwait(false);
            foreach (var channel in (list?["value"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (rooms.Count >= MaxRooms || channels >= MaxChannels || PyNode.AsString(channel["id"]) is not { Length: > 0 } channelId)
                {
                    break;
                }

                channels++;
                var page = await FetchAsync("/teams/" + teamId + "/channels/" + channelId + "/messages?$top=10", cancellationToken).ConfigureAwait(false);
                var messages = Messages(page, since);
                if (messages.Count > 0)
                {
                    rooms.Add(new Room("channel", (PyNode.AsString(team["displayName"]) ?? "a team") + " / " + (PyNode.AsString(channel["displayName"]) ?? "a channel"),
                                       teamId + "/" + channelId, messages));
                }
            }
        }

        return rooms;
    }

    /// <summary>The rooms as the text an observation run is given: each room, then its new messages, oldest first.</summary>
    public static string Digest(IReadOnlyList<Room> rooms)
    {
        var text = new StringBuilder();
        foreach (var room in rooms)
        {
            var header = "## " + room.Name + " (" + room.Kind + ", " + room.Id + ")\n";
            if (text.Length + header.Length > MaxChars)
            {
                break;
            }

            text.Append(header);
            foreach (var message in room.Messages.OrderBy(message => message.At))
            {
                var line = "- " + message.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC, " + message.Sender + ": " + message.Text + "\n";
                if (text.Length + line.Length > MaxChars)
                {
                    text.Append("- [more messages not shown]\n");
                    break;
                }

                text.Append(line);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private async Task<JsonObject?> FetchAsync(string path, CancellationToken cancellationToken)
    {
        var result = await guard.CallAsync("fetch", new JsonObject { ["on_data_of"] = Subjects.Self, ["entityUrls"] = new JsonArray(path) },
                                           RunModes.Unattended, cancellationToken).ConfigureAwait(false);
        if (result.Status != GuardStatuses.Ok)
        {
            return null;
        }

        return (TurnDocuments.FirstData(result.Data) ?? result.Data) as JsonObject;
    }

    private List<RoomMessage> Messages(JsonObject? page, DateTimeOffset since)
    {
        var found = new List<RoomMessage>();
        foreach (var message in (page?["value"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var at = Time(message["createdDateTime"]);
            var sender = message["from"]?["user"];
            if (at is null || at <= since || PyNode.AsString(message["messageType"]) is { } kind && kind != "message"
                || PyNode.AsString(sender?["id"]) == agentUserId || message["deletedDateTime"] is JsonValue)
            {
                continue;
            }

            var text = Plain(PyNode.AsString(message["body"]?["content"]) ?? "");
            if (text.Length == 0)
            {
                continue;
            }

            found.Add(new RoomMessage(at.Value, PyNode.AsString(sender?["displayName"]) ?? "someone",
                                      text.Length > MaxMessageChars ? text[..MaxMessageChars] + " [cut]" : text));
        }

        return found;
    }

    private static DateTimeOffset? Time(JsonNode? value) =>
        PyNode.AsString(value) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                                                                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;

    /// <summary>A Teams message body (HTML) as one line of plain text.</summary>
    private static string Plain(string html)
    {
        var text = Tags().Replace(html, " ");
        return Spaces().Replace(WebUtility.HtmlDecode(text), " ").Trim();
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.None, 1000)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+", RegexOptions.None, 1000)]
    private static partial Regex Spaces();
}
