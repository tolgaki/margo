using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine;

/// <summary>One skill under <c>skills/</c>, as its <c>SKILL.md</c> front matter declares it.</summary>
/// <param name="Name">The front matter's <c>name</c>, or the directory name when it is missing or empty.</param>
/// <param name="Description">The front matter's <c>description</c>, or an empty string.</param>
/// <param name="Directory">The skill's directory (absolute when the library root is).</param>
public sealed record SkillInfo(string Name, string Description, string Directory);

/// <summary>
/// The persona and skills desktop Margo uses, served unchanged to the hosted engine (<c>skills.py</c>).
/// </summary>
/// <remarks>
/// <para>Desktop Margo runs in a Copilot host that lists the installed skills, loads one when its routine is asked
/// for, and lets the agent read the files it points to. This class does the same from the repository's
/// <c>agents/</c> and <c>skills/</c> directories, without editing a word, so the hosted Margo has the same voice and
/// procedures as the desktop one.</para>
/// <para>Two files differ by design, because they hold the manager's private data rather than procedure:
/// <c>chief-of-staff/preferences.md</c> is the instance's private copy, and <c>commitments.md</c> is not a file here
/// because hosted instances keep commitments only in the work ledger.</para>
/// <para>Files are read as Python's <c>Path.read_text(encoding="utf-8")</c> does: strict UTF-8 (an undecodable file
/// is a <see cref="PyUnicodeError"/>), a byte order mark kept, and universal newlines (<c>\r\n</c> and <c>\r</c>
/// become <c>\n</c>).</para>
/// </remarks>
public sealed class SkillLibrary
{
    /// <summary><c>MAX_FILE_CHARS</c>: the most code points of a file returned to the model.</summary>
    public const int MaxFileChars = 40000;

    /// <summary><c>PRIVATE_PREFERENCES</c>: the skill file served from the instance's private copy.</summary>
    public static (string Skill, string Path) PrivatePreferences { get; } = ("chief-of-staff", "preferences.md");

    /// <summary><c>LEDGER_ONLY</c>: the file hosted instances do not have (commitments live in the work ledger).</summary>
    public static (string Skill, string Path) LedgerOnly { get; } = ("chief-of-staff", "commitments.md");

    /// <summary><c>EXCLUDED_DIRS</c>: directories whose Markdown files are not served.</summary>
    public static IReadOnlySet<string> ExcludedDirs { get; } = new HashSet<string>(StringComparer.Ordinal) { "state", "scripts", "__pycache__" };

    /// <param name="repoRoot">The repository (or image) root holding <c>agents/</c> and <c>skills/</c>.</param>
    /// <param name="preferences">The manager's private <c>preferences.md</c>; null or empty when there is none.</param>
    public SkillLibrary(string repoRoot, string? preferences = null)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        Root = repoRoot;
        Preferences = string.IsNullOrEmpty(preferences) ? null : preferences;
    }

    /// <summary>The repository root.</summary>
    public string Root { get; }

    /// <summary>The manager's private preferences file, when configured.</summary>
    public string? Preferences { get; }

    /// <summary><c>split_front_matter(text)</c>: the <c>---</c> front matter fields and the body after it.</summary>
    public static (IReadOnlyDictionary<string, string> Fields, string Body) SplitFrontMatter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return (fields, text);
        }

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end == -1)
        {
            return (fields, text);
        }

        foreach (var line in PyRepr.SplitLines(PyRepr.Strip(text[3..end])))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon];
            if (PyRepr.Strip(key).Length == 0 || key.StartsWith(' ') || key.StartsWith('\t'))
            {
                continue;
            }

            var value = PyRepr.Strip(line[(colon + 1)..]);
            if (value.StartsWith('"'))
            {
                try
                {
                    // A value that starts with a quote parses as a JSON string or not at all.
                    value = PyNode.AsString(PyJson.Loads(value)) ?? value;
                }
                catch (PyValueError)
                {
                    value = value.Trim('"');
                }
            }

            fields[PyRepr.Strip(key)] = value;
        }

        return (fields, TrimStartNewlines(text[(end + 4)..]));
    }

    /// <summary><c>persona()</c>: <c>agents/margo.agent.md</c> without its front matter.</summary>
    public string Persona() => SplitFrontMatter(ReadText(Path.Combine(Root, "agents", "margo.agent.md"))).Body;

    /// <summary><c>skills()</c>: every <c>skills/*/SKILL.md</c>, in Python's sorted path order.</summary>
    public IReadOnlyList<SkillInfo> Skills()
    {
        var found = new List<SkillInfo>();
        foreach (var directory in SkillDirectories(Path.Combine(Root, "skills")))
        {
            var (fields, _) = SplitFrontMatter(ReadText(Path.Combine(directory, "SKILL.md")));
            var name = fields.TryGetValue("name", out var given) && given.Length > 0 ? given : Path.GetFileName(directory);
            found.Add(new SkillInfo(name, fields.TryGetValue("description", out var description) ? description : "", directory));
        }

        return found;
    }

    /// <summary><c>index()</c>: one Markdown line per skill, <c>- **name**: description</c>.</summary>
    public string Index() => string.Join("\n", Skills().Select(skill => "- **" + skill.Name + "**: " + skill.Description));

    /// <summary><c>files(name)</c>: the skill's readable Markdown files, relative to its directory, in Python's order.</summary>
    /// <param name="name">The skill name as the model gave it (any JSON value; only a string can match).</param>
    public IReadOnlyList<string> Files(JsonNode? name) => FindSkill(name) is { } skill ? MarkdownFiles(skill.Directory) : [];

    /// <inheritdoc cref="Files(JsonNode?)"/>
    public IReadOnlyList<string> Files(string? name) => Files(name is null ? null : JsonValue.Create(name));

    /// <summary><c>load(name)</c>: the skill's instructions and the files it can point to.</summary>
    public JsonObject Load(JsonNode? name)
    {
        var skill = FindSkill(name);
        if (skill is null)
        {
            return new JsonObject
            {
                ["status"] = "refused",
                ["reason"] = "unknown skill; available: " + string.Join(", ", Skills().Select(s => s.Name)),
            };
        }

        var body = SplitFrontMatter(ReadText(Path.Combine(skill.Directory, "SKILL.md"))).Body;
        return new JsonObject
        {
            ["status"] = "ok",
            ["skill"] = skill.Name,
            ["text"] = body,
            ["files"] = PyNode.StrList(Files(skill.Name).Where(path => path != "SKILL.md")),
        };
    }

    /// <inheritdoc cref="Load(JsonNode?)"/>
    public JsonObject Load(string? name) => Load(name is null ? null : JsonValue.Create(name));

    /// <summary><c>read(name, path)</c>: a file a skill points to, confined to the skill's Markdown files.</summary>
    public JsonObject Read(JsonNode? name, JsonNode? path)
    {
        var skill = FindSkill(name);
        if (skill is null)
        {
            return new JsonObject { ["status"] = "refused", ["reason"] = "unknown skill" };
        }

        var relative = Relative(path);
        if (relative is null)
        {
            return new JsonObject { ["status"] = "refused", ["reason"] = "path must name a Markdown file inside the skill" };
        }

        if (skill.Name == PrivatePreferences.Skill && relative == PrivatePreferences.Path)
        {
            if (Preferences is null || !File.Exists(Preferences))
            {
                return new JsonObject { ["status"] = "unavailable", ["reason"] = "your manager's preferences are not set up yet" };
            }

            return new JsonObject
            {
                ["status"] = "ok",
                ["skill"] = skill.Name,
                ["path"] = relative,
                ["text"] = PyText.Prefix(ReadText(Preferences), MaxFileChars),
            };
        }

        if (skill.Name == LedgerOnly.Skill && relative == LedgerOnly.Path)
        {
            return new JsonObject
            {
                ["status"] = "unavailable",
                ["skill"] = skill.Name,
                ["path"] = relative,
                ["reason"] = "hosted instances keep commitments only in the work ledger (the migrated state); "
                             + "read them with margo-state work_state.py list or show",
            };
        }

        var listed = new HashSet<string>(Files(skill.Name), StringComparer.Ordinal);
        foreach (var candidate in new[] { relative, "references/" + relative })
        {
            if (!listed.Contains(candidate))
            {
                continue;
            }

            var text = ReadText(Path.Combine(skill.Directory, candidate));
            var result = new JsonObject
            {
                ["status"] = "ok",
                ["skill"] = skill.Name,
                ["path"] = candidate,
                ["text"] = PyText.Prefix(text, MaxFileChars),
            };
            if (PyRepr.Length(text) > MaxFileChars)
            {
                result["truncated"] = true;
            }

            return result;
        }

        return new JsonObject { ["status"] = "unavailable", ["reason"] = "no such file in the " + skill.Name + " skill" };
    }

    /// <inheritdoc cref="Read(JsonNode?, JsonNode?)"/>
    public JsonObject Read(string? name, string? path) =>
        Read(name is null ? null : JsonValue.Create(name), path is null ? null : JsonValue.Create(path));

    /// <summary>
    /// <c>_relative(path)</c>: normalise the paths procedures use (<c>references/x.md</c>, <c>../preferences.md</c>,
    /// <c>x</c>) to a relative Markdown path, or null when the path cannot name a file inside a skill.
    /// </summary>
    public static string? Relative(JsonNode? path)
    {
        if (PyNode.AsString(path) is not { } given || PyRepr.Strip(given).Length == 0 || PyRepr.Length(given) > 512
            || given.Contains('\\', StringComparison.Ordinal) || given.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        var value = PyRepr.Strip(given);
        if (value.StartsWith('/'))
        {
            return null;
        }

        value = NormPath(value);
        while (value.StartsWith("../", StringComparison.Ordinal))
        {
            value = value[3..];
        }

        if (value is "" or "." or ".." || value.StartsWith("..", StringComparison.Ordinal) || value.Contains("/../", StringComparison.Ordinal))
        {
            return null;
        }

        if (!value.EndsWith(".md", StringComparison.Ordinal))
        {
            value += ".md";
        }

        return value;
    }

    /// <inheritdoc cref="Relative(JsonNode?)"/>
    public static string? Relative(string? path) => Relative(path is null ? null : JsonValue.Create(path));

    /// <summary><c>posixpath.normpath(path)</c>.</summary>
    public static string NormPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return ".";
        }

        var initialSlashes = path.StartsWith('/') ? (path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal) ? 2 : 1) : 0;
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part != ".." || (initialSlashes == 0 && parts.Count == 0) || (parts.Count > 0 && parts[^1] == ".."))
            {
                parts.Add(part);
            }
            else if (parts.Count > 0)
            {
                parts.RemoveAt(parts.Count - 1);
            }
        }

        var result = new string('/', initialSlashes) + string.Join("/", parts);
        return result.Length == 0 ? "." : result;
    }

    /// <summary>
    /// <c>Path.read_text(encoding="utf-8")</c>: strict UTF-8 and universal newlines.
    /// </summary>
    public static string ReadText(string path)
    {
        var text = PyIO.DecodeUtf8(File.ReadAllBytes(path));
        return text.Contains('\r', StringComparison.Ordinal) ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;
    }

    /// <summary><c>_skill(name)</c>: the skill whose name equals <c>name.strip().lower()</c>.</summary>
    private SkillInfo? FindSkill(JsonNode? name)
    {
        if (PyNode.AsString(name) is not { } given)
        {
            return null;
        }

        var wanted = PythonLower(PyRepr.Strip(given));
        return Skills().FirstOrDefault(skill => skill.Name == wanted);
    }

    /// <summary>
    /// <c>str.lower()</c>: .NET's simple lower-case mapping, plus Python's one unconditional special case (U+0130
    /// becomes <c>i</c> and a combining dot above). The final-sigma rule is not modelled (see PORTING.md).
    /// </summary>
    internal static string PythonLower(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var codePoint in PyRepr.CodePoints(text))
        {
            if (codePoint == 0x130)
            {
                builder.Append("i̇");
            }
            else if (codePoint is >= 0xD800 and <= 0xDFFF)
            {
                builder.Append((char)codePoint);
            }
            else
            {
                builder.Append(char.ConvertFromUtf32(codePoint).ToLowerInvariant());
            }
        }

        return builder.ToString();
    }

    /// <summary>The directories <c>skills/*/SKILL.md</c> matches, sorted by name in code-point order.</summary>
    private static List<string> SkillDirectories(string skills)
    {
        var directories = new List<string>();
        foreach (var directory in SafeEntries(skills, directoriesOnly: true))
        {
            if (File.Exists(Path.Combine(directory, "SKILL.md")))
            {
                directories.Add(directory);
            }
        }

        directories.Sort((left, right) => PyRepr.CompareStrings(Path.GetFileName(left), Path.GetFileName(right)));
        return directories;
    }

    /// <summary>
    /// <c>sorted(base.rglob("*.md"))</c> without symbolic links and without anything under <see cref="ExcludedDirs"/>,
    /// as POSIX paths relative to <paramref name="baseDirectory"/>. Like Python 3.13's <c>rglob</c>, hidden entries
    /// match, symbolic links to directories are not followed, and a directory whose name ends in <c>.md</c> matches too.
    /// </summary>
    private static List<string> MarkdownFiles(string baseDirectory)
    {
        var found = new List<string[]>();
        Walk(baseDirectory, []);
        found.Sort(CompareParts);
        return found.Select(parts => string.Join("/", parts)).ToList();

        void Walk(string directory, string[] prefix)
        {
            foreach (var entry in SafeEntries(directory, directoriesOnly: false))
            {
                var name = Path.GetFileName(entry);
                string[] parts = [.. prefix, name];
                var info = new FileInfo(entry);
                var isLink = info.LinkTarget is not null;
                if (name.EndsWith(".md", StringComparison.Ordinal) && !isLink && !prefix.Any(ExcludedDirs.Contains))
                {
                    found.Add(parts);
                }

                if (!isLink && Directory.Exists(entry))
                {
                    Walk(entry, parts);
                }
            }
        }
    }

    private static int CompareParts(string[] left, string[] right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var order = PyRepr.CompareStrings(left[i], right[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static List<string> SafeEntries(string directory, bool directoriesOnly)
    {
        try
        {
            return (directoriesOnly ? Directory.EnumerateDirectories(directory) : Directory.EnumerateFileSystemEntries(directory)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // pathlib's glob ignores directories it cannot list
        }
    }

    private static string TrimStartNewlines(string text)
    {
        var start = 0;
        while (start < text.Length && text[start] == '\n')
        {
            start++;
        }

        return text[start..];
    }
}
