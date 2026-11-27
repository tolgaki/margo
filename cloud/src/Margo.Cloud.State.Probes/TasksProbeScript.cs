using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Proactive;
using Margo.Cloud.State.Tasks;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.State.Probes;

/// <summary>
/// C# twin of <c>cloud/contracts/state/probes/tasks_probe.py</c>. Not a Margo command and not offered to the hosted
/// model: <c>tools/state_parity.py</c> runs both on the same program so that time-dependent <c>task_state.py</c> flows
/// (fresh bindings, deadlines, claims and tokens), the module clock (<see cref="TaskRuns.OverrideClock"/>, as Python's
/// tests patch <c>task_runs.clock</c>) and the <see cref="TaskStore"/> library API are judged byte for byte. Keep the
/// two files in step.
/// </summary>
public sealed partial class TasksProbeScript : IStateProbeScript
{
    private readonly Dictionary<string, JsonNode?> _saved = new(StringComparer.Ordinal);
    private IDisposable? _clock;
    private string _account = "";
    private string _stateDir = "";
    private string _directory = "";

    public string ScriptName => "tasks_probe.py";

    // A fresh instance per run: saved results and the clock never leak between runs of the shared registry instance.
    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => new TasksProbeScript().Main(args, stdin, stdout, stderr), stderr);

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
                else if (step.ContainsKey("sql"))
                {
                    Sql(index, step, stdout);
                }
                else if (step.ContainsKey("clock"))
                {
                    Clock(index, step, stdout);
                }
                else if (step.ContainsKey("write"))
                {
                    Write(index, step, stdout);
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
        }

        return 0;
    }

    [GeneratedRegex(@"^\$\{([^}]+)\}$")]
    private static partial Regex Whole();

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Part();

    [GeneratedRegex(@"^now(?:([+-])([0-9]+)([smhd]))?$")]
    private static partial Regex ClockExpression();

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

        if (expr == "dir")
        {
            return _directory;
        }

        if (expr == "account")
        {
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
        if (step.ContainsKey("input"))
        {
            var path = PyPath.Join(_directory, "input-" + index + ".json");
            WriteText(path, PyJson.Dumps(Template(step["input"]), ensureAscii: false));
            args.Add("--input");
            args.Add(path);
        }

        var argv = new List<string> { "--account", _account, "--state-dir", _stateDir };
        argv.AddRange(args);
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        using var input = new StringReader(Text(PyNode.AsString(step["stdin"]) ?? ""));
        string code;
        try
        {
            code = new TaskStateScript().Main(argv, input, output, error).ToString(CultureInfo.InvariantCulture);
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

        if (code == "0" && PyNode.AsString(step["save"]) is { Length: > 0 } name)
        {
            _saved[name] = PyJson.Loads(output.ToString());
        }
    }

    private void Call(int index, JsonObject step, TextWriter stdout)
    {
        var on = PyNode.AsString(PyNode.Get(step, "on")) ?? "store";
        var method = PyNode.AsString(step["call"]);
        var args = (JsonArray?)Template(PyNode.Get(step, "args", new JsonArray())) ?? [];
        var kwargs = (JsonObject?)Template(PyNode.Get(step, "kwargs", new JsonObject())) ?? [];
        var a = new Arguments(args, kwargs);
        TaskStore? store = null;
        PyConnection? connection = null;
        string line;
        try
        {
            JsonNode? result;
            if (on == "module")
            {
                result = Module(method!, a);
            }
            else
            {
                if (on == "borrowed")
                {
                    connection = StateStore.Connect(_account, _stateDir);
                    store = TaskStore.FromConnection(connection, PyNode.AsString(PyNode.Get(step, "borrow_account")) ?? _account);
                }
                else
                {
                    store = new TaskStore(_account, _stateDir, readOnly: PyJson.Truthy(PyNode.Get(step, "read_only")));
                }

                var target = store;
                Func<JsonNode?> call = on switch
                {
                    "ledger" => () => LedgerCall(target.Ledger, method!, a),
                    "proactive" => () => ProactiveCall(target.Conn, method!, a),
                    _ => () => method is null ? "borrowed" : StoreCall(target, method, a),
                };
                if (PyNode.Is(PyNode.Get(step, "outer"), "rollback"))
                {
                    result = null;
                    try
                    {
                        store.Transaction(() =>
                        {
                            result = call();
                            throw new RollbackException();
                        });
                    }
                    catch (RollbackException)
                    {
                    }
                }
                else
                {
                    result = call();
                }
            }

            line = StateStore.CanonicalJson(result);
            if (PyNode.AsString(PyNode.Get(step, "save")) is { Length: > 0 } name)
            {
                _saved[name] = PyNode.Copy(result);
            }
        }
        catch (Exception ex)
        {
            line = "!" + ErrorLine(ex);
        }
        finally
        {
            store?.Close();
            connection?.Close();
        }

        PyIO.Print(stdout, "## " + index + " call " + on + "." + (method ?? "None"));
        PyIO.Print(stdout, line);
    }

    private sealed class RollbackException : Exception;

    private sealed class Arguments(JsonArray positional, JsonObject keywords)
    {
        public bool Has(int index, string name) => index < positional.Count || keywords.ContainsKey(name);

        public JsonNode? Get(int index, string name, JsonNode? fallback = null) =>
            index < positional.Count ? positional[index] : keywords.TryGetPropertyValue(name, out var value) ? value : fallback;

        public string? Text(int index, string name, string? fallback = null) =>
            Has(index, name) ? PyNode.AsString(Get(index, name)) : fallback;

        public bool Flag(int index, string name, bool fallback) => Has(index, name) ? PyJson.Truthy(Get(index, name)) : fallback;
    }

    private static JsonNode? Module(string method, Arguments a) => method switch
    {
        "zero" => TaskRuns.Zero(),
        "task_identity" => TaskRuns.TaskIdentity(a.Text(0, "account")!, a.Get(1, "key")),
        "resource_cost" => TaskRuns.ResourceCost(a.Get(0, "value")),
        "environment" => TaskRuns.Environment(a.Get(0, "value"), a.Text(1, "account")!, a.Flag(2, "fresh", false)),
        "binding_hash" => TaskRuns.BindingHash(a.Get(0, "value")),
        "text" => TaskRuns.Text(a.Get(0, "value"), a.Text(1, "field")!, a.Has(2, "maximum") ? PyJson.AsInt(a.Get(2, "maximum")) : 4000),
        "integer" => PyJson.Int(TaskRuns.Integer(a.Get(0, "value"), a.Text(1, "field")!,
                                                 a.Has(2, "low") ? PyJson.AsInt(a.Get(2, "low")) : 0,
                                                 a.Has(3, "high") ? PyJson.AsInt(a.Get(3, "high")) : 1000000)),
        "when" => TaskRuns.When(a.Get(0, "value")).Isoformat(),
        "clock" => TaskRuns.Clock().Isoformat(),
        _ => throw new InvalidOperationException("probe: unknown task_runs function " + method),
    };

    private static JsonObject StoreCall(TaskStore store, string method, Arguments a) => method switch
    {
        "create" => store.Create(a.Get(0, "key"), a.Get(1, "plan")),
        "start" => store.Start(a.Get(0, "run_id"), a.Get(1, "step_key"), a.Get(2, "revision"), a.Get(3, "plan_hash"), a.Get(4, "binding"),
                               a.Get(5, "preflight")),
        "charge" => store.Charge(a.Get(0, "attempt_id"), a.Get(1, "token"), a.Get(2, "event_id"), a.Get(3, "cost")),
        "finish" => store.Finish(a.Get(0, "attempt_id"), a.Get(1, "token"), a.Get(2, "outcome"), a.Get(3, "result"), a.Get(4, "actual")),
        "pause" => store.Pause(a.Get(0, "run_id"), a.Get(1, "reason")),
        "cancel" => store.Cancel(a.Get(0, "run_id"), a.Get(1, "reason")),
        "resume" => store.Resume(a.Get(0, "run_id"), a.Get(1, "revision"), a.Get(2, "binding")),
        "retry" => store.Retry(a.Get(0, "run_id"), a.Get(1, "step_key"), a.Get(2, "revision"), a.Get(3, "binding")),
        "recover" => store.Recover(a.Get(0, "run_id")),
        "sync" => store.Sync(a.Get(0, "run_id")),
        "reconcile" => store.Reconcile(a.Get(0, "run_id"), a.Get(1, "step_key"), a.Get(2, "revision"), a.Get(3, "outcome"), a.Get(4, "result")),
        "replan_preview" => store.ReplanPreview(a.Get(0, "run_id"), a.Get(1, "plan")),
        "replan" => store.Replan(a.Get(0, "run_id"), a.Get(1, "plan"), a.Get(2, "evidence")),
        "show" => store.Show(a.Get(0, "run_id")),
        "list" => store.List(a.Get(0, "limit", PyNode.Of(50)), a.Get(1, "after")),
        "health" => store.Health(),
        "history" => store.History(a.Get(0, "run_id"), a.Get(1, "limit", PyNode.Of(100))),
        _ => throw new InvalidOperationException("probe: unknown TaskStore method " + method),
    };

    private static JsonObject LedgerCall(Ledger ledger, string method, Arguments a) => method switch
    {
        "source" => ledger.Source(a.Get(0, "family"), a.Get(1, "scope"), a.Get(2, "external_id"), a.Get(3, "revision"), a.Get(4, "evidence"),
                                  a.Get(5, "web_link"), a.Get(6, "sensitivity"), a.Get(7, "observed_at")),
        "propose" => ledger.Propose(a.Get(0, "data")),
        "edit_action" => ledger.EditAction(a.Get(0, "action_id"), a.Get(1, "revision"), a.Get(2, "data"), a.Get(3, "expected_hash")),
        "approve" => ledger.Approve(a.Get(0, "action_id"), a.Get(1, "revision"), a.Get(2, "action_hash"), a.Get(3, "evidence"), a.Get(4, "expires_at")),
        "begin" => ledger.Begin(a.Get(0, "action_id"), a.Get(1, "revision"), a.Get(2, "fresh")),
        "finish" => ledger.Finish(a.Get(0, "attempt_id"), a.Get(1, "state"), a.Get(2, "receipt"), a.Flag(3, "reconcile", false)),
        "show" => ledger.Show(a.Get(0, "entity_id")),
        "ingest" => ledger.Ingest(a.Get(0, "data"), a.Get(1, "claim_key")),
        _ => throw new InvalidOperationException("probe: unknown ledger method " + method),
    };

    private static JsonNode? ProactiveCall(PyConnection conn, string method, Arguments a) => method switch
    {
        "coverage_start" => ProactiveState.CoverageStart(conn, a.Get(0, "data")),
        "coverage_page" => ProactiveState.CoveragePage(conn, a.Text(0, "attempt_id")!, a.Get(1, "data")),
        "coverage_finish" => ProactiveState.CoverageFinish(conn, a.Text(0, "attempt_id")!, a.Get(1, "data")),
        "queue_add" => ProactiveState.QueueAdd(conn, a.Get(0, "data"), a.Text(1, "tier")),
        "queue_drain" => ProactiveState.QueueDrain(conn, a.Text(0, "owner")),
        "publication_record" => ProactiveState.PublicationRecord(conn, a.Text(0, "batch_id"), a.Get(1, "data")),
        "publication_publish" => ProactiveState.PublicationPublish(conn, a.Text(0, "publication_id")!, a.Get(1, "data")),
        _ => throw new InvalidOperationException("probe: unknown proactive_state function " + method),
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
        else
        {
            var repeat = step.ContainsKey("repeat") ? (int)PyJson.AsInt(step["repeat"]) : 1;
            var bytes = step.ContainsKey("hex") ? Convert.FromHexString(PyNode.AsString(step["hex"])!) : PyIO.EncodeUtf8(Text(PyNode.AsString(step["text"])!));
            using var output = new MemoryStream();
            output.Write(PyIO.EncodeUtf8(Text(PyNode.AsString(PyNode.Get(step, "prefix")) ?? "")));
            for (var i = 0; i < repeat; i++)
            {
                output.Write(bytes);
            }

            output.Write(PyIO.EncodeUtf8(Text(PyNode.AsString(PyNode.Get(step, "suffix")) ?? "")));
            output.Write(Convert.FromHexString(PyNode.AsString(PyNode.Get(step, "suffix_hex")) ?? ""));

            File.WriteAllBytes(path, output.ToArray());
        }

        PyIO.Print(stdout, "## " + index + " write " + name);
    }

    /// <summary><c>Path.write_text(text, encoding="utf-8")</c>: mode 0666 minus the umask, as <c>open()</c> creates files.</summary>
    private static void WriteText(string path, string text) => File.WriteAllBytes(path, PyIO.EncodeUtf8(text));
}
