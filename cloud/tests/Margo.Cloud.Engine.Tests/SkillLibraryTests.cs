using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine.Tests;

/// <summary><c>skills.py</c>: <c>test_skill_files_are_served_unchanged_and_confined</c>, and differential checks against the Python library.</summary>
public sealed class SkillLibraryTests
{
    [Fact]
    public void Skill_files_are_served_unchanged_and_confined()
    {
        using var temp = new TempRoot();
        var prefs = temp.Combine("preferences.md");
        File.WriteAllText(prefs, "- **Replies to other people:** none\n");
        var library = new SkillLibrary(Fixtures.Root, prefs);
        var brief = File.ReadAllText(Path.Combine(Fixtures.Root, "skills", "chief-of-staff", "references", "daily-brief.md"));
        foreach (var path in new[] { "references/daily-brief.md", "daily-brief", "../references/daily-brief.md" })
        {
            Assert.Equal(brief, PyNode.AsString(library.Read("chief-of-staff", path)["text"]));
        }

        Assert.Contains("Replies", PyNode.AsString(library.Read("chief-of-staff", "../preferences.md")["text"]), StringComparison.Ordinal);
        Assert.Equal("unavailable", PyNode.AsString(library.Read("chief-of-staff", "commitments.md")["status"]));
        Assert.Equal("ok", PyNode.AsString(library.Read("decision-log", "references/schema.md")["status"]));
        foreach (var path in new[] { "../../agents/margo.agent.md", "/etc/passwd", "scripts/work_state.py", "state/x.md", new string('x', 600), null, "..\\SKILL.md" })
        {
            Assert.NotEqual("ok", PyNode.AsString(library.Read("chief-of-staff", path)["status"]));
        }

        Assert.Equal("refused", PyNode.AsString(library.Load("no-such-skill")["status"]));
        Assert.Equal("unavailable", PyNode.AsString(new SkillLibrary(Fixtures.Root).Read("chief-of-staff", "preferences.md")["status"]));
    }

    [Fact]
    public void Paths_normalise_as_posixpath_does()
    {
        Assert.Equal("references/x.md", SkillLibrary.Relative("references/x"));
        Assert.Equal("preferences.md", SkillLibrary.Relative("../preferences.md"));
        Assert.Equal("a/b.md", SkillLibrary.Relative(" ./a//./b.md "));
        Assert.Equal("b.md", SkillLibrary.Relative("a/../../b")); // normpath gives ../b, and a leading ../ is dropped
        Assert.Null(SkillLibrary.Relative("a/../..")); // normpath gives .., which names nothing
        Assert.Null(SkillLibrary.Relative("/etc/passwd"));
        Assert.Null(SkillLibrary.Relative(""));
        Assert.Null(SkillLibrary.Relative((string?)null));
        Assert.Null(SkillLibrary.Relative(JsonValue.Create(5)));
        Assert.Equal(".", SkillLibrary.NormPath(""));
        Assert.Equal("//a", SkillLibrary.NormPath("//a/./b/.."));
        Assert.Equal("/a", SkillLibrary.NormPath("///a"));
        Assert.Equal("../..", SkillLibrary.NormPath("../x/../.."));
        Assert.Equal("/", SkillLibrary.NormPath("/../.."));
    }

    [Fact]
    public void Relative_paths_and_front_matter_match_python()
    {
        string?[] paths =
        [
            "references/daily-brief.md", "daily-brief", "../references/daily-brief.md", "../preferences.md", "./x", "x/",
            "a/../b", "a/../../b", "../../a", "..", "...", "..x", ".../x", "x/../..", "/x", "  /x", " x ", "x\\y", "x\u0000",
            "\u00a0x\u2003", "\u3000", "", " ", "x.MD", "x.md.md", "a//b/./c", "~/x", "%2e%2e/x", "references/../../../SKILL.md",
            new string('x', 512), new string('x', 513), "x" + string.Concat(Enumerable.Repeat("\U0001F600", 300)), "caf\u00e9", null,
        ];
        string[] texts =
        [
            "no front matter", "---\nname: x\n", "---\nname: alpha\ndescription: \"quoted \\u00e9\"\n---\n\nBody\n",
            "---\n name: indented\n\tdesc: tab\nkey without colon\n: empty key\nname:  spaced  \n---\n\n\nBody",
            "---\nname: \"unterminated\n---\nB", "---\nname: \"a\" trailing\n---B", "---\ndescription: a: b: c\n---\n",
            "---\u2028name: x\u2029---\nBody", "---\r\nname: crlf\r\n---\r\nBody", "--- \nname: y\n---\n", "---\n---\n",
            "---name: z\n---\nq", "---\nname: \"\\ud800\"\n---\n",
        ];
        var python = Programs.AskPython(new JsonObject
        {
            ["relative"] = new JsonArray(paths.Select(p => (JsonNode?)(p is null ? null : JsonValue.Create(p))).ToArray()),
            ["split"] = PyNode.StrList(texts),
        });
        var relative = new JsonArray(paths.Select(p => (JsonNode?)(SkillLibrary.Relative(p) is { } r ? JsonValue.Create(r) : null)).ToArray());
        Assert.Equal(Fixtures.Dumps(python["relative"]), Fixtures.Dumps(relative));
        var split = new JsonArray(texts.Select(text =>
        {
            var (fields, body) = SkillLibrary.SplitFrontMatter(text);
            var obj = new JsonObject();
            foreach (var (key, value) in fields)
            {
                obj[key] = value;
            }

            return (JsonNode?)new JsonArray(obj, body);
        }).ToArray());
        Assert.Equal(Fixtures.Dumps(python["split"]), Fixtures.Dumps(split));
    }

    [Fact]
    public void A_skill_tree_is_listed_loaded_and_read_as_python_does()
    {
        using var temp = new TempRoot();
        var root = temp.Path;
        Write(root, "agents/margo.agent.md", "---\nname: margo\n---\n\n# Margo (fictional)\r\nPersona line\r\n");
        Write(root, "skills/zeta/SKILL.md", "---\nname: zeta\ndescription: Last by name\n---\nZeta body\n");
        Write(root, "skills/Alpha/SKILL.md", "---\ndescription: \"No name, upper-case directory\"\n---\nAlpha body\n");
        Write(root, "skills/.hidden/SKILL.md", "---\nname: hidden\ndescription: A hidden skill\n---\nHidden body\n");
        Write(root, "skills/no-skill/README.md", "not a skill\n");
        Write(root, "skills/beta/SKILL.md", "---\nname: beta\ndescription: Second\n---\n\nBeta body\n");
        Write(root, "skills/beta/references/a.md", "A\r\nwith CRLF\rand CR\n");
        Write(root, "skills/beta/references-x/b.md", "B\n");
        Write(root, "skills/beta/references/deep/c.md", "C\n");
        Write(root, "skills/beta/.dot/d.md", "D\n");
        Write(root, "skills/beta/.md", "dot md\n");
        Write(root, "skills/beta/state/hidden.md", "state\n");
        Write(root, "skills/beta/scripts/s.md", "scripts\n");
        Write(root, "skills/beta/references/scripts/inner.md", "inner\n");
        Write(root, "skills/beta/caf\u00e9.md", "accent\n");
        Write(root, "skills/beta/Upper.md", "upper\n");
        Write(root, "skills/beta/notes.txt", "not markdown\n");
        Write(root, "skills/beta/big.md", new string('y', SkillLibrary.MaxFileChars + 5) + "\n");
        Write(root, "skills/beta/astral.md", new string('z', SkillLibrary.MaxFileChars - 1) + "\U0001F600\U0001F600tail");
        Write(root, "skills/chief-of-staff/SKILL.md", "---\nname: chief-of-staff\ndescription: Fictional\n---\nBody\n");
        Write(root, "skills/chief-of-staff/preferences.md", "repository copy, never served\n");
        Write(root, "skills/chief-of-staff/commitments.md", "never served\n");
        Directory.CreateDirectory(Path.Combine(root, "outside"));
        Write(root, "outside/secret.md", "outside\n");
        File.CreateSymbolicLink(Path.Combine(root, "skills/beta/link.md"), Path.Combine(root, "outside/secret.md"));
        Directory.CreateSymbolicLink(Path.Combine(root, "skills/beta/linked-dir"), Path.Combine(root, "outside"));
        var prefs = Path.Combine(root, "private-preferences.md");
        File.WriteAllText(prefs, "private\r\npreferences\n");

        string[] names = ["beta", " BETA ", "alpha", "Alpha", "zeta", "hidden", "chief-of-staff", "missing", ""];
        string[][] reads =
        [
            ["beta", "references/a.md"], ["beta", "a"], ["beta", "references-x/b"], ["beta", "deep/c"], ["beta", "references/deep/c"],
            ["beta", ".dot/d"], ["beta", ".md"], ["beta", "state/hidden"], ["beta", "scripts/s"], ["beta", "references/scripts/inner"],
            ["beta", "caf\u00e9"], ["beta", "Upper.md"], ["beta", "upper.md"], ["beta", "notes.txt"], ["beta", "big"], ["beta", "astral"],
            ["beta", "link"], ["beta", "linked-dir/secret"], ["beta", "SKILL.md"], ["beta", "../SKILL.md"], ["Alpha", "SKILL"],
            ["chief-of-staff", "preferences"], ["chief-of-staff", "../preferences.md"], ["chief-of-staff", "commitments"],
            ["missing", "x"],
        ];
        var request = new JsonObject
        {
            ["library"] = new JsonObject
            {
                ["root"] = root,
                ["preferences"] = prefs,
                ["names"] = PyNode.StrList(names),
                ["reads"] = new JsonArray(reads.Select(pair => (JsonNode?)PyNode.StrList(pair)).ToArray()),
            },
            ["instructions"] = new JsonArray(new JsonObject
            {
                ["root"] = root, ["preferences"] = prefs, ["timezone"] = "Europe/Istanbul", ["now"] = "2026-03-29T00:30:00+00:00",
            }),
        };
        var python = Programs.AskPython(request);
        var expected = (JsonObject)python["library"]!;

        var library = new SkillLibrary(root, prefs);
        Assert.Equal(PyNode.AsString(expected["persona"]), library.Persona());
        Assert.Equal(Fixtures.Dumps(expected["skills"]),
                     Fixtures.Dumps(new JsonArray(library.Skills().Select(s => (JsonNode?)new JsonArray(s.Name, s.Description, Path.GetFileName(s.Directory))).ToArray())));
        Assert.Equal(PyNode.AsString(expected["index"]), library.Index());
        Assert.Equal(Fixtures.Dumps(expected["files"]), Fixtures.Dumps(new JsonArray(names.Select(n => (JsonNode?)PyNode.StrList(library.Files(n))).ToArray())));
        Assert.Equal(Fixtures.Dumps(expected["load"]), Fixtures.Dumps(new JsonArray(names.Select(n => (JsonNode?)library.Load(n)).ToArray())));
        Assert.Equal(Fixtures.Dumps(expected["read"]), Fixtures.Dumps(new JsonArray(reads.Select(r => (JsonNode?)library.Read(r[0], r[1])).ToArray())));
        Assert.Equal(PyNode.AsString(python["instructions"]![0]),
                     EngineTools.LoadInstructions(root, "Europe/Istanbul", DateTimeOffset.Parse("2026-03-29T00:30:00+00:00", System.Globalization.CultureInfo.InvariantCulture), library));
    }

    [Fact]
    public void Undecodable_files_fail_as_python_read_text_does()
    {
        using var temp = new TempRoot();
        Write(temp.Path, "skills/beta/SKILL.md", "---\nname: beta\n---\nBody\n");
        File.WriteAllBytes(temp.Combine("skills", "beta", "bad.md"), [0x41, 0xFF, 0x42]);
        var library = new SkillLibrary(temp.Path);
        var error = Assert.Throws<PyUnicodeError>(() => library.Read("beta", "bad"));
        Assert.Equal("UnicodeDecodeError", error.PythonType);
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
    }
}
