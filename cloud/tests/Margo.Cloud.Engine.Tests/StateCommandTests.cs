using System.Text.Json.Nodes;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine.Tests;

/// <summary>
/// <c>StateCommandTests</c> of <c>tests/test_autopilot_runtime.py</c>: the skill scripts, run for one instance with the
/// same arguments the procedures show, through the real <c>margo-state</c> CLI against a temporary state directory.
/// </summary>
public sealed class StateCommandTests
{
    private const string FakeCli = """
        import sys, time
        args = sys.argv[2:]  # sys.argv[1] is the script name margo-state receives first
        command = args[4] if args[:1] == ["--account"] else args[0]
        if command == "list":
            print('{"items": []}')
        elif command == "source":
            print('{"error": "refused before any change"}', file=sys.stderr)
            sys.exit(2)
        elif command == "ingest":
            raise RuntimeError("crashed after it may have committed")
        elif command in ("propose", "show"):
            time.sleep(5)
        elif command == "history":
            raise RuntimeError("a read that crashed")
        elif command == "record":
            print('{"error": {"code": 7}}')
        elif command == "record-id":
            print("plain text, not JSON")
        elif command == "recap-check":
            print("x" * 60001)
        elif command == "carry-forward":
            print('{}')
            print('{"error": "from stderr"}', file=sys.stderr)
            sys.exit(3)
        elif command == "relate":
            print("null")
        elif command == "edit":
            sys.exit(9)
        elif command == "defer":
            sys.stdout.write(sys.stdin.read())
        elif command == "dismiss":
            print(open(args[-1].split("=", 1)[-1]).read() + "|" + __import__("os").getcwd(), end="")
        """;

    private readonly List<StateReceipt> _receipts = [];

    private StateCommands Commands(TempRoot temp, CallBudget? budget = null, TimeSpan? timeout = null, IReadOnlyList<string>? cli = null,
                                   string? preferences = null) =>
        new(Fixtures.Binding.Account, temp.Combine("state"), preferences, budget, _receipts.Add, timeout, cli);

    private static JsonObject Args(string json) => Fixtures.Obj(json);

    [Fact]
    public async Task The_same_commands_run_against_the_instance_state()
    {
        using var temp = new TempRoot();
        var state = Commands(temp);
        var source = """{"family": "mail", "scope": "inbox", "external_id": "message-1", "revision": "v1", "evidence": {"quote": "Could you send the fictional deck by Friday?"}, "web_link": "https://example.com/mail/1"}""";
        var reference = await state.RunAsync(Args("""{"script": "work_state.py", "args": ["source", "--input", "-"], "input": """ + source + "}"),
                                             RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.True(PyNode.AsString(reference["status"]) == "ok", Fixtures.Dumps(reference));
        var item = new JsonObject
        {
            ["claim_key"] = "fictional-1",
            ["data"] = new JsonObject
            {
                ["title"] = "Send the fictional deck", ["owner"] = null, ["due"] = null, ["direction"] = "owe",
                ["source_refs"] = new JsonArray(PyNode.Copy(reference["result"])),
            },
        };
        var created = await state.RunAsync(new JsonObject
        {
            ["script"] = "work_state",
            ["args"] = new JsonArray("ingest", "--input=@file:item.json"),
            ["files"] = new JsonObject { ["item.json"] = PyJson.Dumps(item) },
        }, RunModes.Unattended, TestContext.Current.CancellationToken);
        Assert.True(PyNode.AsString(created["status"]) == "ok", Fixtures.Dumps(created));
        Assert.Equal("candidate", PyNode.AsString(created["result"]!["state"]));
        var listed = await state.RunAsync(Args("""{"script": "work_state.py", "args": ["list", "--view", "decisions"]}"""), RunModes.Attended,
                                          TestContext.Current.CancellationToken);
        Assert.Equal([PyNode.AsString(created["result"]!["id"])], ((JsonArray)listed["result"]!["items"]!).Select(entry => PyNode.AsString(entry!["id"])).ToArray());
        Assert.Equal("ok", PyNode.AsString((await state.RunAsync(Args("""{"script": "proactive_state.py", "args": ["status"]}"""), RunModes.Attended,
                                                                 TestContext.Current.CancellationToken))["status"]));
        using (var ledger = Fixtures.Binding.OpenLedger(temp.Combine("state"))) // the host's own view of the same database
        {
            Assert.Single((JsonArray)ledger.List("decisions")["items"]!);
        }

        // task_state.py, as proactive.md shows it: a stable request key needs no initialised state.
        var key = await state.RunAsync(Args("""{"script": "task_state.py", "args": ["record-id", "fictional-request-key"]}"""), RunModes.Unattended,
                                       TestContext.Current.CancellationToken);
        Assert.True(PyNode.AsString(key["status"]) == "ok", Fixtures.Dumps(key));
        Assert.StartsWith("task_", PyNode.AsString(key["result"]!["id"]), StringComparison.Ordinal);
        // A read of state the host has not initialised is an error the model is told, never an empty success.
        var health = await state.RunAsync(Args("""{"script": "task_state.py", "args": ["health"]}"""), RunModes.Attended,
                                          TestContext.Current.CancellationToken);
        Assert.Equal(("error", "health"), (PyNode.AsString(health["status"]), PyNode.AsString(health["command"])));
        Assert.Contains("not initialized", PyNode.AsString(health["reason"]), StringComparison.Ordinal);
        Assert.False(health.ContainsKey("result"));

        Assert.Equal(["source", "ingest", "list", "status", "record-id", "health"], _receipts.Select(receipt => receipt.Command).ToArray());
        Assert.Equal([0, 0, 0, 0, 0, 2], _receipts.Select(receipt => receipt.ExitCode).ToArray());
    }

    [Fact]
    public async Task Initialise_prepares_memory_and_task_state()
    {
        var help = Programs.Cli("--help").Stdout;
        Assert.SkipUnless(help.Contains("memory_state.py", StringComparison.Ordinal) && help.Contains("task_state.py", StringComparison.Ordinal),
                          "memory_state.py is not ported to margo-state yet, so the C# CLI cannot initialise memory state "
                          + "(see cloud/src/Margo.Cloud.Engine/PORTING.md)");
        using var temp = new TempRoot();
        var state = Commands(temp);
        await state.InitialiseAsync(TestContext.Current.CancellationToken);
        await state.InitialiseAsync(TestContext.Current.CancellationToken); // idempotent
        Assert.Equal("ok", PyNode.AsString((await state.RunAsync(Args("""{"script": "memory_state.py", "args": ["status"]}"""), RunModes.Attended,
                                                                 TestContext.Current.CancellationToken))["status"]));
        Assert.All(_receipts.Take(4), receipt => Assert.Equal("host", receipt.Mode));
    }

    [Fact]
    public async Task Initialise_reports_a_script_that_did_not_initialise()
    {
        using var temp = new TempRoot();
        var fake = temp.Combine("fake.py");
        File.WriteAllText(fake, "import sys\nprint('{\"error\": \"fictional init failure\"}', file=sys.stderr)\nsys.exit(2)\n");
        var state = Commands(temp, cli: [Programs.Python(), fake]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => state.InitialiseAsync(TestContext.Current.CancellationToken));
        Assert.Equal("could not initialise memory_state.py: fictional init failure", error.Message);
        Assert.Equal("""{"tool": "margo-state", "script": "memory_state.py", "command": "init", "mode": "host", "status": "error", "exit_code": 2}""",
                     Receipt(_receipts.Single()));
    }

    [Fact]
    public async Task Mode_policy_and_unhosted_commands_run_nothing()
    {
        using var temp = new TempRoot();
        var state = Commands(temp);
        (string Arguments, string Mode, string Expected)[] cases =
        [
            ("""{"script": "work_state.py", "args": ["list"]}""", RunModes.ThirdParty, "refused"),
            ("""{"script": "work_state.py", "args": ["item-update", "item_1", "--revision", "1", "--input", "-"]}""", RunModes.Unattended, "refused"),
            ("""{"script": "work_state.py", "args": ["approve", "action_1", "--revision", "1", "--input", "-"]}""", RunModes.Attended, "unavailable"),
            ("""{"script": "margo_doctor.py", "args": []}""", RunModes.Attended, "unavailable"),
            ("""{"script": "rm", "args": ["-rf", "x"]}""", RunModes.Attended, "refused"),
            ("""{"script": "work_state.py", "args": ["--account", "someone-else", "list"]}""", RunModes.Attended, "refused"),
            ("""{"script": "work_state.py", "args": ["list", "--account", "someone-else"]}""", RunModes.Attended, "refused"),
            ("""{"script": "work_state.py", "args": ["list", "--state-dir=/tmp"]}""", RunModes.Attended, "refused"),
            ("""{"script": "work_state.py", "args": ["import-preview", "/etc/passwd"]}""", RunModes.Attended, "unavailable"),
            ("""{"script": "capacity.py", "args": ["--input", "/etc/passwd"]}""", RunModes.Attended, "refused"),
            ("""{"script": "capacity.py", "args": ["--input", "../secrets.json"]}""", RunModes.Attended, "refused"),
            ("""{"script": "capacity.py", "args": ["--input", "@file:missing"]}""", RunModes.Attended, "refused"),
            ("""{"script": "capacity.py", "args": ["--input=/etc/passwd"]}""", RunModes.Attended, "refused"),
            ("""{"script": "capacity.py", "args": ["--input=../x.json"]}""", RunModes.Attended, "refused"),
            ("""{"script": "memory_state.py", "args": ["capabilities", "--skills-dir", ".."]}""", RunModes.Attended, "refused"),
            ("""{"script": "work_state.py", "args": ["not-a-command"]}""", RunModes.Attended, "refused"),
            ("""{"script": "work_state.py", "args": "list"}""", RunModes.Attended, "refused"),
            ("""{"script": "work_state.py", "args": ["list"], "files": {"../x": "y"}}""", RunModes.Attended, "refused"),
            ("\"not an object\"", RunModes.Attended, "refused"),
        ];
        foreach (var (arguments, mode, expected) in cases)
        {
            var result = await state.RunAsync(Fixtures.Json(arguments), mode, TestContext.Current.CancellationToken);
            Assert.True(PyNode.AsString(result["status"]) == expected, arguments + " in " + mode + ": " + Fixtures.Dumps(result));
        }

        Assert.All(_receipts, receipt => Assert.Null(receipt.ExitCode));
        Assert.All(_receipts, receipt => Assert.Null(receipt.Ms));
        Assert.False(Directory.Exists(temp.Combine("state")), "a refused command must not create state");
    }

    [Fact]
    public async Task Outcomes_are_reported_truthfully()
    {
        using var temp = new TempRoot();
        var fake = temp.Combine("fake_cli.py");
        File.WriteAllText(fake, FakeCli);
        var state = Commands(temp, timeout: TimeSpan.FromSeconds(1), cli: [Programs.Python(), fake]);
        Task<JsonObject> Run(string input = "", params string[] args) =>
            state.RunAsync(new JsonObject { ["script"] = "work_state.py", ["args"] = PyNode.StrList(args), ["input"] = input.Length > 0 ? input : null },
                           RunModes.Attended, TestContext.Current.CancellationToken);

        Assert.Equal("""{"status": "ok", "script": "work_state.py", "command": "list", "result": {"items": []}}""", Fixtures.Dumps(await Run("", "list")));
        var refused = await Run("", "source", "--input", "-");
        Assert.Equal(("error", "refused before any change"), (PyNode.AsString(refused["status"]), PyNode.AsString(refused["reason"])));
        var crashed = await Run("", "ingest", "--input", "-");
        Assert.Equal(("unknown", "RuntimeError: crashed after it may have committed"), (PyNode.AsString(crashed["status"]), PyNode.AsString(crashed["reason"])));
        var write = await Run("", "propose", "--input", "-"); // a write that timed out
        Assert.Equal("""{"status": "unknown", "script": "work_state.py", "command": "propose", "reason": "timed out after 1s; the change may or may not have been saved"}""",
                     Fixtures.Dumps(write));
        var read = await Run("", "show", "item_1"); // a read that timed out changed nothing
        Assert.Equal("""{"status": "error", "script": "work_state.py", "command": "show", "reason": "timed out after 1s"}""", Fixtures.Dumps(read));
        Assert.Equal(("error", "RuntimeError: a read that crashed"), Pair(await Run("", "history", "item_1")));
        Assert.Equal(("error", "{'code': 7}"), Pair(await Run("", "record", "outcome"))); // exit 0 with {"error": ...}
        Assert.Equal("""{"status": "ok", "script": "work_state.py", "command": "record-id", "text": "plain text, not JSON\n"}""",
                     Fixtures.Dumps(await Run("", "record-id", "outcome")));
        var partial = await Run("", "recap-check", "meeting_1");
        Assert.Equal("partial", PyNode.AsString(partial["status"]));
        Assert.Equal(StateCommands.MaxOutput, PyNode.AsString(partial["text"])!.Length);
        Assert.Equal("Output was too large; only the beginning is shown. Narrow the command.", PyNode.AsString(partial["note"]));
        Assert.Equal(("unknown", "from stderr"), Pair(await Run("", "carry-forward", "meeting_1")));
        Assert.Equal("""{"status": "ok", "script": "work_state.py", "command": "relate", "text": "null\n"}""", Fixtures.Dumps(await Run("", "relate", "item_1")));
        Assert.Equal(("unknown", "the command failed"), Pair(await Run("", "edit", "item_1")));
        Assert.Equal("""{"status": "ok", "script": "work_state.py", "command": "defer", "result": {"x": 1}}""",
                     Fixtures.Dumps(await Run("{\"x\": 1}", "defer", "item_1", "--input", "-")));

        // A named file lives in a private directory that exists for one call; the CLI runs there.
        var named = await state.RunAsync(Args("""{"script": "work_state.py", "args": ["dismiss", "item_1", "--input=@file:note.md"], "files": {"note.md": "fictional note"}}"""),
                                         RunModes.Attended, TestContext.Current.CancellationToken);
        var text = PyNode.AsString(named["text"])!.Split('|');
        Assert.Equal("fictional note", text[0]);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "margo-state-"), text[1] + "/", StringComparison.Ordinal);
        Assert.False(Directory.Exists(text[1]), "the private directory is removed after the call");

        Assert.Equal([0, 2, 1, null, null, 1, 0, 0, 0, 3, 0, 9, 0, 0], _receipts.Select(receipt => receipt.ExitCode).ToArray());
        Assert.All(_receipts, receipt => Assert.NotNull(receipt.Ms));
    }

    [Fact]
    public async Task The_cli_sees_only_its_private_directory_and_the_passed_through_environment()
    {
        using var temp = new TempRoot();
        var fake = temp.Combine("inspect.py");
        File.WriteAllText(fake, """
            import json, os, stat, sys
            path = sys.argv[-1].split("=", 1)[-1]
            print(json.dumps({"cwd": os.getcwd(), "cwd_mode": oct(stat.S_IMODE(os.stat(".").st_mode)),
                              "file_dir": os.path.dirname(path), "file_mode": oct(stat.S_IMODE(os.stat(path).st_mode)),
                              "entries": sorted(os.listdir(".")), "environment": sorted(os.environ), "argv": sys.argv[1:-1]}))
            """);
        var environment = PyRuntime.EnvironmentSnapshot();
        environment["IDENTITY_HEADER"] = "fictional-managed-identity-secret";
        environment["AZURE_CLIENT_SECRET"] = "fictional-client-secret";
        environment["MARGO_STATE_PROBES"] = "1";
        environment["DOTNET_STARTUP_HOOKS"] = "/nonexistent/hook.dll";
        using var _ = PyRuntime.Override(environment);
        var state = Commands(temp, cli: [Programs.Python(), "-s", fake]);
        var result = await state.RunAsync(Args("""{"script": "work_state.py", "args": ["ingest", "--input=@file:item.json"], "files": {"item.json": "{}"}}"""),
                                          RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.True(PyNode.AsString(result["status"]) == "ok", Fixtures.Dumps(result));
        var seen = result["result"]!;
        var cwd = PyNode.AsString(seen["cwd"])!;
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "margo-state-"), cwd, StringComparison.Ordinal);
        Assert.Equal("0o700", PyNode.AsString(seen["cwd_mode"]));
        Assert.Equal(cwd, PyNode.AsString(seen["file_dir"]));
        Assert.Equal("0o600", PyNode.AsString(seen["file_mode"]));
        Assert.Equal(["item.json"], ((JsonArray)seen["entries"]!).Select(PyNode.AsString).ToArray());
        Assert.False(Directory.Exists(cwd), "the private directory is removed after the call");
        // The binding's account and state directory, placed by the service before the model's arguments.
        Assert.Equal(["work_state.py", "--account", Fixtures.Binding.Account, "--state-dir", temp.Combine("state"), "ingest"],
                     ((JsonArray)seen["argv"]!).Select(PyNode.AsString).ToArray());
        // Nothing but ENV_PASSTHROUGH (and DOTNET_ROOT) reaches the CLI: no identity secrets, no probes, no startup hooks.
        // Python itself may add LC_CTYPE (PEP 538 locale coercion).
        var names = ((JsonArray)seen["environment"]!).Select(name => PyNode.AsString(name)!).ToList();
        Assert.Empty(names.Except(StateCommands.EnvironmentPassthrough).Except(["LC_CTYPE"]));
        Assert.Contains("MARGO_ALLOW_UNSAFE_STATE_DIR", names);
    }

    [Fact]
    public async Task A_cli_that_cannot_start_ran_nothing()
    {
        using var temp = new TempRoot();
        var missing = new StateCommands(Fixtures.Binding.Account, temp.Combine("state"), recorder: _receipts.Add,
                                        cliCommand: [temp.Combine("no-such-program")]);
        Assert.Equal("""{"status": "error", "script": "work_state.py", "command": "ingest", "reason": "could not start the command: FileNotFoundError"}""",
                     Fixtures.Dumps(await missing.RunAsync(Args("""{"script": "work_state.py", "args": ["ingest", "--input", "-"]}"""), RunModes.Attended,
                                                           TestContext.Current.CancellationToken)));
        File.WriteAllText(temp.Combine("not-executable"), "#!/bin/sh\n");
        var denied = new StateCommands(Fixtures.Binding.Account, temp.Combine("state"), cliCommand: [temp.Combine("not-executable")]);
        Assert.Equal("could not start the command: PermissionError",
                     PyNode.AsString((await denied.RunAsync(Args("""{"script": "capacity.py", "args": []}"""), RunModes.Attended,
                                                            TestContext.Current.CancellationToken))["reason"]));

        var environment = PyRuntime.EnvironmentSnapshot();
        environment[StateCommands.CliVariable] = temp.Combine("missing", "margo-state.dll");
        using (PyRuntime.Override(environment))
        {
            var (command, file) = StateCommands.DefaultCliCommand();
            Assert.Equal(temp.Combine("missing", "margo-state.dll"), file);
            Assert.Equal(file, command[1]);
            var configured = new StateCommands(Fixtures.Binding.Account, temp.Combine("state"));
            Assert.Equal("could not start the command: FileNotFoundError",
                         PyNode.AsString((await configured.RunAsync(Args("""{"script": "work_state.py", "args": ["list"]}"""), RunModes.Attended,
                                                                    TestContext.Current.CancellationToken))["reason"]));
        }

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "margo-state.dll"), StateCommands.DefaultCliCommand().CliFile);
        Assert.Equal(Fixtures.Cli, new StateCommands("a", "b").CliCommand[1]);
        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(new StateCommands("a", "b").CliCommand[0]));
    }

    [Fact]
    public async Task Budget_is_charged_before_running_and_receipts_hold_no_content()
    {
        using var temp = new TempRoot();
        var budget = new CallBudget(1);
        var state = Commands(temp, budget);
        var secret = """{"family": "mail", "scope": "inbox", "external_id": "fictional-secret-id", "revision": "v1", "evidence": {"quote": "fictional secret"}, "web_link": "https://example.com/secret"}""";
        Assert.Equal("refused", PyNode.AsString((await state.RunAsync(Args("""{"script": "work_state.py", "args": ["nope"]}"""), RunModes.Attended,
                                                                      TestContext.Current.CancellationToken))["status"]));
        Assert.Equal(0, budget.Used); // a refusal is not charged
        Assert.Equal("ok", PyNode.AsString((await state.RunAsync(Args("""{"script": "work_state.py", "args": ["source", "--input", "-"], "input": """ + secret + "}"),
                                                                 RunModes.Attended, TestContext.Current.CancellationToken))["status"]));
        var again = await state.RunAsync(Args("""{"script": "work_state.py", "args": ["list"]}"""), RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal("""{"status": "budget_exhausted", "script": "work_state.py", "command": "list", "reason": "state command budget exhausted"}""",
                     Fixtures.Dumps(again));
        var recorded = PyJson.Dumps(new JsonArray(_receipts.Select(receipt => (JsonNode?)receipt.ToJsonObject()).ToArray()));
        foreach (var text in new[] { "fictional secret", "fictional-secret-id", Fixtures.Binding.Account, temp.Path })
        {
            Assert.DoesNotContain(text, recorded, StringComparison.Ordinal);
        }

        Assert.Equal("""{"tool": "margo-state", "script": "work_state.py", "command": "list", "mode": "attended", "status": "budget_exhausted"}""",
                     Receipt(_receipts[^1]));
    }

    [Fact]
    public async Task Cancellation_kills_the_command_and_leaves_an_honest_receipt()
    {
        using var temp = new TempRoot();
        var fake = temp.Combine("fake_cli.py");
        File.WriteAllText(fake, FakeCli);
        var state = Commands(temp, cli: [Programs.Python(), fake]);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            state.RunAsync(Args("""{"script": "work_state.py", "args": ["propose", "--input", "-"]}"""), RunModes.Attended, cancel.Token));
        Assert.Equal("unknown", _receipts.Single().Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            state.RunAsync(Args("""{"script": "work_state.py", "args": ["list"]}"""), RunModes.Attended, cancel.Token));
        Assert.Single(_receipts); // already cancelled: nothing started, nothing recorded
    }

    [Fact]
    public async Task Refusals_and_their_receipts_match_python()
    {
        using var temp = new TempRoot();
        var prefs = temp.Combine("preferences.md");
        File.WriteAllText(prefs, "fictional preferences\n");
        var longArg = new string('a', StateCommands.MaxArg);
        string[] arguments =
        [
            """{"script": "work_state.py", "args": ["list"]}""",
            """{"script": "scripts/work_state", "args": ["list", "--view", "decisions"]}""",
            """{"script": "work_state.py"}""",
            """{"script": "work_state.py", "args": []}""",
            """{"script": "work_state.py", "args": ["--view", "x"]}""",
            """{"script": "work_state.py", "args": null}""",
            """{"script": "work_state.py", "args": [1]}""",
            """{"script": "work_state.py", "args": ["list", "--config-dir=x"]}""",
            """{"script": "work_state.py", "args": ["list", "--state-root"]}""",
            """{"script": "work_state.py", "args": ["approve", "x"]}""",
            """{"script": "work_state.py", "args": ["begin"]}""",
            """{"script": "proactive_state.py", "args": ["import-legacy"]}""",
            """{"script": "memory_state.py", "args": ["init"]}""",
            """{"script": "memory_state.py", "args": ["export"]}""",
            """{"script": "task_state.py", "args": ["init"]}""",
            """{"script": "task_state.py", "args": ["pause", "x"]}""",
            """{"script": "proactive_state.py", "args": ["publication-review"]}""",
            """{"script": "margo_store.py", "args": ["init"]}""",
            """{"script": "m365_files", "args": []}""",
            """{"script": "memory_encoder.py"}""",
            """{"script": null}""",
            """{"script": 5}""",
            """{}""",
            """{"script": "capacity.py", "args": ["--input", "-"], "input": {"b": [1, 2.5, true, null], "a": "café"}}""",
            """{"script": "capacity.py", "args": ["--input", "-"], "input": "plain text"}""",
            """{"script": "capacity.py", "args": ["--input", "-"], "input": null}""",
            """{"script": "capacity.py", "args": ["--input", "@preferences"]}""",
            """{"script": "capacity.py", "args": ["--input=@preferences"]}""",
            """{"script": "capacity.py", "args": ["--input", "@file:a.json"], "files": {"a.json": "{}"}}""",
            """{"script": "capacity.py", "args": ["--input", "@file:b.json"], "files": {"a.json": "{}"}}""",
            """{"script": "capacity.py", "args": ["--input", "@file:"], "files": {}}""",
            """{"script": "capacity.py", "args": [], "files": {".hidden": "x"}}""",
            """{"script": "capacity.py", "args": [], "files": {"a": 1}}""",
            """{"script": "capacity.py", "args": [], "files": ["a"]}""",
            """{"script": "capacity.py", "args": [], "files": null}""",
            """{"script": "capacity.py", "args": [], "files": {"a1": "", "a2": "", "a3": "", "a4": "", "a5": "", "a6": "", "a7": "", "a8": "", "a9": ""}}""",
            """{"script": "capacity.py", "args": [], "files": {"x": "1\n"}}""",
            """{"script": "capacity.py", "args": ["~/x"]}""",
            """{"script": "capacity.py", "args": ["./x"]}""",
            """{"script": "capacity.py", "args": ["..x"]}""",
            """{"script": "capacity.py", "args": ["a/../b"]}""",
            """{"script": "capacity.py", "args": ["\\x"]}""",
            """{"script": "capacity.py", "args": ["a\u0000"]}""",
            """{"script": "capacity.py", "args": ["--a=b=/etc"]}""",
            """{"script": "capacity.py", "args": ["-x=/etc"]}""",
            """{"script": "capacity.py", "args": ["--x", "a/b"]}""",
            """{"script": "teams_feedback.py", "args": ["-"]}""",
            """{"script": "engage_parse.py", "args": ["x"]}""",
            """{"script": "work_state.py", "args": ["list", "--x=--account"]}""",
            """{"script": "work_state.py", "args": ["list", "--accounts"]}""",
            """{"script": "capacity.py", "args": [], "files": {"a": "\ud800"}}""",
            """{"script": "capacity.py", "args": ["--input", "-"], "input": "\udfff"}""",
            """{"script": ["work_state.py"]}""",
            """{"script": {"a": 1}}""",
            """{"script": "capacity.py", "args": ["--a\u0000b=x"]}""",
            "[1]",
            "null",
            """{"script": "capacity.py", "args": ["\u00e9\ud83d\ude00"]}""",
            "{\"script\": \"capacity.py\", \"args\": [\"" + longArg + "\"]}",
            "{\"script\": \"capacity.py\", \"args\": [\"" + longArg + "x\"]}",
            """{"script": "capacity.py", "args": [""" + string.Join(", ", Enumerable.Repeat("\"" + longArg + "\"", 4)) + ", \"y\"]}",
            """{"script": "capacity.py", "args": [""" + string.Join(", ", Enumerable.Repeat("\"a\"", StateCommands.MaxArgs + 1)) + "]}",
            "{\"script\": \"capacity.py\", \"args\": [], \"input\": \"" + new string('i', StateCommands.MaxInput + 1) + "\"}",
            "{\"script\": \"capacity.py\", \"args\": [], \"files\": {\"big\": \"" + new string('f', StateCommands.MaxFilesTotal + 1) + "\"}}",
        ];
        string?[] modes = [RunModes.Attended, RunModes.Unattended, RunModes.ThirdParty, "host", null];
        var cases = new JsonArray();
        foreach (var argument in arguments)
        {
            foreach (var mode in modes)
            {
                cases.Add(new JsonArray(PyJson.Loads(argument), mode is null ? null : JsonValue.Create(mode)));
            }
        }

        var python = (JsonArray)Programs.AskPython(new JsonObject { ["state"] = PyNode.Copy(cases), ["state_preferences"] = prefs })["state"]!;
        for (var i = 0; i < cases.Count; i++)
        {
            _receipts.Clear();
            var state = new StateCommands(Fixtures.Binding.Account, "/nonexistent-margo-state", prefs, recorder: _receipts.Add,
                                          cliCommand: ["/nonexistent-margo-python"]);
            JsonObject result;
            try
            {
                result = await state.RunAsync(PyNode.Copy(cases[i]![0]), PyNode.AsString(cases[i]![1]), TestContext.Current.CancellationToken);
            }
            catch (PyException ex)
            {
                result = new JsonObject { ["raised"] = ex.PythonType, ["message"] = ex.Message };
            }

            var receipts = new JsonArray(_receipts.Select(receipt => (JsonNode?)Strip(receipt.ToJsonObject())).ToArray());
            var mine = Fixtures.Dumps(new JsonObject { ["result"] = result, ["receipts"] = receipts });
            Assert.True(Fixtures.Dumps(python[i]) == mine, "case " + Fixtures.Dumps(cases[i]) + "\npython: " + Fixtures.Dumps(python[i]) + "\nC#:     " + mine);
        }
    }

    [Fact]
    public void Tables_match_python()
    {
        var python = Programs.AskPython(new JsonObject());
        var commands = new JsonObject();
        foreach (var (script, classes) in StateCommands.Commands)
        {
            var entry = new JsonObject();
            foreach (var (klass, names) in classes)
            {
                entry[klass] = names.Contains(null) ? new JsonArray((JsonNode?)null) : PyNode.StrList(PyText.Sorted(names.Select(n => n!)));
            }

            commands[script] = entry;
        }

        Assert.Equal(Fixtures.Dumps(python["commands"]), Fixtures.Dumps(commands));
        Assert.Equal(Fixtures.Dumps(python["stateful"]), Fixtures.Dumps(PyNode.StrList(PyText.Sorted(StateCommands.Stateful))));
        var notHosted = new JsonArray(StateCommands.NotHostedCommands.OrderBy(p => p.Key.Script, PyRepr.StringComparer).ThenBy(p => p.Key.Command, PyRepr.StringComparer)
            .Select(p => (JsonNode?)new JsonArray(p.Key.Script, p.Key.Command, p.Value)).ToArray());
        Assert.Equal(Fixtures.Dumps(python["not_hosted_commands"]), Fixtures.Dumps(notHosted));
        var modeClasses = new JsonObject();
        foreach (var (mode, classes) in StateCommands.ModeClasses)
        {
            modeClasses[mode] = PyNode.StrList(PyText.Sorted(classes));
        }

        // ENV_PASSTHROUGH gains DOTNET_ROOT (see PORTING.md); everything else is the same.
        var limits = new JsonArray(StateCommands.MaxArgs, StateCommands.MaxArg, StateCommands.MaxArgsTotal, StateCommands.MaxInput,
                                   StateCommands.MaxFiles, StateCommands.MaxFilesTotal, StateCommands.MaxOutput, (int)StateCommands.DefaultTimeout.TotalSeconds,
                                   PyNode.StrList(StateCommands.ForbiddenOptions),
                                   PyNode.StrList(StateCommands.EnvironmentPassthrough.Where(name => name != "DOTNET_ROOT")), modeClasses);
        Assert.Equal(Fixtures.Dumps(python["limits"]), Fixtures.Dumps(limits));
    }

    private static (string?, string?) Pair(JsonObject result) => (PyNode.AsString(result["status"]), PyNode.AsString(result["reason"]));

    private static string Receipt(StateReceipt receipt) => Fixtures.Dumps(Strip(receipt.ToJsonObject()));

    private static JsonObject Strip(JsonObject receipt)
    {
        receipt.Remove("at");
        receipt.Remove("ms");
        return receipt;
    }
}
