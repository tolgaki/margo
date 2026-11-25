using System.Collections.Frozen;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.Guard;

/// <summary>
/// What a receipt (and a refusal reason) may say about text the model chose. Receipts are logged by the host, so
/// they hold resource <em>types</em>, never identifiers, addresses, file names, search terms or other free text.
/// No Python equivalent: <c>guard.py</c> recorded the path as given (see PORTING.md, "Privacy and hardening").
/// </summary>
/// <remarks>
/// The rule is an allow-list, so it fails closed: a path segment is kept only when it is one of the fixed Microsoft
/// Graph resource words in <see cref="Words"/>; anything else is an identifier and becomes a placeholder.
/// </remarks>
public static class ReceiptText
{
    /// <summary>Stands for a segment that is not a known resource word (an ID, an alias, free text).</summary>
    public const string Id = "{id}";

    /// <summary>Stands for the segment after <c>users</c>: an object ID, a user principal name or an address.</summary>
    public const string User = "{user}";

    /// <summary>Stands for a drive or site path addressed by name (<c>root:/Folder/File.docx:</c>).</summary>
    public const string ItemPath = "{path}";

    /// <summary>Stands for a function or key segment whose name is not a known resource word.</summary>
    public const string Function = "{function}";

    /// <summary>Stands for a label (tool, run mode, subject) that is not a plain identifier.</summary>
    public const string Other = "{other}";

    private const string GraphNamespace = "microsoft.graph.";
    private const int MaxLabel = 64;
    private const int MaxDetail = 160;
    private const int MaxName = 32;
    private const int MaxNames = 8;

    /// <summary>
    /// The Microsoft Graph resource, navigation, action and well-known-folder words a receipt may show, lower-cased.
    /// A word missing here only makes a receipt less specific (<see cref="Id"/>); it never changes what is allowed.
    /// </summary>
    public static IReadOnlySet<string> Words { get; } = new[]
    {
        // roots and directory
        "me", "users", "groups", "sites", "drives", "chats", "teams", "planner", "search", "communications", "directory",
        "organization", "contacts", "devices", "applications", "serviceprincipals", "authentication", "methods", "places",
        "shares", "subscriptions", "solutions", "security", "reports", "admin", "identity", "education",
        "manager", "directreports", "people", "contactfolders", "photo", "photos", "presence", "memberof",
        "transitivememberof", "ownedobjects", "owneddevices", "createdobjects", "registereddevices", "licensedetails",
        "insights", "trending", "used", "shared", "profile", "settings", "activities", "approleassignments",
        "user", "group", "team", "chat", "site", "message", "event",
        // mail
        "messages", "mailfolders", "childfolders", "attachments", "inbox", "drafts", "sentitems", "deleteditems", "junkemail",
        "archive", "outbox", "msgfolderroot", "searchfolders", "conversationhistory", "clutter", "scheduled", "messagerules",
        "inferenceclassification", "overrides", "mailboxsettings", "outlook", "mastercategories", "extensions", "mentions",
        "send", "sendmail", "reply", "replyall", "forward", "createreply", "createreplyall", "createforward", "move", "copy",
        "delta",
        // calendar
        "calendar", "calendars", "calendargroups", "calendarview", "events", "instances", "accept", "decline",
        "tentativelyaccept", "cancel", "dismissreminder", "snoozereminder", "getschedule", "findmeetingtimes", "reminderview",
        "calendarpermissions", "exceptionoccurrences", "onlinemeetings", "transcripts", "recordings", "content",
        "metadatacontent", "attendancereports", "attendancerecords",
        // Teams
        "joinedteams", "channels", "primarychannel", "allchannels", "incomingchannels", "replies", "members", "tabs",
        "installedapps", "pinnedmessages", "hostedcontents", "filesfolder", "schedule", "shifts", "operations",
        "permissiongrants", "getallmessages", "softdelete", "undosoftdelete", "setreaction", "unsetreaction",
        "lastmessagepreview", "teamwork", "associatedteams", "template", "tags",
        // files and sites
        "drive", "root", "items", "children", "thumbnails", "versions", "permissions", "sharedwithme", "recent", "special",
        "following", "bundles", "list", "lists", "listitem", "fields", "columns", "contenttypes", "driveitem", "analytics",
        "workbook", "worksheets", "tables", "rows", "range", "usedrange", "names", "charts", "comments", "createlink",
        "invite", "preview", "checkin", "checkout", "createuploadsession", "restore", "follow", "unfollow", "getbypath",
        "getactivitiesbyinterval", "lastmodifiedbyuser", "createdbyuser", "pages", "sitepages", "webparts", "onenote",
        "notebooks", "sections", "sectiongroups", "parentnotebook", "parentsection", "documents", "approot", "cameraroll",
        "music", "termstore", "getallsites",
        // planner and to-do
        "plans", "buckets", "tasks", "details", "todo", "linkedresources", "checklistitems",
        // Work IQ path families
        "office-agent-mcp", "generate_artifact", "edit_artifact", "engage", "sharepoint", "copilot", "communications",
        "realtimeactivityfeed", "multiactivitysubscriptions", "create_page", "edit_page", "onlinemeetings", "transcripts",
        "recordings", "content",
        // OData
        "$value", "$count", "$ref", "$batch", "$metadata",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The privacy-safe form of a resource path: resource words stay, every identifier becomes <see cref="Id"/>
    /// (<see cref="User"/> after <c>users</c>), a path addressed by name becomes <see cref="ItemPath"/>, function
    /// or key arguments become <c>name(...)</c>, and a query string is dropped.
    /// </summary>
    /// <example><c>/users/dana@example.com/messages/AAMk=</c> gives <c>/users/{user}/messages/{id}</c>;
    /// <c>/me/drive/root:/Plans/Budget.xlsx:/content</c> gives <c>/me/drive/root:/{path}:/content</c>;
    /// <c>/me/drive/root/search(q='budget')</c> gives <c>/me/drive/root/search(...)</c>.</example>
    public static string PathTemplate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var query = path.IndexOf('?', StringComparison.Ordinal);
        var segments = (query < 0 ? path : path[..query]).Split('/');
        var output = new List<string>(segments.Length);
        var byName = false; // inside "root:/.../name:", where every segment is a folder or file name
        var named = false;
        string? previous = null;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0 && (i == 0 || i == segments.Length - 1))
            {
                output.Add(string.Empty); // the leading slash, and a trailing one
                continue;
            }

            var colon = segment.EndsWith(':');
            if (byName)
            {
                named = !colon;
                if (colon)
                {
                    output.Add(ItemPath + ":");
                    byName = false;
                }

                continue;
            }

            var core = colon ? segment[..^1] : segment;
            output.Add(Segment(core, previous) + (colon ? ":" : string.Empty));
            previous = core.All(char.IsAscii) ? core.ToLowerInvariant() : null;
            byName = colon;
        }

        if (byName && named)
        {
            var trailing = output.Count > 1 && output[^1].Length == 0;
            output.Insert(trailing ? output.Count - 1 : output.Count, ItemPath);
        }

        return string.Join('/', output);
    }

    /// <summary>The first path segment for a refusal reason: the word when it is a known resource word, else a placeholder.</summary>
    internal static string Root(string segment) => Segment(segment.EndsWith(':') ? segment[..^1] : segment, null);

    /// <summary>
    /// A label the caller supplied (tool, run mode): kept when it is a plain identifier of at most 64 ASCII letters,
    /// digits, <c>_</c> or <c>-</c> (or empty), else <see cref="Other"/>.
    /// </summary>
    internal static string? Label(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return text.Length <= MaxLabel && text.All(IsLabelCharacter) ? text : Other;
    }

    /// <summary><c>on_data_of</c> for a receipt: one of the subject words (or empty), else <see cref="Other"/>.</summary>
    internal static string? Subject(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return text.Length == 0 || text is Subjects.Principal or Subjects.Self or Subjects.Margo ? text : Other;
    }

    /// <summary>
    /// The unexpected argument names for a refusal reason: a name that is a plain identifier (ASCII letters, digits
    /// and <c>_</c>, at most 32 characters) is shown, up to eight of them; any other name is counted, never quoted.
    /// </summary>
    internal static string ArgumentNames(IReadOnlyList<string> names)
    {
        var shown = names.Where(IsIdentifier).Take(MaxNames).ToList();
        var others = names.Count - shown.Count;
        if (others > 0)
        {
            shown.Add(others == 1 ? "1 other name" : others + " other names");
        }

        return string.Join(", ", shown);
    }

    /// <summary>
    /// Detail from trusted code (a token provider's or the journal's message) for a reason: printable ASCII only,
    /// at most 160 characters.
    /// </summary>
    internal static string Detail(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var clipped = text.Length > MaxDetail ? text[..MaxDetail] : text;
        return clipped.All(IsPrintableAscii) ? clipped : string.Create(clipped.Length, clipped, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = IsPrintableAscii(source[i]) ? source[i] : '?';
            }
        });
    }

    private static string Segment(string core, string? previous)
    {
        var open = core.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0)
        {
            var arguments = open == core.Length - 2 && core[^1] == ')' ? "()" : "(...)";
            var name = core[..open];
            return (IsWord(name) ? name : Function) + arguments;
        }

        if (IsWord(core))
        {
            return core;
        }

        return previous == "users" ? User : Id;
    }

    private static bool IsWord(string text)
    {
        if (!text.All(char.IsAscii))
        {
            return false;
        }

        var lowered = text.ToLowerInvariant();
        if (lowered.StartsWith(GraphNamespace, StringComparison.Ordinal))
        {
            lowered = lowered[GraphNamespace.Length..];
        }

        return Words.Contains(lowered);
    }

    private static bool IsIdentifier(string text) =>
        text.Length is > 0 and <= MaxName && text.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private static bool IsLabelCharacter(char ch) =>
        ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';

    private static bool IsPrintableAscii(char ch) => ch is >= ' ' and <= '~';
}
