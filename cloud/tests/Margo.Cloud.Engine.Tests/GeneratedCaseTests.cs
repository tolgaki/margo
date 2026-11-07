using System.Globalization;
using System.Text.Json.Nodes;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine.Tests;

/// <summary>
/// Generated inputs nobody wrote by hand, answered by the Python modules (the specification) and by the port, and
/// compared as text: <c>margo-state</c> calls with hostile scripts, arguments, files, input and modes; what a command
/// prints, in any bytes, with any exit status; model function calls with odd names and arguments; whole turns of odd
/// model responses; and the skill path and front matter rules. Each generator has a fixed seed, so a failure always
/// reproduces; set <c>MARGO_ENGINE_SEED</c> to try others. Known deliberate deviations (PORTING.md) are kept out of the
/// generators or normalised where they are compared, and say so.
/// </summary>
public sealed class GeneratedCaseTests
{
    private static readonly string[] Scripts =
    [
        "work_state.py", "work_state", "scripts/work_state.py", "scripts/work_state", "proactive_state.py", "memory_state.py",
        "task_state.py", "task_state", "capacity.py", "capacity", "teams_feedback.py", "engage_parse.py", "margo_doctor.py",
        "m365_files", "memory_encoder.py", "margo_store", "rm", "", ".py", "WORK_STATE.PY", "work_state.py ", "scripts/scripts/work_state.py",
        "work_ledger_probe.py", "helpers_probe", "../work_state.py", "/work_state.py", "work_state.py\u0000",
    ];

    // Lone surrogates in U+DC80..U+DCFF are left out: Python passes them to the CLI as raw bytes (surrogateescape),
    // which .NET cannot (PORTING.md, deviation 6).
    private static readonly string[] Tokens =
    [
        "list", "show", "source", "ingest", "item-update", "approve", "begin", "import-preview", "status", "queue-add", "publication-review",
        "import-legacy", "init", "migrate", "export", "context", "search", "capture", "put", "health", "create", "pause", "replan", "record-id",
        "nope", "LIST", "--account", "--account=x", "--state-dir", "--state-dir=/tmp", "--state-root=x", "--config-dir", "--config-dir=",
        "--acc", "--accounts", "-x", "--input", "-", "@file:a.json", "@file:b", "@file:", "--input=@file:a.json", "--input=@file:zz",
        "@preferences", "--input=@preferences", "--x=@preferences", "/etc/passwd", "~/x", "./x", "..", "../x", "a/../b", "a/..", "\\x",
        "a\u0000b", "--a\u0000=b", "--a=b\u0000", "\u00e9\U0001F600", "\ud800", "", "--x=--account", "--json", "{\"a\":1}", "--view",
        "decisions", "=", "--=", "--=/etc", "--input=", "--input==/x", "-i/etc/passwd", "@FILE:a.json", " @file:a.json", "@file:a.json ",
        "x/../../y", "~", "--account ", " --account", "--Account", "--state-dir\u0000", "--input=~/x", "--input=./x", "--input=..",
        "@preferences=x",
    ];

    private static readonly string[] FileNames = ["a.json", "b", ".hidden", "a/b", "..", "a..", "x.y-z_1", "\u00e9", "a\u0000", "", "A", "9", "-a", "_a", "zz"];

    private static readonly string?[] Modes = [RunModes.Attended, RunModes.Unattended, RunModes.ThirdParty, "host", null, "ATTENDED", ""];

    private static Random Seeded(int seed) =>
        new(int.TryParse(Environment.GetEnvironmentVariable("MARGO_ENGINE_SEED"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var given) ? given + seed : seed);

    // -- model client: retry-after ---------------------------------------------------------------------

    private static readonly string[] RetryPieces =
    [
        "Wed,", "Wed", "wednesday,", "Fri,", "21", "01", "1", "Jan", "jan", "JANUARY", "Oct", "October", "Dec", "Foo", "2099", "99", "68",
        "69", "0", "2015", "10000", "07:28:00", "7:28", "07.28.00", "07.28", "7.28.0.1", "12:00:00,", "25:61:61", "GMT", "UTC", "Z",
        "PST", "pdt", "EST", "+0000", "-0000", "+0530", "-0800", "+5", "foo", "1_000", "+12", "21-Oct-99", "01-Jan-2099", "-", ",", "2099,",
        "Jan,", "21,", "١٢", "²", "2099-01-01T00:00:00Z", "2099-01-01", "01/01/2099", "inf", "nan", "1e3", " ",
        "5", "61", "120", "121", " ", "\t", "99999999999999999999999",
    ];

    [Fact]
    public void Generated_retry_after_headers_are_read_as_the_sdk_does()
    {
        var random = Seeded(55);
        var cases = new JsonArray
        {
            new JsonArray(null, "Wed, 21 Oct 2099 07:28:00 GMT", 1700000000.5),
            new JsonArray(null, "2099-01-01T00:00:00Z", 1700000000.5),
            new JsonArray(null, "Jan 1 2099", 1700000000.5),
            new JsonArray("1_500", "9", 1700000000.5),
            new JsonArray(null, "Friday, 01-Jan-99 00:00:00 GMT", 1700000000.5),
            new JsonArray(null, "1 Jan 2099 00:00 -0800", 1700000000.5),
            new JsonArray(null, "Wed, " + new string('9', 400) + " Jan 2099 00:00 GMT", 1700000000.5),
        };
        for (var i = 0; i < 4000; i++)
        {
            var count = random.Next(1, 7);
            var text = string.Join(random.Next(4) == 0 ? "  " : " ", Enumerable.Range(0, count).Select(_ => RetryPieces[random.Next(RetryPieces.Length)]));
            var ms = random.Next(8) == 0 ? RetryPieces[random.Next(RetryPieces.Length)] : null;
            cases.Add(new JsonArray(ms, random.Next(20) == 0 ? null : text, 1700000000 + (random.Next(1000) / 1000.0)));
        }

        var python = (JsonArray)Programs.AskPython(new JsonObject { ["retry_after"] = PyNode.Copy(cases) })["retry_after"]!;
        var failures = new List<string>();
        var overflows = 0;
        for (var i = 0; i < cases.Count; i++)
        {
            var item = (JsonArray)cases[i]!;
            var now = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(item[2]!.GetValue<double>() * 1000));
            var expected = python[i];
            if (PyNode.AsString(expected) is { } overflow && overflow.StartsWith("OverflowError: ", StringComparison.Ordinal))
            {
                // The SDK lets an OverflowError escape for a date too large for a float; so does the port.
                var error = Assert.Throws<PyOverflowError>(() => AzureOpenAIModelClient.ParseRetryAfter(PyNode.AsString(item[0]), PyNode.AsString(item[1]), now));
                Assert.Equal(overflow["OverflowError: ".Length..], error.Message);
                overflows++;
                continue;
            }

            var seconds = AzureOpenAIModelClient.ParseRetryAfter(PyNode.AsString(item[0]), PyNode.AsString(item[1]), now);
            Compare(failures, item, expected, seconds is { } value ? JsonValue.Create(value) : null);
        }

        Assert.True(failures.Count == 0, failures.Count + " of " + cases.Count + " differ:\n" + string.Join("\n\n", failures.Take(10)));
        Assert.True(overflows > 0);
    }

    // -- margo-state: the policy ----------------------------------------------------------------------

    [Fact]
    public async Task Generated_state_calls_are_refused_or_run_as_python_does()
    {
        using var temp = new TempRoot();
        var prefs = temp.Combine("preferences.md");
        File.WriteAllText(prefs, "fictional preferences\n");
        var random = Seeded(100);
        var cases = new JsonArray();
        for (var i = 0; i < 3000; i++)
        {
            cases.Add(new JsonArray(StateArguments(random), Modes[random.Next(Modes.Length)] is { } mode ? JsonValue.Create(mode) : null));
        }

        // Every case that passes the checks ends in "could not start the command", on both sides.
        var python = (JsonArray)Programs.AskPython(new JsonObject { ["state"] = PyNode.Copy(cases), ["state_preferences"] = prefs })["state"]!;
        var failures = new List<string>();
        for (var i = 0; i < cases.Count; i++)
        {
            var receipts = new List<StateReceipt>();
            var state = new StateCommands(Fixtures.Binding.Account, "/nonexistent-margo-state", prefs, recorder: receipts.Add,
                                          cliCommand: ["/nonexistent-margo-python"]);
            var mine = await RunState(state, cases[i]!, receipts);
            Compare(failures, cases[i], python[i], mine);
        }

        Assert.True(failures.Count == 0, failures.Count + " of " + cases.Count + " differ:\n" + string.Join("\n\n", failures.Take(10)));
    }

    // -- margo-state: what a command prints ----------------------------------------------------------

    [Fact]
    public async Task Generated_command_outcomes_are_reported_as_python_does()
    {
        using var temp = new TempRoot();
        var fake = temp.Combine("fake");
        var specs = temp.Combine("specs");
        Directory.CreateDirectory(fake);
        Directory.CreateDirectory(specs);
        // One fake for both sides: it prints the bytes and exits with the status its spec (the last argument) names.
        var runner = "import json, os, sys\nspec = json.load(open(" + PyRepr.Str(specs) + " + '/' + sys.argv[-1] + '.json'))\n"
                     + "sys.stdout.buffer.write(bytes.fromhex(spec['out'])); sys.stdout.flush()\n"
                     + "sys.stderr.buffer.write(bytes.fromhex(spec['err'])); sys.stderr.flush()\nos._exit(spec['code'])\n";
        foreach (var name in new[] { "work_state.py", "capacity.py", "runner.py" })
        {
            File.WriteAllText(Path.Combine(fake, name), runner);
        }

        var random = Seeded(7);
        int[] codes = [0, 0, 0, 1, 2, 3, 9, 255];
        string[][] commands = [["work_state.py", "list"], ["work_state.py", "source"], ["capacity.py", ""], ["work_state.py", "show"], ["work_state.py", "ingest"]];
        var cases = new JsonArray();
        for (var i = 0; i < 200; i++)
        {
            var id = "spec-" + i.ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(specs, id + ".json"), PyJson.Dumps(new JsonObject
            {
                ["out"] = Convert.ToHexString(Printed(random, Outputs)),
                ["err"] = Convert.ToHexString(Printed(random, Errors)),
                ["code"] = codes[random.Next(codes.Length)],
            }));
            var command = commands[random.Next(commands.Length)];
            var args = command[1].Length > 0 ? new JsonArray(command[1], id) : new JsonArray(id);
            cases.Add(new JsonArray(new JsonObject { ["script"] = command[0], ["args"] = args }, RunModes.Attended));
        }

        var python = (JsonArray)Programs.AskPython(new JsonObject
        {
            ["state_exec"] = new JsonObject { ["scripts_dir"] = fake, ["cases"] = PyNode.Copy(cases) },
        })["state_exec"]!;
        var failures = new List<string>();
        for (var i = 0; i < cases.Count; i++)
        {
            var receipts = new List<StateReceipt>();
            var state = new StateCommands(Fixtures.Binding.Account, "/nonexistent-margo-state", recorder: receipts.Add,
                                          cliCommand: [Programs.Python(), "-s", Path.Combine(fake, "runner.py")]);
            var mine = await RunState(state, cases[i]!, receipts);
            Compare(failures, File.ReadAllText(Path.Combine(specs, "spec-" + i.ToString(CultureInfo.InvariantCulture) + ".json")), python[i], mine);
        }

        Assert.True(failures.Count == 0, failures.Count + " of " + cases.Count + " differ:\n" + string.Join("\n\n", failures.Take(10)));
    }

    // -- function calls ------------------------------------------------------------------------------

    [Fact]
    public async Task Generated_function_calls_are_dispatched_as_python_does()
    {
        var random = Seeded(33);
        string[] names =
        [
            "workiq-fetch", "workiq-fetch_blob", "workiq-call_function", "workiq-create_entity", "workiq-update_entity", "workiq-delete_entity",
            "workiq-do_action", "workiq-ask", "workiq-retrieve", "workiq-list_agents", "workiq-get_schema", "workiq-search_paths", "workiq-",
            "workiq-nothing", "WORKIQ-fetch", "workiq-fetch ", "load_skill", "read_skill_file", "margo-state", "office_requests", "shell",
            "Load_Skill", "", "workiq-workiq-fetch",
        ];
        string[] raws =
        [
            "{not json", "", "[1]", "\"text\"", "null", "0", "{}", " {} ", "\ufeff{}", "{\"a\": NaN}", "{\"name\": \"decision-log\", \"name\": \"chief-of-staff\"}",
        ];
        var cases = new JsonArray();
        for (var i = 0; i < 3000; i++)
        {
            var name = random.Next(15) == 0 ? Odd(random) : JsonValue.Create(names[random.Next(names.Length)]);
            var raw = random.Next(10) switch
            {
                0 => Odd(random),
                1 => raws[random.Next(raws.Length)],
                _ => JsonValue.Create(PyJson.Dumps(ToolArguments(random))),
            };
            var mode = Modes[random.Next(Modes.Length)];
            var office = random.Next(3) switch { 0 => null, 1 => "ok", _ => "broken" };
            cases.Add(new JsonArray(name, raw, mode is null ? null : JsonValue.Create(mode), office is null ? null : JsonValue.Create(office)));
        }

        var python = (JsonArray)Programs.AskPython(new JsonObject { ["dispatch"] = PyNode.Copy(cases) })["dispatch"]!;
        var library = new SkillLibrary(Fixtures.Root);
        var failures = new List<string>();
        for (var i = 0; i < cases.Count; i++)
        {
            var transport = new RecordingTransport();
            var guard = new WorkIQGuard(Fixtures.Binding, new FakeTokens(), transport);
            var office = PyNode.AsString(cases[i]![3]) is { } kind ? new PythonOffice(kind == "broken") : null;
            var result = await Engine.DispatchAsync(PyNode.Copy(cases[i]![0]), PyNode.Copy(cases[i]![1]), PyNode.AsString(cases[i]![2]), guard, office,
                                                    library, null, TestContext.Current.CancellationToken);
            Compare(failures, cases[i], python[i], new JsonObject { ["result"] = result, ["sent"] = Sent(transport) });
        }

        Assert.True(failures.Count == 0, failures.Count + " of " + cases.Count + " differ:\n" + string.Join("\n\n", failures.Take(10)));
    }

    // -- whole turns ---------------------------------------------------------------------------------

    [Fact]
    public async Task Generated_turns_send_the_same_requests_and_end_the_same_way()
    {
        var random = Seeded(55);
        var cases = new JsonArray();
        for (var i = 0; i < 800; i++)
        {
            var maxRounds = random.Next(0, 4);
            var script = new JsonArray();
            for (var j = 0; j < maxRounds + 2; j++)
            {
                script.Add(ModelResponse(random, final: false));
            }

            script.Add(ModelResponse(random, final: true));
            var mode = new[] { RunModes.Attended, RunModes.Unattended, RunModes.ThirdParty }[random.Next(3)];
            var previous = random.Next(3) switch { 0 => null, 1 => "", _ => "r0" };
            cases.Add(new JsonObject { ["script"] = script, ["mode"] = mode, ["max_rounds"] = maxRounds, ["text"] = "Brief me \u00e9", ["previous"] = previous });
        }

        var python = (JsonArray)Programs.AskPython(new JsonObject { ["runs"] = PyNode.Copy(cases) })["runs"]!;
        var failures = new List<string>();
        for (var i = 0; i < cases.Count; i++)
        {
            var spec = cases[i]!;
            var model = new FakeModel([.. ((JsonArray)spec["script"]!).Select(node => (object?)node)]);
            var transport = new RecordingTransport();
            var guard = new WorkIQGuard(Fixtures.Binding, new FakeTokens(), transport);
            var runner = new Engine(model, "fictional-model", Fixtures.Root, maxRounds: (int)spec["max_rounds"]!);
            JsonNode result;
            try
            {
                result = (await runner.RunAsync(PyNode.AsString(spec["text"])!, PyNode.AsString(spec["mode"]), guard, new PythonOffice(false), null,
                                                PyNode.AsString(spec["previous"]), new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero),
                                                cancellationToken: TestContext.Current.CancellationToken)).ToJsonObject();
            }
            catch (MalformedModelResponseException)
            {
                result = new JsonObject { ["raised"] = "malformed" }; // Python: AttributeError or TypeError
            }

            var requests = new JsonArray([.. model.Requests.Select(request =>
            {
                request.Remove("instructions"); // identical in every request; compared by ParityTests
                request.Remove("tools");
                return (JsonNode?)request;
            })]);
            if (python[i]!["result"] is JsonObject expected && expected.ContainsKey("response_id") && PyNode.AsString(expected["response_id"]) is null)
            {
                expected["response_id"] = null; // a non-string id is not kept (PORTING.md, deviation 7)
            }

            Compare(failures, spec, python[i], new JsonObject { ["result"] = result, ["requests"] = requests, ["sent"] = Sent(transport) });
        }

        Assert.True(failures.Count == 0, failures.Count + " of " + cases.Count + " differ:\n" + string.Join("\n\n", failures.Take(8)));
    }

    // -- skill paths and front matter ----------------------------------------------------------------

    [Fact]
    public void Generated_skill_paths_and_front_matter_are_read_as_python_does()
    {
        var random = Seeded(77);
        string[] pathParts =
        [
            "/", ".", "a", "\\", " ", "\t", "\n", "\r", "\u0000", "..", "md", ".md", "\u00e9", "\u2028", "\u001c", "\u0085", "\u3000", "../",
            "/../", "references", "x", "\ud800", "\U0001F600", "\u000b", "\u00a0",
        ];
        var paths = new JsonArray();
        for (var i = 0; i < 8000; i++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(0, 9)).Select(_ => pathParts[random.Next(pathParts.Length)]));
            paths.Add(random.Next(50) == 0 ? text + new string('y', 505 + random.Next(0, 10)) : text); // around the 512 limit
        }

        string[] matterParts =
        [
            "---", "\n", "name", ":", " ", "\"", "\\", "chief", "description", "\t", "---\n", "\n---", "\r\n", "\u2028", "x", "\"a\"",
            "\"\\u00e9\"", "\u0085", "key: value",
        ];
        var texts = new JsonArray();
        for (var i = 0; i < 8000; i++)
        {
            texts.Add((random.Next(3) > 0 ? "---" : "") + string.Concat(Enumerable.Range(0, random.Next(0, 14)).Select(_ => matterParts[random.Next(matterParts.Length)])));
        }

        var python = Programs.AskPython(new JsonObject { ["relative"] = PyNode.Copy(paths), ["split"] = PyNode.Copy(texts) });
        var failures = new List<string>();
        var relative = (JsonArray)python["relative"]!;
        for (var i = 0; i < paths.Count; i++)
        {
            Compare(failures, paths[i], relative[i], JsonValue.Create(SkillLibrary.Relative(paths[i])));
        }

        var split = (JsonArray)python["split"]!;
        for (var i = 0; i < texts.Count; i++)
        {
            var (fields, body) = SkillLibrary.SplitFrontMatter(PyNode.AsString(texts[i])!);
            var parsed = new JsonObject();
            foreach (var (key, value) in fields)
            {
                parsed[key] = value;
            }

            Compare(failures, texts[i], split[i], new JsonArray(parsed, body));
        }

        Assert.True(failures.Count == 0, failures.Count + " differ:\n" + string.Join("\n", failures.Take(15)));
    }

    // -- generators ----------------------------------------------------------------------------------

    private static JsonNode? Odd(Random random) => random.Next(7) switch
    {
        0 => null,
        1 => 1,
        2 => true,
        3 => new JsonArray(),
        4 => new JsonObject(),
        5 => 2.5,
        _ => new JsonArray("x"),
    };

    private static string Text(Random random) => random.Next(10) switch
    {
        0 => new string('a', StateCommands.MaxArg + random.Next(-1, 2)),
        1 => "\ud83d",
        2 => "caf\u00e9 \U0001F600",
        3 => "",
        _ => Tokens[random.Next(Tokens.Length)],
    };

    private static JsonNode? StateArguments(Random random)
    {
        if (random.Next(25) == 0)
        {
            return random.Next(2) == 0 ? Odd(random) : "text";
        }

        var arguments = new JsonObject();
        switch (random.Next(12))
        {
            case < 10:
                arguments["script"] = Scripts[random.Next(Scripts.Length)];
                break;
            case 10:
                arguments["script"] = Odd(random);
                break;
        }

        switch (random.Next(12))
        {
            case < 10:
            {
                var list = new JsonArray();
                var count = random.Next(5) == 0 ? random.Next(0, 70) : random.Next(0, 6); // around MAX_ARGS too
                for (var i = 0; i < count; i++)
                {
                    list.Add(random.Next(40) == 0 ? Odd(random) : JsonValue.Create(Text(random)));
                }

                arguments["args"] = list;
                break;
            }

            case 10:
                arguments["args"] = random.Next(2) == 0 ? Odd(random) : "list";
                break;
        }

        switch (random.Next(8))
        {
            case < 3:
            {
                var files = new JsonObject();
                var count = random.Next(0, 11); // around MAX_FILES
                for (var i = 0; i < count; i++)
                {
                    var name = FileNames[random.Next(FileNames.Length)] + (random.Next(4) == 0 ? i.ToString(CultureInfo.InvariantCulture) : "");
                    files[name] = random.Next(15) switch
                    {
                        0 => Odd(random),
                        1 when random.Next(20) == 0 => new string('f', random.Next(2) == 0 ? StateCommands.MaxFilesTotal / 2 : StateCommands.MaxFilesTotal + 1),
                        2 => "\ud800",
                        _ => Text(random),
                    };
                }

                arguments["files"] = files;
                break;
            }

            case 3:
                arguments["files"] = Odd(random);
                break;
        }

        switch (random.Next(8))
        {
            case < 2:
                arguments["input"] = Text(random);
                break;
            case 2:
                arguments["input"] = random.Next(2) == 0 ? Odd(random)
                    : new JsonObject { ["b"] = new JsonArray(1, 2.5, true, null), ["a"] = "caf\u00e9", ["s"] = "\udfff" };
                break;
            case 3 when random.Next(10) == 0:
                arguments["input"] = new string('i', StateCommands.MaxInput + random.Next(-1, 2));
                break;
        }

        return arguments;
    }

    private static readonly string[] Outputs =
    [
        "{\"error\": \"x\"}", "{\"error\": \"\"}", "{\"error\": null}", "{\"error\": 0}", "{\"error\": 1}", "{\"error\": {\"code\": 7}}",
        "{\"error\": [1]}", "{\"error\": false}", "{\"error\": \"\u00fc\U0001F600\"}", "{}", "{\"a\": 1}", "[1, 2]", "null", "\"str\"", "123",
        "NaN", "-Infinity", "1e400", "  \n", "", "{} trailing", "plain text", "{\"a\": 1}\n", "\u2028{}", "\ufeff{}", "{\"error\": 1.0}",
        "{\"error\": 12345678901234567890}", "{\"a\": \"\\ud800\"}", "{\"error\": \"\\udfff\"}", "{\"a\": 1, \"a\": 2}", "\t{\"x\": [] }\r\n",
        "{\"error\": \"" + new string('e', 2100) + "\"}", "{\"error\": \"" + string.Concat(Enumerable.Repeat("\U0001F600", 2100)) + "\"}",
    ];

    private static readonly string[] Errors =
    [
        "", "plain", "line1\nline2\n", "{\"error\": \"x\"}\n", "{\"error\": \"\"}\n", "{\"error\": {\"a\": 1}}", "[1]", "\n\n  \n",
        new string('x', 600), string.Concat(Enumerable.Repeat("\U0001F600", 600)), "\u2028sep", "a\u001cb", "{\"error\": \"y\"}\r\n",
        "Traceback (most recent call last):\nRuntimeError: boom\n", "\u0085", "\u000b\u000c", "{\"error\": \"z\"}\n\u00a0\n", "x\ry",
        "{\"error\": 5}", "{\"error\": null}\nlast", "first\n{\"error\": [\"a\"]}", "NaN", "{\"error\": \"" + new string('q', 700) + "\"}",
    ];

    /// <summary>Invalid and partial UTF-8: both sides must replace it the same way and count the same code points.</summary>
    private static readonly byte[][] RawBytes =
    [
        [0xff, 0xfe], [0xed, 0xa0, 0x80], [0xf0, 0x9f, 0x98], [0xc0, 0xaf], [0xf4, 0x90, 0x80, 0x80], [0x80], [0xe0, 0x80, 0x80],
        [0xe2, 0x82], [0xf8, 0x88, 0x80, 0x80, 0x80], [0xef, 0xbb, 0xbf], [0xc3], [0xe0, 0xa0], [0xf0, 0x90, 0x80],
    ];

    private static byte[] Printed(Random random, string[] pool)
    {
        var bytes = new List<byte>();
        var count = random.Next(1, 4);
        for (var i = 0; i < count; i++)
        {
            switch (random.Next(10))
            {
                case 0:
                    bytes.AddRange(RawBytes[random.Next(RawBytes.Length)]);
                    break;
                case 1:
                {
                    // Around MAX_OUTPUT code points, in units of one to four UTF-8 bytes (two UTF-16 units for the emoji).
                    string[] units = ["x", "\u00e9", "\U0001F600", "\u20ac"];
                    var unit = units[random.Next(units.Length)];
                    var n = StateCommands.MaxOutput + random.Next(-2, 3) - (random.Next(3) == 0 ? 10 : 0);
                    var big = random.Next(2) == 0 ? string.Concat(Enumerable.Repeat(unit, n))
                        : "{\"result\": \"" + string.Concat(Enumerable.Repeat(unit, n - 14)) + "\"}";
                    bytes.AddRange(PyIO.EncodeUtf8(big));
                    break;
                }

                default:
                    bytes.AddRange(PyIO.EncodeUtf8(pool[random.Next(pool.Length)]));
                    break;
            }

            if (random.Next(3) == 0)
            {
                break;
            }
        }

        return [.. bytes];
    }

    private static readonly string[] Paths =
    [
        "references/daily-brief.md", "daily-brief", "../references/daily-brief.md", "../preferences.md", "preferences.md", "preferences",
        "commitments.md", "commitments", "../commitments.md", "./commitments.md", "references/../commitments.md", "SKILL.md", "SKILL",
        "scripts/work_state.py", "scripts/README.md", "state/x.md", "../../agents/margo.agent.md", "/etc/passwd", "..\\SKILL.md", "",
        " ", " daily-brief ", "references//daily-brief.md", "references/./daily-brief.md", "a/../references/daily-brief.md",
        "references/daily-brief.md/", "references/daily-brief.md\u0000", "..", ".", "../", "../..", "../../", "....//x", ".md",
        "x/../../references/daily-brief.md", "//references/daily-brief.md", "references/schema.md", "schema", "README", "README.md",
        "references/DAILY-BRIEF.md", "daily-brief.MD", "\u00e9", "\U0001F600.md", new string('x', 512), new string('x', 513),
        "references/daily-brief", "references", "references/", "~/x.md", "../x/../../y.md", "...", ".../x.md", "x/..", "x/../..",
    ];

    private static readonly string[] SkillNames =
    [
        "chief-of-staff", "decision-log", " Chief-Of-Staff ", "CHIEF-OF-STAFF", "chief-of-staff\n", "no-such-skill", "", "chief",
        "chief-of-staff ", "\u0130", "decision-log\u00a0",
    ];

    private static JsonNode? Pick(Random random, string[] pool) => random.Next(12) == 0 ? Odd(random) : pool[random.Next(pool.Length)];

    private static JsonObject ToolArguments(Random random)
    {
        var arguments = new JsonObject();
        if (random.Next(4) > 0)
        {
            arguments["name"] = Pick(random, SkillNames);
        }

        if (random.Next(4) > 0)
        {
            arguments["skill"] = Pick(random, SkillNames);
        }

        if (random.Next(4) > 0)
        {
            arguments["path"] = Pick(random, Paths);
        }

        if (random.Next(3) == 0)
        {
            arguments["on_data_of"] = random.Next(5) == 0 ? Odd(random) : new[] { "principal", "self", "other", "" }[random.Next(4)];
            arguments["entityUrls"] = new JsonArray(new[] { "/me/messages", "/users/x/messages", "https://example.com/x" }[random.Next(3)]);
        }

        return arguments;
    }

    private static JsonNode? OutputItem(Random random)
    {
        switch (random.Next(14))
        {
            case 0:
                return random.Next(3) == 0 ? Odd(random) : "x";
            case 1:
            case 2:
                return new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = random.Next(6) == 0 ? Odd(random) : new JsonArray(
                        new JsonObject
                        {
                            ["type"] = new[] { "output_text", "text", "refusal" }[random.Next(3)],
                            ["text"] = random.Next(5) == 0 ? Odd(random) : "hello \U0001F600",
                        },
                        new JsonObject { ["type"] = "output_text", ["text"] = "second" }),
                };
            case 3:
                return new JsonObject { ["type"] = "reasoning" };
            default:
            {
                var call = new JsonObject { ["type"] = random.Next(10) == 0 ? "FUNCTION_CALL" : "function_call" };
                if (random.Next(10) > 0)
                {
                    call["name"] = random.Next(10) == 0 ? Odd(random)
                        : new[] { "load_skill", "read_skill_file", "office_requests", "workiq-fetch", "workiq-do_action", "margo-state", "nope" }[random.Next(7)];
                }

                if (random.Next(10) > 0)
                {
                    call["arguments"] = random.Next(8) == 0 ? Odd(random)
                        : new[]
                        {
                            "{\"name\": \"decision-log\"}", "{\"skill\": \"chief-of-staff\", \"path\": \"daily-brief\"}", "{not json", "",
                            "{\"on_data_of\": \"principal\", \"entityUrls\": [\"/me/messages\"]}", "[]", "{\"name\": \"x\"}",
                        }[random.Next(7)];
                }

                if (random.Next(8) > 0)
                {
                    call["call_id"] = random.Next(6) == 0 ? Odd(random) : "c" + random.Next(100).ToString(CultureInfo.InvariantCulture);
                }

                return call;
            }
        }
    }

    private static JsonNode? ModelResponse(Random random, bool final)
    {
        if (!final && random.Next(40) == 0)
        {
            return Odd(random);
        }

        var response = new JsonObject();
        if (random.Next(8) > 0)
        {
            response["id"] = random.Next(6) == 0 ? Odd(random) : "r" + random.Next(1000).ToString(CultureInfo.InvariantCulture);
        }

        if (random.Next(5) == 0)
        {
            response["output_text"] = random.Next(3) == 0 ? Odd(random) : "direct text";
        }

        if (final)
        {
            response["output"] = new JsonArray(new JsonObject
            {
                ["type"] = "message",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "done" }),
            });
        }
        else if (random.Next(12) == 0)
        {
            response["output"] = random.Next(2) == 0 ? Odd(random) : "";
        }
        else
        {
            response["output"] = new JsonArray([.. Enumerable.Range(0, random.Next(0, 5)).Select(_ => OutputItem(random))]);
        }

        return response;
    }

    // -- helpers -------------------------------------------------------------------------------------

    private static async Task<JsonObject> RunState(StateCommands state, JsonNode testCase, List<StateReceipt> receipts)
    {
        JsonObject result;
        try
        {
            result = await state.RunAsync(PyNode.Copy(testCase[0]), PyNode.AsString(testCase[1]), TestContext.Current.CancellationToken);
        }
        catch (PyException ex)
        {
            result = new JsonObject { ["raised"] = ex.PythonType, ["message"] = ex.Message };
        }

        var recorded = new JsonArray([.. receipts.Select(receipt =>
        {
            var json = receipt.ToJsonObject();
            json.Remove("at");
            json.Remove("ms");
            return (JsonNode?)json;
        })]);
        return new JsonObject { ["result"] = result, ["receipts"] = recorded };
    }

    private static JsonArray Sent(RecordingTransport transport) =>
        new([.. transport.Calls.Select(call => (JsonNode?)new JsonArray(call.Tool, call.Arguments, call.Bearer))]);

    private static void Compare(List<string> failures, object? testCase, JsonNode? python, JsonNode? mine)
    {
        var expected = Fixtures.Dumps(python);
        var actual = Fixtures.Dumps(mine);
        if (expected != actual)
        {
            failures.Add("case " + Clip(testCase is JsonNode node ? Fixtures.Dumps(node) : testCase?.ToString() ?? "null")
                         + "\npython: " + Clip(expected) + "\nC#:     " + Clip(actual));
        }
    }

    private static string Clip(string text) => text.Length > 600 ? text[..600] + "..." : text;

    /// <summary>The office queue engine_cases.py's Python engine reads.</summary>
    private sealed class PythonOffice(bool broken) : IOfficeRequests
    {
        public Task<JsonNode?> PendingAsync(CancellationToken cancellationToken = default) =>
            broken ? Task.FromException<JsonNode?>(new InvalidOperationException("fictional queue failure"))
                : Task.FromResult(PyJson.Loads("""[{"from": "Rafa", "trust": "untrusted"}]"""));
    }
}
