using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine.Tests;

/// <summary>
/// <c>tests/test_autopilot_parity.py</c>: hosted Margo is desktop Margo, with the same persona and skills and every
/// difference named. Plus what only the C# port can drift on: every <c>margo-state</c> command the skills show must be
/// classified and actually implemented by the C# CLI, and the tables, tools and instructions must match the Python
/// modules.
/// </summary>
public sealed partial class ParityTests
{
    /// <summary>
    /// Hosted scripts whose C# port has not landed in <c>margo-state</c> yet. The checks that need the CLI skip (never
    /// pass) while one of these is missing, and run in full as soon as it is there. Any other missing script fails.
    /// </summary>
    private static readonly HashSet<string> PendingCliScripts = new(StringComparer.Ordinal) { };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<(int, string, string)>> PythonRuns = new(StringComparer.Ordinal);

    private static readonly string[] ProcedureRoots = ["skills", "automations", "agents"];

    /// <summary>Programs a desktop procedure might run (<c>PROGRAMS</c>).</summary>
    private static readonly HashSet<string> ProgramNames = new(StringComparer.Ordinal)
    {
        "gh", "az", "git", "curl", "jq", "npm", "npx", "node", "pip", "pip3", "docker", "kubectl", "pwsh",
        "bash", "sh", "zsh", "wget", "ssh", "osascript", "open", "pbcopy", "powershell",
    };

    private readonly SkillLibrary _library = new(Fixtures.Root);

    // -- PersonaAndSkillsAreUnchanged ----------------------------------------------------------------

    [Fact]
    public void The_persona_is_loaded_word_for_word()
    {
        var persona = SkillLibrary.SplitFrontMatter(File.ReadAllText(Path.Combine(Fixtures.Root, "agents", "margo.agent.md"))).Body;
        Assert.NotEmpty(PyRepr.Strip(persona));
        Assert.StartsWith(persona, Instructions(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_skill_is_listed_and_loads_word_for_word()
    {
        var instructions = Instructions();
        var directories = Directory.GetDirectories(Path.Combine(Fixtures.Root, "skills"))
            .Where(directory => File.Exists(Path.Combine(directory, "SKILL.md")))
            .Select(directory => Path.GetFileName(directory)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(directories, _library.Skills().Select(skill => Path.GetFileName(skill.Directory)).Order(StringComparer.Ordinal).ToList());
        Assert.True(directories.Count >= 2);
        foreach (var skill in _library.Skills())
        {
            Assert.Contains("- **" + skill.Name + "**: " + skill.Description, instructions, StringComparison.Ordinal);
            Assert.NotEmpty(skill.Description);
            var text = File.ReadAllText(Path.Combine(skill.Directory, "SKILL.md"));
            Assert.Equal(SkillLibrary.SplitFrontMatter(text).Body, PyNode.AsString(_library.Load(skill.Name)["text"]));
        }
    }

    [Fact]
    public void Every_skill_file_is_reachable()
    {
        var checkedFiles = 0;
        foreach (var skill in _library.Skills())
        {
            foreach (var path in Directory.GetFiles(skill.Directory, "*.md", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(skill.Directory, path).Replace('\\', '/');
                if (SkillLibrary.ExcludedDirs.Contains(relative.Split('/')[0])
                    || (skill.Name, relative) == SkillLibrary.PrivatePreferences || (skill.Name, relative) == SkillLibrary.LedgerOnly)
                {
                    continue;
                }

                var result = _library.Read(skill.Name, relative);
                Assert.True(PyNode.AsString(result["status"]) == "ok", skill.Name + "/" + relative + ": " + Fixtures.Dumps(result));
                Assert.Equal(PyText.Prefix(File.ReadAllText(path), SkillLibrary.MaxFileChars), PyNode.AsString(result["text"]));
                checkedFiles++;
            }
        }

        Assert.True(checkedFiles > 10, "the skills' reference files were not found");
    }

    [Fact]
    public void The_image_carries_the_persona_and_every_skill()
    {
        // Python checks autopilot/Dockerfile. The C# image is built from cloud/ (Margo.Cloud.Host owns it); it must
        // carry the same agents/ and skills/ the engine serves, and margo-state next to the host.
        var dockerfiles = Directory.Exists(Path.Combine(Fixtures.Root, "cloud"))
            ? Directory.GetFiles(Path.Combine(Fixtures.Root, "cloud"), "Dockerfile*", SearchOption.AllDirectories)
                .Where(path => !path.Contains("/bin/", StringComparison.Ordinal) && !path.Contains("/obj/", StringComparison.Ordinal)).ToList()
            : [];
        Assert.SkipWhen(dockerfiles.Count == 0, "Margo Cloud has no container image definition yet (Margo.Cloud.Host owns it); "
                                                + "when it does, this checks that it copies agents/ and skills/");
        foreach (var dockerfile in dockerfiles)
        {
            var text = File.ReadAllText(dockerfile);
            Assert.Matches(@"COPY\s+agents/\s", text);
            Assert.Matches(@"COPY\s+skills/\s", text);
        }
    }

    // -- EveryDifferenceIsNamed ----------------------------------------------------------------------

    [Fact]
    public void Every_script_a_procedure_names_is_hosted_or_listed()
    {
        var named = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in Procedures())
        {
            foreach (Match match in ScriptName().Matches(text))
            {
                named.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(named);
        foreach (var script in named)
        {
            Assert.True(StateCommands.Commands.ContainsKey(script) || NotHosted.Scripts.ContainsKey(script),
                        script + " is used by a procedure but neither hosted nor listed as not hosted");
        }

        foreach (var (script, reason) in NotHosted.Scripts)
        {
            Assert.NotEmpty(PyRepr.Strip(reason));
            Assert.False(StateCommands.Commands.ContainsKey(script));
        }
    }

    [Fact]
    public void Every_subcommand_of_a_hosted_script_is_classified()
    {
        var python = (JsonObject)Programs.AskPython(new JsonObject { ["subcommands"] = true })["subcommands"]!;
        Assert.Equal(PyText.Sorted(StateCommands.Stateful), python.Select(pair => pair.Key).ToList());
        foreach (var script in StateCommands.Stateful)
        {
            var available = PyNode.Iter(python[script]).Select(name => PyNode.AsString(name)!).ToHashSet(StringComparer.Ordinal);
            var (classified, listed) = Classified(script);
            Assert.True(available.Except(classified).Except(listed).Order(StringComparer.Ordinal).ToList() is [],
                        script + ": new subcommands must be classified in StateCommands.Commands or NotHostedCommands");
            Assert.True(classified.Union(listed).Except(available).Order(StringComparer.Ordinal).ToList() is [],
                        script + ": classified commands that no longer exist");
            Assert.Empty(classified.Intersect(listed));
        }

        foreach (var script in StateCommands.Commands.Keys.Where(script => !StateCommands.Stateful.Contains(script)))
        {
            var classes = StateCommands.Commands[script];
            Assert.Single(classes);
            Assert.Equal(StateCommandClasses.Read, classes[0].Key);
            Assert.Equal([null], classes[0].Value.ToArray());
            Assert.True(File.Exists(Path.Combine(Fixtures.Root, "skills", "chief-of-staff", "scripts", script)));
        }
    }

    [Fact]
    public void Every_hosted_command_is_implemented_by_the_cli()
    {
        var (status, help, _) = Programs.Cli("--help");
        Assert.Equal(0, status);
        var implemented = CliScripts(help);
        Assert.Empty(PendingCliScripts.Intersect(implemented)); // a script that landed is checked in full, never skipped
        var parsers = new List<string[]>();
        foreach (var script in StateCommands.Commands.Keys.Where(script => !StateCommands.Stateful.Contains(script)))
        {
            Assert.True(implemented.Contains(script), "margo-state does not implement " + script);
            var (code, output, error) = Programs.Cli(script, "--help");
            Assert.True(code == 0 && output.StartsWith("usage: " + script, StringComparison.Ordinal), script + " --help: " + output + error);
            parsers.Add([script]);
        }

        var pending = new List<string>();
        foreach (var script in PyText.Sorted(StateCommands.Stateful))
        {
            if (!implemented.Contains(script))
            {
                Assert.True(PendingCliScripts.Contains(script), "margo-state does not implement " + script);
                pending.Add(script);
                continue;
            }

            var (classified, listed) = Classified(script);
            Assert.Equal(PyText.Sorted(classified.Union(listed)), PyText.Sorted(CliSubcommands(script)));
            parsers.AddRange(PyText.Sorted(classified.Union(listed)).Select(command => new[] { script, command }));
        }

        // Implemented means the same parser, not just the same name: each subcommand's options, choices and required
        // arguments, as its `--help` prints them, are the Python script's.
        AssertSameParsers(parsers);

        Assert.SkipWhen(pending.Count > 0, "margo-state does not implement " + string.Join(" and ", pending)
                                           + " yet; their commands are classified against the Python scripts only");
    }

    [Fact]
    public void Every_state_command_a_procedure_shows_is_classified_and_implemented()
    {
        // A procedure shows a command in two ways: after its script (`work_state.py --account A list --view decisions`),
        // and bare in a file about that script (`create KEY --input FILE` in task-runs.md, which names task_state.py).
        // A bare word counts when it is a subcommand of a stateful script the same file names.
        var subcommands = (JsonObject)Programs.AskPython(new JsonObject { ["subcommands"] = true })["subcommands"]!;
        var shown = new SortedDictionary<(string Script, string Command), SortedSet<string>>();
        void Show(string script, string command, string rest)
        {
            if (!shown.TryGetValue((script, command), out var options))
            {
                shown[(script, command)] = options = new SortedSet<string>(StringComparer.Ordinal);
            }

            options.UnionWith(ShownOption().Matches(rest).Select(match => match.Groups[1].Value));
        }

        foreach (var (_, text) in Procedures())
        {
            foreach (var span in CodeSpans(text))
            {
                foreach (Match match in ShownCommand().Matches(span))
                {
                    if (StateCommands.Stateful.Contains(match.Groups[1].Value))
                    {
                        Show(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
                    }
                }
            }

            var named = StateCommands.Stateful.Where(script => text.Contains(script, StringComparison.Ordinal)).ToList();
            foreach (Match inline in Inline().Matches(FencedWhole().Replace(text, "")))
            {
                if (BareCommand().Match(inline.Groups[1].Value) is not { Success: true } bare)
                {
                    continue;
                }

                foreach (var script in named.Where(script => PyNode.Iter(subcommands[script]).Any(name => PyNode.Is(name, bare.Groups[1].Value))))
                {
                    Show(script, bare.Groups[1].Value, bare.Groups[2].Value);
                }
            }
        }

        // The scan itself must keep finding real commands, in both forms and with their options.
        Assert.Contains("--view", shown[("work_state.py", "list")]);
        Assert.Contains("--json", shown[("proactive_state.py", "queue-add")]);
        Assert.Contains("--input", shown[("task_state.py", "create")]);
        Assert.Contains(("memory_state.py", "dream-plan"), shown.Keys);
        var (_, help, _) = Programs.Cli("--help");
        var implemented = CliScripts(help);
        var pending = new SortedSet<string>(StringComparer.Ordinal);
        var parsers = new List<string[]>();
        foreach (var ((script, command), options) in shown)
        {
            var (classified, listed) = Classified(script);
            Assert.True(classified.Contains(command) || listed.Contains(command),
                        script + " " + command + " is shown by a procedure but neither classified nor listed as not hosted");
            if (!implemented.Contains(script))
            {
                Assert.True(PendingCliScripts.Contains(script), "margo-state does not implement " + script);
                pending.Add(script + " " + command);
                continue;
            }

            // The C# CLI's own parser must accept it: `margo-state SCRIPT --help` lists it among its subcommands, its own
            // `--help` is the Python script's, and it takes every option the procedure shows with it.
            Assert.True(CliSubcommands(script).Contains(command, StringComparer.Ordinal),
                        "margo-state " + script + " does not implement " + command + ", which a procedure shows");
            parsers.Add([script, command]);
            var usage = Programs.Cli(script, command, "--help").Stdout;
            foreach (var option in options)
            {
                Assert.True(usage.Contains(option, StringComparison.Ordinal),
                            "margo-state " + script + " " + command + " does not take " + option + ", which a procedure shows with it");
            }
        }

        AssertSameParsers(parsers);

        Assert.SkipWhen(pending.Count > 0, "margo-state does not implement these commands the skills show yet: " + string.Join(", ", pending));
    }

    [Fact]
    public void Every_program_a_procedure_runs_is_listed()
    {
        var seen = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, text) in Procedures())
        {
            foreach (var span in CodeSpans(text))
            {
                foreach (Match match in CommandPosition().Matches(span))
                {
                    var program = match.Groups[1].Value;
                    if (ProgramNames.Contains(program))
                    {
                        seen.TryAdd(program, path);
                    }
                }
            }
        }

        Assert.Contains("gh", seen.Keys); // the scan itself must keep finding real commands
        foreach (var (program, where) in seen)
        {
            Assert.True(NotHosted.Programs.ContainsKey(program), where + " runs " + program + ", which hosted Margo cannot run; list it with a reason");
        }
    }

    [Fact]
    public void Every_work_iq_tool_a_procedure_names_is_offered_or_listed()
    {
        var offered = EngineTools.FunctionTools(RunModes.Attended).Select(tool => PyNode.AsString(tool!["name"])!).ToHashSet(StringComparer.Ordinal);
        var named = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in Procedures())
        {
            foreach (Match match in WorkIQTool().Matches(text))
            {
                named.Add(match.Groups[1].Value);
            }
        }

        Assert.Contains("fetch", named);
        foreach (var tool in named)
        {
            Assert.True(offered.Contains(EngineTools.Prefix + tool) || NotHosted.WorkIQ.Any(item => item.Key == tool),
                        "workiq-" + tool + " is used by a procedure but neither offered nor listed");
        }
    }

    [Fact]
    public void The_model_is_told_everything_that_is_missing()
    {
        var instructions = EngineTools.LoadInstructions(Fixtures.Root);
        foreach (var name in NotHosted.Programs.Keys.Concat(NotHosted.Scripts.Keys))
        {
            Assert.Contains("`" + name + "`", instructions, StringComparison.Ordinal);
        }

        foreach (var (script, command) in StateCommands.NotHostedCommands.Keys)
        {
            Assert.Contains("`" + script + " " + command + "`", instructions, StringComparison.Ordinal);
        }

        foreach (var (tool, _) in NotHosted.WorkIQ)
        {
            Assert.Contains("`workiq-" + tool + "`", instructions, StringComparison.Ordinal);
        }
    }

    // -- the C# port against the Python modules ----------------------------------------------------------

    [Fact]
    public void Tools_instructions_and_lists_match_python()
    {
        string[] modes = [RunModes.Attended, RunModes.Unattended, RunModes.ThirdParty, "not-a-mode"];
        var request = new JsonObject
        {
            ["modes"] = PyNode.StrList(modes),
            ["instructions"] = new JsonArray(
                new JsonObject { ["timezone"] = "America/Los_Angeles", ["now"] = "2026-10-17T08:00:00+00:00" },
                new JsonObject { ["timezone"] = "Asia/Kolkata", ["now"] = "2026-01-01T23:59:59.999999+00:00" },
                new JsonObject { ["timezone"] = "UTC", ["now"] = "2026-02-28T12:05:00-08:00" }),
            ["clip"] = new JsonArray(
                new JsonObject { ["status"] = "ok", ["text"] = "caf\u00e9 \U0001F600 \u2028" },
                new JsonObject { ["data"] = new string('\u00e9', EngineTools.MaxOutputChars) },
                new JsonObject { ["data"] = new string('x', EngineTools.MaxOutputChars - 20) + string.Concat(Enumerable.Repeat("\U0001F600", 8)) },
                new JsonArray(1.5, 1e16, true, null)),
        };
        var python = Programs.AskPython(request);
        foreach (var mode in modes)
        {
            Assert.Equal(Fixtures.Dumps(python["function_tools"]![mode]), Fixtures.Dumps(EngineTools.FunctionTools(mode)));
        }

        Assert.Equal(PyNode.AsString(python["summary"]), NotHosted.Summary());
        var notHosted = new JsonObject
        {
            ["programs"] = Dict(NotHosted.Programs),
            ["scripts"] = Dict(NotHosted.Scripts),
            ["workiq"] = Dict(NotHosted.WorkIQ),
        };
        Assert.Equal(Fixtures.Dumps(python["not_hosted"]), Fixtures.Dumps(notHosted));
        var engine = new JsonArray(EngineTools.Prefix, EngineTools.MaxOutputChars, EngineTools.Stopped, EngineTools.HostedAddendum, EngineTools.StateTool,
                                   SkillLibrary.MaxFileChars, new JsonArray(SkillLibrary.PrivatePreferences.Skill, SkillLibrary.PrivatePreferences.Path),
                                   new JsonArray(SkillLibrary.LedgerOnly.Skill, SkillLibrary.LedgerOnly.Path), PyNode.StrList(PyText.Sorted(SkillLibrary.ExcludedDirs)));
        Assert.Equal(Fixtures.Dumps(python["engine"]), Fixtures.Dumps(engine));
        var instructions = (JsonArray)python["instructions"]!;
        for (var i = 0; i < instructions.Count; i++)
        {
            var spec = request["instructions"]![i]!;
            var now = DateTimeOffset.Parse(PyNode.AsString(spec["now"])!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(PyNode.AsString(instructions[i]), EngineTools.LoadInstructions(Fixtures.Root, PyNode.AsString(spec["timezone"])!, now));
        }

        var clips = (JsonArray)python["clip"]!;
        var mine = ((JsonArray)request["clip"]!).Select(value => EngineTools.Clip(value)).ToList();
        Assert.Equal(clips.Select(clip => PyNode.AsString(clip)!).ToList(), mine);
    }

    [Fact]
    public void An_unknown_time_zone_fails_like_zoneinfo()
    {
        Assert.Throws<TimeZoneNotFoundException>(() => EngineTools.LoadInstructions(Fixtures.Root, "Not/A_Zone"));
        // zoneinfo knows IANA keys only (ZoneInfoNotFoundError). .NET with ICU would convert a Windows ID and show the
        // model "Pacific Standard Time" as the zone; InvariantGlobalization (Directory.Build.props) keeps it out. A guard.
        Assert.Throws<TimeZoneNotFoundException>(() => EngineTools.LoadInstructions(Fixtures.Root, "Pacific Standard Time"));
        Assert.Throws<TimeZoneNotFoundException>(() => EngineTools.LoadInstructions(Fixtures.Root, "W. Europe Standard Time"));
        Assert.Contains("(UTC)", EngineTools.LoadInstructions(Fixtures.Root, "UTC"), StringComparison.Ordinal);
    }

    // -- helpers -------------------------------------------------------------------------------------

    /// <summary>
    /// <c>margo-state SCRIPT [COMMAND] --help</c> and <c>python3 SCRIPT [COMMAND] --help</c> give the same exit status,
    /// output and errors for every parser listed (both read <c>COLUMNS</c> the same way).
    /// </summary>
    private static void AssertSameParsers(IReadOnlyList<string[]> parsers)
    {
        var differences = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(parsers, new ParallelOptions { MaxDegreeOfParallelism = 4 }, parser =>
        {
            var csharp = Programs.Cli([.. parser, "--help"]);
            var python = PythonHelp(parser);
            if (csharp != python)
            {
                differences.Add(string.Join(" ", parser) + " --help:\npython: " + python + "\nC#:     " + csharp);
            }
        });
        Assert.True(differences.IsEmpty, "margo-state parsers differ from the Python scripts':\n" + string.Join("\n", differences.Order(StringComparer.Ordinal)));
    }

    /// <summary><c>python3 -s scripts/SCRIPT [COMMAND] --help</c>, once per test run.</summary>
    private static (int Status, string Stdout, string Stderr) PythonHelp(string[] parser) =>
        PythonRuns.GetOrAdd(string.Join('\0', parser), _ => new Lazy<(int, string, string)>(() =>
            Programs.Run(Programs.Python(), ["-B", "-s", Path.Combine(Fixtures.Root, "skills", "chief-of-staff", "scripts", parser[0]), .. parser[1..], "--help"],
                         environment: new Dictionary<string, string> { ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONIOENCODING"] = "utf-8" }))).Value;

    private static string Instructions() => EngineTools.LoadInstructions(Fixtures.Root, library: new SkillLibrary(Fixtures.Root));

    private static (HashSet<string> Classified, HashSet<string> Listed) Classified(string script) =>
        (StateCommands.Commands[script].SelectMany(pair => pair.Value).Where(name => name is not null).Select(name => name!).ToHashSet(StringComparer.Ordinal),
         StateCommands.NotHostedCommands.Keys.Where(key => key.Script == script).Select(key => key.Command).ToHashSet(StringComparer.Ordinal));

    /// <summary>The scripts <c>margo-state --help</c> lists ("Runs a Margo state script: a.py, b.py").</summary>
    private static HashSet<string> CliScripts(string help)
    {
        var line = help.Split('\n').Single(text => text.StartsWith("Runs a Margo state script: ", StringComparison.Ordinal));
        return line["Runs a Margo state script: ".Length..].Split(", ").ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The subcommand choices in <c>margo-state SCRIPT --help</c>'s usage (<c>{a,b,c} ...</c>).</summary>
    private static List<string> CliSubcommands(string script)
    {
        var (status, help, error) = Programs.Cli(script, "--help");
        Assert.True(status == 0, script + " --help: " + error);
        var usage = help[..help.IndexOf("\n\n", StringComparison.Ordinal)];
        var choices = Choices().Match(usage);
        Assert.True(choices.Success, "no subcommands in the usage of " + script + ": " + usage);
        return [.. choices.Groups[1].Value.Split(',')];
    }

    /// <summary><c>procedures()</c>: every Markdown file under skills/, automations/ and agents/, outside state directories.</summary>
    private static IEnumerable<(string Path, string Text)> Procedures()
    {
        foreach (var top in ProcedureRoots)
        {
            var directory = Path.Combine(Fixtures.Root, top);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(Fixtures.Root, path);
                if (!relative.Split('/').Contains("state"))
                {
                    yield return (relative, File.ReadAllText(path));
                }
            }
        }
    }

    /// <summary><c>code_spans(text)</c>: fenced blocks and inline code, the places a procedure tells the agent to run something.</summary>
    private static List<string> CodeSpans(string text)
    {
        var spans = Fenced().Matches(text).Select(match => match.Groups[1].Value).ToList();
        var stripped = FencedWhole().Replace(text, "");
        spans.AddRange(Inline().Matches(stripped).Select(match => match.Groups[1].Value));
        return spans;
    }

    private static JsonObject Dict(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            obj[key] = value;
        }

        return obj;
    }

    [GeneratedRegex(@"\b([a-z0-9_]+\.py)\b")]
    private static partial Regex ScriptName();

    [GeneratedRegex(@"\bworkiq-([a-z_]+)")]
    private static partial Regex WorkIQTool();

    [GeneratedRegex(@"(?:^|[|;&(]|\$\(|\$ )\s*(?:[A-Z_][A-Z0-9_]*=\S+\s+)*([a-z][a-z0-9]*)\b(?=\s|$)", RegexOptions.Multiline)]
    private static partial Regex CommandPosition();

    /// <summary>
    /// A stateful script followed (after optional service options and line continuations) by its subcommand, then the
    /// rest of that command line (continued lines included).
    /// </summary>
    [GeneratedRegex(@"\b([a-z_]+_state\.py)(?:\s+\\?\s*--(?:account|state-dir|state-root)\s+\S+)*\s+\\?\s*([a-z][a-z-]*)\b(?!-)((?:[ \t]+(?:\\\n[ \t]*)?[^\s`]+)*)")]
    private static partial Regex ShownCommand();

    /// <summary>Inline code that is a bare subcommand and its arguments, e.g. <c>create KEY --input FILE</c>.</summary>
    [GeneratedRegex(@"^\s*([a-z][a-z-]*)(?=\s|$)((?:\s+[^\s`]+)*)\s*$")]
    private static partial Regex BareCommand();

    /// <summary>An option a command line shows (<c>--view</c> in <c>--view decisions</c> or <c>--view=decisions</c>).</summary>
    [GeneratedRegex(@"(?:^|\s)(--[a-z][a-z0-9-]*)(?=[\s=]|$)")]
    private static partial Regex ShownOption();

    [GeneratedRegex(@"```[^\n]*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex Fenced();

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex FencedWhole();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex Inline();

    [GeneratedRegex(@"\{([a-z0-9,-]+)\}")]
    private static partial Regex Choices();
}
