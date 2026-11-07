using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.MemoryCommands;
using Margo.Cloud.State.Proactive;
using Margo.Cloud.State.Tasks;

namespace Margo.Cloud.State.Probes;

/// <summary>
/// C# twin of <c>cloud/contracts/state/probes/memory_commands_probe.py</c>. Not a Margo command and not offered to the
/// hosted model: <c>tools/state_parity.py</c> runs both on the same program so that <c>memory_state.py</c> flows that
/// depend on earlier random IDs and tokens (Dream claims), the module values Python's tests patch
/// (<c>task_runs.clock</c>, <c>memory_dream.MAX_SCAN</c>), <c>memory_dream</c>'s library functions, its daily window
/// and the <c>zoneinfo</c> semantics of <see cref="PyZoneInfo"/> are judged byte for byte. Keep the two files in step.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A held store lives for one Run and is closed in its finally block.")]
public sealed partial class MemoryCommandsProbeScript : IStateProbeScript
{
    private static readonly PyDateTime Epoch = new(1970, 1, 1);
    private static readonly long Low = (long)(new PyDateTime(1, 1, 2) - Epoch).TotalSeconds();
    private static readonly long High = (long)(new PyDateTime(9999, 12, 30) - Epoch).TotalSeconds();

    private readonly Dictionary<string, JsonNode?> _saved = new(StringComparer.Ordinal);
    private IDisposable? _clock;
    private MemoryStore? _held;
    private string _account = "";
    private string _stateDir = "";
    private string _directory = "";

    public string ScriptName => "memory_commands_probe.py";

    // A fresh instance per run: saved results, the clock and MAX_SCAN never leak between runs.
    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => new MemoryCommandsProbeScript().Main(args, stdin, stdout, stderr), stderr);

    private int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var parser = new PyArgumentParser(ScriptName);
        parser.AddArgument("--account", required: true);
        parser.AddArgument("--state-dir", required: true);
        parser.AddArgument("--dir", required: true);
        var args = parser.ParseArgs(argv, stdout, stderr);
        if (PyJson.Loads(PyIO.ReadText(stdin)) is not JsonArray program)
        {
            StateScripts.PrintError(stderr, "stdin must be a JSON list of steps");
            return 1;
        }

        _account = args.GetString("account")!;
        _stateDir = args.GetString("state_dir")!;
        _directory = args.GetString("dir")!;
        if (!PyPath.Exists(_directory))
        {
            PosixNative.Mkdir(_directory, Convert.ToUInt32("700", 8));
        }

        try
        {
            for (var index = 0; index < program.Count; index++)
            {
                var step = (JsonObject)program[index]!;
                if (step.ContainsKey("cli"))
                {
                    Cli(index, step, stdout);
                }
                else if (step.ContainsKey("call"))
                {
                    Call(index, step, stdout);
                }
                else if (step.ContainsKey("clock"))
                {
                    Clock(index, step, stdout);
                }
                else if (step.ContainsKey("max_scan"))
                {
                    MemoryDream.OverrideMaxScan(step["max_scan"] is null ? null : (int)PyJson.AsInt(step["max_scan"]));
                    PyIO.Print(stdout, "## " + index + " max_scan " + MemoryDream.MaxScan.ToString(CultureInfo.InvariantCulture));
                }
                else if (step.ContainsKey("sql"))
                {
                    Sql(index, step, stdout);
                }
                else if (step.ContainsKey("write"))
                {
                    Write(index, step, stdout);
                }
                else if (step.ContainsKey("hold"))
                {
                    Hold(index, step, stdout);
                }
                else if (step.ContainsKey("window"))
                {
                    Window(index, step, stdout);
                }
                else if (step.ContainsKey("zone_sweep"))
                {
                    ZoneSweep(index, step, stdout);
                }
                else if (step.ContainsKey("zone"))
                {
                    Zone(index, step, stdout);
                }
                else
                {
                    StateScripts.PrintError(stderr, "unknown step " + index);
                    return 1;
                }
            }
        }
        finally
        {
            _clock?.Dispose();
            _held?.Close();
            MemoryDream.OverrideMaxScan(null);
        }

        return 0;
    }

    [GeneratedRegex(@"^\$\{([^}]+)\}$")]
    private static partial Regex Whole();

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Part();

    [GeneratedRegex(@"^now(?:([+-])([0-9]+)([smhd]))?$")]
    private static partial Regex ClockExpression();

    [GeneratedRegex(@"^day-([0-9]+)$")]
    private static partial Regex DayExpression();

    [GeneratedRegex(@"^([+-])([0-9]+)([smhd])$")]
    private static partial Regex Offset();

    private static PyTimeDelta Delta(string sign, string count, string unit)
    {
        var amount = long.Parse(count, CultureInfo.InvariantCulture);
        var value = unit switch
        {
            "s" => PyTimeDelta.FromSeconds(amount),
            "m" => PyTimeDelta.FromMinutes(amount),
            "h" => PyTimeDelta.FromHours(amount),
            _ => PyTimeDelta.FromDays(amount),
        };
        return sign == "+" ? value : -value;
    }

    private JsonNode? Value(string expr)
    {
        var clock = ClockExpression().Match(expr);
        if (clock.Success)
        {
            var moment = PyDateTime.Now();
            if (clock.Groups[1].Success)
            {
                moment += Delta(clock.Groups[1].Value, clock.Groups[2].Value, clock.Groups[3].Value);
            }

            return moment.Isoformat();
        }

        var day = DayExpression().Match(expr);
        if (day.Success)
        {
            return (PyDateTime.Now() - PyTimeDelta.FromDays(long.Parse(day.Groups[1].Value, CultureInfo.InvariantCulture))).Date.Isoformat();
        }

        switch (expr)
        {
            case "dir":
                return _directory;
            case "state_dir":
                return _stateDir;
            case "account":
                return _account;
        }

        var parts = expr.Split('.');
        var value = _saved[parts[0]];
        foreach (var part in parts.Skip(1))
        {
            value = value is JsonArray list ? list[int.Parse(part, CultureInfo.InvariantCulture)] : PyNode.Index(value, part);
        }

        return PyNode.Copy(value);
    }

    private string Text(string value) => Part().Replace(value, match =>
    {
        var found = Value(match.Groups[1].Value);
        return PyNode.AsString(found) ?? PyJson.Dumps(found, ensureAscii: false);
    });

    private JsonNode? Template(JsonNode? value)
    {
        switch (value)
        {
            case JsonArray list:
                return new JsonArray(list.Select(Template).ToArray());
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, item) in obj)
                {
                    result[key] = Template(item);
                }

                return result;
            default:
                if (PyNode.AsString(value) is { } text)
                {
                    var whole = Whole().Match(text);
                    return whole.Success ? Value(whole.Groups[1].Value) : Text(text);
                }

                return PyNode.Copy(value);
        }
    }

    private static string ErrorLine(Exception ex) => ex switch
    {
        PyException py => py.PythonType + ": " + py.Message,
        InvalidOperationException => "RuntimeError: " + ex.Message,
        _ => ex.GetType().Name + ": " + ex.Message,
    };

    private void Cli(int index, JsonObject step, TextWriter stdout)
    {
        var args = ((JsonArray)step["cli"]!).Select(arg => Text(PyNode.AsString(arg)!)).ToList();
        if (PyNode.Get(step, "files") is JsonObject files)
        {
            foreach (var (option, value) in files)
            {
                var path = PyPath.Join(_directory, option.Trim('-') + "-" + index.ToString(CultureInfo.InvariantCulture) + ".json");
                WriteText(path, PyJson.Dumps(Template(value), ensureAscii: false));
                args.Add(option);
                args.Add(path);
            }
        }

        var argv = new List<string> { "--account", _account, "--state-dir", _stateDir };
        argv.AddRange(args);
        var stdinText = step.ContainsKey("stdin_json")
            ? PyJson.Dumps(Template(step["stdin_json"]))
            : Text(PyNode.AsString(PyNode.Get(step, "stdin")) ?? "");
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        using var input = new StringReader(stdinText);
        string code;
        try
        {
            code = new MemoryStateScript().Main(argv, input, output, error).ToString(CultureInfo.InvariantCulture);
        }
        catch (PySystemExit exit)
        {
            code = exit.Code.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            code = "uncaught " + ErrorLine(ex);
        }

        PyIO.Print(stdout, "## " + index + " cli " + (args.Count > 0 ? args[0] : "") + ": exit " + code);
        PyIO.Write(stdout, output.ToString());
        if (error.ToString().Length > 0)
        {
            PyIO.Print(stdout, "stderr: " + error.ToString().TrimEnd('\n'));
        }

        if (code == "0" && PyNode.AsString(PyNode.Get(step, "save")) is { Length: > 0 } name)
        {
            _saved[name] = PyJson.Loads(output.ToString());
        }
    }

    private void Call(int index, JsonObject step, TextWriter stdout)
    {
        var on = PyNode.AsString(step["on"])!;
        var name = PyNode.AsString(step["call"])!;
        var args = (JsonArray?)Template(PyNode.Get(step, "args", new JsonArray())) ?? [];
        var account = Text(PyNode.AsString(PyNode.Get(step, "account")) ?? _account);
        var stateDir = Text(PyNode.AsString(PyNode.Get(step, "state_dir")) ?? _stateDir);
        MemoryStore? memory = null;
        TaskStore? tasks = null;
        string line;
        try
        {
            if (on == "tasks")
            {
                tasks = new TaskStore(account, stateDir);
            }
            else
            {
                memory = new MemoryStore(account, stateDir);
            }

            var result = Invoke(on, name, args, memory, tasks);
            line = StateStore.CanonicalJson(result);
            if (PyNode.AsString(PyNode.Get(step, "save")) is { Length: > 0 } saved)
            {
                _saved[saved] = PyNode.Copy(result);
            }
        }
        catch (Exception ex)
        {
            line = "!" + ErrorLine(ex);
        }
        finally
        {
            memory?.Close();
            tasks?.Close();
        }

        PyIO.Print(stdout, "## " + index + " call " + on + "." + name);
        PyIO.Print(stdout, line);
    }

    private static JsonNode? Arg(JsonArray args, int index) => index < args.Count ? args[index] : null;

    private static PyArg Optional(JsonArray args, int index) => index < args.Count ? PyArg.Of(args[index]) : PyArg.Omitted;

    private static string Str(JsonArray args, int index) => PyNode.AsString(Arg(args, index)) ?? throw new InvalidOperationException("probe: string expected");

    private static JsonNode? Invoke(string on, string name, JsonArray args, MemoryStore? memory, TaskStore? tasks) => on switch
    {
        "dream" => name switch
        {
            "scope_for" => MemoryDream.ScopeFor(Arg(args, 0), Arg(args, 1), Arg(args, 2)),
            "capability" => MemoryDream.Capability(Str(args, 0), Arg(args, 1)),
            "bounded" => MemoryDream.Bounded(Arg(args, 0), Str(args, 1)),
            "_current" => MemoryDream.Current(memory!, Arg(args, 0)),
            "checkpoint" => MemoryDream.Checkpoint(memory!, Arg(args, 0)),
            "plan" => MemoryDream.Plan(memory!, Arg(args, 0)),
            "start" => MemoryDream.Start(memory!, Arg(args, 0), Arg(args, 1)),
            "finish" => MemoryDream.Finish(memory!, Arg(args, 0)),
            "inspect" => MemoryDream.Inspect(memory!, Arg(args, 0)),
            "status" => MemoryDream.Status(memory!, Str(args, 0), Str(args, 1)),
            _ => throw new InvalidOperationException("probe: unknown memory_dream function " + name),
        },
        "store" => name switch
        {
            "put" => memory!.Put(Arg(args, 0), Arg(args, 1), Optional(args, 2), Arg(args, 3), Arg(args, 4)),
            "revise" => memory!.Revise(Arg(args, 0), Arg(args, 1), Arg(args, 2), Arg(args, 3), Arg(args, 4)),
            "forget" => memory!.Forget(Arg(args, 0), Arg(args, 1), Arg(args, 2)),
            "capture" => memory!.Capture(Arg(args, 0), Arg(args, 1), Arg(args, 2)),
            "show" => memory!.Show(Arg(args, 0)),
            "list" => memory!.List(Arg(args, 0), Arg(args, 1)),
            "history" => memory!.History(Arg(args, 0)),
            "eligible" => memory!.Eligible(Arg(args, 0), Arg(args, 1), Optional(args, 2), Arg(args, 3)),
            "policy" => memory!.Policy(),
            "record_id" => memory!.RecordId(Arg(args, 0), Arg(args, 1)),
            _ => throw new InvalidOperationException("probe: unknown MemoryStore method " + name),
        },
        "governance" => name switch
        {
            "policy_preview" => MemoryGovernance.PolicyPreview(memory!, Arg(args, 0)),
            "set_policy" => MemoryGovernance.SetPolicy(memory!, Arg(args, 0), Arg(args, 1)),
            "tombstones" => MemoryGovernance.Tombstones(memory!),
            "tombstones_preview" => MemoryGovernance.TombstonesPreview(memory!, Arg(args, 0)),
            "restore_tombstones" => MemoryGovernance.RestoreTombstones(memory!, Arg(args, 0), Arg(args, 1)),
            _ => throw new InvalidOperationException("probe: unknown memory_governance function " + name),
        },
        "proactive" => name switch
        {
            "publication_publish" => ProactiveState.PublicationPublish(memory!.Conn, Str(args, 0), Arg(args, 1)),
            "publication_review" => ProactiveState.PublicationReview(memory!.Conn, Str(args, 0), PyNode.AsString(Arg(args, 1))),
            "publication_show" => ProactiveState.PublicationShow(memory!.Conn, Str(args, 0)),
            _ => throw new InvalidOperationException("probe: unknown proactive_state function " + name),
        },
        "tasks" => name switch
        {
            "open" => "opened",
            "cancel" => tasks!.Cancel(Arg(args, 0), Arg(args, 1)),
            "show" => tasks!.Show(Arg(args, 0)),
            _ => throw new InvalidOperationException("probe: unknown TaskStore method " + name),
        },
        _ => throw new InvalidOperationException("probe: unknown target " + on),
    };

    private void Clock(int index, JsonObject step, TextWriter stdout)
    {
        _clock?.Dispose();
        _clock = null;
        if (step["clock"] is null)
        {
            PyIO.Print(stdout, "## " + index + " clock real");
            return;
        }

        var fixedAt = PyDateTime.FromIsoformat(Text(PyNode.AsString(step["clock"])!));
        var offset = Offset().Match(PyNode.AsString(PyNode.Get(step, "offset")) ?? "+0s");
        fixedAt += Delta(offset.Groups[1].Value, offset.Groups[2].Value, offset.Groups[3].Value);
        _clock = TaskRuns.OverrideClock(() => fixedAt);
        PyIO.Print(stdout, "## " + index + " clock fixed");
    }

    private void Sql(int index, JsonObject step, TextWriter stdout)
    {
        var database = PyPath.Join(PyPath.Join(_stateDir, PyIds.Sha256Hex(_account)), "margo.sqlite3");
        string line;
        using (var connection = PyConnection.Connect(database))
        {
            try
            {
                var parameters = ((JsonArray?)Template(PyNode.Get(step, "params", new JsonArray())) ?? []).Cast<object?>().ToArray();
                var cursor = connection.Execute(PyNode.AsString(step["sql"])!, parameters);
                var rows = new JsonArray();
                foreach (var row in cursor.FetchAll())
                {
                    rows.Add(new JsonArray(row.Select(PyNode.FromSql).ToArray()));
                }

                connection.Commit();
                line = PyJson.Dumps(new JsonObject { ["rowcount"] = cursor.RowCount, ["rows"] = rows }, ensureAscii: false);
            }
            catch (PySqliteError ex)
            {
                line = "!" + ex.PythonType + ": " + ex.Message;
            }
        }

        PyIO.Print(stdout, "## " + index + " sql");
        PyIO.Print(stdout, line);
    }

    private void Write(int index, JsonObject step, TextWriter stdout)
    {
        var name = PyNode.AsString(step["write"])!;
        var path = PyPath.Join(_directory, name);
        if (step.ContainsKey("json"))
        {
            WriteText(path, PyJson.Dumps(Template(step["json"]), ensureAscii: false));
        }
        else if (step.ContainsKey("hex"))
        {
            File.WriteAllBytes(path, Convert.FromHexString(PyNode.AsString(step["hex"])!));
        }
        else
        {
            File.WriteAllBytes(path, PyIO.EncodeUtf8(Text(PyNode.AsString(step["text"])!)));
        }

        if (PyNode.AsString(PyNode.Get(step, "save_sha256")) is { Length: > 0 } saved)
        {
            _saved[saved] = PyIds.Sha256Hex(File.ReadAllBytes(path));
        }

        PyIO.Print(stdout, "## " + index + " write " + name);
    }

    private void Hold(int index, JsonObject step, TextWriter stdout)
    {
        if (PyJson.Truthy(step["hold"]))
        {
            _held = new MemoryStore(_account, _stateDir);
            _held.Conn.Execute("BEGIN IMMEDIATE");
            _held.Conn.Execute("INSERT INTO memory_meta VALUES('synthetic-write','pending')");
            PyIO.Print(stdout, "## " + index + " hold");
        }
        else
        {
            _held!.Conn.Rollback();
            _held.Close();
            _held = null;
            PyIO.Print(stdout, "## " + index + " release");
        }
    }

    private void Window(int index, JsonObject step, TextWriter stdout)
    {
        string line;
        try
        {
            var (start, end) = MemoryDream.Window(Template(step["window"]));
            line = StateStore.CanonicalJson(new JsonArray(start.Isoformat(), end.Isoformat()));
        }
        catch (Exception ex)
        {
            line = "!" + ErrorLine(ex);
        }

        PyIO.Print(stdout, "## " + index + " window");
        PyIO.Print(stdout, line);
    }

    private static void Zone(int index, JsonObject step, TextWriter stdout)
    {
        PyIO.Print(stdout, "## " + index + " zone");
        PyZoneInfo zone;
        try
        {
            zone = PyZoneInfo.Load(step["zone"]);
        }
        catch (Exception ex)
        {
            PyIO.Print(stdout, "!" + ErrorLine(ex));
            return;
        }

        foreach (var item in (JsonArray?)PyNode.Get(step, "local") ?? [])
        {
            var pair = (JsonArray)item!;
            var text = PyNode.AsString(pair[0])!;
            var fold = (int)PyJson.AsInt(pair[1]);
            string line;
            try
            {
                var offset = zone.UtcOffset(PyDateTime.FromIsoformat(text).WithOffset(null), fold);
                line = text + " fold=" + fold.ToString(CultureInfo.InvariantCulture) + " "
                       + ((long)offset.TotalSeconds()).ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                line = text + " fold=" + fold.ToString(CultureInfo.InvariantCulture) + " !" + ErrorLine(ex);
            }

            PyIO.Print(stdout, line);
        }

        foreach (var item in (JsonArray?)PyNode.Get(step, "utc") ?? [])
        {
            var text = PyNode.AsString(item)!;
            string line;
            try
            {
                var utc = PyDateTime.FromIsoformat(text).ToUtc();
                var naive = utc.WithOffset(null);
                var offset = zone.FromUtcOffset(PyZoneInfo.LocalSeconds(naive), naive.Year);
                var local = zone.FromUtc(utc);
                line = text + " -> " + local.WithOffset(PyTimeDelta.FromSeconds(offset)).Isoformat();
            }
            catch (Exception ex)
            {
                line = text + " -> !" + ErrorLine(ex);
            }

            PyIO.Print(stdout, line);
        }
    }

    private static void ZoneSweep(int index, JsonObject step, TextWriter stdout)
    {
        PyIO.Print(stdout, "## " + index + " zone_sweep");
        var years = step.ContainsKey("years") ? (int)PyJson.AsInt(step["years"]) : 30;
        foreach (var item in (JsonArray)step["zone_sweep"]!)
        {
            var key = PyNode.AsString(item)!;
            PyZoneInfo zone;
            try
            {
                zone = PyZoneInfo.Load(key);
            }
            catch (Exception ex)
            {
                PyIO.Print(stdout, key + " !" + ErrorLine(ex));
                continue;
            }

            var values = new List<long>();
            void Local(long seconds)
            {
                if (seconds >= Low && seconds <= High)
                {
                    var year = (Epoch + PyTimeDelta.FromSeconds(seconds)).Year;
                    values.Add(zone.UtcOffset(seconds, year, 0));
                    values.Add(zone.UtcOffset(seconds, year, 1));
                }
            }

            long? Utc(long seconds)
            {
                if (seconds < Low || seconds > High)
                {
                    return null;
                }

                var offset = zone.FromUtcOffset(seconds, (Epoch + PyTimeDelta.FromSeconds(seconds)).Year);
                values.Add(offset);
                return offset;
            }

            foreach (var transition in zone.TransitionsUtc)
            {
                foreach (var moment in new[] { transition - 1, transition, transition + 1 })
                {
                    if (Utc(moment) is { } offset)
                    {
                        foreach (var wall in new[] { moment + offset - 1, moment + offset, moment + offset + 1 })
                        {
                            Local(wall);
                        }
                    }
                }
            }

            var last = zone.TransitionsUtc.Count > 0 ? (Epoch + PyTimeDelta.FromSeconds(zone.TransitionsUtc[^1])).Year : 1970;
            if (zone.RuleTransitions(last) is not null)
            {
                foreach (var year in Enumerable.Range(last, years).Append(9999))
                {
                    var (start, end, diff, std, dst) = zone.RuleTransitions(year)!.Value;
                    foreach (var @base in new[] { start, end })
                    {
                        foreach (var wall in new[] { @base - diff - 1, @base - diff, @base - 1, @base, @base + diff - 1, @base + diff })
                        {
                            Local(wall);
                        }
                    }

                    foreach (var @base in new[] { start - std, end - dst })
                    {
                        foreach (var moment in new[] { @base - 1, @base, @base + 1 })
                        {
                            Utc(moment);
                        }
                    }
                }
            }

            var text = string.Join("\n", values.Select(value => value.ToString(CultureInfo.InvariantCulture)));
            PyIO.Print(stdout, key + " " + values.Count.ToString(CultureInfo.InvariantCulture) + " "
                               + PyIds.Sha256Hex(text)[..16]);
        }
    }

    /// <summary><c>Path.write_text(text, encoding="utf-8")</c>.</summary>
    private static void WriteText(string path, string text) => File.WriteAllBytes(path, PyIO.EncodeUtf8(text));
}
