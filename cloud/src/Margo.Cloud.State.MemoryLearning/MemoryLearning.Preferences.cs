using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.State.MemoryLearning;

public static partial class MemoryLearning
{
    /// <summary>
    /// Python's <c>\s</c> for <c>str</c> patterns (<c>str.isspace()</c>, Unicode 15.1). .NET's <c>\s</c> lacks
    /// U+001C..U+001F, so the class is spelled out (<c>PreferenceWhitespaceMatchesPython</c> checks it against
    /// State.Core's table).
    /// </summary>
    internal const string PythonSpace = @"[\t\n\v\f\r\x1c-\x20\x85\xa0  -     　]";

    [GeneratedRegex(@"^[-*]" + PythonSpace + @"+\*\*[^*\n]+:\*\*[ \t]*\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ExampleField();

    [GeneratedRegex(@"\{[^}]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@":\*\*" + PythonSpace + @"*(?:\(e\.g\.|$)", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyField();

    /// <summary>
    /// <c>_preference_sections(content)</c>: the <c>## </c> sections of a preferences file, with balanced template
    /// placeholders, parenthesised example fields, unfilled lines and empty sections removed.
    /// </summary>
    public static List<(string Title, string Text)> ParsePreferenceSections(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Template fields can wrap lines. Drop balanced spans before extracting sections.
        var cleaned = new StringBuilder(content.Length);
        var depth = 0;
        foreach (var character in content)
        {
            if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                if (depth == 0)
                {
                    throw new StateError("unbalanced preference placeholder; review the file before import");
                }

                depth--;
            }
            else if (depth == 0)
            {
                cleaned.Append(character);
            }
        }

        if (depth != 0)
        {
            throw new StateError("unclosed preference placeholder; review the file before import");
        }

        content = cleaned.ToString();
        while (true)
        {
            var match = ExampleField().Match(content);
            if (!match.Success)
            {
                break;
            }

            var start = match.Index + match.Length - 1;
            var nesting = 1;
            var end = start + 1;
            while (end < content.Length && nesting != 0)
            {
                nesting += (content[end] == '(' ? 1 : 0) - (content[end] == ')' ? 1 : 0);
                end++;
            }

            if (nesting != 0)
            {
                throw new StateError("unclosed example field; review preferences before import");
            }

            content = string.Concat(content.AsSpan(0, start), content.AsSpan(end));
        }

        var sections = new List<(string Title, string Body)>();
        string? heading = null;
        var lines = new List<string>();
        foreach (var line in PyRepr.SplitLines(content))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(heading) && lines.Count > 0)
                {
                    sections.Add((heading, PyRepr.Strip(string.Join("\n", lines))));
                }

                heading = PyRepr.Strip(line[3..]);
                lines = [];
            }
            else if (!string.IsNullOrEmpty(heading))
            {
                lines.Add(line);
            }
        }

        if (!string.IsNullOrEmpty(heading) && lines.Count > 0)
        {
            sections.Add((heading, PyRepr.Strip(string.Join("\n", lines))));
        }

        var selected = new List<(string Title, string Text)>();
        foreach (var (title, body) in sections)
        {
            var kept = new List<string>();
            var inFence = false;
            foreach (var line in PyRepr.SplitLines(body))
            {
                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                }

                // Unfilled examples are not user facts. Leave substantive saved prose intact.
                if (!inFence && (Placeholder().IsMatch(line) || EmptyField().IsMatch(line)))
                {
                    continue;
                }

                kept.Add(line);
            }

            var text = PyRepr.Strip(string.Join("\n", kept));
            if (text.Length > 0 && PyRepr.CodePoints(text).Any(PyText.IsAlnum))
            {
                selected.Add((title, text));
            }
        }

        return selected;
    }

    /// <summary><c>_preference_file(path)</c>: the resolved path, its UTF-8 text and its file source reference.</summary>
    public static (string Source, string Content, JsonObject SourceRef) PreferenceFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var source = Resolve(PyPath.ExpandUser(PyPath.Normalize(path)));
        if (!PyPath.IsFile(source))
        {
            throw new StateError("preferences file is missing");
        }

        byte[] payload;
        JsonObject sourceRef;
        try
        {
            (payload, sourceRef) = File(source);
        }
        catch (StateError ex)
        {
            throw new StateError("preferences file is unavailable or exceeds the 1 MiB source budget", ex);
        }

        string content;
        try
        {
            content = PyIO.DecodeUtf8(payload);
        }
        catch (PyUnicodeError ex)
        {
            throw new StateError("preferences file must contain valid UTF-8", ex);
        }

        return (source, content, sourceRef);
    }

    /// <summary><c>preference_sections(path)</c>: preserve source sections rather than guessing people IDs.</summary>
    public static List<(string Title, string Text)> PreferenceSections(string path) => ParsePreferenceSections(PreferenceFile(path).Content);

    /// <summary>
    /// <c>preferences_plan(memory, path)</c>: the import preview, bound to the source content and to the exact target
    /// revisions it would replace (forgotten targets are omitted, never recreated).
    /// </summary>
    public static JsonObject PreferencesPlan(MemoryStore memory, string path)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var (source, content, sourceRef) = PreferenceFile(path);
        var fingerprint = PyNode.AsString(sourceRef["revision"])!;
        var records = new JsonArray();
        var omitted = new JsonArray();
        var sectionNumber = 0;
        foreach (var (title, text) in ParsePreferenceSections(content))
        {
            sectionNumber++;
            var keyBase = "preferences:" + PyIds.Sha256Hex(source) + ":" + title;

            // Each search record is short enough to embed without silently truncating a whole section.
            var paragraphs = text.Split("\n\n").Select(PyRepr.Strip).Where(part => part.Length > 0).ToList();
            var chunks = new List<string>();
            var chunk = "";
            foreach (var paragraph in paragraphs)
            {
                if (PyRepr.Length(paragraph) > 1500)
                {
                    var lineNumber = 0;
                    foreach (var line in PyRepr.SplitLines(paragraph))
                    {
                        if (PyRepr.Length(line) > 1500)
                        {
                            throw new StateError(string.Format(CultureInfo.InvariantCulture,
                                "preference section {0} contains a line over the 1500-character memory chunk budget; review/split that section first",
                                sectionNumber));
                        }

                        var separator = lineNumber == 0 ? "\n\n" : "\n";
                        if (chunk.Length == 0)
                        {
                            chunk = line;
                        }
                        else if (PyRepr.Length(chunk) + separator.Length + PyRepr.Length(line) <= 1500)
                        {
                            chunk += separator + line;
                        }
                        else
                        {
                            chunks.Add(chunk);
                            chunk = line;
                        }

                        lineNumber++;
                    }
                }
                else if (chunk.Length == 0)
                {
                    chunk = paragraph;
                }
                else if (PyRepr.Length(chunk) + 2 + PyRepr.Length(paragraph) <= 1500)
                {
                    chunk += "\n\n" + paragraph;
                }
                else
                {
                    chunks.Add(chunk);
                    chunk = paragraph;
                }
            }

            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            for (var offset = 0; offset < chunks.Count; offset++)
            {
                var folded = PyRepr.CaseFold(title);
                var kind = PreferenceWords.Any(word => folded.Contains(word, StringComparison.Ordinal)) ? "preference" : "profile";
                var routines = folded.Contains("scheduling", StringComparison.Ordinal) ? new JsonArray("calendar", "outcomes")
                    : folded.Contains("communication", StringComparison.Ordinal) ? new JsonArray("drafting", "meeting-prep", "work-products")
                    : new JsonArray();
                var recordData = new JsonObject
                {
                    ["domain"] = "user",
                    ["kind"] = kind,
                    ["title"] = title + (chunks.Count > 1 ? " (" + (offset + 1).ToString(CultureInfo.InvariantCulture) + ")" : ""),
                    ["text"] = chunks[offset],
                    ["authority"] = kind == "preference" ? "user_confirmed" : "source_observed",
                    ["scope"] = "personal",
                    ["sensitivity"] = "private",
                    ["allowed_uses"] = new JsonArray("reasoning"),
                    ["entities"] = new JsonArray("user"),
                    ["routines"] = routines,
                    ["source_refs"] = new JsonArray(PyNode.Copy(sourceRef)),
                    ["metadata"] = new JsonObject
                    {
                        ["imported_configuration"] = true,
                        ["source_heading"] = title,
                        ["not_independently_verified"] = kind != "preference",
                    },
                };
                JsonObject normal;
                try
                {
                    normal = NormaliseProposal(memory, recordData);
                }
                catch (StateError ex)
                {
                    throw new StateError(string.Format(CultureInfo.InvariantCulture,
                        "preference section {0} chunk {1} is not a valid memory record: {2}", sectionNumber, offset + 1, ex.Message), ex);
                }

                var key = keyBase + ":" + offset.ToString(CultureInfo.InvariantCulture);
                string identity;
                try
                {
                    identity = memory.RecordId("user", key);
                }
                catch (StateError ex)
                {
                    throw new StateError(string.Format(CultureInfo.InvariantCulture,
                        "preference section {0} cannot be assigned a valid memory identity: {1}", sectionNumber, ex.Message), ex);
                }

                var prior = memory.List("user").OfType<JsonObject>().FirstOrDefault(row => PyNode.Is(row["id"], identity));
                if (prior is not null && Eq(Item(prior, "status"), "forgotten"))
                {
                    omitted.Add(identity);
                    continue;
                }

                records.Add(new JsonObject
                {
                    ["id"] = identity,
                    ["key"] = key,
                    ["expected_revision"] = prior is null ? null : PyNode.Copy(Item(prior, "revision")),
                    ["expected_status"] = prior is null ? null : PyNode.Copy(Item(prior, "status")),
                    ["data"] = normal,
                });
            }
        }

        try
        {
            File(source, fingerprint);
        }
        catch (StateError ex)
        {
            throw new StateError("preferences changed while building the import preview", ex);
        }

        var manifest = new JsonObject
        {
            ["account"] = memory.Account,
            ["source_hash"] = fingerprint,
            ["records"] = records,
            ["omitted_forgotten"] = omitted,
        };
        var subject = "preferences-import:" + Digest(manifest);
        return PyNode.DictWith(manifest, ("subject_id", subject), ("revision", 1), ("requires_human_import", true));
    }

    /// <summary>
    /// <c>import_preferences(memory, path, evidence)</c>: apply one revision-bound import plan atomically; a replay of
    /// the approved import is checked against its receipt and never rebinds a stale approval.
    /// </summary>
    public static JsonObject ImportPreferences(MemoryStore memory, string path, JsonNode? evidence)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(path);
        return memory.Transaction(() =>
        {
            if (evidence is not JsonObject approval)
            {
                throw new StateError("preferences import requires explicit human evidence");
            }

            var receiptKey = "import-receipt:" + PyNode.Str(PyNode.Get(approval, "subject_id", ""));
            var previous = memory.Conn.Execute("SELECT value FROM memory_meta WHERE key=?", receiptKey).FetchOne();
            if (previous is not null)
            {
                var stored = PyNode.Loads(previous[0]);
                WorkLedger.Human(approval, Item(approval, "subject_id"), 1, "import");
                JsonObject currentSource;
                try
                {
                    (_, currentSource) = File(Resolve(PyPath.ExpandUser(PyPath.Normalize(path))));
                }
                catch (StateError ex)
                {
                    throw new StateError("import source changed; obtain approval of a fresh preview", ex);
                }

                if (!Eq(currentSource["revision"], Item(stored, "source_hash")))
                {
                    throw new StateError("import source changed; obtain approval of a fresh preview");
                }

                foreach (var expected in PyNode.Iter(Item(stored, "targets")))
                {
                    var current = memory.Show(Item(expected, "id"));
                    if (!Eq(Item(current, "revision"), Item(expected, "revision")))
                    {
                        throw new StateError("import target changed since approval; obtain a fresh preview");
                    }
                }

                return new JsonObject
                {
                    ["imported"] = 0,
                    ["memory_ids"] = new JsonArray(PyNode.Iter(Item(stored, "targets")).Select(row => PyNode.Copy(Item(row, "id"))).ToArray()),
                    ["replayed"] = true,
                    ["source_hash"] = PyNode.Copy(Item(stored, "source_hash")),
                };
            }

            var plan = PreferencesPlan(memory, path);
            WorkLedger.Human(approval, plan["subject_id"], 1, "import");
            var result = new List<string>();
            foreach (var item in (JsonArray)plan["records"]!)
            {
                var targetRevision = PyJson.Truthy(item!["expected_revision"]) ? PyNode.Copy(item["expected_revision"]) : 1;
                var confirmation = PyNode.DictWith(approval,
                    ("subject_id", PyNode.Copy(item["id"])),
                    ("revision", targetRevision),
                    ("decision", Eq(item["expected_status"], "active") ? "edit" : "confirm"),
                    ("approved_import_subject", PyNode.Copy(plan["subject_id"])),
                    ("approved_import_revision", 1));
                var record = memory.Put(PyNode.Copy(item["key"]), PyNode.Copy(item["data"]), "active", PyNode.Copy(targetRevision), confirmation);
                result.Add(PyNode.AsString(record["id"])!);
            }

            var receipt = new JsonObject
            {
                ["source_hash"] = PyNode.Copy(plan["source_hash"]),
                ["targets"] = new JsonArray(result.Select(identity => (JsonNode?)new JsonObject
                {
                    ["id"] = identity,
                    ["revision"] = PyNode.Copy(memory.Show(identity)["revision"]),
                }).ToArray()),
            };
            memory.Conn.Execute("INSERT INTO memory_meta VALUES (?,?)", receiptKey, StateStore.CanonicalJson(receipt));
            return new JsonObject
            {
                ["imported"] = result.Count,
                ["memory_ids"] = PyNode.StrList(result),
                ["source_hash"] = PyNode.Copy(plan["source_hash"]),
                ["replayed"] = false,
                ["note"] = "Imported only saved configuration; profile observations are not independently verified facts.",
            };
        });
    }
}
