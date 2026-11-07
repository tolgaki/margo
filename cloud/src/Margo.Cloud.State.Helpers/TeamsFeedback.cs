using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Helpers.Python;

namespace Margo.Cloud.State.Helpers;

/// <summary>
/// One root post of the triage view (a row of <c>build()</c> in teams_feedback.py). Fields that come from the
/// message keep their JSON value (any type, as Python keeps it); <see cref="Created"/> is the first ten
/// characters of <c>createdDateTime</c> (or the first ten items when the input put a list there).
/// </summary>
public sealed record TeamsFeedbackRow(
    object? Id,
    object? Created,
    object? Author,
    object? Subject,
    string Text,
    int Reactions,
    int Mentions,
    int Replies,
    IReadOnlyList<object?> Responders,
    object? LastSpeaker,
    object? LastReplyAt,
    string Status,
    bool Answered,
    object? Url)
{
    /// <summary>The row as the ordered dict teams_feedback.py prints with <c>--format json</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> ToPython() =>
    [
        new("id", Id),
        new("created", Created),
        new("author", Author),
        new("subject", Subject),
        new("text", Text),
        new("reactions", (BigInteger)Reactions),
        new("mentions", (BigInteger)Mentions),
        new("replies", (BigInteger)Replies),
        new("responders", Responders),
        new("last_speaker", LastSpeaker),
        new("last_reply_at", LastReplyAt),
        new("status", Status),
        new("answered", Answered),
        new("url", Url),
    ];
}

/// <summary>
/// Port of <c>skills/chief-of-staff/scripts/teams_feedback.py</c>: consolidates Teams channel messages saved
/// from <c>workiq-fetch</c> into a triage view. Every page that yields no usable data is reported as a
/// problem (the result is then partial and the exit status 1), so a failed page never reads as a quiet
/// channel. Message fields are untrusted JSON: each Python operation on them keeps its Python semantics and
/// raises the exception class CPython raises for an unexpected type.
/// </summary>
public static class TeamsFeedback
{
    /// <summary>
    /// The nesting <c>json.load</c> accepts inside <c>iter_messages</c> (a generator resumed by
    /// <c>collect</c>'s loop): two levels below <see cref="PyJson.MaxDepth"/>. Measured with CPython 3.13.
    /// </summary>
    public const int MaxJsonDepth = 9996;

    private static readonly JsonObject EmptyDict = [];

    /// <summary><c>clean(body)</c>: Teams HTML as one readable line.</summary>
    public static string Clean(object? body)
    {
        if (!PyObj.Truthy(body))
        {
            return "";
        }

        if (PyObj.Normalize(body) is not string text)
        {
            throw new PyTypeError("expected string or bytes-like object, got '" + PyObj.TypeName(body) + "'");
        }

        text = ReplaceTags(text, image: true);
        text = ReplaceTags(text, image: false);
        text = PyHtml.Unescape(text);
        return PyRepr.Strip(PyText.CollapseWhitespace(text));
    }

    /// <summary><c>re.sub(r'&lt;img[^&gt;]*&gt;', ' [image] ', text)</c> or <c>re.sub(r'&lt;[^&gt;]+&gt;', ' ', text)</c>.</summary>
    private static string ReplaceTags(string text, bool image)
    {
        var builder = new StringBuilder(text.Length);
        var last = 0;
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf(image ? "<img" : "<", i, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var bodyStart = open + (image ? 4 : 1);
            if (!image && (bodyStart >= text.Length || text[bodyStart] == '>'))
            {
                // [^>]+ needs at least one character before the '>'.
                i = open + 1;
                continue;
            }

            var close = text.IndexOf('>', bodyStart);
            if (close < 0)
            {
                break; // no '>' after this '<': no later '<' has one either
            }

            builder.Append(text, last, open - last).Append(image ? " [image] " : " ");
            last = close + 1;
            i = close + 1;
        }

        builder.Append(text, last, text.Length - last);
        return builder.ToString();
    }

    /// <summary>
    /// <c>iter_messages(paths, problems)</c>: the raw messages of every readable page, lazily, with a problem
    /// recorded for each file or page that yields no data.
    /// </summary>
    public static IEnumerable<object?> IterMessages(IEnumerable<string> paths, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(problems);
        foreach (var path in paths)
        {
            JsonNode? document;
            try
            {
                document = PyJsonLoad.Loads(PyIO.ReadTextFile(path), MaxJsonDepth);
            }
            catch (Exception ex) when (ex is PyOSError or PyJsonDecodeError)
            {
                problems.Add(path + ": unreadable (" + ((PyException)ex).PythonType + ")");
                continue;
            }

            var results = PyObj.Get(document, "results");
            if (!PyObj.Truthy(results))
            {
                problems.Add(path + ": no results array");
                continue;
            }

            var page = 0;
            foreach (var result in PyObj.Iterate(results))
            {
                page++;
                var status = PyObj.Get(result, "statusCode");
                if (status is not null && PyObj.Int(status) >= 400)
                {
                    var error = PyObj.Get(result, "error");
                    if (!PyObj.Truthy(error))
                    {
                        error = PyObj.Or(GetOrEmpty(PyObj.Get(result, "data"), "error"), "");
                    }

                    problems.Add(path + " page " + page.ToString(CultureInfo.InvariantCulture) + ": HTTP "
                                 + PyObj.Str(status) + " " + PyObj.Str(error));
                    continue;
                }

                var data = PyObj.Or(PyObj.Get(result, "data"), EmptyDict);
                if (!PyObj.Contains(data, "value"))
                {
                    problems.Add(path + " page " + page.ToString(CultureInfo.InvariantCulture) + ": no value array");
                    continue;
                }

                foreach (var message in PyObj.Iterate(PyObj.Index(data, "value")))
                {
                    yield return message;
                }
            }
        }
    }

    /// <summary><c>collect(paths, problems)</c>: messages by id, keeping the most recently modified copy.</summary>
    public static PyDict<object?> Collect(IEnumerable<string> paths, List<string> problems)
    {
        var seen = new PyDict<object?>();
        foreach (var message in IterMessages(paths, problems))
        {
            if (PyObj.Ne(PyObj.Get(message, "messageType"), "message"))
            {
                continue;
            }

            if (PyObj.Truthy(PyObj.Get(message, "deletedDateTime")))
            {
                continue;
            }

            var id = PyObj.Get(message, "id");
            if (!seen.TryGetValue(id, out var previous)
                || PyObj.Gt(PyObj.Or(PyObj.Get(message, "lastModifiedDateTime"), ""),
                            PyObj.Or(PyObj.Get(previous, "lastModifiedDateTime"), "")))
            {
                seen.Set(id, message);
            }
        }

        return seen;
    }

    /// <summary><c>build(paths, reply_paths, problems)</c>: one row per root post, ordered by creation date.</summary>
    public static List<TeamsFeedbackRow> Build(IReadOnlyList<string> paths, IReadOnlyList<string> replyPaths, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(replyPaths);
        var roots = Collect(paths, problems);
        var replies = replyPaths.Count > 0 ? Collect(replyPaths, problems) : new PyDict<object?>();

        // Replies can also arrive inline in the root files.
        foreach (var (id, message) in roots.Items())
        {
            if (PyObj.Truthy(PyObj.Get(message, "replyToId")))
            {
                replies.Set(id, message);
                roots.Remove(id);
            }
        }

        var threads = new PyDict<List<object?>>();
        foreach (var (id, message) in replies.Items())
        {
            var parent = PyObj.Get(message, "replyToId");
            if (!PyObj.Truthy(parent))
            {
                // A root-level message that arrived in a reply payload is a thread of its own.
                roots.SetDefault(id, message);
                continue;
            }

            threads.SetDefault(parent, []).Add(message);
        }

        var rows = new List<TeamsFeedbackRow>();
        foreach (var (id, message) in roots.Items())
        {
            var kids = PySort.SortedBy(threads.TryGetValue(id, out var thread) ? thread : [], CreatedKey);
            var author = PyObj.Or(DisplayName(message), "unknown");
            var responders = new List<object?>();
            foreach (var kid in kids)
            {
                var name = DisplayName(kid);
                if (PyObj.Truthy(name) && PyObj.Ne(name, author) && !PyObj.Contains(responders, name))
                {
                    responders.Add(name);
                }
            }

            // Who spoke last decides whether a thread still needs someone (asker-last versus team-last).
            var ordered = PySort.SortedBy(kids, CreatedKey);
            var lastSpeaker = author;
            object? lastReplyAt = "";
            if (ordered.Count > 0)
            {
                var last = ordered[^1];
                lastSpeaker = PyObj.Or(DisplayName(last), author);
                lastReplyAt = PyObj.Or(PyObj.Get(last, "createdDateTime"), "");
            }

            var status = kids.Count == 0 ? "no-reply" : PyObj.Eq(lastSpeaker, author) ? "asker-last" : "team-last";
            var created = PyObj.Slice(PyObj.Or(PyObj.Get(message, "createdDateTime"), ""), 10);
            var subject = PyObj.Or(PyObj.Get(message, "subject"), "");
            var text = Clean(PyObj.Get(PyObj.Or(PyObj.Get(message, "body"), EmptyDict), "content", ""));
            var reactions = PyObj.Len(PyObj.Or(PyObj.Get(message, "reactions"), new List<object?>()));
            var mentions = PyObj.Len(PyObj.Or(PyObj.Get(message, "mentions"), new List<object?>()));
            rows.Add(new TeamsFeedbackRow(id, created, author, subject, text, reactions, mentions, kids.Count, responders,
                                          lastSpeaker, lastReplyAt, status, status == "team-last",
                                          PyObj.Get(message, "webUrl", "")));
        }

        var sorted = rows.ToArray();
        PySort.Sort(sorted, (a, b) => PyObj.Lt(a.Created, b.Created));
        return [.. sorted];
    }

    /// <summary><c>m.get('createdDateTime') or ''</c>, the sort key of replies.</summary>
    private static object? CreatedKey(object? message) => PyObj.Or(PyObj.Get(message, "createdDateTime"), "");

    /// <summary><c>((m.get('from') or {}).get('user') or {}).get('displayName')</c>.</summary>
    private static object? DisplayName(object? message) =>
        GetOrEmpty(GetOrEmpty(PyObj.Get(message, "from"), "user"), "displayName");

    /// <summary><c>(value or {}).get(key)</c>.</summary>
    private static object? GetOrEmpty(object? value, string key) => PyObj.Truthy(value) ? PyObj.Get(value, key) : null;

    /// <summary>The table view's text, or the exception CPython raises while formatting it.</summary>
    internal static string TableLine(TeamsFeedbackRow row)
    {
        var mark = row.Status switch { "no-reply" => "NONE", "asker-last" => "BACK", _ => " ans" };
        var author = PyObj.Slice(row.Author, 18);
        var summary = (PyObj.Truthy(row.Subject) ? Concat(row.Subject, " ") : "") + PyText.Prefix(row.Text, 72);
        return mark + " " + PyObj.Str(row.Created) + " " + PyText.LeftJustify(PyObj.Str(author), 18) + " r:"
               + row.Replies.ToString(CultureInfo.InvariantCulture).PadRight(2) + " x:"
               + row.Reactions.ToString(CultureInfo.InvariantCulture).PadRight(2) + " " + summary;
    }

    /// <summary><c>left + right</c> for a str right operand.</summary>
    private static string Concat(object? left, string right) => PyObj.Normalize(left) switch
    {
        string text => text + right,
        JsonArray or IReadOnlyList<object?> => throw new PyTypeError("can only concatenate list (not \"str\") to list"),
        _ => throw new PyTypeError("unsupported operand type(s) for +: '" + PyObj.TypeName(left) + "' and 'str'"),
    };
}

/// <summary>
/// <c>python3 teams_feedback.py PATH [PATH ...] [--replies [PATH ...]] [--since YYYY-MM-DD] [--format table|json]</c>.
/// Exit status 1 when any page yielded no data (the counts are then partial), otherwise 0.
/// </summary>
public sealed class TeamsFeedbackScript : IStateScript
{
    public string ScriptName => "teams_feedback.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => PyProcess.OnLargeStack(() => Main(args, stdout, stderr)), stderr);

    private int Main(IReadOnlyList<string> argv, TextWriter stdout, TextWriter stderr)
    {
        var parser = new PyArgumentParser(ScriptName);
        parser.AddArgument("paths", nargs: PyNargs.OneOrMore);
        parser.AddArgument("--replies", nargs: PyNargs.ZeroOrMore, @default: new List<object?>());
        parser.AddArgument("--since", help: "only posts created on/after YYYY-MM-DD");
        parser.AddArgument("--format", choices: ["table", "json"], @default: "table");
        var args = parser.ParseArgs(argv, stdout, stderr);

        var problems = new List<string>();
        var rows = TeamsFeedback.Build(args.GetStrings("paths")!, args.GetStrings("replies")!, problems);
        var since = args.GetString("since");
        if (!string.IsNullOrEmpty(since))
        {
            rows = rows.Where(row => PyObj.Ge(row.Created, since)).ToList();
        }

        if (args.GetString("format") == "json")
        {
            PyJsonDump.Dump(stdout, new List<KeyValuePair<string, object?>>
            {
                new("partial", problems.Count > 0),
                new("problems", problems.Cast<object?>().ToList()),
                new("posts", rows.Select(row => (object?)row.ToPython()).ToList()),
            });
            return problems.Count > 0 ? 1 : 0;
        }

        if (problems.Count > 0)
        {
            // Loud, on stdout, above the counts: every number below is derived from what survived.
            PyIO.Print(stdout, "WARNING: " + problems.Count.ToString(CultureInfo.InvariantCulture)
                               + " page(s) yielded no data. COUNTS BELOW ARE PARTIAL.");
            foreach (var problem in problems)
            {
                PyIO.Print(stdout, "  ! " + problem);
            }
        }

        var noReply = rows.Count(row => row.Status == "no-reply");
        var askerLast = rows.Count(row => row.Status == "asker-last");
        PyIO.Print(stdout, string.Format(CultureInfo.InvariantCulture,
            "{0} posts, {1} with no reply, {2} awaiting a follow-up (asker spoke last){3}",
            rows.Count, noReply, askerLast, problems.Count > 0 ? " (PARTIAL)" : ""));
        PyIO.Print(stdout, new string('-', 100));
        foreach (var row in rows)
        {
            PyIO.Print(stdout, TeamsFeedback.TableLine(row));
        }

        return problems.Count > 0 ? 1 : 0;
    }
}
