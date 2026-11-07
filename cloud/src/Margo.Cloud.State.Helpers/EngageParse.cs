using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Helpers.Python;

namespace Margo.Cloud.State.Helpers;

/// <summary>A footnote of the retrieval markdown: <c>[^h1]: "Title" — https://...</c>.</summary>
public readonly record struct EngageFootnote(string Ref, string Title, string Url);

/// <summary>
/// One Viva Engage row of <c>load_hits()</c> in engage_parse.py. Fields copied from the hit keep their JSON
/// value (any type, as Python keeps it).
/// </summary>
public sealed record EngageRow(
    string Title,
    string Url,
    object? Group,
    object? Type,
    object? Author,
    object? Created,
    object? Updated,
    BigInteger ActiveDays,
    object? Upvotes,
    bool Answered,
    object? AnsweredBy,
    object? Topics,
    object? Body,
    bool Salvaged,
    bool Linked)
{
    /// <summary>The row as the ordered dict engage_parse.py prints with <c>--format json</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> ToPython() =>
    [
        new("title", Title),
        new("url", Url),
        new("group", Group),
        new("type", Type),
        new("author", Author),
        new("created", Created),
        new("updated", Updated),
        new("active_days", ActiveDays),
        new("upvotes", Upvotes),
        new("answered", Answered),
        new("answered_by", AnsweredBy),
        new("topics", Topics),
        new("body", Body),
        new("salvaged", Salvaged),
        new("linked", Linked),
    ];
}

/// <summary>
/// Port of <c>skills/chief-of-staff/scripts/engage_parse.py</c>: parses <c>workiq-retrieve</c> markdown into
/// Viva Engage rows. The script's regular expressions are matched by hand with CPython's semantics for
/// <c>str</c> patterns (Unicode <c>\d</c>, <c>\s</c>, <c>\w</c> from CPython 3.13's tables, code points
/// rather than UTF-16 units), and malformed blobs are salvaged field by field, never dropped.
/// </summary>
public static class EngageParse
{
    private const string SourceJsonMarker = "**sourceJson:**";

    private static readonly (string Key, bool IsCount)[] Fields =
    [
        ("GroupName", false),
        ("Type", false),
        ("SenderName", false),
        ("CreatedAt", false),
        ("UpdatedAt", false),
        ("EncodedId", false),
        ("PostUpvoteCount", true),
        ("BestAnswerAuthorName", false),
    ];

    /// <summary><c>unescape(raw)</c>: collapse doubled backslashes, then undo the markdown escapes.</summary>
    public static string Unescape(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var collapsed = raw.Replace("\\\\", "\\", StringComparison.Ordinal);
        return collapsed.Replace("\\[", "[", StringComparison.Ordinal).Replace("\\]", "]", StringComparison.Ordinal)
                        .Replace("\\_", "_", StringComparison.Ordinal);
    }

    /// <summary><c>salvage(raw)</c>: the flat scalar fields of a blob that does not parse, marked <c>_salvaged</c>.</summary>
    public static JsonObject Salvage(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var result = new JsonObject();
        foreach (var (key, isCount) in Fields)
        {
            if (isCount)
            {
                if (SearchDigits(raw, "\"" + key + "\":") is { } digits)
                {
                    result[key] = PyJson.Int(PyArgumentParser.ParsePyInt(digits));
                }
            }
            else if (SearchQuoted(raw, "\"" + key + "\":\"") is { } value)
            {
                result[key] = value;
            }
        }

        result["PostBody"] = SearchBody(raw) ?? "";
        result["_salvaged"] = true;
        if (!result.ContainsKey("ScopeType"))
        {
            result["ScopeType"] = raw.Contains("YAMMER_GROUP", StringComparison.Ordinal) ? "YAMMER_GROUP" : "";
        }

        return result;
    }

    /// <summary><c>_encoded_key(value)</c>: <c>unquote(value or '').rstrip('=')</c>.</summary>
    public static string EncodedKey(object? value)
    {
        var text = PyObj.Or(value, "");
        if (PyObj.Normalize(text) is string str)
        {
            return PyText.RStrip(PyUrl.Unquote(str), "=");
        }

        // unquote() of a non-str: "'%' not in x" first, then x.split (no '%') or re.finditer (a '%').
        if (PyObj.Contains(text, "%"))
        {
            throw new PyTypeError("expected string or bytes-like object, got '" + PyObj.TypeName(text) + "'");
        }

        throw new PyAttributeError("'" + PyObj.TypeName(text) + "' object has no attribute 'split'");
    }

    /// <summary><c>_daygap(created, updated)</c>: days between two YYYY-MM-DD values, 0 when either is not a date.</summary>
    public static BigInteger DayGap(object? created, object? updated)
    {
        if (ParseDate(created) is { } start && ParseDate(updated) is { } end)
        {
            return end.ToOrdinal() - start.ToOrdinal();
        }

        return BigInteger.Zero;
    }

    private static PyDate? ParseDate(object? value)
    {
        // date(*[int(x) for x in value.split('-')]); any exception means 0.
        if (PyObj.Normalize(value) is not string text)
        {
            return null;
        }

        var parts = text.Split('-');
        if (parts.Length != 3)
        {
            return null;
        }

        var numbers = new BigInteger[3];
        for (var i = 0; i < 3; i++)
        {
            if (!PyArgumentParser.TryParsePyInt(parts[i], out numbers[i]))
            {
                return null;
            }
        }

        if (numbers.Any(number => number < int.MinValue || number > int.MaxValue))
        {
            return null;
        }

        try
        {
            return new PyDate((int)numbers[0], (int)numbers[1], (int)numbers[2]);
        }
        catch (PyException)
        {
            return null;
        }
    }

    /// <summary><c>FOOTNOTE.findall(text)</c>: <c>^\[\^(h\d+)\]:\s*"(.*?)"\s*[—-]\s*(\S+)</c> with <c>re.M</c>.</summary>
    public static List<EngageFootnote> Footnotes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<EngageFootnote>();
        var position = 0;
        while (position <= text.Length)
        {
            var start = position;
            if (start > 0 && text[start - 1] != '\n')
            {
                var newline = text.IndexOf('\n', start);
                if (newline < 0)
                {
                    break;
                }

                start = newline + 1;
            }

            if (MatchFootnote(text, start) is { } match)
            {
                result.Add(match.Footnote);
                position = match.End;
            }
            else
            {
                position = start + 1;
            }
        }

        return result;
    }

    private static (EngageFootnote Footnote, int End)? MatchFootnote(string text, int start)
    {
        var i = start;
        if (!At(text, i, "[^h"))
        {
            return null;
        }

        i += 3;
        var digitsStart = i;
        while (i < text.Length && PyText.IsDigit(PyText.CodePointAt(text, i, out var length)))
        {
            i += length;
        }

        if (i == digitsStart || !At(text, i, "]:"))
        {
            return null;
        }

        var reference = text[(start + 2)..i];
        i = SkipSpace(text, i + 2);
        if (i >= text.Length || text[i] != '"')
        {
            return null;
        }

        var titleStart = i + 1;
        for (var quote = titleStart; quote < text.Length; quote++)
        {
            if (text[quote] == '\n')
            {
                return null; // '.' does not match a newline
            }

            if (text[quote] != '"')
            {
                continue;
            }

            var j = SkipSpace(text, quote + 1);
            if (j >= text.Length || text[j] is not ('\u2014' or '-'))
            {
                continue;
            }

            j = SkipSpace(text, j + 1);
            var urlStart = j;
            while (j < text.Length && !PyText.IsSpace(PyText.CodePointAt(text, j, out var length)))
            {
                j += length;
            }

            if (j == urlStart)
            {
                continue;
            }

            return (new EngageFootnote(reference, text[titleStart..quote], text[urlStart..j]), j);
        }

        return null;
    }

    /// <summary><c>SOURCE_JSON.findall(text)</c>: <c>\*\*sourceJson:\*\*[ \t]*(\{.*)$</c> with <c>re.M</c>.</summary>
    public static List<string> SourceBlobs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<string>();
        var position = 0;
        while (true)
        {
            var marker = text.IndexOf(SourceJsonMarker, position, StringComparison.Ordinal);
            if (marker < 0)
            {
                break;
            }

            var i = marker + SourceJsonMarker.Length;
            while (i < text.Length && text[i] is ' ' or '\t')
            {
                i++;
            }

            if (i < text.Length && text[i] == '{')
            {
                var end = text.IndexOf('\n', i);
                end = end < 0 ? text.Length : end;
                result.Add(text[i..end]);
                position = end;
            }
            else
            {
                position = marker + 1;
            }
        }

        return result;
    }

    /// <summary><c>re.search(r'/(?:threads|articles)/([A-Za-z0-9_%+/=-]+)', url)</c>: the captured id, or null.</summary>
    public static string? ThreadId(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        for (var i = 0; i < url.Length; i++)
        {
            if (url[i] != '/')
            {
                continue;
            }

            var start = At(url, i + 1, "threads/") ? i + 9 : At(url, i + 1, "articles/") ? i + 10 : -1;
            if (start < 0)
            {
                continue;
            }

            var end = start;
            while (end < url.Length && (char.IsAsciiLetterOrDigit(url[end]) || url[end] is '_' or '%' or '+' or '/' or '=' or '-'))
            {
                end++;
            }

            if (end > start)
            {
                return url[start..end];
            }
        }

        return null;
    }

    /// <summary><c>load_hits(path)</c>: the Viva Engage rows of a retrieval output file.</summary>
    public static List<EngageRow> LoadHits(string path)
    {
        var text = PyIO.ReadTextFile(path);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var links = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (reference, title, url) in Footnotes(text))
        {
            titles[reference] = title;
            links[reference] = PyText.RStrip(PyText.RStrip(url, "\\"), ".");
        }

        var hits = new List<JsonObject>();
        foreach (var blob in SourceBlobs(text))
        {
            JsonNode? parsed;
            try
            {
                parsed = PyJson.Loads(Unescape(blob));
            }
            catch (PyJsonDecodeError)
            {
                parsed = Salvage(blob);
            }

            // A blob starts with '{', so whatever parses is a dict.
            var hit = (JsonObject)parsed!;
            if (PyObj.Ne(PyObj.Get(hit, "ScopeType"), "YAMMER_GROUP"))
            {
                continue;
            }

            hits.Add(hit);
        }

        // Match hits to titles/links by encoded thread id where possible.
        var byEncoded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (reference, url) in links)
        {
            if (ThreadId(url) is { } id)
            {
                byEncoded[EncodedKey(id)] = reference;
            }
        }

        var rows = new List<EngageRow>();
        foreach (var hit in hits)
        {
            var reference = byEncoded.GetValueOrDefault(EncodedKey(PyObj.Get(hit, "EncodedId", "")));
            var body = PyObj.Or(PyObj.Get(hit, "PostBody"), "");
            var created = PyObj.Slice(PyObj.Or(PyObj.Get(hit, "CreatedAt"), ""), 10);
            var updated = PyObj.Slice(PyObj.Or(PyObj.Get(hit, "UpdatedAt"), ""), 10);
            var title = reference is not null && titles.TryGetValue(reference, out var named) && named.Length > 0
                ? named
                : BodyTitle(body);
            rows.Add(new EngageRow(
                title,
                reference is not null && links.TryGetValue(reference, out var link) ? link : "",
                PyObj.Get(hit, "GroupName"),
                PyObj.Get(hit, "Type"),
                PyObj.Get(hit, "SenderName"),
                created,
                updated,
                DayGap(created, updated),
                PyObj.Or(PyObj.Get(hit, "PostUpvoteCount"), BigInteger.Zero),
                PyObj.Truthy(PyObj.Get(hit, "BestAnswerBody")) || PyObj.Truthy(PyObj.Get(hit, "BestAnswerAuthorName")),
                PyObj.Get(hit, "BestAnswerAuthorName"),
                PyObj.Get(hit, "Topics"),
                body,
                PyObj.Truthy(PyObj.Get(hit, "_salvaged")),
                reference is not null));
        }

        return rows;
    }

    /// <summary><c>body[:90].replace('\n', ' ')</c>.</summary>
    private static string BodyTitle(object? body)
    {
        var prefix = PyObj.Slice(body, 90);
        if (PyObj.Normalize(prefix) is string text)
        {
            return text.Replace('\n', ' ');
        }

        throw new PyAttributeError("'" + PyObj.TypeName(prefix) + "' object has no attribute 'replace'");
    }

    /// <summary>The table view's line for one row.</summary>
    internal static string TableLine(EngageRow row)
    {
        var mark = row.Answered ? "ANS" : PyObj.Eq(row.Type, "QUESTION") ? "ASK" : "  .";
        var flags = (row.Salvaged ? "*" : "") + (row.Linked ? "" : "^");
        var type = PyObj.Slice(PyObj.Or(row.Type, "?"), 4);
        var author = PyObj.Slice(PyObj.Or(row.Author, "?"), 20);
        var title = PyText.Prefix(row.Title, 72);
        return mark + PyText.LeftJustify(flags, 2) + " [" + PyText.LeftJustify(PyObj.Str(type), 4) + "] act:"
               + PyJson.IntStr(row.ActiveDays).PadRight(4) + " " + PyObj.Str(row.Created) + " "
               + PyText.LeftJustify(PyObj.Str(author), 20) + " " + (title.Length > 0 ? title : "(no title, no body)");
    }

    private static bool At(string text, int index, string literal) =>
        index >= 0 && index + literal.Length <= text.Length && string.CompareOrdinal(text, index, literal, 0, literal.Length) == 0;

    private static int SkipSpace(string text, int index)
    {
        while (index < text.Length && PyText.IsSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary><c>re.search(prefix + r'(.*?)"', raw)</c> (no DOTALL): the text up to the next quote on the line.</summary>
    private static string? SearchQuoted(string raw, string prefix)
    {
        for (var at = raw.IndexOf(prefix, StringComparison.Ordinal); at >= 0; at = raw.IndexOf(prefix, at + 1, StringComparison.Ordinal))
        {
            var start = at + prefix.Length;
            for (var i = start; i < raw.Length && raw[i] != '\n'; i++)
            {
                if (raw[i] == '"')
                {
                    return raw[start..i];
                }
            }
        }

        return null;
    }

    /// <summary><c>re.search(prefix + r'(\d+)', raw)</c> with Unicode decimal digits.</summary>
    private static string? SearchDigits(string raw, string prefix)
    {
        for (var at = raw.IndexOf(prefix, StringComparison.Ordinal); at >= 0; at = raw.IndexOf(prefix, at + 1, StringComparison.Ordinal))
        {
            var start = at + prefix.Length;
            var i = start;
            while (i < raw.Length && PyText.IsDigit(PyText.CodePointAt(raw, i, out var length)))
            {
                i += length;
            }

            if (i > start)
            {
                return raw[start..i];
            }
        }

        return null;
    }

    /// <summary><c>re.search(r'"PostBody":"(.*?)"\s*,\s*"[A-Za-z]\w*":', raw, re.S)</c>.</summary>
    private static string? SearchBody(string raw)
    {
        const string Prefix = "\"PostBody\":\"";
        for (var at = raw.IndexOf(Prefix, StringComparison.Ordinal); at >= 0; at = raw.IndexOf(Prefix, at + 1, StringComparison.Ordinal))
        {
            var start = at + Prefix.Length;
            for (var quote = raw.IndexOf('"', start); quote >= 0; quote = raw.IndexOf('"', quote + 1))
            {
                var i = SkipSpace(raw, quote + 1);
                if (i >= raw.Length || raw[i] != ',')
                {
                    continue;
                }

                i = SkipSpace(raw, i + 1);
                if (i + 1 >= raw.Length || raw[i] != '"' || !char.IsAsciiLetter(raw[i + 1]))
                {
                    continue;
                }

                i += 2;
                while (i < raw.Length && PyText.IsWord(PyText.CodePointAt(raw, i, out var length)))
                {
                    i += length;
                }

                if (At(raw, i, "\":"))
                {
                    return raw[start..quote];
                }
            }
        }

        return null;
    }
}

/// <summary><c>python3 engage_parse.py PATH [--group NAME] [--format json|table]</c>.</summary>
public sealed class EngageParseScript : IStateScript
{
    public string ScriptName => "engage_parse.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => PyProcess.OnLargeStack(() => Main(args, stdout, stderr)), stderr);

    private int Main(IReadOnlyList<string> argv, TextWriter stdout, TextWriter stderr)
    {
        var parser = new PyArgumentParser(ScriptName);
        parser.AddArgument("path");
        parser.AddArgument("--group", help: "filter to one community, e.g. \"Work IQ\"");
        parser.AddArgument("--format", choices: ["json", "table"], @default: "table");
        var args = parser.ParseArgs(argv, stdout, stderr);

        var rows = EngageParse.LoadHits(args.GetString("path")!);
        var group = args.GetString("group");
        if (!string.IsNullOrEmpty(group))
        {
            rows = rows.Where(row => PyObj.Eq(row.Group, group)).ToList();
        }

        // Unanswered questions first, then by thread activity.
        var keyed = rows.Select(row => new KeyValuePair<object?[], EngageRow>([row.Answered, -row.ActiveDays, row.Created], row)).ToArray();
        PySort.Sort(keyed, (a, b) => PyObj.TupleLt(a.Key, b.Key));
        rows = keyed.Select(pair => pair.Value).ToList();

        if (args.GetString("format") == "json")
        {
            PyJsonDump.Dump(stdout, rows.Select(row => (object?)row.ToPython()).ToList());
            return 0;
        }

        var salvaged = rows.Count(row => row.Salvaged);
        var unlinked = rows.Count(row => !row.Linked);
        PyIO.Print(stdout, string.Format(CultureInfo.InvariantCulture,
            "{0} posts{1} \u2014 {2} linked, {3} unlinked, {4} salvaged (body may be partial)",
            rows.Count, string.IsNullOrEmpty(group) ? "" : " in " + group, rows.Count - unlinked, unlinked, salvaged));
        if (unlinked > 0 || salvaged > 0)
        {
            PyIO.Print(stdout, "! degraded rows are marked below: NOLINK = no permalink, SALV = recovered from a malformed blob");
        }

        foreach (var row in rows)
        {
            PyIO.Print(stdout, EngageParse.TableLine(row));
        }

        if (unlinked > 0 || salvaged > 0)
        {
            PyIO.Print(stdout, new string('-', 100));
            PyIO.Print(stdout, "* = salvaged (body partial or empty)   ^ = no permalink resolved");
            PyIO.Print(stdout, "Do not present a ^ row as citable, and do not quote a * row without re-reading the source.");
        }

        return 0;
    }
}
