using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Engine;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Orchestrator;

/// <summary>
/// <c>create_document</c> for one manager turn: creates an Office document in Margo's own OneDrive through Work IQ's
/// Office tools, then shares it with the manager and nobody else. Both run through a guard with the manager's standing
/// permission for documents they ask for (<see cref="WorkIQGuard.AllowsOwnDocuments"/>), so no card is needed; any
/// other recipient, or any further sharing, still needs a card. Creation is never retried: an unclear answer is
/// reported as unknown.
/// </summary>
internal sealed class TurnDocuments(WorkIQGuard guard, string principalId) : IDocuments
{
    /// <summary>At most this many documents in one turn.</summary>
    public const int MaxDocuments = 3;

    /// <summary>The longest outline accepted.</summary>
    public const int MaxOutlineChars = 100_000;

    /// <summary>The note in the sharing email.</summary>
    public const string ShareMessage = "Margo created this document at your request.";

    private int _created;

    /// <summary>The links of the documents created in this turn.</summary>
    public List<string> Links { get; } = [];

    public async Task<JsonObject> CreateAsync(string app, string outline, string? request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(outline);
        if (_created >= MaxDocuments)
        {
            return Answer(GuardStatuses.Refused, "at most " + MaxDocuments + " documents in one turn");
        }

        if (outline.Length > MaxOutlineChars)
        {
            return Answer(GuardStatuses.Refused, "the outline is longer than " + MaxOutlineChars + " characters; split the document");
        }

        _created++;
        var body = new JsonObject { ["appName"] = app, ["taskDescription"] = outline };
        if (!string.IsNullOrWhiteSpace(request))
        {
            body["userRequest"] = request;
        }

        var created = await guard.CallAsync("do_action", new JsonObject
        {
            ["on_data_of"] = Subjects.Self,
            ["actionUrl"] = "/office-agent-mcp/generate_artifact",
            ["jsonBody"] = body.ToJsonString(),
        }, RunModes.Attended, cancellationToken).ConfigureAwait(false);
        if (created.Status == GuardStatuses.Unknown)
        {
            return Answer(GuardStatuses.Unknown, "I don't know whether the document was created; do not try again, and say so");
        }

        if (created.Status != GuardStatuses.Ok)
        {
            return Answer(created.Status, created.Reason ?? "the document was not created");
        }

        if (At(created.Data, "resource_links") is not JsonArray { Count: > 0 } links
            || PyNode.AsString(At(links[0], "uri")) is not { Length: > 0 } link)
        {
            // The tool must return a link to what it made; without one, success is not established.
            return Answer(GuardStatuses.Unknown, "Work IQ answered without a link to a document, so it may or may not exist; do not try again");
        }

        Links.Add(link);
        var name = PyNode.AsString(At(links[0], "name"));
        var item = await guard.CallAsync("fetch", new JsonObject
        {
            ["on_data_of"] = Subjects.Self,
            ["entityUrls"] = new JsonArray("/shares/" + ShareId(link) + "/driveItem?$select=id,parentReference"),
        }, RunModes.Attended, cancellationToken).ConfigureAwait(false);
        var found = FirstData(item.Data);
        if (item.Status != GuardStatuses.Ok || PyNode.AsString(At(found, "id")) is not { Length: > 0 } itemId
            || PyNode.AsString(At(At(found, "parentReference"), "driveId")) is not { Length: > 0 } driveId)
        {
            return Partial(link, name, "created, but the file could not be found to share it (" + (item.Reason ?? item.Status) + ")");
        }

        var shared = await guard.CallAsync("do_action", new JsonObject
        {
            ["on_data_of"] = Subjects.Self,
            ["actionUrl"] = "/drives/" + driveId + "/items/" + itemId + "/invite",
            ["jsonBody"] = new JsonObject
            {
                ["recipients"] = new JsonArray(new JsonObject { ["objectId"] = principalId }),
                ["roles"] = new JsonArray("write"),
                ["requireSignIn"] = true,
                ["sendInvitation"] = true,
                ["message"] = ShareMessage,
            }.ToJsonString(),
        }, RunModes.Attended, cancellationToken).ConfigureAwait(false);
        if (shared.Status != GuardStatuses.Ok)
        {
            var reason = shared.Status == GuardStatuses.Unknown
                ? "created; sharing it got no clear answer, so your manager may or may not have access yet"
                : "created, but not shared (" + (shared.Reason ?? shared.Status) + ")";
            return Partial(link, name, reason);
        }

        var answer = new JsonObject { ["status"] = GuardStatuses.Ok, ["link"] = link, ["shared_with"] = "your manager only, who can edit it" };
        if (name is not null)
        {
            answer["name"] = name;
        }

        return answer;
    }

    /// <summary>Microsoft Graph's sharing ID for a URL: <c>u!</c> and the URL in unpadded base64url.</summary>
    public static string ShareId(string url) =>
        "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static JsonNode? FirstData(JsonNode? data) =>
        At(data, "results") is JsonArray { Count: > 0 } results ? At(results[0], "data") : data;

    internal static JsonNode? At(JsonNode? node, string key) => (node as JsonObject)?[key];

    private static JsonObject Partial(string link, string? name, string reason)
    {
        var answer = new JsonObject { ["status"] = GuardStatuses.Partial, ["link"] = link, ["reason"] = reason };
        if (name is not null)
        {
            answer["name"] = name;
        }

        return answer;
    }

    private static JsonObject Answer(string status, string reason) => new() { ["status"] = status, ["reason"] = reason };
}
